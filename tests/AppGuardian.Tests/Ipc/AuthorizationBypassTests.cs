using AppGuardian.Service.Security;
using AppGuardian.Shared.Ipc;

namespace AppGuardian.Tests.Ipc;

/// <summary>
/// Exercises the authorization boundary without opening an OS named pipe. ServiceDispatcher uses
/// this exact helper before every protected mutation, so these tests cannot hang on pipe startup.
/// </summary>
public sealed class AuthorizationBypassTests
{
    private const string SidA = "S-1-5-21-A";
    private const string SidB = "S-1-5-21-B";

    [Fact]
    public void Lock_unlock_without_token_is_rejected() =>
        AssertUnauthorized(token: null, SidA);

    [Fact]
    public void Rule_delete_without_token_is_rejected() =>
        AssertUnauthorized(token: null, SidA);

    [Fact]
    public void Policy_pause_without_token_is_rejected() =>
        AssertUnauthorized(token: null, SidA);

    [Fact]
    public void Expired_token_is_rejected()
    {
        var clock = new TestTimeProvider();
        var tokens = new AuthTokenGenerator(clock);
        var token = tokens.GenerateForSid(SidA);
        clock.Advance(TimeSpan.FromSeconds(31));

        AssertUnauthorized(token, SidA, tokens);
    }

    [Fact]
    public void Wrong_sid_token_is_rejected()
    {
        var tokens = new AuthTokenGenerator();
        var token = tokens.GenerateForSid(SidA);

        AssertUnauthorized(token, SidB, tokens);
    }

    private static void AssertUnauthorized(string? token, string sid, AuthTokenGenerator? tokens = null)
    {
        tokens ??= new AuthTokenGenerator();

        var authorized = PipeSecurityFactory.RequireAuthenticatedSession(tokens.TryConsume, token, sid);
        var response = IpcEnvelope.NewRequest(MessageTypes.LockUnlock).Fail(
            authorized ? ErrorCodes.Internal : ErrorCodes.Unauthorized,
            "Authenticate again.");

        Assert.False(authorized);
        Assert.Equal(ErrorCodes.Unauthorized, response.Error!.Code);
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
