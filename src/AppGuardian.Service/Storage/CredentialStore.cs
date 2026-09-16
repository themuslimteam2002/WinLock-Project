using AppGuardian.Shared.Contracts;
using AppGuardian.Shared.Models;
using AppGuardian.Shared.Security;
using AppGuardian.Shared.Storage;
using Microsoft.Extensions.Logging;

namespace AppGuardian.Service.Storage;

/// <summary>
/// Persisted credential set, keyed by user SID. SRS FR-300..308, §9.2.
/// </summary>
/// <remarks>
/// Verification happens here, in the LocalSystem process, and never in the UI or agent. That is the
/// point of the split: a non-elevated process that could see the hash could attack it offline at its
/// leisure, and a process that decided the answer itself could simply be patched to return true.
/// <para>
/// The plaintext secret crosses the pipe once per attempt and is never logged, never persisted, and
/// never echoed back in a response (TC-03).
/// </para>
/// </remarks>
public sealed class CredentialStore
{
    private readonly AtomicJsonStore<CredentialFile> _store;
    private readonly IKeyDerivation _kdf;
    private readonly ILogger<CredentialStore> _log;
    private readonly SemaphoreSlim _mutate = new(1, 1);
    private readonly TimeProvider _clock;

    private CredentialFile _file = new();

    public CredentialStore(
        IKeyDerivation kdf,
        ILogger<CredentialStore> log,
        string? path = null,
        TimeProvider? clock = null)
    {
        _kdf = kdf;
        _log = log;
        _clock = clock ?? TimeProvider.System;
        _store = new AtomicJsonStore<CredentialFile>(path ?? StoragePaths.CredentialsFile);
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        var result = await _store.LoadAsync(ct).ConfigureAwait(false);
        _file = result.Value;

        if (result.IsFailure)
        {
            // An unreadable credential file cannot be recovered from — the hash is gone. The user
            // must re-run setup, which is why auth.reset exists (FR-307) and why it is
            // administrator-gated: it is the documented recovery path, not a bypass.
            _log.LogError(
                "Credential store could not be read: {Reason}. Authentication will report unconfigured.",
                result.Error);
        }

        _log.LogInformation("Credential store holds {Count} record(s).", _file.Records.Count);
    }

    public bool IsConfiguredFor(string userSid) => Find(userSid) is not null;

    /// <summary>Status for <c>auth.getStatus</c>. FR-300, FR-308.</summary>
    public AuthStatus StatusFor(string userSid, bool helloAvailable)
    {
        var record = Find(userSid);
        var now = _clock.GetUtcNow();

        if (record is null)
        {
            return new AuthStatus
            {
                IsConfigured = false,
                WindowsHelloAvailable = helloAvailable,
                AttemptsRemaining = PolicyConstants.MaxAuthFailures,
            };
        }

        return new AuthStatus
        {
            IsConfigured = true,
            PreferredMethod = record.PreferredMethod,
            WindowsHelloAvailable = helloAvailable,
            FallbackConfigured = record.FallbackConfigured,
            IsLockedOut = record.IsLockedOut(now),
            LockedUntilUtc = record.IsLockedOut(now) ? record.LockedUntilUtc : null,
            FailedAttempts = record.FailCount,
            AttemptsRemaining = AuthBackoff.AttemptsRemaining(record.FailCount),
        };
    }

    /// <summary>
    /// First-run setup, or a change of the fallback secret. FR-300, FR-302.
    /// </summary>
    /// <remarks>
    /// Changing an existing secret requires the caller to have already authenticated — enforced by
    /// the dispatcher, not here, so that this method has one job. A setup that overwrote an existing
    /// credential without that check would be a complete auth bypass.
    /// </remarks>
    public async Task SetupAsync(
        string userSid,
        string secret,
        AuthMethod preferredMethod,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(secret))
        {
            throw new ArgumentException("A fallback secret is required.", nameof(secret));
        }

        await _mutate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            var record = Find(userSid);

            if (record is null)
            {
                record = new CredentialRecord { UserSid = userSid };
                _file.Records.Add(record);
            }

