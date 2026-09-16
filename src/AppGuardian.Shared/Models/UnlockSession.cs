namespace AppGuardian.Shared.Models;

/// <summary>
/// An active temporary unlock. API Design §5; SRS FR-305, FR-308.
/// </summary>
/// <remarks>
/// Sessions are owned solely by the Service (ADR/SC-10) so there is one authority on whether an
/// app is currently unlocked, even though Windows Hello verification happens in the Agent.
/// <para>
/// Sessions are held in memory only and are deliberately not persisted: SRS Appendix C question 2
/// asks whether a temporary unlock should survive a process restart, and the safer default while
/// that is unanswered is that a restart re-locks. Persisting them would be the less safe choice to
/// undo later.
/// </para>
/// </remarks>
public sealed class UnlockSession
{
    public string AppId { get; set; } = string.Empty;

    /// <summary>Null when <see cref="Mode"/> is <see cref="TempUnlockMode.UntilAppClose"/>.</summary>
    public DateTimeOffset? ExpiresUtc { get; set; }

    public TempUnlockMode Mode { get; set; } = TempUnlockMode.Duration;

    /// <summary>
    /// The process this session was granted for, when <see cref="Mode"/> is
    /// <see cref="TempUnlockMode.UntilAppClose"/>. The session ends when this PID exits.
    /// </summary>
    public int? ProcessId { get; set; }

    public DateTimeOffset GrantedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>How the user proved identity for this session. Recorded for the audit log.</summary>
    public AuthMethod Method { get; set; } = AuthMethod.Pin;

    /// <summary>
    /// True when the session is still valid at <paramref name="now"/>.
    /// An until-app-close session is checked against process liveness by the caller, since this
    /// assembly is platform-neutral and cannot query processes.
    /// </summary>
    public bool IsActive(DateTimeOffset now) => Mode switch
    {
        TempUnlockMode.Duration => ExpiresUtc is not null && ExpiresUtc > now,
        TempUnlockMode.UntilAppClose => true,
        _ => false,
    };

    public TimeSpan? Remaining(DateTimeOffset now) =>
        ExpiresUtc is null ? null : ExpiresUtc.Value - now;

    public static UnlockSession ForDuration(string appId, int seconds, AuthMethod method) =>
        new()
        {
            AppId = appId,
            Mode = TempUnlockMode.Duration,
            ExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(seconds),
            Method = method,
        };

    public static UnlockSession UntilClose(string appId, int processId, AuthMethod method) =>
        new()
        {
            AppId = appId,
            Mode = TempUnlockMode.UntilAppClose,
            ProcessId = processId,
            ExpiresUtc = null,
            Method = method,
        };
}

/// <summary>Authentication method. SRS §9.2 AuthConfig, FR-301/302.</summary>
public enum AuthMethod
{
    WindowsHello = 0,
    Pin = 1,
    Password = 2,
}
