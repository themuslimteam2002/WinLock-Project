using AppGuardian.Shared.Models;

namespace AppGuardian.Shared.Security;

/// <summary>
/// Maps a <see cref="PowerProfile"/> to concrete enforcement parameters. FR-700..703.
/// </summary>
/// <remarks>
/// The mapping lives in Shared rather than in the service so the dashboard can tell the user
/// exactly what a profile will do (FR-704) without duplicating the numbers, and so the values are
/// unit-testable without touching Win32.
/// <para>
/// The CPU rate cap is expressed as a percentage of total system capacity, matching
/// <c>JOBOBJECT_CPU_RATE_CONTROL_INFORMATION.CpuRate</c>, which is specified in hundredths of a
/// percent — so 25% becomes 2500 at the API boundary.
/// </para>
/// </remarks>
public static class PowerProfileMap
{
    public static PowerProfileSpec Resolve(PowerProfile profile) => profile switch
    {
        PowerProfile.Balanced => new PowerProfileSpec
        {
            Profile = PowerProfile.Balanced,
            // No cap. A "Balanced" rule exists so the user can record an explicit decision to leave
            // an app alone, which is different from having no rule at all.
            CpuRateCapPercent = null,
            Priority = ProcessPriorityHint.Normal,
            EcoQos = false,
            Description = "No restriction. Runs at normal priority with no CPU cap.",
        },

        PowerProfile.LowImpact => new PowerProfileSpec
        {
            Profile = PowerProfile.LowImpact,
            CpuRateCapPercent = 25,
            Priority = ProcessPriorityHint.BelowNormal,
            EcoQos = true,
            Description =
                "Caps the app at about 25% of total CPU, lowers its priority, and asks Windows to " +
                "schedule it for efficiency. Noticeable slowdown under heavy load.",
        },

        PowerProfile.StrictSavings => new PowerProfileSpec
        {
            Profile = PowerProfile.StrictSavings,
            CpuRateCapPercent = 10,
            Priority = ProcessPriorityHint.Idle,
            EcoQos = true,
            Description =
                "Caps the app at about 10% of total CPU and runs it at the lowest priority. " +
                "Expect the app to feel slow; use for background apps you rarely interact with.",
        },

        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown power profile."),
    };

    /// <summary>
    /// Converts a percentage into the hundredths-of-a-percent unit that
    /// <c>JOBOBJECT_CPU_RATE_CONTROL_INFORMATION</c> expects.
    /// </summary>
    public static uint ToCpuRateUnits(int percent)
    {
        if (percent is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(percent), percent, "CPU rate cap must be between 1 and 100 percent.");
        }

        return (uint)(percent * 100);
    }
}

/// <summary>Concrete enforcement parameters for one profile.</summary>
public sealed class PowerProfileSpec
{
    public required PowerProfile Profile { get; init; }

    /// <summary>Null means no CPU cap is applied.</summary>
    public int? CpuRateCapPercent { get; init; }

    public required ProcessPriorityHint Priority { get; init; }

    /// <summary>Requested only; silently skipped where the OS lacks support (FR-703).</summary>
    public bool EcoQos { get; init; }

    /// <summary>
    /// User-facing explanation. Shown in the dashboard so the disclosure in FR-707 is concrete
    /// rather than a generic disclaimer.
    /// </summary>
    public required string Description { get; init; }
}

/// <summary>
/// Priority hint, kept as an AppGuardian enum rather than <c>ProcessPriorityClass</c> so this
/// assembly stays platform-neutral.
/// </summary>
public enum ProcessPriorityHint
{
    Normal = 0,
    BelowNormal = 1,
    Idle = 2,
}
