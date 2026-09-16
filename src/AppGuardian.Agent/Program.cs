using System.Runtime.Versioning;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using AppGuardian.Agent.Auth;
using AppGuardian.Agent.Coordination;
using AppGuardian.Agent.Desktops;
using AppGuardian.Agent.Foreground;
using AppGuardian.Agent.Ipc;
using AppGuardian.Agent.Overlay;
using AppGuardian.Shared.Contracts;
using AppGuardian.Shared.Ipc;
using AppGuardian.Shared.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AppGuardian.Agent;

/// <summary>
/// Agent entry point. SRS §8.3.
/// </summary>
/// <remarks>
/// A WPF <see cref="Application"/> and a generic host in one process. The host owns the pipe server, the
/// heartbeat and the shutdown hook; WPF owns the message loop the overlay needs. The alternative — a
/// plain host with windows created on an ad-hoc STA thread — was rejected because a WPF window whose
/// <see cref="Dispatcher"/> outlives its thread produces failures that are almost impossible to read.
/// <para>
/// The dispatcher is registered in DI so the overlay can marshal onto it from the WinEvent hook thread and
/// from the pipe read pump, neither of which may touch a window directly.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows10.0.17763.0")]
public static class Program
{
    /// <summary>
    /// One agent per interactive session.
    /// </summary>
    /// <remarks>
    /// Local, not Global: each session gets its own agent by design, and a Global mutex would let the
    /// first user to log on block every subsequent one. Two agents in one session would fight over the
    /// pipe name and double every overlay.
    /// </remarks>
    private const string SingleInstanceMutex = @"Local\AppGuardian.Agent.Instance";

    [STAThread]
    public static int Main(string[] args)
    {
        using var instanceGate = new Mutex(initiallyOwned: false, SingleInstanceMutex, out var isNew);

        if (!isNew)
        {
            // Silent exit. The agent is started automatically at logon, and a second copy launched by a
            // stale run key should not put a dialog in front of a user who did not ask for anything.
            return 0;
        }

        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        var host = BuildHost(application.Dispatcher, args);

        // Started before Run so the pipe is listening before the message loop begins. The service may be
        // waiting to push an overlay for an app that is already in the foreground at logon.
        host.Start();

        // Shutdown is chained in both directions: a host stop (Ctrl+C, logoff) ends the message loop, and
        // an application exit stops the host. Either alone would leave the other running.
        host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(() =>
            application.Dispatcher.InvokeAsync(application.Shutdown));

        try
        {
            application.Run();
        }
        finally
        {
            // Bounded, because this runs while Windows may already be tearing the session down at logoff.
            // The reveal-on-shutdown hook inside needs to complete; waiting forever for it does not.
            try
            {
                host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"The agent did not shut down cleanly: {ex.Message}");
            }

            host.Dispose();
        }

