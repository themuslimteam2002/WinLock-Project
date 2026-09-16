using System.Runtime.Versioning;
using AppGuardian.Agent.Overlay;
using AppGuardian.Shared.Contracts;
using AppGuardian.Shared.Ipc;
using AppGuardian.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AppGuardian.Agent.Coordination;

/// <summary>
/// Decides when the overlay goes up and comes down. FR-501, FR-502, FR-505, FR-506.
/// </summary>
/// <remarks>
/// Two inputs, one decision. The service pushes <c>lock.showOverlay</c> when it sees a locked app launch;
/// the local foreground hook catches the case the service cannot see at all — an already-running app the
/// user switches back to, which produces no process event.
/// <para>
/// The set of locked app ids is cached from the policy rather than queried per foreground change. A pipe
/// round trip on every Alt+Tab would blow NFR-P4's one-second budget and put constant load on the service;
/// the cache is refreshed on <c>policy.changed</c>, which the service pushes for every mutation.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class LockCoordinator : IDisposable
{
    private readonly IForegroundWatcher _watcher;
    private readonly LockOverlay _overlay;
    private readonly ServiceConnection _service;
    private readonly ILogger<LockCoordinator> _log;

    private readonly object _gate = new();

    /// <summary>Rules with locking on, keyed by appId. Refreshed on policy.changed.</summary>
    private Dictionary<string, AppRule> _lockedApps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>App ids with a live unlock session, so a switch back does not re-prompt (FR-506).</summary>
    private readonly HashSet<string> _unlocked = new(StringComparer.OrdinalIgnoreCase);

    private bool _paused;
    private string? _showingFor;

    public LockCoordinator(
        IForegroundWatcher watcher,
        LockOverlay overlay,
        ServiceConnection service,
        ILogger<LockCoordinator> log)
    {
        _watcher = watcher;
        _overlay = overlay;
        _service = service;
        _log = log;

        _watcher.ForegroundChanged += OnForegroundChanged;
        _overlay.Unlocked += OnOverlayUnlocked;
        _overlay.Dismissed += OnOverlayDismissed;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        // Policy first. Starting the watcher before the cache is populated would let the first foreground
        // change through unchecked, and on a machine where the user's locked app is already in front at
        // logon that first event is exactly the one that matters.
        await RefreshPolicyAsync(ct).ConfigureAwait(false);
        await _watcher.StartAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Re-reads the policy. Called at startup and on every <c>policy.changed</c> push.</summary>
    public async Task RefreshPolicyAsync(CancellationToken ct)
    {
        var snapshot = await _service
            .SendAsync<PolicySnapshot>(MessageTypes.PolicyGet, ct: ct)
            .ConfigureAwait(false);

        if (snapshot?.Policy is null)
        {
            // The previous cache is kept rather than cleared. Clearing it would silently unlock every
            // protected app for the duration of a service outage, which is the opposite of what a lock
            // should do when something goes wrong.
            _log.LogWarning("The policy could not be read; the previous rule set stays in effect.");
            return;
        }

        var locked = snapshot.Policy.Rules
            .Where(rule => rule.Enabled && rule.Lock.Enabled)
            .GroupBy(rule => rule.Identity.AppId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        lock (_gate)
        {
            _lockedApps = locked;
            _paused = snapshot.Policy.ProtectionPaused;

            // Sessions for apps that are no longer locked are dropped. Keeping them would leave stale
            // entries that suppress the overlay if the user re-enabled the lock later.
            _unlocked.RemoveWhere(appId => !locked.ContainsKey(appId));
        }

        _log.LogDebug(
            "{Count} locked app(s) in effect; paused={Paused}.",
            locked.Count,
            snapshot.Policy.ProtectionPaused);
    }

    /// <summary>Handles the service's <c>lock.showOverlay</c> push. FR-502.</summary>
    public void ShowOverlayFor(ShowOverlayRequest request)
    {
        lock (_gate)
        {
            if (_paused)
            {
                return;
            }

            // The service asked, so its view of the rule wins. The local cache is only consulted for
            // foreground changes, where there is nobody to ask.
            _unlocked.Remove(request.Identity.AppId);
            _showingFor = request.Identity.AppId;
        }

        _overlay.Show(request.Identity, _watcher.GetMonitors());
    }

    /// <summary>Marks an app unlocked without prompting. Used when the dashboard unlocks it. FR-506.</summary>
    public void NoteUnlocked(string appId)
    {
        lock (_gate)
        {
            _unlocked.Add(appId);

            if (string.Equals(_showingFor, appId, StringComparison.OrdinalIgnoreCase))
            {
                _showingFor = null;
                _overlay.Hide();
            }
        }
    }

    /// <summary>Drops an app's unlock session, so the next switch to it prompts again. FR-505.</summary>
    public void NoteRelocked(string appId)
    {
        lock (_gate)
        {
            _unlocked.Remove(appId);
        }
    }

    public void SetPaused(bool paused)
    {
        lock (_gate)
        {
            _paused = paused;
        }

        if (paused)
        {
            // Pausing takes the overlay down immediately. A pause that left a lock screen up would look
            // like the toggle had not worked, and the user has already told us to stop enforcing.
            _overlay.Hide();
        }
    }

    private void OnForegroundChanged(object? sender, ForegroundChangedEventArgs e)
    {
        // Runs on the WinEvent hook thread. Everything here is either cheap or handed off; a blocking
        // call would delay delivery of every subsequent foreground change on the desktop.
        try
        {
            if (_overlay.IsVisible)
            {
                // Something took the foreground while the overlay was up — a UAC prompt, the locked app
                // raising its own window, another topmost window. Re-asserting is what keeps the lock a
                // lock rather than a window the user can click behind.
                _overlay.ReassertTopmost();
                return;
            }

            var path = e.ExecutablePath;

            if (string.IsNullOrWhiteSpace(path))
            {
                // No path means a protected or cross-user process, which cannot be one of the user's
                // own locked apps. Treated as not-a-match rather than as an error.
                return;
            }

            var appId = AppIdentity.DeriveAppId(path, null);

            AppRule? rule;

            lock (_gate)
            {
                if (_paused || _unlocked.Contains(appId) || !_lockedApps.TryGetValue(appId, out rule))
                {
                    return;
                }

                _showingFor = appId;
            }

            _log.LogDebug("Locked app {AppId} came to the foreground; showing the overlay.", appId);

            _overlay.Show(rule.Identity, _watcher.GetMonitors());
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "A foreground change could not be evaluated.");
        }
    }

    private void OnOverlayUnlocked(object? sender, OverlayUnlockedEventArgs e)
    {
        lock (_gate)
        {
            _unlocked.Add(e.AppId);
            _showingFor = null;
        }

        // Fire-and-forget. The overlay is already down and the user is already in their app; making them
        // wait on a pipe round trip to see their own window would be a second of unexplained delay.
        _ = Task.Run(async () =>
        {
            try
            {
                await _service
                    .AuditAsync(AuditEntry.Create(
                        AuditActor.Agent,
                        AuditAction.Unlock,
                        AuditResult.Ok,
                        e.AppId,
                        detail: $"Unlocked with {Describe(e.Method)}."))
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "The unlock of {AppId} could not be audited.", e.AppId);
            }
        });
    }

    private void OnOverlayDismissed(object? sender, EventArgs e)
    {
        string? appId;

        lock (_gate)
        {
            appId = _showingFor;
            _showingFor = null;
        }

        if (appId is null)
        {
            return;
        }

        // The app stays locked and the session is not created, so the next switch to it prompts again.
        // Nothing is minimised or closed: the user dismissed a prompt, they did not ask us to interfere
        // with their window.
        _log.LogDebug("The overlay for {AppId} was dismissed; the app stays locked.", appId);
    }

    private static string Describe(AuthMethod method) => method switch
    {
        AuthMethod.WindowsHello => "Windows Hello",
        AuthMethod.Pin => "a PIN",
        AuthMethod.Password => "a password",
        _ => "an unknown method",
    };

    public void Dispose()
    {
        _watcher.ForegroundChanged -= OnForegroundChanged;
        _overlay.Unlocked -= OnOverlayUnlocked;
        _overlay.Dismissed -= OnOverlayDismissed;
    }
}
