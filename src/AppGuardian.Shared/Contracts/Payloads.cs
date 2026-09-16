using AppGuardian.Shared.Models;

namespace AppGuardian.Shared.Contracts;

// Request/response payload DTOs for the message catalog (API Design §4).
// Kept in one file because they are small, numerous, and only meaningful as a set.

// ---- auth.* ----

/// <summary><c>auth.setup</c>. FR-300, FR-302, FR-303.</summary>
/// <remarks>
/// The secret crosses the local, ACL-restricted pipe exactly once here and once on
/// <c>auth.verifyPin</c>, and is never persisted (API Design §5 credential note). Callers must not
/// log this DTO.
/// </remarks>
public sealed class AuthSetupRequest
{
    public AuthMethod PreferredMethod { get; set; } = AuthMethod.Pin;

    /// <summary>The fallback PIN or password in cleartext. FR-300 requires one to exist.</summary>
    public string FallbackSecret { get; set; } = string.Empty;
}

/// <summary><c>auth.verifyPin</c>. FR-302, FR-303, FR-306.</summary>
public sealed class AuthVerifyRequest
{
    public string Secret { get; set; } = string.Empty;

    /// <summary>App the verification is for. Null for a dashboard-level unlock.</summary>
    public string? AppId { get; set; }
}

/// <summary>Reply to a verification attempt.</summary>
public sealed class AuthVerifyResult
{
    public bool Verified { get; set; }

    public AuthMethod Method { get; set; }

    /// <summary>Remaining attempts before backoff. FR-306; surfaced in the error details.</summary>
    public int AttemptsRemaining { get; set; }

    /// <summary>True while an FR-306 backoff is in force.</summary>
    public bool LockedOut { get; set; }

    public DateTimeOffset? LockedUntilUtc { get; set; }

    /// <summary>
    /// User-facing explanation of a failure.
    /// </summary>
    /// <remarks>
    /// Deliberately non-specific about <em>why</em> verification failed beyond what the user needs to
    /// act: "that PIN is not correct" and "try again in 2 minutes" are actionable, whereas
    /// distinguishing "no credential exists" from "wrong secret" tells an unauthenticated caller
    /// something about the machine for no benefit to the legitimate user.
    /// </remarks>
    public string? Reason { get; set; }

    /// <summary>A short-lived single-use grant returned only after successful authentication.</summary>
    public string? AuthorizationToken { get; set; }
}

/// <summary>Implemented by requests that mutate protected service state.</summary>
public interface IAuthorizationTokenRequest
{
    string? AuthorizationToken { get; set; }
}

/// <summary><c>auth.startSession</c>. FR-305, FR-308.</summary>
public sealed class StartSessionRequest : IAuthorizationTokenRequest
{
    public string AppId { get; set; } = string.Empty;

    /// <summary>Null uses the rule's configured duration.</summary>
    public int? DurationSeconds { get; set; }

    public TempUnlockMode Mode { get; set; } = TempUnlockMode.Duration;

    /// <summary>Required when <see cref="Mode"/> is until-app-close.</summary>
    public int? ProcessId { get; set; }

    public string? AuthorizationToken { get; set; }
}

/// <summary><c>auth.endSession</c>. FR-305.</summary>
public sealed class EndSessionRequest
{
    /// <summary>Null ends every active session — used by the pause and lock-now flows.</summary>
    public string? AppId { get; set; }
}

// ---- apps.* ----

/// <summary><c>apps.listInstalled</c>. FR-400, FR-404.</summary>
public sealed class ListInstalledRequest
{
    /// <summary>Case-insensitive substring filter on display name.</summary>
    public string? Filter { get; set; }

    /// <summary>
    /// Icons are excluded from list responses regardless of this flag when the result would exceed
    /// the envelope cap (SC-15c). Fetch icons individually via <c>apps.getIcon</c>.
    /// </summary>
    public bool IncludeIcons { get; set; }
}

/// <summary>One discovered application.</summary>
public sealed class DiscoveredApp
{
    public AppIdentity Identity { get; set; } = new();

    /// <summary>True when at least one process for this app is running now. FR-404.</summary>
    public bool IsRunning { get; set; }

    /// <summary>PIDs currently running for this identity. Empty when not running.</summary>
    public List<int> ProcessIds { get; set; } = new();

    /// <summary>Existing rule for this app, if any, so the UI can show current state.</summary>
    public string? RuleId { get; set; }

    /// <summary>Where this entry was discovered. Shown in the picker to explain duplicates.</summary>
    public DiscoverySource Source { get; set; }

