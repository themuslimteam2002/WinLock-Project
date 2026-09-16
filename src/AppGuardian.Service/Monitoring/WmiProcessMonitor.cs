using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AppGuardian.Shared.Contracts;
using Microsoft.Extensions.Logging;

namespace AppGuardian.Service.Monitoring;

/// <summary>
/// Process start/stop notification over WMI, with a polling fallback. FR-401, FR-501.
/// </summary>
/// <remarks>
/// WMI's <c>Win32_ProcessStartTrace</c> is used rather than a poll loop because NFR-P4 allows one
/// second from launch to overlay and NFR-P2 caps idle CPU at 2%; a poll interval short enough for the
/// first would threaten the second. The trace class requires LocalSystem, which is why this lives in
/// the service and the agent learns about launches over IPC.
/// <para>
/// <c>Win32_ProcessStartTrace</c> is preferred over the more commonly cited
/// <c>__InstanceCreationEvent WITHIN 1</c> query: the latter is itself a polling query inside WMI, so
/// it costs the same CPU as polling plus the WMI overhead, and adds up to a second of latency.
/// </para>
/// <para>
/// The trace gives a PID and a process name but no executable path, so the path is resolved after the
/// fact. That resolution races the process: a program that exits within milliseconds may be gone
/// before the path can be read. That is accepted — an app that has already exited needs no lock — and
/// the failure is logged at debug rather than warning so it does not fill the log with noise.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WmiProcessMonitor : IProcessMonitor, IAsyncDisposable
{
    private readonly ILogger<WmiProcessMonitor> _log;
    private readonly ConcurrentDictionary<int, TrackedProcess> _known = new();

    private ManagementEventWatcher? _startWatcher;
    private ManagementEventWatcher? _stopWatcher;
    private Task? _pollLoop;
    private CancellationTokenSource? _pollCts;

    public WmiProcessMonitor(ILogger<WmiProcessMonitor> log) => _log = log;

    public event EventHandler<AppLaunchedEventArgs>? AppLaunched;

    public event EventHandler<AppExitedEventArgs>? AppExited;

    /// <summary>
    /// True when WMI notification failed and the slower polling path is in use.
    /// </summary>
    /// <remarks>
    /// Exposed rather than hidden so <c>system.getStatus</c> can report degraded monitoring. A user
    /// whose locks take four seconds to appear should be told why, instead of concluding the product
    /// is simply slow.
    /// </remarks>
    public bool IsDegraded { get; private set; }

    public string? DegradedReason { get; private set; }

    public Task StartAsync(CancellationToken ct)
    {
        // Seed the known set before subscribing. Without this, the first poll pass — or the first
        // exit event after a service restart — would treat every already-running process as new and
        // fire a launch event for the user's whole session.
        SeedKnownProcesses();

        try
        {
            StartWmiWatchers();

            _log.LogInformation("Process monitoring started over WMI process traces.");
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException)
        {
            IsDegraded = true;
            DegradedReason =
                "Windows process notifications are unavailable, so app launches are detected by " +
                "polling instead. Locks may take a few seconds to appear.";

            _log.LogError(ex, "WMI process trace subscription failed; falling back to polling.");

            StartPolling();
        }

        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        DisposeWatchers();

        _pollCts?.Cancel();

        return _pollLoop ?? Task.CompletedTask;
    }

    public IReadOnlyList<RunningApp> ListRunning()
    {
        var results = new List<RunningApp>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    // Skip the idle/system pseudo-processes: they cannot be locked, hidden, or
                    // throttled, and offering them in the picker would only invite a confusing failure.
                    if (process.Id <= 4)
                    {
                        continue;
                    }

                    results.Add(new RunningApp
                    {
                        ProcessId = process.Id,
                        ProcessName = process.ProcessName,
                        ExecutablePath = TryGetPath(process),
                        MainWindowTitle = SafeWindowTitle(process),
                        MainWindowHandle = process.MainWindowHandle.ToInt64(),

                        // A path we cannot read is the practical signal that the process is at a
                        // higher integrity level than we can touch, since LocalSystem can read the
                        // path of anything it can control.
                        IsElevated = TryGetPath(process) is null,
                        StartedUtc = SafeStartTime(process),
                    });
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    // The process exited between enumeration and inspection. Nothing to report.
                }
            }
        }

        return results;
    }

    private void StartWmiWatchers()
    {
        var scope = new ManagementScope(@"\\.\root\CIMV2");

        _startWatcher = new ManagementEventWatcher(
            scope, new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace"));

        _startWatcher.EventArrived += OnProcessStarted;
        _startWatcher.Start();

        _stopWatcher = new ManagementEventWatcher(
            scope, new WqlEventQuery("SELECT * FROM Win32_ProcessStopTrace"));

        _stopWatcher.EventArrived += OnProcessStopped;
        _stopWatcher.Start();
    }

    private void OnProcessStarted(object sender, EventArrivedEventArgs e)
    {
        try
        {
            var pid = Convert.ToInt32(e.NewEvent.Properties["ProcessID"].Value);
            var name = e.NewEvent.Properties["ProcessName"].Value as string ?? string.Empty;

            RaiseLaunched(pid, name);
        }
        catch (Exception ex)
        {
            // A throw out of a WMI callback tears down the watcher, so nothing may escape here.
            // Losing one launch notification is far cheaper than losing all future ones.
            _log.LogWarning(ex, "A process start event could not be processed.");
        }
    }

    private void OnProcessStopped(object sender, EventArrivedEventArgs e)
    {
        try
        {
            var pid = Convert.ToInt32(e.NewEvent.Properties["ProcessID"].Value);

            RaiseExited(pid);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "A process stop event could not be processed.");
        }
    }

    private void RaiseLaunched(int pid, string processName)
    {
        var path = ResolvePath(pid);

        var tracked = new TrackedProcess(pid, processName, path);

        // TryAdd, not indexer assignment: the polling fallback and the WMI watcher can both be live
        // during a transition, and a duplicate launch event would show the overlay twice.
        if (!_known.TryAdd(pid, tracked))
        {
            return;
        }

        AppLaunched?.Invoke(this, new AppLaunchedEventArgs
        {
            ProcessId = pid,
            ExecutablePath = path ?? string.Empty,
            PackageFamilyName = null,
        });
    }

    private void RaiseExited(int pid)
    {
        if (!_known.TryRemove(pid, out _))
        {
            return;
        }

        AppExited?.Invoke(this, new AppExitedEventArgs { ProcessId = pid });
    }

    private void SeedKnownProcesses()
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id <= 4)
                {
                    continue;
                }

                try
                {
                    _known.TryAdd(
                        process.Id,
                        new TrackedProcess(process.Id, process.ProcessName, TryGetPath(process)));
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    // Exited mid-enumeration.
                }
            }
        }

        _log.LogDebug("Seeded {Count} already-running processes.", _known.Count);
    }

    private void StartPolling()
    {
        _pollCts = new CancellationTokenSource();

        _pollLoop = Task.Run(() => PollLoopAsync(_pollCts.Token));
    }

    /// <summary>
    /// Fallback detection by difference. Deliberately a two-second interval.
    /// </summary>
    /// <remarks>
    /// Two seconds breaks NFR-P4's one-second target, which is why this path reports degraded status
    /// rather than silently substituting itself. A shorter interval would enumerate every process on
    /// the machine several times a second and put NFR-P2's 2% idle ceiling at risk — trading a
    /// visible latency problem for an invisible battery one.
    /// </remarks>
    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, ct).ConfigureAwait(false);

                var live = new HashSet<int>();

                foreach (var process in Process.GetProcesses())
                {
                    using (process)
                    {
                        if (process.Id <= 4)
                        {
                            continue;
                        }

                        live.Add(process.Id);

                        if (!_known.ContainsKey(process.Id))
                        {
                            RaiseLaunched(process.Id, SafeName(process));
                        }
                    }
                }

                foreach (var pid in _known.Keys.ToArray())
                {
                    if (!live.Contains(pid))
                    {
                        RaiseExited(pid);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // The loop must outlive any single failed pass; stopping would leave the service with
                // no process detection at all and no indication of it.
                _log.LogWarning(ex, "A process polling pass failed.");
            }
        }
    }

    private string? ResolvePath(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return TryGetPath(process);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            _log.LogDebug("Pid {Pid} exited before its path could be resolved.", pid);
            return null;
        }
    }

    private static string? TryGetPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception)
        {
            // Access denied on a protected process, or a 32/64-bit mismatch. Either way the path is
            // simply unknown; callers treat null as "cannot be identified by path".
            return null;
        }
    }

    private static string SafeName(Process process)
    {
        try
        {
            return process.ProcessName;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private static string? SafeWindowTitle(Process process)
    {
        try
        {
            var title = process.MainWindowTitle;
            return string.IsNullOrWhiteSpace(title) ? null : title;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static DateTimeOffset SafeStartTime(Process process)
    {
        try
        {
            return process.StartTime;
        }
        catch (Exception)
        {
            return DateTimeOffset.MinValue;
        }
    }

    private void DisposeWatchers()
    {
        foreach (var watcher in new[] { _startWatcher, _stopWatcher })
        {
            if (watcher is null)
            {
                continue;
            }

            try
            {
                watcher.Stop();
                watcher.Dispose();
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "A WMI watcher did not stop cleanly.");
            }
        }

        _startWatcher = null;
        _stopWatcher = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);

        _pollCts?.Dispose();
    }

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private sealed record TrackedProcess(int ProcessId, string ProcessName, string? ExecutablePath);
}
