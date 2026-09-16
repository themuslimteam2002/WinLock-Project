using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AppGuardian.Service.Native;
using AppGuardian.Shared.Contracts;
using AppGuardian.Shared.Models;
using AppGuardian.Shared.Security;
using Microsoft.Extensions.Logging;

namespace AppGuardian.Service.Power;

/// <summary>
/// CPU and power restriction via Job Objects, priority class, and EcoQoS. FR-700..708, ADR-008.
/// </summary>
/// <remarks>
/// One job object per restricted process, named per PID. A shared job would be simpler but wrong: the
/// CPU cap on a job applies to the job as a whole, so two apps in one job would share a single 25%
/// budget rather than getting 25% each.
/// <para>
/// The cap lives on the kernel object, which is why it survives the service dying — the point SC-08
/// makes about NFR-S4 being partly achievable. It is also why the job handle must be held open: the
/// job is destroyed when its last handle closes and no processes remain, so releasing the handle
/// early would silently drop the limit.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class JobObjectPowerController : IPowerController, IDisposable
{
    private readonly ConcurrentDictionary<int, Throttle> _throttles = new();
    private readonly ILogger<JobObjectPowerController> _log;
    private readonly Lazy<bool> _ecoQosSupported;

    public JobObjectPowerController(ILogger<JobObjectPowerController> log)
    {
        _log = log;

        // Probed once, by attempting the call on our own process rather than by checking the OS
        // build number. Version checks lie: EcoQoS can be absent on a build that nominally has it,
        // and the call is cheap and side-effect-free on ourselves.
        _ecoQosSupported = new Lazy<bool>(ProbeEcoQos);
    }

    public bool SupportsEcoQos => _ecoQosSupported.Value;

    public IReadOnlyDictionary<int, PowerProfile> ActiveThrottles =>
        _throttles.ToDictionary(kv => kv.Key, kv => kv.Value.Profile);

    public PowerApplyResult ApplyThrottle(int pid, PowerProfile profile, bool reducePriority, bool ecoQos)
    {
        var spec = PowerProfileMap.Resolve(profile);

        if (_throttles.TryGetValue(pid, out var existing))
        {
            // Re-applying the same profile is a no-op rather than a rebuild, so the periodic
            // reconciliation loop does not churn kernel objects on every pass.
            if (existing.Profile == profile)
            {
                return new PowerApplyResult
                {
                    Succeeded = true,
                    CpuRateCapPercent = spec.CpuRateCapPercent,
                    PriorityReduced = existing.PriorityReduced,
                    EcoQosApplied = existing.EcoQosApplied,
                };
            }

            // A different profile: tear down first. A process can only be in one job (nesting aside),
            // so the old job has to go before a new one can take it.
            RemoveThrottle(pid);
        }

        var process = NativeMethods.OpenProcess(ProcessAccess.ThrottleRights, false, (uint)pid);

        if (process == NativeMethods.InvalidHandle)
        {
            var reason = DescribeLastError(
                "This app cannot be restricted because AppGuardian could not open it.");

            _log.LogWarning("OpenProcess failed for pid {Pid}: {Reason}", pid, reason);

            return PowerApplyResult.Unsupported(reason);
        }

        nint job = NativeMethods.InvalidHandle;

        try
        {
            // Already jobbed by something else — common with browsers, game launchers, and anything
            // started by a sandbox. FR-509 wants this surfaced, not silently ignored.
            if (NativeMethods.IsProcessInJob(process, NativeMethods.InvalidHandle, out var inJob) && inJob)
            {
                _log.LogInformation(
                    "Pid {Pid} already belongs to another job object; CPU capping is unavailable for it.",
                    pid);

                // Priority and EcoQoS still work, so a partial restriction is applied rather than
                // refusing outright — a below-normal priority is a real, if weaker, restriction.
                var partial = ApplyPriorityAndQos(process, spec, reducePriority, ecoQos);

                if (!partial.Any)
                {
                    return PowerApplyResult.Unsupported(
                        "This app is already managed by another program and cannot be restricted.");
                }

                _throttles[pid] = new Throttle
                {
                    Pid = pid,
                    Profile = profile,
                    JobHandle = NativeMethods.InvalidHandle,
                    ProcessHandle = process,
                    PriorityReduced = partial.PriorityReduced,
                    EcoQosApplied = partial.EcoQosApplied,
                };

                return new PowerApplyResult
                {
                    Succeeded = true,
                    CpuRateCapPercent = null,
                    PriorityReduced = partial.PriorityReduced,
                    EcoQosApplied = partial.EcoQosApplied,
                    UnsupportedReason =
                        "CPU capping is unavailable for this app because another program already " +
                        "manages it. Priority and efficiency settings were still applied.",
                };
            }

            // Unnamed: a named job would be reusable across restarts but also squattable by any
            // process that can guess the name, and there is nothing to gain from persistence here.
            job = NativeMethods.CreateJobObject(NativeMethods.InvalidHandle, null);

            if (job == NativeMethods.InvalidHandle)
            {
                var reason = DescribeLastError("A CPU limit could not be created for this app.");
                return PowerApplyResult.Unsupported(reason);
            }

            if (spec.CpuRateCapPercent is { } percent && !SetCpuRate(job, percent))
            {
                var reason = DescribeLastError("The CPU limit could not be applied to this app.");
                return PowerApplyResult.Unsupported(reason);
            }

            if (!NativeMethods.AssignProcessToJobObject(job, process))
            {
                var reason = DescribeLastError("This app could not be assigned a CPU limit.");

                _log.LogWarning("AssignProcessToJobObject failed for pid {Pid}: {Reason}", pid, reason);

                return PowerApplyResult.Unsupported(reason);
            }

            var applied = ApplyPriorityAndQos(process, spec, reducePriority, ecoQos);

            _throttles[pid] = new Throttle
            {
                Pid = pid,
                Profile = profile,
                JobHandle = job,
                ProcessHandle = process,
                PriorityReduced = applied.PriorityReduced,
                EcoQosApplied = applied.EcoQosApplied,
            };

            _log.LogInformation(
                "Applied {Profile} to pid {Pid}: cap {Cap}, priority {Priority}, EcoQoS {Eco}.",
                profile,
                pid,
                spec.CpuRateCapPercent is null ? "none" : $"{spec.CpuRateCapPercent}%",
                applied.PriorityReduced ? spec.Priority.ToString() : "unchanged",
                applied.EcoQosApplied);

            // Handles are now owned by the Throttle record and closed in RemoveThrottle.
            job = NativeMethods.InvalidHandle;
            process = NativeMethods.InvalidHandle;

            return new PowerApplyResult
            {
                Succeeded = true,
                CpuRateCapPercent = spec.CpuRateCapPercent,
                PriorityReduced = applied.PriorityReduced,
                EcoQosApplied = applied.EcoQosApplied,
            };
        }
        finally
        {
            // Anything still owned locally at this point belongs to a failed path.
            if (job != NativeMethods.InvalidHandle)
            {
                NativeMethods.CloseHandle(job);
            }

            if (process != NativeMethods.InvalidHandle)
            {
                NativeMethods.CloseHandle(process);
            }
        }
    }

    public void RemoveThrottle(int pid)
    {
        if (!_throttles.TryRemove(pid, out var throttle))
        {
            return;
        }

        // Restoring priority before closing the job: the priority class was set on the process, not
        // the job, so closing the job alone would leave the app at idle priority forever.
        if (throttle.PriorityReduced && throttle.ProcessHandle != NativeMethods.InvalidHandle)
        {
            NativeMethods.SetPriorityClass(throttle.ProcessHandle, PriorityClass.Normal);
        }

        if (throttle.EcoQosApplied && throttle.ProcessHandle != NativeMethods.InvalidHandle)
        {
            ClearEcoQos(throttle.ProcessHandle);
        }

        if (throttle.JobHandle != NativeMethods.InvalidHandle)
        {
            // Closing the last handle destroys the job and with it the CPU cap. The process is not
            // affected, because KILL_ON_JOB_CLOSE was never set.
            NativeMethods.CloseHandle(throttle.JobHandle);
        }

        if (throttle.ProcessHandle != NativeMethods.InvalidHandle)
        {
            NativeMethods.CloseHandle(throttle.ProcessHandle);
        }

        _log.LogInformation("Removed restriction from pid {Pid}.", pid);
    }

    public bool IsOnBattery()
    {
        if (NativeMethods.GetSystemPowerStatus(out var status))
        {
            return status.IsOnBattery;
        }

        // Unknown is reported as "not on battery": FR-708 uses this to decide whether to apply a
        // battery-only restriction, and guessing "on battery" would throttle a desktop machine.
        return false;
    }

    /// <summary>
    /// Drops tracking for processes that have exited, closing their handles.
    /// </summary>
    /// <remarks>
    /// Necessary because a held process handle keeps the PID's kernel object alive as a zombie. Without
    /// this sweep the service would accumulate one handle per app launch for its whole lifetime, which
    /// is exactly the sort of slow leak NFR-P3's memory ceiling would eventually catch.
    /// </remarks>
    public int SweepExited()
    {
        var removed = 0;

        foreach (var pid in _throttles.Keys.ToArray())
        {
            if (!IsAlive(pid))
            {
                RemoveThrottle(pid);
                removed++;
            }
        }

        return removed;
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

    private bool SetCpuRate(nint job, int percent)
    {
        var info = new JobObjectCpuRateControlInformation
        {
            // HardCap, not WeightBased: FR-701 promises a ceiling, and a weight only expresses a
            // relative share that rises to 100% whenever the machine is otherwise idle.
            ControlFlags = JobCpuRateControlFlags.Enable | JobCpuRateControlFlags.HardCap,
            CpuRate = PowerProfileMap.ToCpuRateUnits(percent),
        };

        var size = (uint)Marshal.SizeOf<JobObjectCpuRateControlInformation>();
        var buffer = Marshal.AllocHGlobal((int)size);

        try
        {
            Marshal.StructureToPtr(info, buffer, fDeleteOld: false);

            return NativeMethods.SetInformationJobObject(
                job,
                JobObjectInfoClass.CpuRateControlInformation,
                buffer,
                size);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private AppliedExtras ApplyPriorityAndQos(
        nint process,
        PowerProfileSpec spec,
        bool reducePriority,
        bool ecoQos)
    {
        var priorityReduced = false;
        var ecoApplied = false;

        if (reducePriority && spec.Priority != ProcessPriorityHint.Normal)
        {
            priorityReduced = NativeMethods.SetPriorityClass(process, Map(spec.Priority));

            if (!priorityReduced)
            {
                _log.LogDebug("SetPriorityClass failed: {Error}", Marshal.GetLastWin32Error());
            }
        }

        if (ecoQos && spec.EcoQos && SupportsEcoQos)
        {
            ecoApplied = SetEcoQos(process, enable: true);
        }

        return new AppliedExtras(priorityReduced, ecoApplied);
    }

    private static PriorityClass Map(ProcessPriorityHint hint) => hint switch
    {
        ProcessPriorityHint.Idle => PriorityClass.Idle,
        ProcessPriorityHint.BelowNormal => PriorityClass.BelowNormal,
        _ => PriorityClass.Normal,
    };

    private static bool SetEcoQos(nint process, bool enable)
    {
        var state = new ProcessPowerThrottlingState
        {
            Version = ProcessPowerThrottlingState.CurrentVersion,

            // ControlMask names the bit we are taking control of; StateMask says what to set it to.
            // Both must name EXECUTION_SPEED or the call succeeds and changes nothing.
            ControlMask = ProcessPowerThrottlingState.ExecutionSpeed,
            StateMask = enable ? ProcessPowerThrottlingState.ExecutionSpeed : 0,
        };

        return WriteProcessPowerThrottling(process, state);
    }

    private static void ClearEcoQos(nint process)
    {
        // ControlMask 0 returns the process to system-managed throttling, rather than pinning it to
        // "never throttle" — which is what StateMask 0 with ControlMask set would do.
        var state = new ProcessPowerThrottlingState
        {
            Version = ProcessPowerThrottlingState.CurrentVersion,
            ControlMask = 0,
            StateMask = 0,
        };

        WriteProcessPowerThrottling(process, state);
    }

    private static bool WriteProcessPowerThrottling(nint process, ProcessPowerThrottlingState state)
    {
        var size = (uint)Marshal.SizeOf<ProcessPowerThrottlingState>();
        var buffer = Marshal.AllocHGlobal((int)size);

        try
        {
            Marshal.StructureToPtr(state, buffer, fDeleteOld: false);

            return NativeMethods.SetProcessInformation(
                process,
                ProcessInformationClass.PowerThrottling,
                buffer,
                size);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private bool ProbeEcoQos()
    {
        var self = Process.GetCurrentProcess().Handle;

        // Enable then immediately clear on our own process. A service that spends its life waiting on
        // a pipe is unaffected either way, and this is the only way to know the call actually works
        // on this build.
        var supported = SetEcoQos(self, enable: true);

        ClearEcoQos(self);

        _log.LogInformation("EcoQoS is {State} on this system.", supported ? "available" : "unavailable");

        return supported;
    }

    private static string DescribeLastError(string prefix)
    {
        var code = Marshal.GetLastWin32Error();

        return code switch
        {
            5 => $"{prefix} Access was denied — it may be running with higher privileges than AppGuardian.",
            87 => $"{prefix} The app is no longer running.",
            _ => $"{prefix} (Windows error {code}.)",
        };
    }

    public void Dispose()
    {
        // Removing rather than just closing handles, so a clean shutdown restores priority and QoS.
        // The CPU cap would survive handle closure, and leaving an app permanently capped after
        // AppGuardian is uninstalled would be indistinguishable from a broken app.
        foreach (var pid in _throttles.Keys.ToArray())
        {
            RemoveThrottle(pid);
        }
    }

    private readonly record struct AppliedExtras(bool PriorityReduced, bool EcoQosApplied)
    {
        public bool Any => PriorityReduced || EcoQosApplied;
    }

    private sealed class Throttle
    {
        public required int Pid { get; init; }

        public required PowerProfile Profile { get; init; }

        /// <summary>Held open for the life of the restriction; closing it destroys the CPU cap.</summary>
        public required nint JobHandle { get; init; }

        public required nint ProcessHandle { get; init; }

        public bool PriorityReduced { get; init; }

        public bool EcoQosApplied { get; init; }
    }
}
