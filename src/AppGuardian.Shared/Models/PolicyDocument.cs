namespace AppGuardian.Shared.Models;

/// <summary>
/// The full persisted policy document. SRS §9.1 <c>policy.json</c>, FR-605/705.
/// </summary>
/// <remarks>
/// Owned authoritatively by the Service (API Design §1.1). The UI and Agent read it over IPC and
/// never touch the file, so there is exactly one writer and atomic replacement is sufficient — no
/// cross-process file locking is needed.
/// </remarks>
public sealed class PolicyDocument
{
    /// <summary>Schema version of this document, for on-disk migration.</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public List<AppRule> Rules { get; set; } = new();

    /// <summary>Global pause switch. FR-806. Auth-guarded to change.</summary>
    public bool ProtectionPaused { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Integrity hash over the rules, checked on load. SRS NFR-S3 asks that the policy store be
    /// protected against casual tampering; the ACL is the real control, and this catches an edit
    /// that got past it. It is deliberately not a MAC — a keyed MAC would need a key that
    /// LocalSystem can read, which an administrator can also read, so it would add complexity
    /// without adding a real barrier against the only attacker who can write the file.
    /// </summary>
    public string? IntegrityHash { get; set; }

    public const int CurrentSchemaVersion = 1;

    public AppRule? FindByAppId(string appId) =>
        Rules.FirstOrDefault(r => string.Equals(r.Identity.AppId, appId, StringComparison.OrdinalIgnoreCase));

    public AppRule? FindByRuleId(string ruleId) =>
        Rules.FirstOrDefault(r => string.Equals(r.RuleId, ruleId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Rules that should currently be enforced. Returns nothing while globally paused, which is
    /// what makes FR-806 a single check rather than a condition scattered across every enforcer.
    /// </summary>
    public IEnumerable<AppRule> ActiveRules =>
        ProtectionPaused ? Enumerable.Empty<AppRule>() : Rules.Where(r => r.HasAnyProtection);
}

/// <summary>Per-user UI preferences. SRS §9.1 <c>settings.json</c>, FR-802.</summary>
public sealed class UserSettings
{
    public int SchemaVersion { get; set; } = 1;

    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>Whether the dashboard itself launches at logon. The agent always does (FR-102).</summary>
    public bool LaunchDashboardAtLogon { get; set; }

    public bool StartMinimizedToTray { get; set; } = true;

    /// <summary>Default applied to newly created lock rules. FR-305.</summary>
    public int DefaultTempUnlockSeconds { get; set; } = LockSettings.DefaultTempUnlockSeconds;

    public bool VerboseLogging { get; set; }

    /// <summary>
    /// Set once the user has acknowledged the best-effort disclosures (FR-607, FR-707). Recorded so
    /// the dashboard can show the full explanation on first visit and a compact reminder after.
    /// </summary>
    public bool DisclosuresAcknowledged { get; set; }

    /// <summary>
    /// Set once the premium onboarding wizard reaches its final step, so the shell can skip the wizard on
    /// subsequent launches. FR-300: onboarding is mandatory exactly once.
    /// </summary>
    /// <remarks>
    /// Stored in the same <c>settings.json</c> the theme lives in, because it is a per-user UI decision with
    /// no service-side consequence: the gate can only open once a credential exists, and that fact is held by
    /// the service, not by this flag. A machine that lost its credential therefore re-runs onboarding even if
    /// this flag is set, which is the safe failure direction.
    /// </remarks>
    public bool HasCompletedOnboarding { get; set; }
}

public enum AppTheme
{
    System = 0,
    Light = 1,
    Dark = 2,
}

/// <summary>Paths, names, and tunables that must agree across all three processes.</summary>
public static class PolicyConstants
{
    /// <summary>SRS FR-101. Must match the installer's service registration.</summary>
    public const string ServiceName = "AppGuardian.Service";

    /// <summary>
    /// Shown in services.msc.
    /// </summary>
    /// <remarks>SC-15a: no document specifies a display name or description.</remarks>
    public const string ServiceDisplayName = "AppGuardian Protection Service";

    public const string ServiceDescription =
        "Enforces AppGuardian application lock, hide, and power-restriction policies. " +
        "Local only; makes no network connections.";

    /// <summary>
    /// The single hidden virtual desktop name. SRS FR-600.
    /// </summary>
    /// <remarks>
    /// SC-15b: API Design §5 models this per-rule, implying multiple hidden desktops, which nothing
    /// else in the spec supports. One global constant instead.
    /// </remarks>
    public const string HiddenWorkspaceName = "GuardianHidden";

    public const string DataFolderName = "AppGuardian";
    public const string PolicyFileName = "policy.json";
    public const string CredentialsFileName = "credentials.dat";
    public const string AuditFileName = "audit.log";
    public const string SettingsFileName = "settings.json";

    /// <summary>SRS §9.3: rotate at 5 MB, keep 3 files.</summary>
    public const long AuditRotateBytes = 5 * 1024 * 1024;

    public const int AuditKeepFiles = 3;

    /// <summary>FR-306: failures before backoff.</summary>
    public const int MaxAuthFailures = 5;

    /// <summary>FR-306: first backoff, escalating on each subsequent trip.</summary>
    public static readonly TimeSpan BaseAuthBackoff = TimeSpan.FromSeconds(30);

    /// <summary>Ceiling on the escalating backoff, so a user is never locked out indefinitely.</summary>
    public static readonly TimeSpan MaxAuthBackoff = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Watchdog restart budget for a dead enforcer.
    /// </summary>
    /// <remarks>
    /// SC-08 / ADR: NFR-S4's "protected apps remain locked" is not literally achievable, because
    /// the overlay is a window owned by the agent and dies with it. What is achievable is that a
    /// crash never mutates policy, and that the enforcer is restarted within this budget. The
    /// residual gap is disclosed to the user rather than papered over.
    /// </remarks>
    public static readonly TimeSpan WatchdogRestartBudget = TimeSpan.FromSeconds(5);

    /// <summary>API Design §2.4 default request timeout.</summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>API Design §2.4: auth prompts involve a human, so they get much longer.</summary>
    public static readonly TimeSpan AuthRequestTimeout = TimeSpan.FromSeconds(60);

    /// <summary>NFR-P4: lock trigger latency budget after focus or launch.</summary>
    public static readonly TimeSpan LockLatencyBudget = TimeSpan.FromSeconds(1);
}
