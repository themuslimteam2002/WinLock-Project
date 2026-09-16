namespace AppGuardian.Shared.Models;

/// <summary>Who performed an audited action.</summary>
/// <remarks>
/// SPEC CONFLICT SC-11: SRS §9.2 has three values (<c>user</c>/<c>service</c>/<c>agent</c>);
/// API Design §5 has two (<c>user</c>/<c>system</c>). We keep the SRS's three-way split inside the
/// API Design field names (ADR-009) — knowing which component acted is genuinely useful when
/// diagnosing an unexpected enforcement, and collapsing service and agent loses that.
/// </remarks>
public enum AuditActor
{
    User = 0,
    Service = 1,
    Agent = 2,
}

/// <summary>Audited action. Union of both spec lists (SC-11).</summary>
public enum AuditAction
{
    Lock = 0,
    Unlock = 1,
    Hide = 2,
    Reveal = 3,

    /// <summary>SRS calls this <c>applyPower</c>, API Design calls it <c>throttle</c>.</summary>
    ApplyPower = 4,

    RemovePower = 5,
    AuthFail = 6,

    /// <summary>Settings or rule change. Present in the SRS list, absent from API Design.</summary>
    ConfigChange = 7,

    /// <summary>An operation was refused because the target cannot be controlled (FR-509).</summary>
    Unsupported = 8,
}

/// <summary>Outcome of an audited action.</summary>
/// <remarks>
/// SC-11: SRS uses <c>success|failure|blocked</c>, API Design uses <c>ok|denied|error</c>. These
/// are the same three shapes with different names; we keep the SRS semantics under neutral names.
/// </remarks>
public enum AuditResult
{
    /// <summary>The action completed.</summary>
    Ok = 0,

    /// <summary>The action was refused deliberately — no auth, rate limited, or paused.</summary>
    Denied = 1,

    /// <summary>The action was attempted and failed unexpectedly.</summary>
    Error = 2,
}

/// <summary>
/// One append-only audit record. API Design §5; SRS FR-508, FR-803, §9.2.
/// </summary>
/// <remarks>
/// Written only by the Service (ADR-005): the log lives under %ProgramData% with admin-only write,
/// so a user-privilege agent cannot append directly, and loosening that ACL would let any
/// user-level process forge or truncate entries — defeating the repudiation mitigation in SRS §11.
/// The agent emits <c>system.audit</c> over IPC instead.
/// <para>
/// Both <see cref="RuleId"/> and <see cref="AppId"/> are recorded. SC-11 notes that the two specs
/// disagree on which to use; they answer different questions (which rule fired, versus which app
/// was affected) and both are cheap.
/// </para>
/// </remarks>
public sealed class AuditEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// When the event occurred, not when it was written. During a service outage the agent buffers
    /// entries and flushes on reconnect, so these can differ — see <see cref="BufferedUtc"/>.
    /// </summary>
    public DateTimeOffset Utc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Set when the entry was buffered by a client during a service outage and flushed later.
    /// Its presence marks the entry as having a less trustworthy timestamp (ADR-005).
    /// </summary>
    public DateTimeOffset? BufferedUtc { get; set; }

    public AuditActor Actor { get; set; } = AuditActor.Service;

    public AuditAction Action { get; set; }

    public AuditResult Result { get; set; } = AuditResult.Ok;

    public string? AppId { get; set; }

    public string? RuleId { get; set; }

    /// <summary>
    /// Human-readable detail. Must never contain a credential, a hash, or a stack trace —
    /// this file is readable by all users (SRS §9.1 ACL: Users read).
    /// </summary>
    public string Detail { get; set; } = string.Empty;

    public static AuditEntry Create(
        AuditActor actor,
        AuditAction action,
        AuditResult result,
        string? appId = null,
        string? ruleId = null,
        string detail = "") =>
        new()
        {
            Actor = actor,
            Action = action,
            Result = result,
            AppId = appId,
            RuleId = ruleId,
            Detail = detail,
        };
}
