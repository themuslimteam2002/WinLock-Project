using AppGuardian.Service.Security;

namespace AppGuardian.Tests.Security;

public sealed class AuthTokenTests
{
    [Fact]
    public void Generated_token_is_accepted_once_for_its_issuing_sid()
    {
        var subject = new AuthTokenGenerator();
        var token = subject.GenerateForSid("S-1-5-21-A");

        Assert.NotEqual(32, token.Length); // Base64 representation, not raw bytes.
        Assert.True(subject.TryConsume(token, "S-1-5-21-A"));
    }

    [Fact]
    public void Token_expires_after_thirty_seconds()
    {
        var clock = new TestTimeProvider();
        var subject = new AuthTokenGenerator(clock);
        var token = subject.GenerateForSid("S-1-5-21-A");

        clock.Advance(TimeSpan.FromSeconds(31));

        Assert.False(subject.TryConsume(token, "S-1-5-21-A"));
    }

    [Fact]
    public void Token_from_one_sid_is_rejected_for_another_sid()
    {
        var subject = new AuthTokenGenerator();
        var token = subject.GenerateForSid("S-1-5-21-A");

        Assert.False(subject.TryConsume(token, "S-1-5-21-B"));
    }

    [Fact]
    public void Token_is_single_use()
    {
        var subject = new AuthTokenGenerator();
        var token = subject.GenerateForSid("S-1-5-21-A");

        Assert.True(subject.TryConsume(token, "S-1-5-21-A"));
        Assert.False(subject.TryConsume(token, "S-1-5-21-A"));
    }

    [Fact(Timeout = 5000)]
    public async Task Concurrent_consumers_allow_exactly_one_success()
    {
        var subject = new AuthTokenGenerator();
        var token = subject.GenerateForSid("S-1-5-21-A");

        var attempts = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => subject.TryConsume(token, "S-1-5-21-A"))));

        Assert.Equal(1, attempts.Count(result => result));
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
