using AppGuardian.Shared.Contracts;
using AppGuardian.Shared.Models;
using AppGuardian.Shared.Security;

namespace AppGuardian.Tests.Security;

/// <summary>
/// Key derivation and lockout behaviour. FR-303, FR-304, FR-306, NFR-S1.
/// </summary>
/// <remarks>
/// These run on the portable TFM: nothing here touches DPAPI or the file system, because the properties
/// worth testing — a salt that differs per hash, a verify that survives an iteration-count change, a backoff
/// that escalates and then stops — are all pure.
/// <para>
/// Every test constructs the derivation with the 100,000-iteration floor rather than the 600,000 default.
/// At the default each Hash call costs a noticeable fraction of a second, and a suite that derives thirty
/// keys would take long enough that developers would stop running it.
/// </para>
/// </remarks>
public sealed class Pbkdf2KeyDerivationTests
{
    private const int FastIterations = 100_000;

    private static Pbkdf2KeyDerivation Subject() => new(FastIterations);

    [Fact]
    public void Hash_records_the_algorithm_and_iteration_count()
    {
        var record = Subject().Hash("correct horse");

        Assert.Equal("PBKDF2-HMAC-SHA256", record.Algo);
        Assert.Equal(FastIterations, record.Params["iterations"]);

        // Persisted per record so a later algorithm change can verify existing hashes rather than
        // forcing every user to reset. ADR-003.
        Assert.NotEmpty(record.Salt);
        Assert.NotEmpty(record.Digest);
    }

    [Fact]
    public void Hash_uses_a_fresh_salt_each_time()
    {
        var subject = Subject();

        var first = subject.Hash("same secret");
        var second = subject.Hash("same secret");

        // Equal digests for equal secrets would mean a shared salt, which makes a precomputation attack
        // across machines worthwhile.
        Assert.NotEqual(first.Salt, second.Salt);
        Assert.NotEqual(first.Digest, second.Digest);
    }

    [Fact]
    public void Verify_accepts_the_original_secret()
    {
        var subject = Subject();
        var record = subject.Hash("1234-secret");

        Assert.True(subject.Verify("1234-secret", record));
    }

    [Theory]
    [InlineData("1234-Secret")]      // case
    [InlineData("1234-secret ")]     // trailing space
    [InlineData("1234-secre")]       // truncation
    [InlineData("")]                 // empty
    public void Verify_rejects_anything_else(string attempt)
    {
        var subject = Subject();
        var record = subject.Hash("1234-secret");

        Assert.False(subject.Verify(attempt, record));
    }

    [Fact]
    public void Verify_uses_the_iteration_count_from_the_record()
    {
        // Hashed at the floor, verified by an instance configured for far more. The stored count has to
        // win, or raising the default in a later release would lock every existing user out.
        var record = new Pbkdf2KeyDerivation(FastIterations).Hash("portable");

        var laterDefault = new Pbkdf2KeyDerivation(FastIterations * 2);

        Assert.True(laterDefault.Verify("portable", record));
    }

    [Fact]
    public void Verify_rejects_a_record_from_another_algorithm()
    {
        var record = new HashRecord
        {
            Algo = "Argon2id",
            Salt = Convert.ToBase64String(new byte[16]),
            Digest = Convert.ToBase64String(new byte[32]),
            Params = new Dictionary<string, int>(),
        };

        // Refused rather than attempted. Deriving PBKDF2 over an Argon2id digest would fail anyway, but
        // failing on the algorithm name makes the reason legible.
        Assert.False(Subject().Verify("anything", record));
    }

    [Theory]
    [InlineData("!!!not base64!!!")]
    [InlineData("")]
    public void Verify_rejects_a_corrupt_record_without_throwing(string salt)
    {
        var record = new HashRecord
        {
            Algo = "PBKDF2-HMAC-SHA256",
            Salt = salt,
            Digest = Convert.ToBase64String(new byte[32]),
            Params = new Dictionary<string, int> { ["iterations"] = FastIterations },
        };

        // A truncated or hand-edited credential file must read as "wrong secret", not as an unhandled
        // exception on the service's auth path.
        Assert.False(Subject().Verify("anything", record));
    }

    [Fact]
    public void NeedsRehash_is_true_for_a_weaker_record()
    {
        var weak = new Pbkdf2KeyDerivation(FastIterations).Hash("secret");

        Assert.True(new Pbkdf2KeyDerivation(FastIterations * 2).NeedsRehash(weak));
        Assert.False(new Pbkdf2KeyDerivation(FastIterations).NeedsRehash(weak));
    }

    [Fact]
    public void NeedsRehash_is_true_for_another_algorithm()
    {
        var foreign = new HashRecord
        {
            Algo = "Argon2id",
            Salt = string.Empty,
            Digest = string.Empty,
            Params = new Dictionary<string, int>(),
        };

        Assert.True(Subject().NeedsRehash(foreign));
    }

    [Fact]
    public void Below_the_floor_is_refused_at_construction()
    {
        // FR-303 sets 100,000 as a hard floor. Refusing here means no code path can quietly configure a
        // weaker derivation from a config value.
        Assert.Throws<ArgumentOutOfRangeException>(() => new Pbkdf2KeyDerivation(99_999));
    }

    [Fact]
    public void Hashing_an_empty_secret_is_refused()
    {
        Assert.Throws<ArgumentException>(() => Subject().Hash(string.Empty));
    }
}

/// <summary>Escalating lockout after repeated failures. FR-306.</summary>
public sealed class AuthBackoffTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    public void No_backoff_before_the_threshold(int failures)
    {
        // Five is the threshold, so four failures still get a prompt. A user who mistypes twice should not
        // be waiting on a timer.
        Assert.Null(AuthBackoff.ComputeBackoff(failures, threshold: 5));
    }

    [Fact]
    public void The_first_backoff_is_the_base_delay()
    {
        var backoff = AuthBackoff.ComputeBackoff(5, threshold: 5);

        Assert.Equal(PolicyConstants.BaseAuthBackoff, backoff);
    }

    [Fact]
    public void Each_further_failure_doubles_the_delay()
    {
        var first = AuthBackoff.ComputeBackoff(5, threshold: 5)!.Value;
        var second = AuthBackoff.ComputeBackoff(6, threshold: 5)!.Value;
        var third = AuthBackoff.ComputeBackoff(7, threshold: 5)!.Value;

        Assert.Equal(first * 2, second);
        Assert.Equal(first * 4, third);
    }

    [Fact]
    public void The_delay_is_capped()
    {
        // Twenty failures past the threshold. Without the cap this would be the base delay times a
        // million, which is a permanent lockout of the machine's owner — a worse outcome than the guessing
        // it is meant to prevent.
        var backoff = AuthBackoff.ComputeBackoff(25, threshold: 5);

        Assert.Equal(PolicyConstants.MaxAuthBackoff, backoff);
    }

    [Fact]
    public void The_cap_is_never_exceeded_at_any_count()
    {
        for (var failures = 5; failures < 200; failures++)
        {
            var backoff = AuthBackoff.ComputeBackoff(failures, threshold: 5);

            Assert.NotNull(backoff);
            Assert.True(backoff <= PolicyConstants.MaxAuthBackoff, $"Exceeded the cap at {failures}.");
        }
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(3, 2)]
    [InlineData(5, 0)]
    [InlineData(9, 0)]
    public void Attempts_remaining_counts_down_and_stops_at_zero(int failures, int expected)
    {
        // Never negative: this number is shown to the user, and "-4 attempts remaining" is not a sentence.
        Assert.Equal(expected, AuthBackoff.AttemptsRemaining(failures, threshold: 5));
    }
}
