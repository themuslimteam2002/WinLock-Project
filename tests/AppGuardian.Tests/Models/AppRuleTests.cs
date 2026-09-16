using AppGuardian.Shared.Models;

namespace AppGuardian.Tests.Models;

/// <summary>
/// Rule shape, normalisation, and the "is this rule enforced" question. FR-305, FR-500, FR-806.
/// </summary>
public sealed class AppRuleTests
{
    [Fact]
    public void A_new_rule_is_enabled_with_no_protection_turned_on()
    {
        var rule = new AppRule();

        // Enabled defaults true because the master switch means "the user has not disabled this rule",
        // not "the user has asked for something". HasAnyProtection is what distinguishes the two.
        Assert.True(rule.Enabled);
        Assert.False(rule.HasAnyProtection);
        Assert.NotEqual(string.Empty, rule.RuleId);
    }

    [Fact]
    public void Rule_ids_are_unique_per_instance()
    {
        Assert.NotEqual(new AppRule().RuleId, new AppRule().RuleId);
    }

    [Theory]
    [InlineData(true, false, null, true)]
    [InlineData(false, true, null, true)]
    [InlineData(false, false, PowerProfile.Balanced, true)]
    [InlineData(false, false, PowerProfile.LowImpact, true)]
    [InlineData(false, false, null, false)]
    public void HasAnyProtection_covers_every_feature(
        bool lockEnabled, bool hideEnabled, PowerProfile? profile, bool expected)
    {
        var rule = new AppRule
        {
            Lock = new LockSettings { Enabled = lockEnabled },
            Hide = new HideSettings { Enabled = hideEnabled },
            Power = new PowerSettings { Profile = profile },
        };

        // An explicit Balanced profile counts: it is a recorded decision the enforcer has to honour by
        // clearing any cap left over from a previous profile, which is work, not a no-op.
        Assert.Equal(expected, rule.HasAnyProtection);
    }

    [Fact]
    public void A_disabled_rule_asks_for_nothing_however_it_is_configured()
    {
        var rule = new AppRule
        {
            Enabled = false,
            Lock = new LockSettings { Enabled = true },
            Hide = new HideSettings { Enabled = true },
            Power = new PowerSettings { Profile = PowerProfile.StrictSavings },
        };

        // The rule is retained rather than deleted so the user's configuration survives a temporary
        // disable, but nothing about it is enforced meanwhile.
        Assert.False(rule.HasAnyProtection);
    }

    [Fact]
    public void ReducePriority_and_EcoQos_alone_are_not_protection()
    {
        // Both are FR-702/703 modifiers on a profile. Without a profile there is nothing to modify, and
        // treating them as protection on their own would put an app on the Battery page with no rule to
        // show against it.
        var rule = new AppRule
        {
            Power = new PowerSettings { Profile = null, ReducePriority = true, EcoQos = true },
        };

        Assert.False(rule.HasAnyProtection);
    }

    [Fact]
    public void Touch_moves_the_updated_timestamp_without_touching_created()
    {
        var rule = new AppRule { UpdatedUtc = DateTimeOffset.UtcNow.AddHours(-1) };
        var created = rule.CreatedUtc;
        var before = rule.UpdatedUtc;

        rule.Touch();

        Assert.True(rule.UpdatedUtc > before);
        Assert.Equal(created, rule.CreatedUtc);
    }
}

/// <summary>Temporary unlock bounds. FR-305, SC-04.</summary>
public sealed class LockSettingsTests
{
    [Fact]
    public void The_default_duration_is_the_one_the_requirement_names()
    {
        Assert.Equal(300, LockSettings.DefaultTempUnlockSeconds);
        Assert.Equal(LockSettings.DefaultTempUnlockSeconds, new LockSettings().TempUnlockSeconds);

        // SC-04: the SRS writes this as 5 minutes and the API Design as 300 seconds. ADR-009 keeps
        // seconds, so the constant is the place that has to agree with the requirement.
        Assert.Equal(60, LockSettings.MinTempUnlockSeconds);
        Assert.Equal(3600, LockSettings.MaxTempUnlockSeconds);
    }

