using System.Runtime.Versioning;
using AppGuardian.Agent.Coordination;
using AppGuardian.Shared.Contracts;
using AppGuardian.Shared.Ipc;
using AppGuardian.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AppGuardian.Agent.Ipc;

/// <summary>
/// Handles the messages the agent owns. API Design §4, SRS §8.3.
/// </summary>
/// <remarks>
/// Only session-bound work is registered here: drawing the overlay, prompting Windows Hello, and moving
/// windows between desktops. Everything else — policy, credentials, throttling, the audit log — belongs to
/// the service, and a client that sends one of those here gets <c>E_BAD_REQUEST</c> naming the type rather
/// than a silent no-op.
/// <para>
/// The same dispatcher is used for two transports: the agent's own pipe (which the service dials when it
/// needs to reach a restarted agent) and the inbound direction of the agent's outbound connection to the
/// service. One handler table means a message behaves identically whichever way it arrived.
/// </para>
/// <para>
/// SC-01 / SC-02 alignment (ADR-014, 2026-08-25). This dispatcher matches the service's expectations
/// structurally rather than by convention: the pipe it is served on comes from
/// <see cref="PipeNames.Agent"/>, and every message in and out is an <see cref="IpcEnvelope"/> built by
/// <see cref="MessageDispatcher"/>, which is the same type and the same version negotiation the service
/// uses. Neither this class nor anything below it parses or emits JSON of its own, so there is no second
/// schema to drift. The six types registered below are exactly the six the service's
/// <c>AgentBridge</c> sends plus the two events it publishes; anything else is answered
/// <c>E_BAD_REQUEST</c> by the dispatcher's own unknown-type path.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class AgentDispatcher
{
    private readonly LockCoordinator _locks;
    private readonly HideCoordinator _hiding;
    private readonly IWindowsHelloVerifier _hello;
    private readonly ILogger<AgentDispatcher> _log;

    public AgentDispatcher(
        LockCoordinator locks,
        HideCoordinator hiding,
        IWindowsHelloVerifier hello,
        ILogger<AgentDispatcher> log)
    {
        _locks = locks;
        _hiding = hiding;
        _hello = hello;
        _log = log;
    }

    /// <summary>
    /// Registers the agent's handlers onto a dispatcher.
    /// </summary>
    /// <remarks>
    /// Separate from construction so the dispatcher can be a dependency-free singleton shared by both
    /// transports. Building it inside this class instead would create a resolution cycle: the pipe client
    /// that carries inbound messages needs the dispatcher, and the coordinators the dispatcher calls need
    /// that same pipe client to reach the service.
    /// </remarks>
    public MessageDispatcher Register(MessageDispatcher dispatcher)
    {
        dispatcher
            .On(MessageTypes.AuthVerifyWindowsHello, VerifyHelloAsync)
            .On(MessageTypes.LockShowOverlay, ShowOverlayAsync)
            .On(MessageTypes.HideEnsureWorkspace, EnsureWorkspaceAsync)
            .On(MessageTypes.HideMoveToHidden, MoveToHiddenAsync)
            .On(MessageTypes.HideReveal, RevealAsync)
            .On(MessageTypes.SystemPing, PingAsync);

        // Events, not requests: the sender does not wait for an answer and must not be blocked by one.
        dispatcher
            .OnEvent(MessageTypes.PolicyChanged, PolicyChangedAsync)
            .OnEvent(MessageTypes.LockAppLaunched, AppLaunchedAsync);

        return dispatcher;
    }

    private async Task<IpcEnvelope> VerifyHelloAsync(RequestContext ctx)
    {
        var request = ctx.PayloadAs<AuthVerifyRequest>();

        // The reason text is shown by Windows in its own dialog, so it has to read as a sentence to the
        // user rather than as an API string.
        var reason = string.IsNullOrWhiteSpace(request?.AppId)
            ? "Verify your identity for AppGuardian"
            : $"Unlock {request!.AppId}";

        var result = await _hello.VerifyAsync(reason, ctx.CancellationToken).ConfigureAwait(false);

        // Returned as a successful response carrying verified=false, not as an error. A rejected
        // credential is an answer to the question that was asked; only a broken call is a failure.
        return ctx.Request.Ok(new AuthVerifyResult
        {
            Verified = result.Succeeded,
            Method = AuthMethod.WindowsHello,

            // Deliberately not populated. The agent has no view of the lockout counter — the service owns
            // it — and inventing a number here would contradict what the service tells the same user a
            // moment later through the PIN path.
            Reason = result.Detail,
        });
    }

    private Task<IpcEnvelope> ShowOverlayAsync(RequestContext ctx)
    {
        var request = ctx.PayloadAs<ShowOverlayRequest>();

        if (request is null || string.IsNullOrWhiteSpace(request.Identity.AppId))
        {
            return Task.FromResult(ctx.Request.Fail(
                ErrorCodes.BadRequest,
                "A showOverlay request needs an app identity."));
        }

        _locks.ShowOverlayFor(request);

        // Acknowledged as soon as the show is queued, not after it is painted. The service is waiting on
        // this response inside NFR-P4's one-second budget, and WPF window creation can take longer than
        // that on a cold logon.
        return Task.FromResult(ctx.Request.Ok(new { shown = true }));
    }

    private Task<IpcEnvelope> EnsureWorkspaceAsync(RequestContext ctx)
    {
        var desktop = _hiding.EnsureWorkspace();

        return Task.FromResult(ctx.Request.Ok(new
        {
            // Reported honestly. When the fallback is in use there is no desktop id, and the dashboard
            // shows the weaker guarantee instead of implying a hidden desktop exists (FR-604, FR-806).
            desktopId = desktop.IsNone ? null : desktop.Value.ToString(),
            usesRealDesktops = _hiding.UsesRealDesktops,
            strategy = _hiding.StrategyDescription,
        }));
    }

    private async Task<IpcEnvelope> MoveToHiddenAsync(RequestContext ctx)
    {
        var request = ctx.PayloadAs<MoveToHiddenRequest>();

        if (request is null || string.IsNullOrWhiteSpace(request.AppId) || request.ProcessId <= 0)
        {
            return ctx.Request.Fail(
                ErrorCodes.BadRequest,
                "A moveToHidden request needs an appId and a processId.");
        }

        var result = await _hiding.HideAsync(request, ctx.CancellationToken).ConfigureAwait(false);

        return ctx.Request.Ok(result);
    }

    private async Task<IpcEnvelope> RevealAsync(RequestContext ctx)
    {
        var request = ctx.PayloadAs<RevealRequest>();

        if (request is null || string.IsNullOrWhiteSpace(request.AppId))
        {
            return ctx.Request.Fail(ErrorCodes.BadRequest, "A reveal request needs an appId.");
        }

        var result = await _hiding.RevealAsync(request, ctx.CancellationToken).ConfigureAwait(false);

        return ctx.Request.Ok(result);
    }

    private Task<IpcEnvelope> PingAsync(RequestContext ctx) =>
        Task.FromResult(ctx.Request.Ok(new
        {
            // The service uses this to decide whether an agent is present at all, which gates the whole
            // lock and hide feature set. Reporting the strategy alongside it saves a second round trip
            // for the status the dashboard needs anyway.
            alive = true,
            usesRealDesktops = _hiding.UsesRealDesktops,
            hiddenAppCount = _hiding.HiddenAppIds().Count,
        }));

    private async Task<IpcEnvelope> PolicyChangedAsync(RequestContext ctx)
    {
        await _locks.RefreshPolicyAsync(ctx.CancellationToken).ConfigureAwait(false);

        // An event handler's return value is discarded by the dispatcher; it exists only to satisfy the
        // handler signature. Nothing is written back to the sender.
        return ctx.Request.Ok();
    }

    private Task<IpcEnvelope> AppLaunchedAsync(RequestContext ctx)
    {
        var payload = ctx.PayloadAs<AppLaunchedEvent>();

        if (payload is not null)
        {
            // Informational only. The overlay is driven by lock.showOverlay, which the service sends
            // deliberately; acting on the launch event as well would show two overlays for one launch.
            _log.LogDebug(
                "The service reported {AppId} launching as PID {Pid}.",
                payload.AppId,
                payload.ProcessId);
        }

        return Task.FromResult(ctx.Request.Ok());
    }
}
