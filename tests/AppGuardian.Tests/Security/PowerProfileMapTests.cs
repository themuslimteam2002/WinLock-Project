using AppGuardian.Shared.Models;
using AppGuardian.Shared.Security;

namespace AppGuardian.Tests.Security;

/// <summary>
/// Profile-to-enforcement mapping. FR-700..704, FR-707.
/// </summary>
/// <remarks>
/// The numbers are asserted rather than derived because they are what the dashboard promises the user
/// (FR-704) and what the Job Object is configured with. A silent change to either would make the
/// disclosure text a lie without breaking anything else.
/// </remarks>
public sealed class PowerProfileMapTests
{
    [Fact]
    public void Balanced_applies_no_cap_and_no_eco_hint()
    {
        var spec = PowerProfileMap.Resolve(PowerProfile.Balanced);

        Assert.Null(spec.CpuRateCapPercent);
        Assert.Equal(ProcessPriorityHint.Normal, spec.Priority);
        Assert.False(spec.EcoQos);
    }

    [Fact]
    public void LowImpact_caps_at_twenty_five_percent_below_normal()
    {
        var spec = PowerProfileMap.Resolve(PowerProfile.LowImpact);

        Assert.Equal(25, spec.CpuRateCapPercent);
        Assert.Equal(ProcessPriorityHint.BelowNormal, spec.Priority);
        Assert.True(spec.EcoQos);
    }

    [Fact]
    public void StrictSavings_caps_at_ten_percent_at_idle()
    {
        var spec = PowerProfileMap.Resolve(PowerProfile.StrictSavings);

        Assert.Equal(10, spec.CpuRateCapPercent);
        Assert.Equal(ProcessPriorityHint.Idle, spec.Priority);
        Assert.True(spec.EcoQos);
    }

    [Fact]
    public void Strictness_increases_monotonically()
    {
        var low = PowerProfileMap.Resolve(PowerProfile.LowImpact);
        var strict = PowerProfileMap.Resolve(PowerProfile.StrictSavings);

        // The profiles are presented to the user as an ordered choice, so a cap that went up as the
        // label got stricter would make the page nonsense even though each value read correctly alone.
        Assert.True(strict.CpuRateCapPercent < low.CpuRateCapPercent);
        Assert.True(strict.Priority > low.Priority);
    }

    [Fact]
    public void Every_profile_resolves_and_carries_a_description()
    {
        foreach (var profile in Enum.GetValues<PowerProfile>())
        {
            var spec = PowerProfileMap.Resolve(profile);

            Assert.Equal(profile, spec.Profile);

            // FR-704 requires the dashboard to say what a profile will do. An empty description would
            // leave a radio button with a label and no explanation of the tradeoff behind it.
            Assert.False(string.IsNullOrWhiteSpace(spec.Description));
        }
    }

    [Fact]
    public void An_unknown_profile_value_is_refused_rather_than_silently_treated_as_balanced()
    {
        // A value off the end of the enum can only arrive from a newer client or a hand-edited policy
        // file. Falling through to Balanced would answer a request for stricter savings by applying
        // nothing, which is the wrong direction to fail in.
        Assert.Throws<ArgumentOutOfRangeException>(() => PowerProfileMap.Resolve((PowerProfile)99));
    }

    [Theory]
    [InlineData(1, 100u)]
    [InlineData(10, 1000u)]
    [InlineData(25, 2500u)]
    [InlineData(100, 10_000u)]
    public void Percentages_convert_to_hundredths_for_the_job_object(int percent, uint expected)
    {
        // JOBOBJECT_CPU_RATE_CONTROL_INFORMATION.CpuRate is in hundredths of a percent. Passing 25 where
        // 2500 is expected would cap the app at a quarter of one percent — a hang, not a limit.
        Assert.Equal(expected, PowerProfileMap.ToCpuRateUnits(percent));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public void An_out_of_range_percentage_is_refused(int percent)
    {
        // Zero is the dangerous one: the Win32 call accepts it and it means "no CPU at all".
        Assert.Throws<ArgumentOutOfRangeException>(() => PowerProfileMap.ToCpuRateUnits(percent));
    }

    [Fact]
    public void Each_profiles_own_cap_survives_the_conversion()
    {
        foreach (var profile in Enum.GetValues<PowerProfile>())
        {
            if (PowerProfileMap.Resolve(profile).CpuRateCapPercent is { } percent)
            {
                Assert.Equal((uint)percent * 100, PowerProfileMap.ToCpuRateUnits(percent));
            }
        }
    }
}
