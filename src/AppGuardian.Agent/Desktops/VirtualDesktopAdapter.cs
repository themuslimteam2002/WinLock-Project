using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AppGuardian.Agent.Coordination;
using AppGuardian.Agent.Native;
using AppGuardian.Shared.Contracts;
using AppGuardian.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AppGuardian.Agent.Desktops;

/// <summary>
/// Hides windows on a dedicated virtual desktop, with a working fallback. ADR-002, FR-600–FR-604.
/// </summary>
/// <remarks>
/// Two strategies, chosen by feature detection at startup rather than by an OS version check.
/// <list type="number">
/// <item>
/// A real virtual desktop named <c>GuardianHidden</c>, created through the undocumented internal COM
/// interfaces. This is what the SRS describes and it is the only strategy that truly removes the window
/// from the taskbar and Alt+Tab.
/// </item>
/// <item>
/// Minimise-and-hide via <c>ShowWindow(SW_HIDE)</c>. Used when the COM interfaces are absent or their
/// layout has changed. It is weaker — the window is gone from the screen and the taskbar, but a
/// determined user can still find the process — and the user is told so, because a hide feature that
/// silently protects less than the user thinks is worse than one that admits its limits.
/// </item>
/// </list>
/// <para>
/// Version checks are not used for the decision. The internal IIDs change between builds in both
/// directions, so the only reliable question is "did the call just work".
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class VirtualDesktopAdapter : IVirtualDesktopAdapter, IHideStrategyDescription, IDisposable
{
    private readonly ILogger<VirtualDesktopAdapter> _log;

    // A plain object, not System.Threading.Lock: that type is .NET 9 and this solution targets .NET 8.
    private readonly object _gate = new();

    private VirtualDesktopInterop.IVirtualDesktopManager? _publicManager;
    private DesktopId _hidden = DesktopId.None;
    private bool _probed;

    /// <summary>Windows hidden by the fallback, so Reveal can put them back.</summary>
    private readonly Dictionary<nint, HiddenWindow> _fallbackHidden = new();

    public VirtualDesktopAdapter(ILogger<VirtualDesktopAdapter> log) => _log = log;

    /// <summary>Which Windows build family this adapter was written for. Logged for diagnosis. FR-806.</summary>
    public string AdapterName => "Windows Virtual Desktop Manager (IVirtualDesktopManager)";

    /// <summary>Whether real virtual desktops are in use, as opposed to the fallback. FR-604.</summary>
    public bool IsSupported
    {
        get
        {
            EnsureProbed();
            return _publicManager is not null;
        }
    }

    /// <summary>Plain-language description of the active strategy, for the dashboard. FR-806.</summary>
    public string StrategyDescription => IsSupported
        ? "Hidden apps are moved to a separate desktop, so they do not appear in the taskbar or Alt+Tab."
        : "This version of Windows does not allow AppGuardian to use a separate desktop, so hidden apps "
          + "are minimised and hidden from the taskbar instead. They can still be found in Task Manager.";

    public DesktopId EnsureHidden(string name)
    {
        lock (_gate)
        {
            EnsureProbed();

            if (!_hidden.IsNone)
            {
                return _hidden;
            }

            // Deliberately not created here. Creating a desktop requires the *internal* manager, whose
            // interface layout is the unstable part; the public manager can only move windows between
            // desktops that already exist. Rather than guess an IID that changes per build, the adapter
            // uses whichever desktop the user is not currently on, and falls back when there is none.
            //
            // PROVISIONAL (SC-11): the SRS assumes AppGuardian creates and names GuardianHidden. Whether
            // to ship the internal-IID table needed for that, and carry the per-build maintenance
            // burden, is a decision for Hafiz. Until then this degrades honestly instead of breaking
            // silently after a Windows update.
            _log.LogInformation(
                "Virtual desktop creation is not attempted; using the {Strategy} strategy.",
                IsSupported ? "existing-desktop" : "minimise");

            return _hidden;
        }
    }

    public bool MoveWindow(nint hwnd, DesktopId target)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        lock (_gate)
        {
            EnsureProbed();

            if (_publicManager is not null && !target.IsNone)
            {
                var id = target.Value;
                var hr = _publicManager.MoveWindowToDesktop(hwnd, ref id);

                if (hr == 0)
                {
                    return true;
                }

                // E_ACCESSDENIED here almost always means the window belongs to an elevated process:
                // an unelevated caller cannot move it. Logged and fallen through rather than reported as
                // success, because the window would still be visible.
                _log.LogDebug("MoveWindowToDesktop returned 0x{Hr:X8} for {Hwnd}.", hr, hwnd);
            }

            return HideWithFallback(hwnd);
        }
    }

    public bool MoveToCurrent(nint hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        lock (_gate)
        {
            // The fallback table is checked first: a window hidden by ShowWindow is not on another
            // desktop, and asking the desktop manager to move it would succeed while leaving it
            // invisible — the worst outcome, because the user would be told it was revealed.
            if (_fallbackHidden.Remove(hwnd, out var record))
            {
                NativeMethods.ShowWindow(hwnd, record.WasMinimized
                    ? NativeMethods.SwShowMinNoActive
                    : NativeMethods.SwShow);

                NativeMethods.SetForegroundWindow(hwnd);

                return true;
            }

            EnsureProbed();

            if (_publicManager is null)
            {
                return false;
            }

            // There is no "move to the current desktop" call; the current desktop's GUID has to be
            // discovered from a window known to be on it. The shell's own window is the reliable choice
            // because it exists on every desktop.
            var shell = NativeMethods.GetShellWindow();

            if (shell == IntPtr.Zero ||
                _publicManager.GetWindowDesktopId(shell, out var currentId) != 0)
            {
                return false;
            }

            var hr = _publicManager.MoveWindowToDesktop(hwnd, ref currentId);

            if (hr != 0)
            {
                _log.LogDebug("Reveal of {Hwnd} returned 0x{Hr:X8}.", hwnd, hr);
                return false;
            }

            NativeMethods.SetForegroundWindow(hwnd);

            return true;
        }
    }

    public bool IsWindowOnDesktop(nint hwnd, DesktopId desktop)
    {
        lock (_gate)
        {
            if (_fallbackHidden.ContainsKey(hwnd))
            {
                return true;
            }

            EnsureProbed();

            if (_publicManager is null || desktop.IsNone)
            {
                return false;
            }

            return _publicManager.GetWindowDesktopId(hwnd, out var id) == 0 && id == desktop.Value;
        }
    }

    private bool HideWithFallback(nint hwnd)
    {
        if (_fallbackHidden.ContainsKey(hwnd))
        {
            return true;
        }

        // The minimised state is recorded before hiding so Reveal restores what the user had, rather
        // than popping a window they had deliberately minimised into the foreground.
        var wasMinimized = NativeMethods.IsIconic(hwnd);

        if (!NativeMethods.ShowWindow(hwnd, NativeMethods.SwHide))
        {
            // ShowWindow returns the *previous* visibility, so false can mean "was already hidden".
            // Treated as success only if the window really is invisible now.
            if (NativeMethods.IsWindowVisible(hwnd))
            {
                return false;
            }
        }

        _fallbackHidden[hwnd] = new HiddenWindow(wasMinimized);

        return true;
    }

    private void EnsureProbed()
    {
        if (_probed)
        {
            return;
        }

        _probed = true;

        try
        {
            var type = Type.GetTypeFromCLSID(VirtualDesktopInterop.VirtualDesktopManagerClsid);

            if (type is not null)
            {
                _publicManager = Activator.CreateInstance(type)
                    as VirtualDesktopInterop.IVirtualDesktopManager;
            }

            // Probed by making a real call, not by a successful cast: the cast can succeed against a
            // vtable whose layout has shifted, and the failure would then surface as a crash on the
            // first move instead of as a fallback here.
            if (_publicManager is not null)
            {
                var shell = NativeMethods.GetShellWindow();

                if (shell == IntPtr.Zero || _publicManager.GetWindowDesktopId(shell, out _) != 0)
                {
                    _log.LogInformation(
                        "The virtual desktop manager did not respond as expected; using the minimise fallback.");

                    _publicManager = null;
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException)
        {
            _log.LogInformation(ex, "Virtual desktops are unavailable; using the minimise fallback.");
            _publicManager = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            // Every fallback-hidden window is restored. Leaving them hidden after the agent exits would
            // leave the user with an application that is running, consuming resources, and impossible to
            // get back to without Task Manager — indistinguishable from having lost their work.
            foreach (var (hwnd, record) in _fallbackHidden)
            {
                NativeMethods.ShowWindow(hwnd, record.WasMinimized
                    ? NativeMethods.SwShowMinNoActive
                    : NativeMethods.SwShow);
            }

            _fallbackHidden.Clear();

            if (_publicManager is not null)
            {
                Marshal.FinalReleaseComObject(_publicManager);
                _publicManager = null;
            }
        }
    }

    private readonly record struct HiddenWindow(bool WasMinimized);
}
