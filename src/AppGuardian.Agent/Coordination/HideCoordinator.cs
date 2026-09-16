using System.Diagnostics;
using System.Runtime.Versioning;
using AppGuardian.Shared.Contracts;
using AppGuardian.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AppGuardian.Agent.Coordination;

/// <summary>
/// Hides and reveals an app's windows. FR-601–FR-606.
/// </summary>
/// <remarks>
/// The service decides <em>what</em> to hide; this decides <em>which windows</em>, because window handles
/// only exist in the interactive session and a process can own several.
/// <para>
/// Hidden windows are tracked per app so a reveal can restore exactly what was hidden. Re-enumerating at
/// reveal time would not work: a hidden window is not enumerable in the same way, and a window the app
/// opened while hidden must not be restored as though the user had hidden it.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class HideCoordinator
{
    private readonly IVirtualDesktopAdapter _desktops;
    private readonly ServiceConnection _service;
    private readonly ILogger<HideCoordinator> _log;

    private readonly Dictionary<string, HiddenApp> _hidden = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public HideCoordinator(
        IVirtualDesktopAdapter desktops,
        ServiceConnection service,
        ILogger<HideCoordinator> log)
    {
        _desktops = desktops;
        _service = service;
        _log = log;
    }

    /// <summary>Whether real virtual desktops are in use. Surfaced to the dashboard. FR-604, FR-806.</summary>
    public bool UsesRealDesktops => _desktops.IsSupported;

    public string StrategyDescription => _desktops is IHideStrategyDescription described
        ? described.StrategyDescription
        : "Hidden apps are removed from the taskbar.";

    /// <summary>Creates or locates the hidden workspace. FR-600.</summary>
    public DesktopId EnsureWorkspace() => _desktops.EnsureHidden(PolicyConstants.HiddenWorkspaceName);

    /// <summary>Hides every top-level window belonging to the request's process. FR-601.</summary>
    public async Task<HideResult> HideAsync(MoveToHiddenRequest request, CancellationToken ct)
    {
        var target = EnsureWorkspace();
        var windows = WindowEnumerator.TopLevelWindowsFor(request.ProcessId).ToList();

        // The service may have supplied the handle it saw first. It is added rather than used alone:
        // hiding only the window the service happened to notice would leave an app's other windows —
        // a document, a preferences dialog — on screen, which reads as the feature not working.
        if (request.WindowHandle != 0 && !windows.Contains((nint)request.WindowHandle))
        {
            windows.Add((nint)request.WindowHandle);
        }

        if (windows.Count == 0)
        {
            // Not an error. A process that has not created its window yet is the normal case immediately
            // after launch, and the reconcile pass in the service will call again.
            return new HideResult
            {
                Succeeded = false,
                Detail = "The app has no windows on screen yet.",
            };
        }

        var moved = new List<nint>();

        foreach (var hwnd in windows)
        {
            if (_desktops.MoveWindow(hwnd, target))
            {
                moved.Add(hwnd);
            }
        }

        lock (_gate)
        {
            if (_hidden.TryGetValue(request.AppId, out var existing))
            {
                foreach (var hwnd in moved)
                {
                    existing.Windows.Add(hwnd);
                }
            }
            else
            {
                _hidden[request.AppId] = new HiddenApp(request.ProcessId, new HashSet<nint>(moved));
            }
        }

        var usedFallback = !_desktops.IsSupported;

        var result = new HideResult
        {
            Succeeded = moved.Count > 0,
            UsedFallback = usedFallback,
            Detail = moved.Count == windows.Count
                ? usedFallback
                    ? $"{moved.Count} window(s) minimised and removed from the taskbar."
                    : $"{moved.Count} window(s) moved to the hidden desktop."

                // Partial success is reported as such rather than rounded up. A window that refused to
                // move is almost always one owned by an elevated process, and the user needs to know it
                // is still visible instead of trusting a success message.
                : $"{moved.Count} of {windows.Count} window(s) hidden; the rest could not be moved, "
                  + "usually because the app is running as administrator.",
        };

        if (!result.Succeeded)
        {
            await AuditAsync(AuditAction.Hide, AuditResult.Error, request.AppId, result.Detail!, ct)
                .ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>Brings an app's hidden windows back. FR-604, FR-606.</summary>
    public async Task<HideResult> RevealAsync(RevealRequest request, CancellationToken ct)
    {
        HiddenApp? record;

        lock (_gate)
        {
            // Removed regardless of the outcome below. A reveal that failed leaves nothing useful in the
            // table — the handles are stale — and keeping them would make every later reveal retry them.
            _hidden.Remove(request.AppId, out record);
        }

        if (record is null || record.Windows.Count == 0)
        {
            return new HideResult
            {
                Succeeded = false,
                Detail = "AppGuardian is not currently hiding that app.",
            };
        }

        var restored = 0;
        var stale = 0;

        foreach (var hwnd in record.Windows)
        {
            // A window whose process has exited leaves a handle that is either invalid or reused by an
            // unrelated window. Restoring it blindly would pop a stranger's window into the foreground.
            if (!WindowEnumerator.BelongsToProcess(hwnd, record.ProcessId))
            {
                stale++;
                continue;
            }

            if (_desktops.MoveToCurrent(hwnd))
            {
                restored++;
            }
        }

        var succeeded = restored > 0;

        var detail = succeeded
            ? $"{restored} window(s) restored."
            : stale > 0
                ? "The app is no longer running, so there was nothing to restore."
                : "The app's windows could not be restored.";

        await AuditAsync(
            AuditAction.Reveal,
            succeeded ? AuditResult.Ok : AuditResult.Error,
            request.AppId,
            detail,
            ct).ConfigureAwait(false);

        return new HideResult
        {
            Succeeded = succeeded,
            UsedFallback = !_desktops.IsSupported,
            Detail = detail,
        };
    }

    /// <summary>App ids the agent is currently hiding, for the dashboard's hidden-apps page. FR-605.</summary>
    public IReadOnlyList<string> HiddenAppIds()
    {
        lock (_gate)
        {
            return _hidden.Keys.ToList();
        }
    }

    /// <summary>
    /// Drops entries whose process has exited.
    /// </summary>
    /// <remarks>
    /// Called from the agent's reconcile pass. Without it the hidden-apps page would keep listing an app
    /// the user closed, and offering a Reveal button that can only fail.
    /// </remarks>
    public void PruneExited()
    {
        lock (_gate)
        {
            foreach (var (appId, record) in _hidden.ToList())
            {
                if (!IsAlive(record.ProcessId))
                {
                    _hidden.Remove(appId);
                }
            }
        }
    }

    /// <summary>
    /// Restores everything. Called on shutdown and on logoff.
    /// </summary>
    /// <remarks>
    /// Leaving windows hidden after the agent stops would leave the user with a running application they
    /// cannot reach except through Task Manager — indistinguishable from having lost their work. This runs
    /// even when the service is unreachable, because it needs nothing from it.
    /// </remarks>
    public void RevealAll()
    {
        List<HiddenApp> records;

        lock (_gate)
        {
            records = _hidden.Values.ToList();
            _hidden.Clear();
        }

        foreach (var record in records)
        {
            foreach (var hwnd in record.Windows)
            {
                if (WindowEnumerator.BelongsToProcess(hwnd, record.ProcessId))
                {
                    _desktops.MoveToCurrent(hwnd);
                }
            }
        }
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private async Task AuditAsync(
        AuditAction action,
        AuditResult result,
        string appId,
        string detail,
        CancellationToken ct)
    {
        try
        {
            await _service
                .AuditAsync(
                    AuditEntry.Create(AuditActor.Agent, action, result, appId, detail: detail),
                    ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Auditing must never fail the operation it describes. A hide that refused to happen because
            // the log was unreachable would be strictly worse than a gap in the history.
            _log.LogDebug(ex, "An audit entry for {AppId} could not be recorded.", appId);
        }
    }

    private sealed record HiddenApp(int ProcessId, HashSet<nint> Windows);
}

/// <summary>
/// Lets the coordinator report the adapter's strategy without depending on the concrete adapter.
/// </summary>
/// <remarks>
/// The description is user-facing text (FR-806) and belongs to whichever strategy is actually in use, but
/// <see cref="IVirtualDesktopAdapter"/> is in the platform-neutral Shared assembly and cannot carry it.
/// </remarks>
public interface IHideStrategyDescription
{
    string StrategyDescription { get; }
}