            record.Hash = _kdf.Hash(secret);
            record.PreferredMethod = preferredMethod;
            record.FallbackConfigured = true;

            // A successful setup clears any lockout: the user proved control of the account through
            // whatever path got them here, so keeping a stale backoff would only punish them.
            record.FailCount = 0;
            record.LockedUntilUtc = null;
            record.UpdatedUtc = _clock.GetUtcNow();

            await _store.SaveAsync(_file, ct).ConfigureAwait(false);

            _log.LogInformation(
                "Credential configured for {Sid} using {Algo}, preferred method {Method}.",
                userSid,
                record.Hash.Algo,
                preferredMethod);
        }
        finally
        {
            _mutate.Release();
        }
    }

    /// <summary>
    /// Verifies a PIN or password, applying and persisting the FR-306 backoff.
    /// </summary>
    /// <remarks>
    /// Serialized through the same mutation gate as writes, so concurrent attempts cannot each read a
    /// stale fail count and race past the threshold — which would turn a 5-attempt limit into an
    /// unbounded one for an attacker willing to send requests in parallel.
    /// </remarks>
    public async Task<AuthVerifyResult> VerifyAsync(string userSid, string secret, CancellationToken ct)
    {
        await _mutate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            var now = _clock.GetUtcNow();
            var record = Find(userSid);

            if (record is null)
            {
                return new AuthVerifyResult
                {
                    Verified = false,
                    // Not "no credential exists for this user" — that is a fact about the machine
                    // that an unauthenticated caller does not need. It is also indistinguishable in
                    // practice, since the dashboard already knows from auth.getStatus.
                    Reason = "Authentication is not configured.",
                    AttemptsRemaining = PolicyConstants.MaxAuthFailures,
                };
            }

            if (record.IsLockedOut(now))
            {
                var wait = record.LockedUntilUtc!.Value - now;

                return new AuthVerifyResult
                {
                    Verified = false,
                    LockedOut = true,
                    LockedUntilUtc = record.LockedUntilUtc,
                    Reason = $"Too many failed attempts. Try again in {Describe(wait)}.",
                    AttemptsRemaining = 0,
                };
            }

            var ok = _kdf.Verify(secret, record.Hash);

            if (ok)
            {
                var rehashed = false;

                if (_kdf.NeedsRehash(record.Hash))
                {
                    // Upgrade in place while we hold the plaintext. This is the only moment it is
                    // available, and it is what makes the ADR-003 PBKDF2-to-Argon2id migration
                    // invisible to the user rather than a forced reset.
                    record.Hash = _kdf.Hash(secret);
                    rehashed = true;
                }

                record.FailCount = 0;
                record.LockedUntilUtc = null;
                record.UpdatedUtc = now;

                await _store.SaveAsync(_file, ct).ConfigureAwait(false);

                if (rehashed)
                {
                    _log.LogInformation("Credential for {Sid} rehashed to {Algo}.", userSid, record.Hash.Algo);
                }

                return new AuthVerifyResult
                {
                    Verified = true,
                    AttemptsRemaining = PolicyConstants.MaxAuthFailures,
                };
            }

            record.FailCount++;
            record.UpdatedUtc = now;

            var backoff = AuthBackoff.ComputeBackoff(record.FailCount);

            if (backoff is not null)
            {
                record.LockedUntilUtc = now + backoff.Value;

                _log.LogWarning(
                    "Authentication locked out for {Sid} until {Until} after {Count} consecutive failures.",
                    userSid,
                    record.LockedUntilUtc,
                    record.FailCount);
            }

            // Persisted on every failure, deliberately: an in-memory-only counter would reset if the
            // attacker could restart the service, and restarting a service is something an
            // administrator can do — but an administrator is already outside this threat model
            // (SRS §11), whereas a crash is not.
            await _store.SaveAsync(_file, ct).ConfigureAwait(false);

            return new AuthVerifyResult
            {
                Verified = false,
                LockedOut = backoff is not null,
                LockedUntilUtc = record.LockedUntilUtc,
                Reason = backoff is not null
                    ? $"Too many failed attempts. Try again in {Describe(backoff.Value)}."
                    : "That PIN or password is not correct.",
                AttemptsRemaining = AuthBackoff.AttemptsRemaining(record.FailCount),
            };
        }
        finally
        {
            _mutate.Release();
        }
    }

    /// <summary>
    /// Clears the failure counter after a successful Windows Hello verification.
    /// </summary>
    /// <remarks>
    /// Hello is verified by the agent (only an interactive process can show the prompt), so the
    /// service never sees a secret for it and cannot verify it itself. That asymmetry is worth being
    /// explicit about: the service trusts the agent's assertion here, and the pipe DACL is what makes
    /// that trust reasonable. See SC-10, which mislabels this message's direction.
    /// </remarks>
    public async Task NoteExternalSuccessAsync(string userSid, CancellationToken ct)
    {
        await _mutate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            var record = Find(userSid);

            if (record is null || (record.FailCount == 0 && record.LockedUntilUtc is null))
            {
                return;
            }

            record.FailCount = 0;
            record.LockedUntilUtc = null;
            record.UpdatedUtc = _clock.GetUtcNow();

            await _store.SaveAsync(_file, ct).ConfigureAwait(false);
        }
        finally
        {
            _mutate.Release();
        }
    }

    /// <summary>
    /// Records a failed Windows Hello attempt so Hello and PIN share one lockout counter.
    /// </summary>
    /// <remarks>
    /// Sharing the counter matters: two independent counters would let an attacker get 5 free
    /// attempts at each, and Hello failures are the cheaper of the two to generate.
    /// </remarks>
    public async Task NoteExternalFailureAsync(string userSid, CancellationToken ct)
    {
        await _mutate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            var record = Find(userSid);

            if (record is null)
            {
                return;
            }

            var now = _clock.GetUtcNow();

            record.FailCount++;
            record.UpdatedUtc = now;

            var backoff = AuthBackoff.ComputeBackoff(record.FailCount);

            if (backoff is not null)
            {
                record.LockedUntilUtc = now + backoff.Value;
            }

            await _store.SaveAsync(_file, ct).ConfigureAwait(false);
        }
        finally
        {
            _mutate.Release();
        }
    }

    /// <summary>
    /// Destroys the credential for a user. FR-307. Administrator-gated by the dispatcher.
    /// </summary>
    /// <remarks>
    /// This is a genuine bypass of the user's protection and is documented as such: an administrator
    /// can already stop the service or edit the policy file, so refusing to provide a recovery path
    /// would remove nothing from an attacker and would strand a user who forgot their PIN. It is
    /// audited (FR-807) so the user can see it happened.
    /// </remarks>
    public async Task<bool> ResetAsync(string userSid, CancellationToken ct)
    {
        await _mutate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            var record = Find(userSid);

            if (record is null)
            {
                return false;
            }

            _file.Records.Remove(record);

            await _store.SaveAsync(_file, ct).ConfigureAwait(false);

            _log.LogWarning("Credential for {Sid} was reset by an administrator.", userSid);

            return true;
        }
        finally
        {
            _mutate.Release();
        }
    }

    private CredentialRecord? Find(string userSid) =>
        _file.Records.FirstOrDefault(r =>
            string.Equals(r.UserSid, userSid, StringComparison.OrdinalIgnoreCase));

    private static string Describe(TimeSpan wait) =>
        wait.TotalMinutes >= 1
            ? $"{Math.Ceiling(wait.TotalMinutes):0} minute(s)"
            : $"{Math.Ceiling(wait.TotalSeconds):0} second(s)";
}

/// <summary>
/// On-disk shape of <c>credentials.dat</c>: a keyed set rather than a single record (SC-12).
/// </summary>
/// <remarks>
/// The <c>.dat</c> extension is the SRS's choice; the content is JSON. It is not encrypted, because
/// it holds only salted hashes — encrypting it would need a key readable by LocalSystem, and anyone
/// who can read that key can read the file.
/// </remarks>
public sealed class CredentialFile
{
    public int SchemaVersion { get; set; } = 1;

    public List<CredentialRecord> Records { get; set; } = new();
}
