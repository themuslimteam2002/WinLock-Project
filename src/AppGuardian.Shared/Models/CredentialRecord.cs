namespace AppGuardian.Shared.Models;

/// <summary>
/// Stored credential record. SRS §9.2 AuthConfig, FR-303/304, NFR-S1.
/// </summary>
/// <remarks>
/// Contains only a salted hash. No reversible secret is ever persisted, which is what TC-03
/// inspects for.
/// <para>
/// SPEC CONFLICT SC-12: SRS §9.1 puts credentials in a machine-wide file, but FR-300 configures
/// auth per user and §2.2 lists multi-user support as out of scope — a single shared PIN for every
/// user of the machine is not what FR-300 describes. Records are therefore keyed by
/// <see cref="UserSid"/> inside the machine-wide file, which keeps the admin-only-write ACL that
/// NFR-S3 wants while making the credential per-user.
/// </para>
/// </remarks>
public sealed class CredentialRecord
{
    /// <summary>Windows SID of the owning user. The key within the credential store.</summary>
    public string UserSid { get; set; } = string.Empty;

    /// <summary>The user's preferred method. A fallback PIN/password is always also configured (FR-300).</summary>
    public AuthMethod PreferredMethod { get; set; } = AuthMethod.Pin;

    /// <summary>FR-300 requires a fallback to exist before the app is usable.</summary>
    public bool FallbackConfigured { get; set; }

    public HashRecord Hash { get; set; } = new();

    /// <summary>Consecutive failures since the last success. Drives FR-306 backoff.</summary>
    public int FailCount { get; set; }

    /// <summary>Set while a backoff is in force (FR-306). Null when not locked out.</summary>
    public DateTimeOffset? LockedUntilUtc { get; set; }

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>True when a backoff is currently in force.</summary>
    public bool IsLockedOut(DateTimeOffset now) =>
        LockedUntilUtc is not null && LockedUntilUtc > now;
}

/// <summary>
/// A salted password hash plus the parameters needed to verify it.
/// </summary>
/// <remarks>
/// The algorithm name and its parameters are persisted per record (ADR-003) so a future migration
/// to Argon2id can verify existing PBKDF2 credentials and rehash on the next successful unlock,
/// without forcing every user to reset their PIN.
/// </remarks>
public sealed class HashRecord
{
    /// <summary>Algorithm identifier, e.g. <c>PBKDF2-HMAC-SHA256</c> or <c>Argon2id</c>.</summary>
    public string Algo { get; set; } = string.Empty;

    /// <summary>Base64 per-credential random salt. FR-303 requires this to be unique per credential.</summary>
    public string Salt { get; set; } = string.Empty;

    /// <summary>Base64 derived key.</summary>
    public string Digest { get; set; } = string.Empty;

    /// <summary>
    /// Algorithm parameters. For PBKDF2: <c>iterations</c>. For Argon2id: <c>memoryKiB</c>,
    /// <c>iterations</c>, <c>parallelism</c>.
    /// </summary>
    public Dictionary<string, int> Params { get; set; } = new();
}

/// <summary>Reply payload for <c>auth.getStatus</c>. FR-300, FR-308.</summary>
public sealed class AuthStatus
{
    /// <summary>False on first run, which makes onboarding mandatory (FR-300).</summary>
    public bool IsConfigured { get; set; }

    public AuthMethod PreferredMethod { get; set; }

    public bool WindowsHelloAvailable { get; set; }

    public bool FallbackConfigured { get; set; }

    public bool IsLockedOut { get; set; }

    /// <summary>When a backoff expires. Shown to the user rather than a bare refusal (NFR §7.5).</summary>
    public DateTimeOffset? LockedUntilUtc { get; set; }

    public int FailedAttempts { get; set; }

    public int AttemptsRemaining { get; set; }

    /// <summary>App IDs with a currently active unlock session.</summary>
    public List<string> ActiveUnlockedAppIds { get; set; } = new();
}