    /// <summary>
    /// Set when AppGuardian can already tell this target will resist control — an elevated or
    /// protected process, for instance. Disclosed in the picker rather than failing later (FR-509).
    /// </summary>
    public string? UnsupportedReason { get; set; }
}

public enum DiscoverySource
{
    RegistryUninstall = 0,
    StartMenuShortcut = 1,
    AppxPackage = 2,
    RunningProcess = 3,
    ManuallyAdded = 4,
}

/// <summary><c>apps.listInstalled</c> / <c>apps.listRunning</c> reply.</summary>
public sealed class AppListResult
{
    public List<DiscoveredApp> Apps { get; set; } = new();

    /// <summary>True when icons were stripped to fit the envelope cap (SC-15c).</summary>
    public bool IconsOmitted { get; set; }

    public DateTimeOffset GeneratedUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary><c>apps.resolveIdentity</c> / <c>apps.addManual</c>. FR-402, FR-403.</summary>
public sealed class ResolveIdentityRequest
{
    public string? ExecutablePath { get; set; }

    public string? PackageFamilyName { get; set; }

    public int? ProcessId { get; set; }

    /// <summary>Compute the content hash too. Optional because hashing a large exe is not free.</summary>
    public bool ComputeFileHash { get; set; }
}

/// <summary><c>apps.getIcon</c>. FR-404.</summary>
public sealed class GetIconRequest
{
    public string AppId { get; set; } = string.Empty;
}

/// <summary><c>apps.getIcon</c> reply.</summary>
public sealed class GetIconResult
{
    public string AppId { get; set; } = string.Empty;

    /// <summary>Base64 PNG, or null when no icon could be extracted.</summary>
    public string? IconBase64 { get; set; }
}

// ---- lock.* / hide.* / power.* / policy.* ----

/// <summary><c>lock.setRule</c>, <c>hide.setRule</c>, <c>power.setProfile</c>. FR-500/601/700, FR-801.</summary>
/// <remarks>
/// One DTO for all three because they are the same operation on different fields of the same rule.
/// A null section leaves that section unchanged, so the UI can update one facet without
/// round-tripping the others and risking a lost update.
/// </remarks>
public sealed class SetRuleRequest : IAuthorizationTokenRequest
{
    /// <summary>Required. Identifies the target app; a rule is created if none exists.</summary>
    public AppIdentity Identity { get; set; } = new();

    public LockSettings? Lock { get; set; }

    public HideSettings? Hide { get; set; }

    public PowerSettings? Power { get; set; }

    /// <summary>Null leaves the rule's enabled state unchanged.</summary>
    public bool? Enabled { get; set; }

    public string? AuthorizationToken { get; set; }
}

/// <summary>Reply carrying the rule as persisted, so the UI can refresh without a second call.</summary>
public sealed class RuleResult
{
    public AppRule Rule { get; set; } = new();

    /// <summary>True when this call created the rule rather than updating one.</summary>
    public bool Created { get; set; }
}

/// <summary><c>policy.deleteRule</c>. FR-801.</summary>
public sealed class DeleteRuleRequest : IAuthorizationTokenRequest
{
    public string RuleId { get; set; } = string.Empty;

    public string? AuthorizationToken { get; set; }
}

/// <summary><c>policy.setPaused</c>. FR-806.</summary>
public sealed class SetPausedRequest : IAuthorizationTokenRequest
{
    public bool Paused { get; set; }

    public string? AuthorizationToken { get; set; }
}

/// <summary><c>policy.get</c> reply. Added per SC-03.</summary>
public sealed class PolicySnapshot
{
    public PolicyDocument Policy { get; set; } = new();

    /// <summary>Lets a client skip a re-fetch when nothing has changed.</summary>
    public string? IntegrityHash { get; set; }
}

/// <summary><c>lock.appLaunched</c> event. FR-501.</summary>
public sealed class AppLaunchedEvent
{
    public string AppId { get; set; } = string.Empty;

    public int ProcessId { get; set; }

    /// <summary>Foreground window handle as a 64-bit integer, or 0 when not yet known.</summary>
    public long WindowHandle { get; set; }

    public string? ExecutablePath { get; set; }
}

/// <summary><c>lock.showOverlay</c>. FR-502, FR-503, FR-507.</summary>
public sealed class ShowOverlayRequest
{
    public AppIdentity Identity { get; set; } = new();

    public int ProcessId { get; set; }

    public long WindowHandle { get; set; }

    /// <summary>Duration to grant on a successful unlock, from the rule.</summary>
    public int TempUnlockSeconds { get; set; } = LockSettings.DefaultTempUnlockSeconds;

