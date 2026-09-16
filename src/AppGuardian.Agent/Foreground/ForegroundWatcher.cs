using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows;
using AppGuardian.Agent.Native;
using AppGuardian.Shared.Contracts;
using Microsoft.Extensions.Logging;

namespace AppGuardian.Agent.Foreground;

/// <summary>
/// Tracks foreground window changes with a WinEvent hook. FR-501.
/// </summary>
/// <remarks>
/// The hook has to be installed on a thread that pumps messages, and the callback is delivered on that
/// same thread. This class owns a dedicated STA thread with its own message loop rather than borrowing
/// the WPF dispatcher: overlay creation and layout run on the dispatcher, and a foreground event that
/// arrived while the dispatcher was mid-layout would be delayed by exactly the interval NFR-P4's
/// one-second budget cannot afford.
/// <para>
/// The delegate is held in a field. A collected delegate leaves the OS calling into freed memory, and
/// the resulting crash appears at a random later moment with no connection to this file — the single
/// most common defect in WinEvent code.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ForegroundWatcher : IForegroundWatcher, IDisposable
{
    private readonly ILogger<ForegroundWatcher> _log;
    private readonly object _gate = new();

    private Thread? _pumpThread;
    private NativeMethods.WinEventProc? _callback;
    private nint _hook;
    private uint _pumpThreadId;
    private volatile bool _running;

    /// <summary>Own PID, so the overlay's own windows never trigger a foreground event.</summary>
    private readonly int _selfPid = Environment.ProcessId;

    public event EventHandler<ForegroundChangedEventArgs>? ForegroundChanged;

    public ForegroundWatcher(ILogger<ForegroundWatcher> log) => _log = log;

    public Task StartAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_running)
            {
                return Task.CompletedTask;
            }

            _running = true;

            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            _pumpThread = new Thread(() => Pump(ready))
            {
                IsBackground = true,
                Name = "AppGuardian.ForegroundWatcher",
            };

            // STA because the hook thread also ends up in COM calls (the desktop adapter is invoked from
            // handlers downstream) and MTA would marshal every one of them across an extra apartment.
            _pumpThread.SetApartmentState(ApartmentState.STA);
            _pumpThread.Start();

            return ready.Task.WaitAsync(ct);
        }
    }

    private void Pump(TaskCompletionSource ready)
    {
        try
        {
            _pumpThreadId = NativeMethods.GetCurrentThreadId();
            _callback = OnWinEvent;

            _hook = NativeMethods.SetWinEventHook(
                NativeMethods.EventSystemForeground,
                NativeMethods.EventSystemForeground,
                IntPtr.Zero,
                _callback,
                idProcess: 0,
                idThread: 0,
                NativeMethods.WineventOutOfContext | NativeMethods.WineventSkipOwnProcess);

            if (_hook == IntPtr.Zero)
            {
                // Not fatal to the agent. Locking then depends on the service's process-launch events
                // alone, which still covers "app starts" but not "app is switched back to" — worth
                // saying out loud rather than degrading silently.
                _log.LogError(
                    "The foreground hook could not be installed (error {Error}). Locking will only trigger "
                    + "on app launch, not on switching to an already-running app.",
                    Marshal.GetLastWin32Error());
            }

            ready.TrySetResult();

            // A plain GetMessage loop. There is nothing else on this thread; the only messages that
            // arrive are the WM_QUIT that StopAsync posts.
            while (_running && NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessage(ref msg);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "The foreground watcher thread stopped unexpectedly.");
            ready.TrySetResult();
        }
        finally
        {
            if (_hook != IntPtr.Zero)
            {
                NativeMethods.UnhookWinEvent(_hook);
                _hook = IntPtr.Zero;
            }

            // Cleared only after the hook is gone: releasing it earlier would leave a window in which
            // the OS could still invoke a collected delegate.
            _callback = null;
        }
    }

    private void OnWinEvent(
        nint hook,
        uint eventType,
        nint hwnd,
        int idObject,
        int idChild,
        uint thread,
        uint time)
    {
        // idObject OBJID_WINDOW (0) only. The foreground event also fires for child objects such as a
        // focused control, and treating those as window changes would raise several events per switch.
        if (hwnd == IntPtr.Zero || idObject != 0)
        {
            return;
        }

        try
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);

            if (pid == 0 || (int)pid == _selfPid)
            {
                return;
            }

            var args = new ForegroundChangedEventArgs
            {
                WindowHandle = hwnd,
                ProcessId = (int)pid,
                ExecutablePath = NativeMethods.TryGetProcessPath((int)pid),
                WindowTitle = NativeMethods.GetWindowTitle(hwnd),
            };

            // Raised synchronously on the hook thread. Subscribers must not block: this is documented on
            // the interface, and the coordinator that consumes it hands off to the dispatcher immediately.
            ForegroundChanged?.Invoke(this, args);
        }
        catch (Exception ex)
        {
            // An exception escaping a native callback is undefined behaviour in the general case and
            // terminates the process in practice. Nothing gets out of here.
            _log.LogWarning(ex, "A foreground change could not be processed.");
        }
    }

    public Task StopAsync()
    {
        Thread? thread;

        lock (_gate)
        {
            if (!_running)
            {
                return Task.CompletedTask;
            }

            _running = false;
            thread = _pumpThread;
            _pumpThread = null;

            if (_pumpThreadId != 0)
            {
                NativeMethods.PostThreadMessage(_pumpThreadId, NativeMethods.WmQuit, IntPtr.Zero, IntPtr.Zero);
                _pumpThreadId = 0;
            }
        }

        // Bounded wait. If the pump is wedged in a callback the agent still needs to shut down, and a
        // background thread will not hold the process open.
        thread?.Join(TimeSpan.FromSeconds(2));

        return Task.CompletedTask;
    }

    /// <summary>Enumerates monitors for overlay placement. FR-507.</summary>
    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();

        try
        {
            // Enumerated fresh on every call rather than cached: monitors are hot-plugged, laptops are
            // docked, and an overlay placed on a monitor that is no longer there leaves the real window
            // uncovered — a lock that does not lock.
            NativeMethods.EnumDisplayMonitors(
                IntPtr.Zero,
                IntPtr.Zero,
                (nint handle, nint _, ref NativeMethods.Rect _, nint _) =>
                {
                    var info = new NativeMethods.MonitorInfoEx
                    {
                        Size = Marshal.SizeOf<NativeMethods.MonitorInfoEx>(),
                        DeviceName = string.Empty,
                    };

                    if (!NativeMethods.GetMonitorInfo(handle, ref info))
                    {
                        return true;
                    }

                    monitors.Add(new MonitorInfo
                    {
                        DeviceName = info.DeviceName,
                        X = info.Monitor.Left,
                        Y = info.Monitor.Top,
                        Width = info.Monitor.Width,
                        Height = info.Monitor.Height,
                        IsPrimary = (info.Flags & NativeMethods.MonitorinfofPrimary) != 0,
                        DpiScale = DpiScaleFor(handle),
                    });

                    return true;
                },
                IntPtr.Zero);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Monitors could not be enumerated.");
        }

        if (monitors.Count == 0)
        {
            // A last resort so the overlay is never skipped entirely. The primary screen's bounds from
            // WPF are less precise across mixed-DPI setups but cover the common single-monitor case.
            monitors.Add(new MonitorInfo
            {
                DeviceName = @"\\.\DISPLAY1",
                X = 0,
                Y = 0,
                Width = (int)SystemParameters.PrimaryScreenWidth,
                Height = (int)SystemParameters.PrimaryScreenHeight,
                IsPrimary = true,
            });
        }

        return monitors;
    }

    private static double DpiScaleFor(nint monitor)
    {
        // shcore's GetDpiForMonitor is Windows 8.1+. A failure here is not worth reporting: 1.0 is the
        // correct answer on an unscaled display and the only cost of getting it wrong is overlay
        // geometry, which the WPF layer corrects against its own visual transform anyway.
        if (NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MdtEffectiveDpi, out var dpiX, out _) == 0
            && dpiX > 0)
        {
            return dpiX / 96.0;
        }

        return 1.0;
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }
}
