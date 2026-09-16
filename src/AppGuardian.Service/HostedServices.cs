using System.Runtime.Versioning;
using AppGuardian.Service.Ipc;
using AppGuardian.Service.Storage;
using AppGuardian.Shared.Ipc;
using AppGuardian.Shared.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AppGuardian.Service;

/// <summary>
/// Owns the lifetime of the service's named pipe. API Design §2.2.
/// </summary>
/// <remarks>
/// A hosted service rather than a <c>Start()</c> call from <c>Main</c>, so the pipe is torn down by the
/// same shutdown that stops everything else. Left to the process exit instead, a stop-start cycle of the
/// service could race a half-closed pipe instance and fail to bind the name.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class PipeHostedService : IHostedService
{
    private readonly PipeServer _server;
    private readonly ILogger<PipeHostedService> _log;

    public PipeHostedService(PipeServer server, ILogger<PipeHostedService> log)
    {
        _server = server;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _server.Start();

        _log.LogInformation("Listening on pipe '{Pipe}'.", PipeNames.Service);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _server.DisposeAsync().ConfigureAwait(false);

        _log.LogInformation("Pipe '{Pipe}' closed.", PipeNames.Service);
    }
}

/// <summary>
/// Pushes <c>policy.changed</c> whenever the store mutates. API Design §2.4.
/// </summary>
/// <remarks>
/// Separate from the dispatcher because the store can also change without a request — a rule whose
/// target executable was uninstalled, for instance — and clients that only re-fetch in response to their
/// own writes would miss it. Bridging the event here means every mutation path notifies, whatever
/// caused it.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class PolicyChangeBroadcaster : IHostedService
{
    private readonly PolicyStore _policy;
    private readonly AgentBridge _agent;
    private readonly ILogger<PolicyChangeBroadcaster> _log;

    public PolicyChangeBroadcaster(
        PolicyStore policy,
        AgentBridge agent,
        ILogger<PolicyChangeBroadcaster> log)
    {
        _policy = policy;
        _agent = agent;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _policy.Changed += OnPolicyChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _policy.Changed -= OnPolicyChanged;
        return Task.CompletedTask;
    }

    private void OnPolicyChanged(object? sender, PolicyDocument document)
    {
        // Fire-and-forget with its own faults swallowed: the mutation that raised this event has already
        // been persisted, and failing to notify a client must not make the write look like it failed.
        _ = Task.Run(async () =>
        {
            try
            {
                await _agent.PublishPolicyChangedAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "policy.changed could not be published.");
            }
        });
    }
}
