using System.Security.Cryptography;
using System.Text;
using AppGuardian.Shared.Contracts;
using AppGuardian.Shared.Models;

namespace AppGuardian.Shared.Security;

/// <summary>
/// PBKDF2-HMAC-SHA256 key derivation. FR-303, FR-304, NFR-S1.
/// </summary>
/// <remarks>
/// ADR-003 (provisional, see SPEC_CONFLICTS SC-07): the SRS Appendix C default is Argon2id, but
/// .NET 8 has no built-in Argon2 and adding one means a third-party package, usually with a native
/// dependency — awkward against NFR-S5, SRS §7.8's reproducible-build requirement, and the
/// AV-flagging risk. PBKDF2 at 600,000 iterations uses only the BCL and is six times FR-303's
/// stated floor.
/// <para>
/// The algorithm name and iteration count are persisted per record, so a later Argon2id
/// implementation can verify existing hashes and rehash on the next successful unlock instead of
/// forcing a reset.
/// </para>
/// </remarks>
public sealed class Pbkdf2KeyDerivation : IKeyDerivation
{
    /// <summary>Current OWASP guidance for PBKDF2-HMAC-SHA256. FR-303 requires at least 100,000.</summary>
    public const int DefaultIterations = 600_000;

    public const int SaltBytes = 16;
    public const int DigestBytes = 32;

    private const string IterationsKey = "iterations";

    private readonly int _iterations;

    public Pbkdf2KeyDerivation(int iterations = DefaultIterations)
    {
        if (iterations < 100_000)
        {
            // FR-303 sets a hard floor; refuse rather than silently weaken.
            throw new ArgumentOutOfRangeException(
                nameof(iterations), iterations, "FR-303 requires at least 100,000 iterations.");
        }

        _iterations = iterations;
    }

    public string AlgorithmName => "PBKDF2-HMAC-SHA256";

    public HashRecord Hash(string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var digest = Derive(secret, salt, _iterations);

        return new HashRecord
        {
            Algo = AlgorithmName,
            Salt = Convert.ToBase64String(salt),
            Digest = Convert.ToBase64String(digest),
            Params = new Dictionary<string, int> { [IterationsKey] = _iterations },
        };
    }

    public bool Verify(string secret, HashRecord record)
    {
        if (string.IsNullOrEmpty(secret) ||
            !string.Equals(record.Algo, AlgorithmName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(record.Salt);
            expected = Convert.FromBase64String(record.Digest);
        }
        catch (FormatException)
        {
            return false;
        }

        if (salt.Length == 0 || expected.Length == 0)
        {
            return false;
        }

        // Use the iteration count from the record, not the current default, so hashes written
        // before a defaults change stay verifiable.
        var iterations = record.Params.TryGetValue(IterationsKey, out var stored) && stored > 0
            ? stored
            : DefaultIterations;

        var actual = Derive(secret, salt, iterations, expected.Length);

        // Constant-time: a timing-variable comparison would leak the digest a byte at a time.
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public bool NeedsRehash(HashRecord record)
    {
        if (!string.Equals(record.Algo, AlgorithmName, StringComparison.OrdinalIgnoreCase))
        {
            // A different algorithm — a future Argon2id record read by this implementation, or a
            // legacy one. Either way the caller should rehash with the current algorithm.
            return true;
        }

        return !record.Params.TryGetValue(IterationsKey, out var iterations) || iterations < _iterations;
    }

    private static byte[] Derive(string secret, byte[] salt, int iterations, int length = DigestBytes) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(secret),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            length);
}

/// <summary>
/// Escalating backoff after failed authentication. FR-306.
/// </summary>
/// <remarks>
/// Pure and side-effect free so the escalation curve can be unit tested without a clock or a
/// credential file — the requirement is stated in terms of counts and durations, which is exactly
/// what this takes and returns.
/// </remarks>
public static class AuthBackoff
{
    /// <summary>
    /// Backoff to apply given a consecutive-failure count, or null when the threshold is not yet
    /// reached. Doubles on each trip past the threshold, capped so a user is never locked out
    /// indefinitely.
    /// </summary>
    public static TimeSpan? ComputeBackoff(
        int consecutiveFailures,
        int threshold = PolicyConstants.MaxAuthFailures)
    {
        if (consecutiveFailures < threshold)
        {
            return null;
        }

        // Trip 1 at the threshold, trip 2 one failure later, and so on.
        var trip = consecutiveFailures - threshold;
        var multiplier = Math.Pow(2, Math.Min(trip, 10));
        var backoff = PolicyConstants.BaseAuthBackoff * multiplier;

        return backoff > PolicyConstants.MaxAuthBackoff ? PolicyConstants.MaxAuthBackoff : backoff;
    }

    /// <summary>Attempts left before the next backoff. Shown to the user rather than a bare refusal.</summary>
    public static int AttemptsRemaining(
        int consecutiveFailures,
        int threshold = PolicyConstants.MaxAuthFailures) =>
        Math.Max(0, threshold - consecutiveFailures);
}
