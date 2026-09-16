using AppGuardian.Shared.Models;

namespace AppGuardian.Shared.Contracts;

// OS-integration interface contracts. API Design §6.
// These live in Shared so OS specifics stay behind adapters, which is what makes the
// virtual-desktop risk (SRS Risk 1) survivable and keeps the shared assembly platform-neutral.

/// <summary>Windows Hello verification. FR-301.</summary>
public interface IWindowsHelloVerifier
{
    /// <summary>
    /// False when Hello is not enrolled or not present. FR-300 makes a PIN/password fallback
    /// mandatory precisely because this can be false on any given machine.
    /// </summary>
    Task<bool> IsAvailableAsync();

    /// <summary>
    /// Prompts the user. <paramref name="reason"/> is shown in the system dialog.
    /// A cancellation by the user is a normal <see cref="VerifyOutcome.Cancelled"/> result,
    /// not an exception.
    /// </summary>
    Task<VerifyResult> VerifyAsync(string reason, CancellationToken ct);
}

public sealed class VerifyResult
{
    public VerifyOutcome Outcome { get; init; }

    public string? Detail { get; init; }

    public bool Succeeded => Outcome == VerifyOutcome.Verified;

    public static VerifyResult Verified() => new() { Outcome = VerifyOutcome.Verified };

    public static VerifyResult Cancelled() => new() { Outcome = VerifyOutcome.Cancelled };

    public static VerifyResult Failed(string? detail = null) =>
        new() { Outcome = VerifyOutcome.Failed, Detail = detail };

    public static VerifyResult Unavailable(string? detail = null) =>
        new() { Outcome = VerifyOutcome.Unavailable, Detail = detail };
}

public enum VerifyOutcome
{
    Verified = 0,

    /// <summary>Credential rejected. Counts toward the FR-306 failure tally.</summary>
    Failed = 1,

    /// <summary>User dismissed the prompt. Does not count as a failure.</summary>
    Cancelled = 2,

    /// <summary>Hello not enrolled or unavailable. Caller should offer the fallback.</summary>
    Unavailable = 3,
}

/// <summary>Process discovery and launch notification. FR-401, FR-501.</summary>
public interface IProcessMonitor
{
    /// <summary>Raised when a process starts. Drives the lock and hide triggers.</summary>
    event EventHandler<AppLaunchedEventArgs>? AppLaunched;

    /// <summary>Raised when a watched process exits, so state and until-close sessions can be cleared.</summary>
    event EventHandler<AppExitedEventArgs>? AppExited;

    IReadOnlyList<RunningApp> ListRunning();

    Task StartAsync(CancellationToken ct);

    Task StopAsync();
}

public sealed class AppLaunchedEventArgs : EventArgs
{
    public required int ProcessId { get; init; }

    public required string ExecutablePath { get; init; }

    public string? PackageFamilyName { get; init; }

    public string? AppId { get; init; }

    public DateTimeOffset ObservedUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class AppExitedEventArgs : EventArgs
{
    public required int ProcessId { get; init; }

    public string? AppId { get; init; }
}

public sealed class RunningApp
{
    public required int ProcessId { get; init; }

    public required string ProcessName { get; init; }

    public string? ExecutablePath { get; init; }

    public string? PackageFamilyName { get; init; }

    public string? MainWindowTitle { get; init; }

    public long MainWindowHandle { get; init; }

    /// <summary>
    /// True when the process runs at a higher integrity level than AppGuardian, in which case it
    /// cannot be controlled (SRS CN-5). Surfaced as <c>UnsupportedReason</c> rather than a silent
    /// failure at enforcement time.
    /// </summary>
    public bool IsElevated { get; init; }

    public DateTimeOffset StartedUtc { get; init; }
}

/// <summary>CPU and power restriction. FR-701..703, FR-706.</summary>
public interface IPowerController
{
    /// <summary>
    /// Applies a profile to a running process without restarting it (FR-706).
    /// Returns a result rather than throwing when the target cannot be controlled — most commonly
    /// because it already belongs to another job object (ADR-008).
    /// </summary>
    PowerApplyResult ApplyThrottle(int pid, PowerProfile profile, bool reducePriority, bool ecoQos);

    void RemoveThrottle(int pid);

    /// <summary>FR-703: false on OS builds or hardware without EcoQoS.</summary>
    bool SupportsEcoQos { get; }

    /// <summary>Whether the device is currently on battery. FR-708.</summary>
    bool IsOnBattery();

    /// <summary>PIDs currently under an AppGuardian restriction. FR-704.</summary>
    IReadOnlyDictionary<int, PowerProfile> ActiveThrottles { get; }
}

public sealed class PowerApplyResult
{
    public bool Succeeded { get; init; }

    public int? CpuRateCapPercent { get; init; }

    public bool EcoQosApplied { get; init; }

    public bool PriorityReduced { get; init; }

    /// <summary>Populated when <see cref="Succeeded"/> is false. Shown to the user (FR-509).</summary>
    public string? UnsupportedReason { get; init; }

