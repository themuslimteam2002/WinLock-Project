using System.Runtime.Versioning;
using AppGuardian.Shared.Contracts;
using AppGuardian.Shared.Ipc;
using AppGuardian.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AppGuardian.Service.Ipc;

/// <summary>
/// The service's outbound channel to the agent. SRS §8.1, API Design §2.2.
/// </summary>
/// <remarks>
/// A second pipe in the opposite direction, rather than pushing over the client's existing
/// connection. The reason is lifetime: the agent restarts with the interactive session and the service
/// does not, so a connection the agent owns would leave the service unable to reach a newly started
/// agent until the agent happened to call in. With the agent hosting its own pipe, the service simply
/// reconnects.
/// <para>
/// Every send here is best-effort. The agent being absent is a normal, expected state — nobody is
/// logged on, or the agent crashed and the watchdog has not restarted it yet — so a failed push
/// degrades protection and is reported through <c>system.status</c>, but never fails the service-side
/// operation that triggered it.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class AgentBridge : IAsyncDisposable
{
    private readonly ILogger<AgentBridge> _log;
    private readonly PipeClient _client;

    private DateTimeOffset _lastSeenUtc = DateTimeOffset.MinValue;

    public AgentBridge(ILogger<AgentBridge> log)
    {
        _log = log;

        _client = new PipeClient(
            PipeNames.Agent,
            inboundDispatcher: null,
            log: (message, ex) =>
            {
                if (ex is null)
                {
                    _log.LogDebug("{Message}", message);
                }
                else
                {
                    _log.LogDebug(ex, "{Message}", message);
                }
            });
    }

    /// <summary>
    /// Whether the agent has answered recently enough to be considered present.
    /// </summary>
    /// <remarks>
    /// Time-windowed rather than a live probe, because <c>system.status</c> is called on every
    /// dashboard refresh and a synchronous connect attempt on each would add a connection round trip
    /// to a call that has a two-second budget (NFR-P1).
    /// </remarks>
    public bool IsAgentPresent => DateTimeOffset.UtcNow - _lastSeenUtc < PresenceWindow;

    /// <summary>
    /// Performs Windows Hello on the agent's trusted interactive-session pipe. The service, not the
    /// original caller, initiates this request so a client cannot forge a successful Hello result.
    /// </summary>
    public async Task<AuthVerifyResult?> VerifyWindowsHelloAsync(AuthVerifyRequest request, CancellationToken ct)
    {
        var response = await _client
            .SendAsync(MessageTypes.AuthVerifyWindowsHello, request, ct: ct)
            .ConfigureAwait(false);

        return Note(response, MessageTypes.AuthVerifyWindowsHello)
            ? response.PayloadAs<AuthVerifyResult>()
            : null;
    }

    /// <summary>Asks the agent to show the lock overlay. FR-502, FR-503.</summary>
    public async Task<bool> ShowOverlayAsync(ShowOverlayRequest request, CancellationToken ct)
    {
        var response = await _client
            .SendAsync(MessageTypes.LockShowOverlay, request, ct: ct)
            .ConfigureAwait(false);

        return Note(response, MessageTypes.LockShowOverlay);
    }

    /// <summary>Asks the agent to move a window to the hidden desktop. FR-602.</summary>
    public async Task<HideResult> MoveToHiddenAsync(MoveToHiddenRequest request, CancellationToken ct)
    {
        var response = await _client
            .SendAsync(MessageTypes.HideMoveToHidden, request, ct: ct)
            .ConfigureAwait(false);

        if (!Note(response, MessageTypes.HideMoveToHidden))
        {
            return new HideResult
            {
                Succeeded = false,

                // Named plainly, because "hiding failed" with no reason is the kind of message that
                // makes a user think the app is broken rather than that a component is not running.
                Detail = response.Error?.Message
                         ?? "AppGuardian's desktop helper is not running, so the app could not be hidden.",
            };
        }

        return response.PayloadAs<HideResult>() ?? new HideResult { Succeeded = true };
    }

    /// <summary>Asks the agent to bring a hidden window back. FR-604, FR-606.</summary>
    /// <remarks>
    /// Forwarded rather than handled here for the same reason as the hide: the window and the desktop it
    /// sits on only exist in the interactive session. The dashboard talks to the service, so the service is
    /// the hop in the middle — see <c>ServiceDispatcher</c>'s <c>hide.reveal</c> registration.
    /// </remarks>
    public async Task<HideResult> RevealAsync(RevealRequest request, CancellationToken ct)
    {
        var response = await _client
            .SendAsync(MessageTypes.HideReveal, request, ct: ct)
            .ConfigureAwait(false);

        if (!Note(response, MessageTypes.HideReveal))
        {
            return new HideResult
            {
                Succeeded = false,

                // The wording matters more here than anywhere else in the bridge. This is the recovery
                // path: a user who has hidden a window and cannot get it back needs to be told what to
                // restart, not that an operation failed.
                Detail = response.Error?.Message
                         ?? "AppGuardian's desktop helper is not running, so the window could not be "
                            + "brought back. Signing out and back in restarts it, and any hidden app will "
                            + "reappear when it is started again.",
            };
        }

        return response.PayloadAs<HideResult>() ?? new HideResult { Succeeded = true };
    }

    /// <summary>Notifies the agent that an app it may care about started. FR-501.</summary>
    public async Task PublishAppLaunchedAsync(AppLaunchedEvent payload, CancellationToken ct)
    {
        // An event, not a request: the service must not wait on the agent to decide whether to
        // continue enforcing, and there is no answer worth waiting for.
        var sent = await _client
            .PublishAsync(MessageTypes.LockAppLaunched, payload, ct)
            .ConfigureAwait(false);

        if (sent)
        {
            _lastSeenUtc = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>Tells connected clients the policy changed so they can re-fetch. API Design §2.4.</summary>
    public async Task PublishPolicyChangedAsync(CancellationToken ct)
    {
        await _client.PublishAsync(MessageTypes.PolicyChanged, null, ct).ConfigureAwait(false);
    }

    /// <summary>Heartbeat probe used by the watchdog. FR-205, NFR-R2.</summary>
    public async Task<bool> PingAsync(CancellationToken ct)
    {
        var alive = await _client.PingAsync(ct).ConfigureAwait(false);

        if (alive)
        {
            _lastSeenUtc = DateTimeOffset.UtcNow;
        }

        return alive;
    }

    private bool Note(IpcEnvelope response, string type)
    {
        if (response.Success == true)
        {
            _lastSeenUtc = DateTimeOffset.UtcNow;
            return true;
        }

        _log.LogWarning(
            "Agent call '{Type}' failed: {Code} {Message}",
            type,
            response.Error?.Code,
            response.Error?.Message);

        return false;
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();

    private static readonly TimeSpan PresenceWindow = TimeSpan.FromSeconds(45);
}
