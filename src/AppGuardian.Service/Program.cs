using System.Runtime.Versioning;
using AppGuardian.Service.Discovery;
using AppGuardian.Service.Enforcement;
using AppGuardian.Service.Ipc;
using AppGuardian.Service.Locking;
using AppGuardian.Service.Monitoring;
using AppGuardian.Service.Power;
using AppGuardian.Service.Storage;
using AppGuardian.Service.Security;
using AppGuardian.Shared.Contracts;
using AppGuardian.Shared.Ipc;
using AppGuardian.Shared.Models;
using AppGuardian.Shared.Security;
using AppGuardian.Shared.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;

namespace AppGuardian.Service;

/// <summary>
/// Service entry point. SRS §8.2, FR-200.
/// </summary>
/// <remarks>
/// Hosted with <c>AddWindowsService</c> so the same binary runs under the SCM in production and as a
/// console app during development — a service that can only be debugged by attaching to a running
/// service is a service nobody debugs.
/// <para>
/// Startup order is deliberate. Stores are initialised before the pipe opens, because a client that
/// connects and asks for policy before the file has loaded would be told there are no rules, which is
/// indistinguishable from the user having none.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Services.AddWindowsService(options =>
        {
            options.ServiceName = PolicyConstants.ServiceName;
        });

        ConfigureLogging(builder);
        ConfigureServices(builder.Services);

        var host = builder.Build();

        var log = host.Services.GetRequiredService<ILogger<HostShim>>();

        try
        {
            StoragePaths.EnsureFolders();

            await InitializeStoresAsync(host.Services, host.Services
                .GetRequiredService<IHostApplicationLifetime>()
                .ApplicationStopping).ConfigureAwait(false);

            await host.RunAsync().ConfigureAwait(false);

            return 0;
        }
        catch (Exception ex)
        {
            // Logged before rethrowing is abandoned: an unhandled exception out of Main under the SCM
            // shows up only as "the service terminated unexpectedly" with no cause, so the event log
            // entry here is the only diagnostic the user would ever see.
            log.LogCritical(ex, "AppGuardian service failed to start.");

            return 1;
        }
    }

    private static void ConfigureLogging(HostApplicationBuilder builder)
    {
        builder.Logging.ClearProviders();

        // Event log rather than a file as the primary sink: it is the place an administrator already
        // looks, it survives the data folder being deleted, and it needs no rotation logic of its own.
        builder.Logging.AddEventLog(settings =>
        {
            settings.SourceName = PolicyConstants.ServiceDisplayName;
        });

        // Console stays on for the development path. It is a no-op under the SCM, which has no console.
        builder.Logging.AddConsole();

        builder.Logging.SetMinimumLevel(LogLevel.Information);

        // WMI and pipe teardown are chatty at Debug and neither is interesting unless something is
        // actually wrong; the audit log, not this, is the user-facing record.
        builder.Logging.AddFilter("AppGuardian.Service.Monitoring", LogLevel.Information);
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // ---- Shared ----

        // Registered as the interface so the Argon2id migration (ADR-001) is a one-line change here
        // rather than a change at every call site.
        services.AddSingleton<IKeyDerivation>(_ => new Pbkdf2KeyDerivation());
        services.AddSingleton(TimeProvider.System);

        // ---- Stores ----

        // All singletons, and all of them own a file: two instances would mean two writers racing on the
        // same path, which the atomic-replace scheme protects the file's integrity against but not the
        // in-memory copies' agreement with each other.
        services.AddSingleton<PolicyStore>();
        services.AddSingleton<CredentialStore>();
        services.AddSingleton<AuditLog>();
        services.AddSingleton<UnlockSessionTracker>();
        services.AddSingleton<AuthTokenGenerator>();

        // ---- OS integration ----

        services.AddSingleton<JobObjectPowerController>();
        services.AddSingleton<IPowerController>(sp => sp.GetRequiredService<JobObjectPowerController>());

        services.AddSingleton<WmiProcessMonitor>();
        services.AddSingleton<IProcessMonitor>(sp => sp.GetRequiredService<WmiProcessMonitor>());

        services.AddSingleton<AppDiscovery>();
        services.AddSingleton<IAppDiscovery>(sp => sp.GetRequiredService<AppDiscovery>());

        // ---- IPC ----

        services.AddSingleton<AgentBridge>();
        services.AddSingleton<ServiceDispatcher>();

        services.AddSingleton(sp =>
        {
            var dispatcher = sp.GetRequiredService<ServiceDispatcher>().Build();
            var log = sp.GetRequiredService<ILogger<PipeServer>>();

            return new PipeServer(
                PipeNames.Service,
                PipeSecurityFactory.ForServicePipe(),
                dispatcher,

                // Eight instances: the dashboard, the agent, and headroom for a second dashboard the
                // user opened before the first one's connection timed out. Message-mode pipes are cheap;
                // running out of instances would present as the UI hanging on connect.
                maxConcurrentInstances: 8,
                log: (message, ex) =>
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

        // ---- Hosted services ----

        // Order matters: the pipe host starts listening first so the agent, which the watchdog may
        // launch at any moment, never finds the pipe missing. Enforcement then starts and immediately
        // reconciles.
        services.AddHostedService<PipeHostedService>();
        services.AddHostedService<EnforcementService>();
        services.AddHostedService<PolicyChangeBroadcaster>();
    }

    private static async Task InitializeStoresAsync(IServiceProvider services, CancellationToken ct)
    {
        await services.GetRequiredService<PolicyStore>().InitializeAsync(ct).ConfigureAwait(false);
        await services.GetRequiredService<CredentialStore>().InitializeAsync(ct).ConfigureAwait(false);

        var log = services.GetRequiredService<ILogger<HostShim>>();
        var policy = services.GetRequiredService<PolicyStore>();

        if (policy.IsDegraded)
        {
            // Surfaced at Error, not Warning: a policy that failed to load means protection the user
            // configured is not being applied, which is the most serious non-fatal state this service
            // has (FR-806).
            log.LogError("Policy is degraded: {Reason}", policy.DegradedReason);
        }

        await services.GetRequiredService<AuditLog>().AppendAsync(
            AuditActor.Service,
            AuditAction.ConfigChange,
            AuditResult.Ok,
            detail: $"Service started. {policy.Current.Rules.Count} rule(s) loaded.",
            ct: ct).ConfigureAwait(false);
    }

    /// <summary>Logger category anchor for host-level messages that belong to no component.</summary>
    private sealed class HostShim
    {
    }
}