    public static PowerApplyResult Unsupported(string reason) =>
        new() { Succeeded = false, UnsupportedReason = reason };
}

/// <summary>
/// Virtual desktop access. FR-600..606, SRS Risk 1.
/// </summary>
/// <remarks>
/// Every implementation must degrade gracefully: when <see cref="IsSupported"/> is false, callers
/// surface <c>E_VD_UNAVAILABLE</c> and the UI discloses the fallback (FR-607). See ADR-007 —
/// Windows exposes no supported API for moving an arbitrary window to a chosen desktop, and the
/// undocumented interfaces change shape across builds.
/// </remarks>
public interface IVirtualDesktopAdapter
{
    /// <summary>Runtime feature-detect. Probed once at startup, never assumed from the OS version.</summary>
    bool IsSupported { get; }

    /// <summary>Which Windows build family this adapter was written for. Logged for diagnosis.</summary>
    string AdapterName { get; }

    /// <summary>Creates the hidden desktop if absent and returns its id. FR-600.</summary>
    DesktopId EnsureHidden(string name);

    /// <summary>Moves a window to the target desktop. FR-602.</summary>
    bool MoveWindow(nint hwnd, DesktopId target);

    /// <summary>Moves a window back to the desktop the user is on. FR-604, FR-606.</summary>
    bool MoveToCurrent(nint hwnd);

    /// <summary>True when the window is on the given desktop. Used to verify a move actually took.</summary>
    bool IsWindowOnDesktop(nint hwnd, DesktopId desktop);
}

/// <summary>Opaque virtual desktop identifier.</summary>
public readonly record struct DesktopId(Guid Value)
{
    public static readonly DesktopId None = new(Guid.Empty);

    public bool IsNone => Value == Guid.Empty;
}

/// <summary>The lock overlay. FR-502, FR-503, FR-507.</summary>
public interface ILockOverlay
{
    /// <summary>
    /// Shows a covering window on every supplied monitor (FR-507). Idempotent — showing an
    /// already-shown overlay refreshes it rather than stacking a second one.
    /// </summary>
    void Show(AppIdentity app, IReadOnlyList<MonitorInfo> monitors);

    void Hide();

    bool IsVisible { get; }

    /// <summary>Raised when the user authenticates successfully through the overlay.</summary>
    event EventHandler<OverlayUnlockedEventArgs>? Unlocked;

    /// <summary>Raised when the user dismisses without authenticating. The app stays locked.</summary>
    event EventHandler? Dismissed;
}

public sealed class OverlayUnlockedEventArgs : EventArgs
{
    public required string AppId { get; init; }

    public required AuthMethod Method { get; init; }
}

public sealed class MonitorInfo
{
    public required string DeviceName { get; init; }

    public required int X { get; init; }

    public required int Y { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public bool IsPrimary { get; init; }

    /// <summary>Effective DPI scale, for per-monitor DPI awareness (NFR §7.6, PMv2).</summary>
    public double DpiScale { get; init; } = 1.0;
}

/// <summary>
/// Foreground window tracking. FR-501.
/// </summary>
/// <remarks>
/// Implemented over WinEvent hooks rather than polling, because NFR-P4 allows only 1 s from focus
/// to overlay and NFR-P2 caps idle CPU at 2% — a poll loop tight enough for the first would
/// jeopardise the second.
/// </remarks>
public interface IForegroundWatcher
{
    event EventHandler<ForegroundChangedEventArgs>? ForegroundChanged;

    Task StartAsync(CancellationToken ct);

    Task StopAsync();

    /// <summary>Enumerates monitors for overlay placement (FR-507).</summary>
    IReadOnlyList<MonitorInfo> GetMonitors();
}

public sealed class ForegroundChangedEventArgs : EventArgs
{
    public required nint WindowHandle { get; init; }

    public required int ProcessId { get; init; }

    public string? ExecutablePath { get; init; }

    public string? WindowTitle { get; init; }
}

/// <summary>Installed application discovery. FR-400, FR-402, FR-404.</summary>
public interface IAppDiscovery
{
    /// <summary>
    /// Enumerates installed applications from registry uninstall keys, Start Menu shortcuts, and
    /// packaged app sources, de-duplicated by <c>appId</c> (FR-405).
    /// </summary>
    Task<IReadOnlyList<DiscoveredApp>> ListInstalledAsync(CancellationToken ct);

    Task<AppIdentity?> ResolveAsync(ResolveIdentityRequest request, CancellationToken ct);

    /// <summary>Base64 PNG icon, or null. Separate from listing to keep responses small (SC-15c).</summary>
    Task<string?> GetIconBase64Async(string appId, CancellationToken ct);
}

/// <summary>
/// Key derivation for stored credentials. FR-303, FR-304, NFR-S1.
/// </summary>
/// <remarks>
/// Abstracted so ADR-003's provisional choice of PBKDF2 can be revisited without a credential
/// migration: the algorithm name and parameters are persisted per record, so a future Argon2id
/// implementation verifies existing PBKDF2 hashes and rehashes on the next successful unlock.
/// </remarks>
public interface IKeyDerivation
{
    /// <summary>Value stored in <see cref="HashRecord.Algo"/>.</summary>
    string AlgorithmName { get; }

    /// <summary>Hashes a secret with a fresh random salt.</summary>
    HashRecord Hash(string secret);

    /// <summary>
    /// Verifies against a stored record, using the parameters in that record rather than current
    /// defaults, so old hashes stay verifiable after the defaults change.
    /// Must be constant-time in the comparison.
    /// </summary>
    bool Verify(string secret, HashRecord record);

    /// <summary>
    /// True when the record used weaker parameters than current defaults and should be rehashed on
    /// the next successful verification.
    /// </summary>
    bool NeedsRehash(HashRecord record);
}
