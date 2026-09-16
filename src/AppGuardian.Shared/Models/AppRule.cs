namespace AppGuardian.Shared.Models;

/// <summary>Power profile. API Design §5, SRS FR-700.</summary>
public enum PowerProfile
{
    /// <summary>No CPU cap. Priority and QoS left at OS defaults.</summary>
    Balanced = 0,

    /// <summary>Moderate CPU cap, below-normal priority, EcoQoS where available.</summary>
    LowImpact = 1,

    /// <summary>Aggressive CPU cap, idle priority, EcoQoS where available.</summary>
    StrictSavings = 2,
}

/// <summary>
/// How a temporary unlock is bounded.
/// </summary>
/// <remarks>
/// SPEC CONFLICT SC-04: FR-305 offers a duration <em>or</em> "until app close", but neither the SRS
/// nor the API Design schema can express the latter — both model the field as a bare number. This
/// discriminator makes it representable. Enforcement of <see cref="UntilAppClose"/> is stubbed in
/// the agent pending the ruling on SRS Appendix C question 2 (does a temporary unlock survive a
/// process restart within its window?), because the two questions have to be answered together.
/// </remarks>
public enum TempUnlockMode
{
    Duration = 0,
    UntilAppClose = 1,
}

/// <summary>Lock settings for one rule. SRS FR-500, FR-305.</summary>
public sealed class LockSettings
{
    public bool Enabled { get; set; }

    /// <summary>
    /// Temporary unlock duration in seconds. FR-305: default 300, range 60–3600.
    /// </summary>
    /// <remarks>
    /// SPEC CONFLICT SC-04: SRS §9.2 calls this <c>tempUnlockMinutes</c> (value 5); API Design §5
    /// calls it <c>tempUnlockSeconds</c> (value 300). We use seconds per ADR-009 — no rounding is
    /// needed when the UI presents minutes, and the finer unit is a superset.
    /// </remarks>
    public int TempUnlockSeconds { get; set; } = DefaultTempUnlockSeconds;

    public TempUnlockMode TempUnlockMode { get; set; } = TempUnlockMode.Duration;

    public const int DefaultTempUnlockSeconds = 300;
    public const int MinTempUnlockSeconds = 60;
    public const int MaxTempUnlockSeconds = 3600;

    /// <summary>Clamps <see cref="TempUnlockSeconds"/> into the FR-305 range.</summary>
    public void Normalize() =>
        TempUnlockSeconds = Math.Clamp(TempUnlockSeconds, MinTempUnlockSeconds, MaxTempUnlockSeconds);
}

/// <summary>Hide settings for one rule. SRS FR-601.</summary>
public sealed class HideSettings
{
    public bool Enabled { get; set; }

    // SPEC CONFLICT SC-15b: API Design §5 puts a per-rule "workspace" name here, which implies
    // multiple hidden desktops — nothing else in the spec supports that. The workspace name is a
    // single global constant instead; see PolicyConstants.HiddenWorkspaceName.
}

/// <summary>Power settings for one rule. SRS FR-700..703.</summary>
public sealed class PowerSettings
{
    /// <summary>Null means no power rule for this app (distinct from an explicit Balanced).</summary>
    public PowerProfile? Profile { get; set; }

    /// <summary>FR-702, priority <c>S</c>. Independently toggleable so it can be cut.</summary>
    public bool ReducePriority { get; set; }

    /// <summary>FR-703, priority <c>S</c>. Silently ignored where the OS lacks support.</summary>
    public bool EcoQos { get; set; }
}

/// <summary>
/// Persisted policy for one application. API Design §5; SRS FR-500/601/700, FR-605/705.
/// </summary>
public sealed class AppRule
{
    /// <summary>SRS §9.2 calls this <c>id</c>; API Design calls it <c>ruleId</c> (SC-05, ADR-009).</summary>
    public string RuleId { get; set; } = Guid.NewGuid().ToString();

    public AppIdentity Identity { get; set; } = new();

    public LockSettings Lock { get; set; } = new();

    public HideSettings Hide { get; set; } = new();

    public PowerSettings Power { get; set; } = new();

    /// <summary>Master switch for the rule. A disabled rule is retained but not enforced.</summary>
    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>True when the rule asks for any enforcement at all.</summary>
    public bool HasAnyProtection =>
        Enabled && (Lock.Enabled || Hide.Enabled || Power.Profile is not null);

    public void Touch() => UpdatedUtc = DateTimeOffset.UtcNow;
}
