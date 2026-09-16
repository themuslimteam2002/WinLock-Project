using System.Runtime.Versioning;
using System.Windows.Threading;
using AppGuardian.Agent.Coordination;
using AppGuardian.Agent.Ipc;
using AppGuardian.Shared.Ipc;
using AppGuardian.Shared.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AppGuardian.Agent;

/// <summary>
/// Starts the agent's pipe server and keeps the service informed that it exists. SRS §8.3, §10.4.
/// </summary>
/// <remarks>
/// The agent hosts a pipe of its own so the <em>service</em> can dial <em>it</em>. Without that, a service
/// restart would leave no way to reach an already-running agent: the service would be holding a dead
/// client connection and the agent would have no reason to reconnect until its next outbound call.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class AgentPipeHostedService : IHostedService, IAsyncDisposable
{
    private readonly PipeServer _server;
    private readonly ILogger<AgentPipeHostedService> _log;

    public AgentPipeHostedService(PipeServer server, ILogger<AgentPipeHostedService> log)
    {
        _server = server;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _server.Start();
        _log.LogInformation("The agent is listening on '{Pipe}'.", PipeNames.Agent);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken) =>
        await _server.DisposeAsync().ConfigureAwait(false);

    public ValueTask DisposeAsync() => _server.DisposeAsync();
}

/// <summary>
/// Announces the agent to the service and keeps the announcement fresh. FR-901, FR-905.
/// </summary>
/// <remarks>
/// The service treats "an agent pinged recently" as the condition for locking, hiding and Hello being
/// available at all, so this heartbeat is what turns those features on. It also carries the reconnect
/// logic: a ping that fails is how the agent discovers the service came back, at which point the policy
/// cache is stale and the audit buffer needs draining.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class AgentHeartbeatService : BackgroundService
{
    /// <summary>
    /// Shorter than the service's presence window, so a single dropped ping does not make the agent
    /// look absent and silently disable locking.
    /// </summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(20);

    private readonly ServiceConnection _service;
    private readonly LockCoordinator _locks;
    private readonly HideCoordinator _hiding;
    private readonly ILogger<AgentHeartbeatService> _log;

    private bool _wasConnected;

    public AgentHeartbeatService(
        ServiceConnection service,
        LockCoordinator locks,
        HideCoordinator hiding,
        ILogger<AgentHeartbeatService> log)
    {
        _service = service;
        _locks = locks;
        _hiding = hiding;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        // Once immediately. Waiting a full interval would leave locking disabled for the first twenty
        // seconds after logon, which is exactly when the user is opening things.
        await BeatAsync(stoppingToken).ConfigureAwait(false);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await BeatAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task BeatAsync(CancellationToken ct)
    {
        try
        {
            var alive = await _service.PingAsync(ct).ConfigureAwait(false);

            if (alive && !_wasConnected)
            {
                // A transition from down to up, not merely an up state. The policy is re-read because
                // anything could have changed while the agent was blind, and re-reading on every beat
                // would put a needless pipe round trip on a twenty-second loop forever.
                _log.LogInformation("The service is reachable; refreshing the rule set.");

                await _locks.RefreshPolicyAsync(ct).ConfigureAwait(false);
            }
            else if (!alive && _wasConnected)
            {
                // Said out loud because the user-visible consequence is real: locking still works from
                // the cached rules, but a PIN cannot be verified while the service is down (FR-905).
                _log.LogWarning(
                    "The service is not responding. Locks stay in effect, but PIN entry will not work "
                    + "until it returns.");
            }

            _wasConnected = alive;

            // Cheap upkeep on the same tick rather than a second timer: an app the user closed while
            // hidden must stop appearing on the hidden-apps page with a Reveal button that cannot work.
            _hiding.PruneExited();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Nothing escapes to the host. A faulting BackgroundService takes the whole process with it
            // under the default BackgroundServiceExceptionBehavior, and that process is the only thing
            // that can un-hide the user's windows.
            _log.LogWarning(ex, "A heartbeat could not be completed.");
        }
    }
}

/// <summary>
/// Restores hidden windows when the agent stops. FR-604.
/// </summary>
/// <remarks>
/// Registered as a hosted service purely so it participates in graceful shutdown. It does nothing on
/// start; its whole purpose is <see cref="StopAsync"/>, which runs on Ctrl+C, on logoff, and on a normal
/// exit — leaving a user with a running application they cannot reach is the worst failure this component
/// can produce, and it is worth a dedicated hook to avoid.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class RevealOnShutdownService : IHostedService
{
    private readonly HideCoordinator _hiding;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<RevealOnShutdownService> _log;

    public RevealOnShutdownService(
        HideCoordinator hiding,
        Dispatcher dispatcher,
        ILogger<RevealOnShutdownService> log)
    {
        _hiding = hiding;
        _dispatcher = dispatcher;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            var hidden = _hiding.HiddenAppIds();

            if (hidden.Count > 0)
            {
                _log.LogInformation("Restoring {Count} hidden app(s) before shutting down.", hidden.Count);
            }

            _hiding.RevealAll();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Hidden windows could not be restored during shutdown.");
        }

        try
        {
            // The WPF dispatcher is shut down last. Doing it before the reveal would leave any pending
            // window operation unable to complete.
            _dispatcher.InvokeShutdown();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "The dispatcher was already shut down.");
        }

        return Task.CompletedTask;
    }
}
