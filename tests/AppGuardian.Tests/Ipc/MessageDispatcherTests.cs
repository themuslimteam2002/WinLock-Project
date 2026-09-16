using AppGuardian.Shared.Ipc;

namespace AppGuardian.Tests.Ipc;

public sealed class MessageDispatcherTests
{
    private static IpcEnvelope Request(string type, object? payload = null) =>
        IpcEnvelope.NewRequest(type, payload);

    private static Task<IpcEnvelope?> Dispatch(
        MessageDispatcher dispatcher,
        IpcEnvelope envelope,
        bool isElevated = false) =>
        dispatcher.DispatchAsync(envelope, clientUserSid: "S-1-5-21-test", isElevated, CancellationToken.None);

    [Fact(Timeout = 5000)]
    public async Task Unknown_type_returns_a_bad_request_rather_than_throwing()
    {
        // SRS §10.3: an unknown op must not crash the peer.
        var dispatcher = new MessageDispatcher();

        var response = await Dispatch(dispatcher, Request("does.notExist"));

        Assert.NotNull(response);
        Assert.False(response.Success);
        Assert.Equal(ErrorCodes.BadRequest, response.Error!.Code);
    }

    [Fact(Timeout = 5000)]
    public async Task Newer_major_apiVersion_is_rejected()
    {
        var dispatcher = new MessageDispatcher()
            .On(MessageTypes.SystemPing, ctx => Task.FromResult(ctx.Request.Ok()));

        var envelope = Request(MessageTypes.SystemPing);
        envelope.ApiVersion = "2.0";

        var response = await Dispatch(dispatcher, envelope);

        Assert.Equal(ErrorCodes.UnsupportedVersion, response!.Error!.Code);
    }

    [Fact(Timeout = 5000)]
    public async Task Older_minor_apiVersion_is_accepted()
    {
        // Minor versions are additive (API Design §7), so a 1.0 client talks to a 1.1 server.
        var dispatcher = new MessageDispatcher()
            .On(MessageTypes.SystemPing, ctx => Task.FromResult(ctx.Request.Ok()));

        var envelope = Request(MessageTypes.SystemPing);
        envelope.ApiVersion = "1.0";

        var response = await Dispatch(dispatcher, envelope);

        Assert.True(response!.Success);
    }

    [Fact(Timeout = 5000)]
    public async Task Response_carries_the_request_messageId_as_correlationId()
    {
        var dispatcher = new MessageDispatcher()
            .On(MessageTypes.SystemPing, ctx => Task.FromResult(ctx.Request.Ok()));

        var envelope = Request(MessageTypes.SystemPing);

        var response = await Dispatch(dispatcher, envelope);

        Assert.Equal(envelope.MessageId, response!.CorrelationId);
    }

    [Fact(Timeout = 5000)]
    public async Task Elevation_gated_type_is_refused_for_a_non_elevated_caller()
    {
        var invoked = false;

        var dispatcher = new MessageDispatcher()
            .On(MessageTypes.AuthReset, ctx =>
            {
                invoked = true;
                return Task.FromResult(ctx.Request.Ok());
            }, requiresElevation: true);

        var response = await Dispatch(dispatcher, Request(MessageTypes.AuthReset), isElevated: false);

        Assert.Equal(ErrorCodes.Unauthorized, response!.Error!.Code);

        // The handler must not run at all — a reset that partially executed before the check would
        // still have destroyed the credential.
        Assert.False(invoked);
    }

    [Fact(Timeout = 5000)]
    public async Task Elevation_gated_type_is_allowed_for_an_elevated_caller()
    {
        var dispatcher = new MessageDispatcher()
            .On(MessageTypes.AuthReset, ctx => Task.FromResult(ctx.Request.Ok()), requiresElevation: true);

        var response = await Dispatch(dispatcher, Request(MessageTypes.AuthReset), isElevated: true);

        Assert.True(response!.Success);
    }

    [Fact(Timeout = 5000)]
    public async Task A_throwing_handler_becomes_an_internal_error_without_leaking_detail()
    {
        var dispatcher = new MessageDispatcher()
            .On(MessageTypes.PolicyGet, _ =>
                throw new InvalidOperationException("C:\\ProgramData\\AppGuardian\\credentials.dat is locked"));

        var response = await Dispatch(dispatcher, Request(MessageTypes.PolicyGet));

        Assert.Equal(ErrorCodes.Internal, response!.Error!.Code);

        // API Design §3: the wire message must not carry paths, stack traces, or secrets.
        Assert.DoesNotContain("credentials.dat", response.Error.Message);
        Assert.DoesNotContain("ProgramData", response.Error.Message);
    }

    [Fact(Timeout = 5000)]
    public async Task A_repeated_write_is_served_from_the_dedup_cache()
    {
        var calls = 0;

        var dispatcher = new MessageDispatcher()
            .On(MessageTypes.LockSetRule, ctx =>
            {
                calls++;
                return Task.FromResult(ctx.Request.Ok());
            });

        var envelope = Request(MessageTypes.LockSetRule);

        await Dispatch(dispatcher, envelope);
        await Dispatch(dispatcher, envelope);

        // API Design §2.4: a client retry that crossed with a slow response must not apply twice.
        Assert.Equal(1, calls);
    }

    [Fact(Timeout = 5000)]
    public async Task A_repeated_read_is_re_executed()
    {
        var calls = 0;

        var dispatcher = new MessageDispatcher()
            .On(MessageTypes.PolicyGet, ctx =>
            {
                calls++;
                return Task.FromResult(ctx.Request.Ok());
            });

        var envelope = Request(MessageTypes.PolicyGet);

        await Dispatch(dispatcher, envelope);
        await Dispatch(dispatcher, envelope);

        // Reads are not cached, so a caller can never be served stale state.
        Assert.Equal(2, calls);
    }

    [Fact(Timeout = 5000)]
    public async Task Events_get_no_response_and_a_failing_handler_is_swallowed()
    {
        var dispatcher = new MessageDispatcher()
            .OnEvent(MessageTypes.PolicyChanged, _ => throw new InvalidOperationException("boom"));

        var response = await Dispatch(dispatcher, IpcEnvelope.NewEvent(MessageTypes.PolicyChanged));

        // SRS §10.3: events are one-way and best-effort, so a broken subscriber must not tear down
        // the connection or produce a reply nobody is waiting for.
        Assert.Null(response);
    }

    [Fact(Timeout = 5000)]
    public async Task An_unregistered_event_is_ignored_silently()
    {
        var dispatcher = new MessageDispatcher();

        var response = await Dispatch(dispatcher, IpcEnvelope.NewEvent(MessageTypes.PolicyChanged));

        Assert.Null(response);
    }

    [Fact(Timeout = 5000)]
    public async Task A_structurally_invalid_envelope_is_rejected()
    {
        var dispatcher = new MessageDispatcher();

        var envelope = Request(MessageTypes.SystemPing);
        envelope.Type = null;

        var response = await Dispatch(dispatcher, envelope);

        Assert.Equal(ErrorCodes.BadRequest, response!.Error!.Code);
    }

    [Fact(Timeout = 5000)]
    public async Task A_stray_response_is_ignored()
    {
        var dispatcher = new MessageDispatcher()
            .On(MessageTypes.SystemPing, ctx => Task.FromResult(ctx.Request.Ok()));

        var response = await Dispatch(dispatcher, Request(MessageTypes.SystemPing).Ok());

        // A server does not answer responses; replying would start a loop between two peers.
        Assert.Null(response);
    }
}