    public TempUnlockMode TempUnlockMode { get; set; } = TempUnlockMode.Duration;
}

/// <summary><c>lock.unlock</c>. FR-504, FR-505.</summary>
public sealed class UnlockRequest : IAuthorizationTokenRequest
{
    public string AppId { get; set; } = string.Empty;

    public int? ProcessId { get; set; }

    public AuthMethod Method { get; set; }

    /// <summary>Null uses the rule's configured duration.</summary>
    public int? DurationSeconds { get; set; }

    public string? AuthorizationToken { get; set; }
}

/// <summary><c>lock.getState</c> reply. FR-506.</summary>
public sealed class LockStateResult
{
    public List<LockStateEntry> Entries { get; set; } = new();
}

/// <summary>Per-app lock state. FR-506.</summary>
public sealed class LockStateEntry
{
    public string AppId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public bool IsLocked { get; set; }

    public bool IsRunning { get; set; }

    /// <summary>Null when there is no active temporary unlock.</summary>
    public DateTimeOffset? UnlockExpiresUtc { get; set; }
}

/// <summary><c>hide.moveToHidden</c>. FR-602, FR-603.</summary>
public sealed class MoveToHiddenRequest
{
    public string AppId { get; set; } = string.Empty;

    public int ProcessId { get; set; }

    public long WindowHandle { get; set; }
}

/// <summary><c>hide.reveal</c>. FR-604, FR-606.</summary>
public sealed class RevealRequest
{
    public string AppId { get; set; } = string.Empty;

    /// <summary>
    /// True to bring the window back only for now, leaving the hide rule in force; false to also
    /// disable the rule. FR-606 requires both "reveal temporarily" and "return to main desktop".
    /// </summary>
    public bool Temporary { get; set; } = true;
}

/// <summary>Result of a hide or reveal attempt.</summary>
public sealed class HideResult
{
    public bool Succeeded { get; set; }

    /// <summary>
    /// True when the real virtual desktop path was unavailable and the weaker fallback was used
    /// instead. Must be surfaced to the user, not swallowed (ADR-007, FR-607).
    /// </summary>
    public bool UsedFallback { get; set; }

    public string? Detail { get; set; }
}

/// <summary><c>power.setProfile</c>. FR-700, FR-705.</summary>
public sealed class SetPowerProfileRequest : IAuthorizationTokenRequest
{
    public AppIdentity Identity { get; set; } = new();

    /// <summary>Null removes the power rule entirely.</summary>
    public PowerProfile? Profile { get; set; }

    public bool ReducePriority { get; set; }

    public bool EcoQos { get; set; }

    public string? AuthorizationToken { get; set; }
}

/// <summary><c>power.getStatus</c> reply. FR-704.</summary>
public sealed class PowerStatusResult
{
    public List<PowerStatusEntry> Entries { get; set; } = new();

    /// <summary>False on hardware or OS builds without EcoQoS support. FR-703.</summary>
    public bool EcoQosSupported { get; set; }

    /// <summary>True when running on battery. Relevant to FR-708 if that Could item is built.</summary>
    public bool OnBattery { get; set; }
}

/// <summary>Per-app restriction status. FR-704.</summary>
public sealed class PowerStatusEntry
{
    public string AppId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public PowerProfile? Profile { get; set; }

    public bool IsThrottled { get; set; }

    public List<int> ProcessIds { get; set; } = new();

    /// <summary>Effective CPU rate cap as a percentage, when one is applied.</summary>
    public int? CpuRateCapPercent { get; set; }

    /// <summary>
    /// Set when the profile could not be applied — most often because the process already belongs
    /// to another job object (ADR-008). Reported rather than silently treated as success.
    /// </summary>
    public string? UnsupportedReason { get; set; }
}

// ---- system.* ----

/// <summary><c>system.getAuditLog</c>. FR-803.</summary>
public sealed class GetAuditLogRequest
{
    public int MaxEntries { get; set; } = 200;

    public DateTimeOffset? SinceUtc { get; set; }

    public AuditAction? ActionFilter { get; set; }
}

/// <summary><c>system.getAuditLog</c> reply. FR-803.</summary>
public sealed class AuditLogResult
{
    public List<AuditEntry> Entries { get; set; } = new();

    /// <summary>True when the result was truncated by <c>MaxEntries</c>.</summary>
    public bool Truncated { get; set; }
}

/// <summary><c>system.audit</c>. FR-508. Service-only write path per ADR-005.</summary>
public sealed class AuditRequest
{
    public AuditEntry Entry { get; set; } = new();
}
