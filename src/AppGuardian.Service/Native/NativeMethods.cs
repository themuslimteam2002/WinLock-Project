using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AppGuardian.Service.Native;

/// <summary>
/// P/Invoke declarations for Job Objects, process QoS, and power state. API Design §6.3.
/// </summary>
/// <remarks>
/// Kept in one place so the marshalling is reviewable as a set. Every struct here is layout-sensitive:
/// a wrong field order or a missing pack directive produces a call that succeeds and does nothing,
/// which is far worse than one that fails.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class NativeMethods
{
    private const string Kernel32 = "kernel32.dll";

    // ---- Job Objects ----

    [LibraryImport(Kernel32, EntryPoint = "CreateJobObjectW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint CreateJobObject(nint securityAttributes, string? name);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetInformationJobObject(
        nint job,
        JobObjectInfoClass infoClass,
        nint info,
        uint infoLength);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsProcessInJob(nint process, nint job, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);

    // ---- Process access ----

    [LibraryImport(Kernel32, SetLastError = true)]
    internal static partial nint OpenProcess(ProcessAccess access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetPriorityClass(nint process, PriorityClass priorityClass);

    // ---- EcoQoS (FR-703) ----

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetProcessInformation(
        nint process,
        ProcessInformationClass infoClass,
        nint info,
        uint infoLength);

    // ---- Power state (FR-708) ----

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetSystemPowerStatus(out SystemPowerStatus status);

    internal const nint InvalidHandle = 0;
}

internal enum JobObjectInfoClass : uint
{
    /// <summary>JobObjectBasicLimitInformation.</summary>
    BasicLimitInformation = 2,

    /// <summary>JobObjectExtendedLimitInformation.</summary>
    ExtendedLimitInformation = 9,

    /// <summary>JobObjectCpuRateControlInformation. The FR-701 CPU cap.</summary>
    CpuRateControlInformation = 15,
}

[Flags]
internal enum ProcessAccess : uint
{
    Terminate = 0x0001,
    SetQuota = 0x0100,
    SetInformation = 0x0200,
    QueryInformation = 0x0400,
    QueryLimitedInformation = 0x1000,

    /// <summary>
    /// The minimum needed to assign a process to a job and change its priority and QoS.
    /// Deliberately not PROCESS_ALL_ACCESS — a service running as LocalSystem should ask for the
    /// least it needs, so a bug here cannot terminate or write to an arbitrary process.
    /// </summary>
    ThrottleRights = SetQuota | SetInformation | QueryInformation | Terminate,
}

internal enum PriorityClass : uint
{
    Normal = 0x0020,
    Idle = 0x0040,
    BelowNormal = 0x4000,
}

internal enum ProcessInformationClass
{
    /// <summary>ProcessPowerThrottling. Requires Windows 10 2004 or later.</summary>
    PowerThrottling = 4,
}

[Flags]
internal enum JobCpuRateControlFlags : uint
{
    Enable = 0x1,
    WeightBased = 0x2,
    HardCap = 0x4,
    NotifyOnly = 0x8,
    MinMaxRate = 0x10,
}

[Flags]
internal enum JobLimitFlags : uint
{
    /// <summary>JOB_OBJECT_LIMIT_PRIORITY_CLASS.</summary>
    PriorityClass = 0x00000020,

    /// <summary>
    /// JOB_OBJECT_LIMIT_BREAKAWAY_OK. Lets a child process opt out of the job.
    /// </summary>
    /// <remarks>
    /// Deliberately <em>not</em> set: children inheriting the cap is the desired behaviour, since a
    /// browser or launcher that spawns its real work into a child would otherwise escape the
    /// restriction entirely and FR-701 would appear to do nothing.
    /// </remarks>
    BreakawayOk = 0x00000800,

    /// <summary>
    /// JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE. Never set — AppGuardian restricts apps, it does not kill
    /// them, and setting this would terminate the user's app when the service stopped.
    /// </summary>
    KillOnJobClose = 0x00002000,
}

[StructLayout(LayoutKind.Sequential)]
internal struct JobObjectCpuRateControlInformation
{
    public JobCpuRateControlFlags ControlFlags;

    /// <summary>
    /// In hundredths of a percent of total system capacity, so 25% is 2500. Union member with
    /// Weight and the MinRate/MaxRate pair in the native definition; only one is meaningful at a
    /// time, selected by <see cref="ControlFlags"/>.
    /// </summary>
    public uint CpuRate;
}

[StructLayout(LayoutKind.Sequential)]
internal struct IoCounters
{
    public ulong ReadOperationCount;
    public ulong WriteOperationCount;
    public ulong OtherOperationCount;
    public ulong ReadTransferCount;
    public ulong WriteTransferCount;
    public ulong OtherTransferCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct JobObjectBasicLimitInformation
{
    public long PerProcessUserTimeLimit;
    public long PerJobUserTimeLimit;
    public JobLimitFlags LimitFlags;
    public nuint MinimumWorkingSetSize;
    public nuint MaximumWorkingSetSize;
    public uint ActiveProcessLimit;
    public nuint Affinity;
    public uint PriorityClass;
    public uint SchedulingClass;
}

[StructLayout(LayoutKind.Sequential)]
internal struct JobObjectExtendedLimitInformation
{
    public JobObjectBasicLimitInformation BasicLimitInformation;
    public IoCounters IoInfo;
    public nuint ProcessMemoryLimit;
    public nuint JobMemoryLimit;
    public nuint PeakProcessMemoryUsed;
    public nuint PeakJobMemoryUsed;
}

/// <summary>
/// PROCESS_POWER_THROTTLING_STATE. The EcoQoS request. FR-703.
/// </summary>
/// <remarks>
/// <c>ControlMask</c> selects which bits <c>StateMask</c> is allowed to set, so both must name
/// EXECUTION_SPEED for the request to take effect. Setting StateMask alone is a common mistake that
/// silently does nothing.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct ProcessPowerThrottlingState
{
    public uint Version;
    public uint ControlMask;
    public uint StateMask;

    internal const uint CurrentVersion = 1;

    /// <summary>PROCESS_POWER_THROTTLING_EXECUTION_SPEED.</summary>
    internal const uint ExecutionSpeed = 0x1;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SystemPowerStatus
{
    public byte AcLineStatus;
    public byte BatteryFlag;
    public byte BatteryLifePercent;
    public byte SystemStatusFlag;
    public uint BatteryLifeTime;
    public uint BatteryFullLifeTime;

    /// <summary>0 = on battery, 1 = on AC, 255 = unknown.</summary>
    public bool IsOnBattery => AcLineStatus == 0;
}
