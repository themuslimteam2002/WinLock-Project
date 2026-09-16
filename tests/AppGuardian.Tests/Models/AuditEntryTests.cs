using AppGuardian.Shared.Models;

namespace AppGuardian.Tests.Models;

/// <summary>Audit record construction. FR-508, FR-803, SC-11.</summary>
public sealed class AuditEntryTests
{
    [Fact]
    public void Create_sets_every_field_it_is_given()
    {
        var entry = AuditEntry.Create(
            AuditActor.Agent,
            AuditAction.Unlock,
            AuditResult.Ok,
            appId: "sha256:abc",
            ruleId: "rule-1",
            detail: "Unlocked after Windows Hello.");

        Assert.Equal(AuditActor.Agent, entry.Actor);
        Assert.Equal(AuditAction.Unlock, entry.Action);
        Assert.Equal(AuditResult.Ok, entry.Result);
        Assert.Equal("sha256:abc", entry.AppId);
        Assert.Equal("rule-1", entry.RuleId);
        Assert.Equal("Unlocked after Windows Hello.", entry.Detail);
    }

    [Fact]
    public void Create_stamps_the_time_and_leaves_the_buffered_marker_unset()
    {
        var before = DateTimeOffset.UtcNow;

        var entry = AuditEntry.Create(AuditActor.Service, AuditAction.Lock, AuditResult.Ok);

        Assert.InRange(entry.Utc, before, DateTimeOffset.UtcNow);

        // BufferedUtc is set only by the flush path on reconnect. An entry written directly by the
        // service has a trustworthy timestamp, and marking it otherwise would weaken every row the
        // Activity page shows.
        Assert.Null(entry.BufferedUtc);
    }

    [Fact]
    public void Ids_are_unique_and_optional_fields_default_to_null()
    {
        var first = AuditEntry.Create(AuditActor.Service, AuditAction.Hide, AuditResult.Ok);
        var second = AuditEntry.Create(AuditActor.Service, AuditAction.Hide, AuditResult.Ok);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Null(first.AppId);
        Assert.Null(first.RuleId);
        Assert.Equal(string.Empty, first.Detail);
    }

    [Fact]
    public void A_bare_entry_defaults_to_the_service_acting_successfully()
    {
        var entry = new AuditEntry();

        // The defaults matter because the service writes the majority of entries. A default of User
        // would attribute automatic enforcement to the person, which is the one thing an audit log
        // must not get wrong.
        Assert.Equal(AuditActor.Service, entry.Actor);
        Assert.Equal(AuditResult.Ok, entry.Result);
    }

    [Fact]
    public void Every_action_and_result_in_both_specs_is_representable()
    {
        // SC-11: the SRS and API Design list different action and result vocabularies. These enums are
        // the union, so this asserts the union is complete rather than that a particular name exists —
        // a member dropped later would take a spec requirement with it.
        Assert.Equal(9, Enum.GetValues<AuditAction>().Length);
        Assert.Equal(3, Enum.GetValues<AuditResult>().Length);
        Assert.Equal(3, Enum.GetValues<AuditActor>().Length);
    }

    [Fact]
    public void Denied_and_Error_are_distinct_outcomes()
    {
        var denied = AuditEntry.Create(AuditActor.User, AuditAction.AuthFail, AuditResult.Denied);
        var failed = AuditEntry.Create(AuditActor.Service, AuditAction.ApplyPower, AuditResult.Error);

        // A refusal is a working control; an error is a broken one. Collapsing them would make a service
        // that cannot apply a CPU cap look identical to one that deliberately declined to.
        Assert.NotEqual(denied.Result, failed.Result);
    }
}
