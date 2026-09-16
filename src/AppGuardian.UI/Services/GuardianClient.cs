using System.Runtime.Versioning;
using AppGuardian.Shared.Contracts;
using AppGuardian.Shared.Ipc;
using AppGuardian.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AppGuardian.UI.Services;

/// <summary>
/// The dashboard's only route to state. SRS §8.2, API Design §4.
/// </summary>
/// <remarks>
/// Every method here is a pipe call. The dashboard holds no policy of its own and caches nothing across a
/// navigation, because two sources of truth for a rule set would eventually disagree and the user would be
/// editing whichever one they happened to be looking at.
/// <para>
/// No call throws for a service-side failure. The dashboard has to render something useful when the
/// service is down — at minimum an explanation — so failures come back as results with a reason attached
/// rather than as exceptions each view model would have to catch.
/// </para>
/// <para>
/// SC-01 / SC-02 alignment (ADR-014, 2026-08-25). This client speaks the service's contract by
/// construction, not by convention. The pipe is <see cref="PipeNames.Service"/> — the constant the service
/// itself listens on, so the two cannot disagree — and every message is an <see cref="IpcEnvelope"/> built
/// by <see cref="PipeClient"/>, which stamps <see cref="ApiVersion.Current"/> and a fresh messageId and
/// correlates the reply on <c>correlationId</c>. Nothing here writes a pipe path or a JSON field name as a
/// literal, so the service's envelope remains the single source of truth for the wire format.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class GuardianClient : IAsyncDisposable
{
    private readonly PipeClient _client;
    private readonly ILogger<GuardianClient> _log;

    /// <summary>Raised when the service reports a policy change, so open views can re-read.</summary>
    public event EventHandler? PolicyChanged;

    public GuardianClient(ILogger<GuardianClient> log)
    {
        _log = log;

        // An inbound dispatcher is supplied so the service can push policy.changed down this same
        // connection. Without it the dashboard would only see changes it made itself, and a rule the user
        // edited in a second window — or a rule the service dropped because its target was uninstalled —
        // would silently disagree with what is on screen.
        var inbound = new MessageDispatcher();

        inbound.OnEvent(MessageTypes.PolicyChanged, ctx =>
        {
            PolicyChanged?.Invoke(this, EventArgs.Empty);
            return Task.FromResult(ctx.Request.Ok());
        });

        _client = new PipeClient(
            PipeNames.Service,
            inbound,
            (message, ex) =>
            {
                if (ex is null)
                {
                    log.LogDebug("{Message}", message);
                }
                else
                {
                    log.LogDebug(ex, "{Message}", message);
                }
            });
    }

    // ---- status ----

    public Task<SystemStatus?> GetStatusAsync(CancellationToken ct = default) =>
        GetAsync<SystemStatus>(MessageTypes.SystemStatus, null, ct);

    public Task<AuthStatus?> GetAuthStatusAsync(CancellationToken ct = default) =>
        GetAsync<AuthStatus>(MessageTypes.AuthGetStatus, null, ct);

    // ---- onboarding (FR-801) ----

    public async Task<CallResult> SetUpAuthAsync(
        AuthMethod method,
        string secret,
        CancellationToken ct = default)
    {
        var response = await SendAsync(
            MessageTypes.AuthSetup,
            new AuthSetupRequest { PreferredMethod = method, FallbackSecret = secret },
            PolicyConstants.AuthRequestTimeout,
            ct).ConfigureAwait(false);

        return CallResult.From(response);
    }

    public async Task<CallResult> ResetAuthAsync(CancellationToken ct = default)
    {
        var response = await SendAsync(MessageTypes.AuthReset, null, null, ct).ConfigureAwait(false);

        return CallResult.From(response);
    }

    // ---- app picker (FR-800) ----

    public Task<AppListResult?> ListInstalledAsync(string? filter, CancellationToken ct = default) =>
        GetAsync<AppListResult>(
            MessageTypes.AppsListInstalled,

            // Icons are never requested in a list call. Two hundred inlined PNGs would exceed the 256 KB
            // envelope cap (SC-15c); the picker fetches them one at a time as rows scroll into view.
            new ListInstalledRequest { Filter = filter, IncludeIcons = false },
            ct,

            // Enumerating the registry, the Start Menu and packaged apps is slow on a machine with a lot
            // installed, and a five-second default would time out on exactly the machines that need the
            // longest.
            TimeSpan.FromSeconds(30));

    public Task<AppListResult?> ListRunningAsync(CancellationToken ct = default) =>
        GetAsync<AppListResult>(MessageTypes.AppsListRunning, null, ct);

    public Task<GetIconResult?> GetIconAsync(string appId, CancellationToken ct = default) =>
        GetAsync<GetIconResult>(MessageTypes.AppsGetIcon, new GetIconRequest { AppId = appId }, ct);

    public Task<AppIdentity?> ResolveAsync(string executablePath, CancellationToken ct = default) =>
        GetAsync<AppIdentity>(
            MessageTypes.AppsResolveIdentity,
            new ResolveIdentityRequest { ExecutablePath = executablePath },
            ct);

    // ---- rules (FR-802) ----

    public Task<PolicySnapshot?> GetPolicyAsync(CancellationToken ct = default) =>
        GetAsync<PolicySnapshot>(MessageTypes.PolicyGet, null, ct);

    public async Task<CallResult<RuleResult>> SetLockRuleAsync(
        AppIdentity identity,
        LockSettings settings,
        CancellationToken ct = default)
    {
        // Only the lock section is sent. Including the others would let this page overwrite whatever the
        // power page had saved a moment earlier, which the service explicitly refuses to honour anyway.
        var response = await SendAsync(
            MessageTypes.LockSetRule,
            new SetRuleRequest { Identity = identity, Lock = settings },
            null,
            ct).ConfigureAwait(false);

        return CallResult<RuleResult>.From(response);
    }

    public async Task<CallResult<RuleResult>> SetHideRuleAsync(
        AppIdentity identity,
        HideSettings settings,
        CancellationToken ct = default)
    {
        var response = await SendAsync(
            MessageTypes.HideSetRule,
            new SetRuleRequest { Identity = identity, Hide = settings },
            null,
            ct).ConfigureAwait(false);

        return CallResult<RuleResult>.From(response);
    }

    public async Task<CallResult<RuleResult>> SetPowerProfileAsync(
        AppIdentity identity,
        PowerSettings settings,
        CancellationToken ct = default)
    {
        var response = await SendAsync(
            MessageTypes.PowerSetProfile,
            new SetPowerProfileRequest
            {
                Identity = identity,
                Profile = settings.Profile,
                ReducePriority = settings.ReducePriority,
                EcoQos = settings.EcoQos,
            },
            null,
            ct).ConfigureAwait(false);

        return CallResult<RuleResult>.From(response);
    }

    public async Task<CallResult> DeleteRuleAsync(string ruleId, CancellationToken ct = default)
    {
        var response = await SendAsync(
            MessageTypes.PolicyDeleteRule,
            new DeleteRuleRequest { RuleId = ruleId },
            null,
            ct).ConfigureAwait(false);

        return CallResult.From(response);
    }

    public async Task<CallResult> SetPausedAsync(bool paused, CancellationToken ct = default)
    {
        var response = await SendAsync(
            MessageTypes.PolicySetPaused,
            new SetPausedRequest { Paused = paused },
            null,
            ct).ConfigureAwait(false);

        return CallResult.From(response);
    }

    // ---- lock, hide, power state ----

    public Task<LockStateResult?> GetLockStateAsync(CancellationToken ct = default) =>
        GetAsync<LockStateResult>(MessageTypes.LockGetState, null, ct);

    public Task<PowerStatusResult?> GetPowerStatusAsync(CancellationToken ct = default) =>
        GetAsync<PowerStatusResult>(MessageTypes.PowerGetStatus, null, ct);

    public async Task<CallResult> RevealAsync(string appId, CancellationToken ct = default)
    {
        var response = await SendAsync(
            MessageTypes.HideReveal,
            new RevealRequest { AppId = appId, Temporary = true },
            null,
            ct).ConfigureAwait(false);

        return CallResult.From(response);
    }

    public async Task<CallResult> EndSessionAsync(string? appId, CancellationToken ct = default)
    {
        var response = await SendAsync(
            MessageTypes.AuthEndSession,
            new EndSessionRequest { AppId = appId },
            null,
            ct).ConfigureAwait(false);

        return CallResult.From(response);
    }

    // ---- audit (FR-803) ----

    public Task<AuditLogResult?> GetAuditLogAsync(
        int maxEntries,
        AuditAction? filter,
        CancellationToken ct = default) =>
        GetAsync<AuditLogResult>(
            MessageTypes.SystemGetAuditLog,
            new GetAuditLogRequest { MaxEntries = maxEntries, ActionFilter = filter },
            ct);

    // ---- plumbing ----

    private async Task<T?> GetAsync<T>(
        string type,
        object? payload,
        CancellationToken ct,
        TimeSpan? timeout = null)
        where T : class
    {
        var response = await SendAsync(type, payload, timeout, ct).ConfigureAwait(false);

        if (response?.Success != true)
        {
            return null;
        }

        try
        {
            return response.PayloadAs<T>();
        }
        catch (System.Text.Json.JsonException ex)
        {
            _log.LogWarning(ex, "The response to '{Type}' did not match {Shape}.", type, typeof(T).Name);
            return null;
        }
    }

    private async Task<IpcEnvelope?> SendAsync(
        string type,
        object? payload,
        TimeSpan? timeout,
        CancellationToken ct)
    {
        try
        {
            return await _client.SendAsync(type, payload, timeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "'{Type}' could not be sent to the service.", type);
            return null;
        }
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}

/// <summary>Outcome of a mutating call, with a message fit to show the user.</summary>
public class CallResult
{
    public bool Succeeded { get; init; }

    /// <summary>Null on success. On failure, always populated and always user-facing.</summary>
    public string? Message { get; init; }

    public string? ErrorCode { get; init; }

    /// <summary>True when the service could not be reached, which needs different wording. FR-905.</summary>
    public bool ServiceUnavailable { get; init; }

    public static CallResult From(IpcEnvelope? response) => new()
    {
        Succeeded = response?.Success == true,
        ErrorCode = response?.Error?.Code,
        ServiceUnavailable = response is null,
        Message = MessageFor(response),
    };

    protected static string? MessageFor(IpcEnvelope? response)
    {
        if (response is null)
        {
            return "AppGuardian's background service is not running. Start it from Windows Services, "
                   + "or restart your computer.";
        }

        if (response.Success == true)
        {
            return null;
        }

        // The service's own message is preferred. It is written for the user, it knows the specifics, and
        // replacing it with a generic string here would throw away the only useful part of the failure.
        return string.IsNullOrWhiteSpace(response.Error?.Message)
            ? "That change could not be saved."
            : response.Error!.Message;
    }
}

/// <summary>A call result that also carries a payload.</summary>
public sealed class CallResult<T> : CallResult
    where T : class
{
    public T? Value { get; init; }

    public static new CallResult<T> From(IpcEnvelope? response)
    {
        T? value = null;

        if (response?.Success == true)
        {
            try
            {
                value = response.PayloadAs<T>();
            }
            catch (System.Text.Json.JsonException)
            {
                // Treated as a successful call with no value rather than as a failure: the change was
                // made, and reporting it as failed would have the user try again and make it twice.
            }
        }

        return new CallResult<T>
        {
            Succeeded = response?.Success == true,
            ErrorCode = response?.Error?.Code,
            ServiceUnavailable = response is null,
            Message = MessageFor(response),
            Value = value,
        };
    }
}