        return 0;
    }

    private static IHost BuildHost(Dispatcher dispatcher, string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Logging.ClearProviders();
        builder.Logging.AddDebug();

        // No event log. That source belongs to the service, which runs as LocalSystem; an unelevated
        // process cannot write to it, and creating a second source per user would clutter the log the
        // administrator actually reads.
        builder.Logging.AddSimpleConsole(options => options.SingleLine = true);

        builder.Services.AddSingleton(dispatcher);

        builder.Services.AddSingleton<IWindowsHelloVerifier, WindowsHelloVerifier>();

        // Concrete and interface both registered, because the coordinator needs the strategy description
        // that only the concrete adapter can give and the shared contract cannot carry.
        builder.Services.AddSingleton<VirtualDesktopAdapter>();
        builder.Services.AddSingleton<IVirtualDesktopAdapter>(sp =>
            sp.GetRequiredService<VirtualDesktopAdapter>());

        builder.Services.AddSingleton<ForegroundWatcher>();
        builder.Services.AddSingleton<IForegroundWatcher>(sp => sp.GetRequiredService<ForegroundWatcher>());

        builder.Services.AddSingleton<IUnlockAuthenticator, ServiceUnlockAuthenticator>();
        builder.Services.AddSingleton<LockOverlay>();
        builder.Services.AddSingleton<ILockOverlay>(sp => sp.GetRequiredService<LockOverlay>());

        builder.Services.AddSingleton<HideCoordinator>();
        builder.Services.AddSingleton<LockCoordinator>();
        builder.Services.AddSingleton<AgentDispatcher>();

        // One dispatcher instance, shared by both transports, and deliberately constructed with no
        // handlers. Registering them here would need the coordinators, which need the connection, which
        // needs this dispatcher — a cycle. The startup service breaks it by calling Register once both
        // sides exist, so a message that arrives before that point is answered with E_BAD_REQUEST rather
        // than dispatched into a half-built graph.
        builder.Services.AddSingleton(sp =>
        {
            var log = sp.GetRequiredService<ILogger<MessageDispatcher>>();

            return new MessageDispatcher((message, ex) =>
            {
                if (ex is null)
                {
                    log.LogDebug("{Message}", message);
                }
                else
                {
                    log.LogWarning(ex, "{Message}", message);
                }
            });
        });

        builder.Services.AddSingleton(sp => new ServiceConnection(
            sp.GetRequiredService<MessageDispatcher>(),
            sp.GetRequiredService<ILogger<ServiceConnection>>()));

        builder.Services.AddSingleton(sp =>
        {
            var log = sp.GetRequiredService<ILogger<PipeServer>>();

            return new PipeServer(
                PipeNames.Agent,
                PipeSecurityFactory.ForAgentPipe(),
                sp.GetRequiredService<MessageDispatcher>(),

                // Four, against the service's eight. The only caller is the service, and it holds one
                // connection; the spare instances exist so a restart can bind before the old handle is
                // fully released.
                maxConcurrentInstances: 4,
                (message, ex) =>
                {
                    if (ex is null)
                    {
                        log.LogDebug("{Message}", message);
                    }
                    else
                    {
                        log.LogWarning(ex, "{Message}", message);
                    }
                });
        });

        // Order matters. The coordinator starts first because it is what registers the message handlers:
        // a pipe listening before then would accept a push and answer E_BAD_REQUEST. The heartbeat goes
        // last, because it is what tells the service the agent exists and therefore what enables locking
        // service-side — and it should only say so once the agent can actually act.
        builder.Services.AddHostedService<CoordinatorStartupService>();
        builder.Services.AddHostedService<AgentPipeHostedService>();
        builder.Services.AddHostedService<AgentHeartbeatService>();
        builder.Services.AddHostedService<RevealOnShutdownService>();

        return builder.Build();
    }
}

/// <summary>
/// Starts the lock coordinator once the pipe is up. FR-501.
/// </summary>
/// <remarks>
/// A separate hosted service rather than work inside the coordinator's constructor: reading the policy
/// requires a pipe round trip, and a constructor that blocks on IPC would make the whole DI graph
/// unresolvable whenever the service happened to be starting at the same moment.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class CoordinatorStartupService : IHostedService
{
    private readonly AgentDispatcher _handlers;
    private readonly MessageDispatcher _dispatcher;
    private readonly LockCoordinator _locks;
    private readonly HideCoordinator _hiding;
    private readonly ILogger<CoordinatorStartupService> _log;

    public CoordinatorStartupService(
        AgentDispatcher handlers,
        MessageDispatcher dispatcher,
        LockCoordinator locks,
        HideCoordinator hiding,
        ILogger<CoordinatorStartupService> log)
    {
        _handlers = handlers;
        _dispatcher = dispatcher;
        _locks = locks;
        _hiding = hiding;
        _log = log;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Handlers first, before anything can arrive. Resolving AgentDispatcher here is also what forces
        // the coordinator graph to be constructed, so a DI mistake surfaces at startup rather than on the
        // first message.
        _handlers.Register(_dispatcher);

        try
        {
            StoragePaths.EnsureFolders();

            await _locks.StartAsync(cancellationToken).ConfigureAwait(false);

            // Probed at startup so the strategy is known before the first hide request, and so the
            // fallback is reported once in the log rather than discovered mid-operation.
            _log.LogInformation(
                "Hide strategy: {Strategy}",
                _hiding.UsesRealDesktops ? "virtual desktop" : "minimise fallback");
        }
        catch (Exception ex)
        {
            // The agent stays up. Without the foreground hook it still serves overlay pushes from the
            // service and can still reveal hidden windows, which is a great deal better than exiting and
            // leaving the user with no agent at all.
            _log.LogError(ex, "The lock coordinator could not be started; foreground locking is disabled.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _locks.Dispose();
        return Task.CompletedTask;
    }
}