    [Theory]
    [InlineData(0, 60)]
    [InlineData(-1, 60)]
    [InlineData(59, 60)]
    [InlineData(60, 60)]
    [InlineData(300, 300)]
    [InlineData(3600, 3600)]
    [InlineData(3601, 3600)]
    [InlineData(int.MaxValue, 3600)]
    public void Normalize_clamps_rather_than_rejecting(int given, int expected)
    {
        var settings = new LockSettings { TempUnlockSeconds = given };

        settings.Normalize();

        // Clamping, not throwing: this runs on the service's write path against a value that came over
        // IPC, and a hostile or stale client sending 0 must not be able to produce an unlock that never
        // expires — nor should a validation exception drop a rule the user just created.
        Assert.Equal(expected, settings.TempUnlockSeconds);
    }

    [Fact]
    public void Normalize_is_idempotent()
    {
        var settings = new LockSettings { TempUnlockSeconds = 99_999 };

        settings.Normalize();
        var once = settings.TempUnlockSeconds;
        settings.Normalize();

        Assert.Equal(once, settings.TempUnlockSeconds);
    }

    [Fact]
    public void The_default_mode_is_a_fixed_duration()
    {
        // SC-04 again: "until app close" is representable but unenforced pending the Appendix C ruling,
        // so it must not be the default a new rule silently gets.
        Assert.Equal(TempUnlockMode.Duration, new LockSettings().TempUnlockMode);
    }
}

/// <summary>Policy document lookups and the global pause. FR-605, FR-806.</summary>
public sealed class PolicyDocumentTests
{
    private static AppRule RuleFor(string path, bool lockEnabled = true, bool enabled = true) =>
        new()
        {
            Identity = new AppIdentity { ExecutablePath = path, DisplayName = path }.WithDerivedId(),
            Lock = new LockSettings { Enabled = lockEnabled },
            Enabled = enabled,
        };

    [Fact]
    public void A_new_document_carries_the_current_schema_version_and_no_rules()
    {
        var document = new PolicyDocument();

        Assert.Equal(PolicyDocument.CurrentSchemaVersion, document.SchemaVersion);
        Assert.Empty(document.Rules);
        Assert.False(document.ProtectionPaused);
    }

    [Fact]
    public void FindByAppId_matches_case_insensitively_and_returns_null_when_absent()
    {
        var rule = RuleFor(@"C:\Apps\a.exe");
        var document = new PolicyDocument { Rules = { rule } };

        Assert.Same(rule, document.FindByAppId(rule.Identity.AppId));
        Assert.Same(rule, document.FindByAppId(rule.Identity.AppId.ToUpperInvariant()));
        Assert.Null(document.FindByAppId("sha256:" + new string('0', 64)));
    }

    [Fact]
    public void FindByRuleId_matches_case_insensitively_and_returns_null_when_absent()
    {
        var rule = RuleFor(@"C:\Apps\a.exe");
        var document = new PolicyDocument { Rules = { rule } };

        Assert.Same(rule, document.FindByRuleId(rule.RuleId));
        Assert.Same(rule, document.FindByRuleId(rule.RuleId.ToUpperInvariant()));
        Assert.Null(document.FindByRuleId(Guid.NewGuid().ToString()));
    }

    [Fact]
    public void ActiveRules_omits_rules_that_ask_for_nothing()
    {
        var enforced = RuleFor(@"C:\Apps\a.exe");
        var document = new PolicyDocument
        {
            Rules =
            {
                enforced,
                RuleFor(@"C:\Apps\b.exe", lockEnabled: false),
                RuleFor(@"C:\Apps\c.exe", enabled: false),
            },
        };

        Assert.Same(enforced, Assert.Single(document.ActiveRules));
    }

    [Fact]
    public void ActiveRules_is_empty_while_paused_but_the_rules_are_still_there()
    {
        var document = new PolicyDocument
        {
            ProtectionPaused = true,
            Rules = { RuleFor(@"C:\Apps\a.exe"), RuleFor(@"C:\Apps\b.exe") },
        };

        // FR-806. Pausing is one check in one place; if it were a condition in each enforcer, a new
        // enforcer added later would silently keep running through a pause.
        Assert.Empty(document.ActiveRules);
        Assert.Equal(2, document.Rules.Count);

        document.ProtectionPaused = false;
        Assert.Equal(2, document.ActiveRules.Count());
    }
}
