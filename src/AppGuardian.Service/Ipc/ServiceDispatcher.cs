using System.Runtime.Versioning;
using AppGuardian.Service.Discovery;
using AppGuardian.Service.Locking;
using AppGuardian.Service.Monitoring;
using AppGuardian.Service.Power;
using AppGuardian.Service.Storage;
using AppGuardian.Service.Security;
using AppGuardian.Shared.Contracts;
using AppGuardian.Shared.Ipc;
using AppGuardian.Shared.Models;
using AppGuardian.Shared.Security;
using Microsoft.Extensions.Logging;

namespace AppGuardian.Service.Ipc;

/// <summary>
/// Wires the API Design §4 message catalog onto a <see cref="MessageDispatcher"/>. SRS §10.
/// </summary>
/// <remarks>
/// Every handler here is thin on purpose: parse the payload, call one component, shape the response.
/// The rules that apply to all messages — version negotiation, elevation, write deduplication,
/// exception-to-error conversion — live in the dispatcher, so a handler that forgets one of them is
/// not possible.
/// <para>
/// Only the messages the service actually owns are registered. <c>auth.verifyWindowsHello</c>,
/// <c>hide.ensureWorkspace</c>, <c>hide.moveToHidden</c> and <c>hide.reveal</c> are agent-side and are
/// deliberately absent, so a client that sends one here gets <c>E_BAD_REQUEST</c> naming the type
/// rather than a silent no-op.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ServiceDispatcher
{
    private readonly PolicyStore _policy;
    private readonly CredentialStore _credentials;
    private readonly UnlockSessionTracker _sessions;
    private readonly AuditLog _audit;
    private readonly AppDiscovery _discovery;
    private readonly JobObjectPowerController _powerController;
    private readonly WmiProcessMonitor _monitor;
    private readonly AgentBridge _agent;
    private readonly AuthTokenGenerator _authTokens;
    private readonly ILogger<ServiceDispatcher> _log;
    private readonly DateTimeOffset _startedUtc = DateTimeOffset.UtcNow;

    public ServiceDispatcher(
        PolicyStore policy,
        CredentialStore credentials,
        UnlockSessionTracker sessions,
        AuditLog audit,
        AppDiscovery discovery,
        JobObjectPowerController powerController,
        WmiProcessMonitor monitor,
        AgentBridge agent,
        AuthTokenGenerator authTokens,
        ILogger<ServiceDispatcher> log)
    {
        _policy = policy;
        _credentials = credentials;
        _sessions = sessions;
        _audit = audit;
        _discovery = discovery;
        _powerController = powerController;
        _monitor = monitor;
        _agent = agent;
        _authTokens = authTokens;
        _log = log;
    }

    public MessageDispatcher Build()
    {
        var dispatcher = new MessageDispatcher(
            log: (message, ex) =>
            {
                if (ex is null)
                {
                    _log.LogDebug("{Message}", message);
                }
                else
                {
                    _log.LogWarning(ex, "{Message}", message);
                }
            });

        // ---- auth.* (§4.1) ----

        dispatcher
            .On(MessageTypes.AuthGetStatus, GetAuthStatusAsync)
            .On(MessageTypes.AuthSetup, SetupAuthAsync)
            .On(MessageTypes.AuthVerifyPin, VerifyPinAsync)
            .On(MessageTypes.AuthVerifyWindowsHello, VerifyWindowsHelloAsync)
            .On(MessageTypes.AuthStartSession, StartSessionAsync)
            .On(MessageTypes.AuthEndSession, EndSessionAsync)

            // Elevation-gated per ADR-006: this destroys the credential, and gating it on the
            // credential the user has forgotten would make it useless as a recovery path.
            .On(MessageTypes.AuthReset, ResetAuthAsync, requiresElevation: true);

        // ---- apps.* (§4.2) ----

        dispatcher
            .On(MessageTypes.AppsListInstalled, ListInstalledAsync)
            .On(MessageTypes.AppsListRunning, ListRunningAsync)
            .On(MessageTypes.AppsResolveIdentity, ResolveIdentityAsync)
            .On(MessageTypes.AppsAddManual, AddManualAsync)
            .On(MessageTypes.AppsGetIcon, GetIconAsync);

        // ---- lock.* / hide.* / power.* (§4.3–4.5) ----

        // One handler for three messages: each sets a different section of the same rule, and the
        // store merges sections. Registering them separately with a shared body keeps the catalog
        // honest — a client can still only reach the section its message names.
        dispatcher
            .On(MessageTypes.LockSetRule, ctx => SetRuleAsync(ctx, RuleSection.Lock))
            .On(MessageTypes.HideSetRule, ctx => SetRuleAsync(ctx, RuleSection.Hide))
            .On(MessageTypes.HideReveal, RevealAsync)
            .On(MessageTypes.PowerSetProfile, SetPowerProfileAsync)
            .On(MessageTypes.LockUnlock, UnlockAsync)
            .On(MessageTypes.LockGetState, GetLockStateAsync)
            .On(MessageTypes.PowerGetStatus, GetPowerStatusAsync);

        // ---- policy.* (SC-03) ----

        dispatcher
            .On(MessageTypes.PolicyGet, GetPolicyAsync)
            .On(MessageTypes.PolicyDeleteRule, DeleteRuleAsync)
            .On(MessageTypes.PolicySetPaused, SetPausedAsync);

        // ---- system.* (§4.6) ----

        dispatcher
            .On(MessageTypes.SystemStatus, GetSystemStatusAsync)
            .On(MessageTypes.SystemGetAuditLog, GetAuditLogAsync)
            .On(MessageTypes.SystemPing, PingAsync)
            .On(MessageTypes.SystemAudit, AppendAuditAsync);

        return dispatcher;
    }

    // ---- auth handlers ----

    private Task<IpcEnvelope> GetAuthStatusAsync(RequestContext ctx)
    {
        var sid = ctx.ClientUserSid;

        if (sid is null)
        {
            return Task.FromResult(ctx.Request.Fail(
                ErrorCodes.Unauthorized,
                "AppGuardian could not identify the signed-in user."));
        }

        // Hello availability is an agent-side fact — only an interactive process can ask. Reported as
        // false when the agent is absent rather than guessed, so the dashboard offers the PIN path
        // instead of a Hello button that cannot work.
        var status = _credentials.StatusFor(sid, helloAvailable: _agent.IsAgentPresent);

        status.ActiveUnlockedAppIds = _sessions.Active().Select(s => s.AppId).ToList();

        return Task.FromResult(ctx.Request.Ok(status));
    }

    private async Task<IpcEnvelope> SetupAuthAsync(RequestContext ctx)
    {
        var request = ctx.PayloadAs<AuthSetupRequest>();

        if (request is null || string.IsNullOrEmpty(request.FallbackSecret))
        {
            return ctx.Request.Fail(ErrorCodes.BadRequest, "A fallback PIN or password is required.");
        }

        if (ctx.ClientUserSid is not { } sid)
        {
            return ctx.Request.Fail(ErrorCodes.Unauthorized, "The calling user could not be identified.");
        }

        // Changing an existing secret has to prove something, otherwise any process running as the
        // interactive user could silently replace the credential and unlock everything. The API Design
        // gives auth.setup no field for the current secret (SC-09, unresolved), so the only proof
        // available here is elevation — the same gate auth.reset uses. If the ruling on SC-09 adds a
        // currentSecret field, this branch should verify that instead.
        if (_credentials.IsConfiguredFor(sid) && !ctx.IsElevated)
        {
            return ctx.Request.Fail(
                ErrorCodes.Unauthorized,
                "A PIN is already set up. Changing it requires administrator approval.");
        }

        if (request.FallbackSecret.Length < MinSecretLength)
        {
            return ctx.Request.Fail(
                ErrorCodes.BadRequest,
                $"Use at least {MinSecretLength} characters.");
        }

        await _credentials
            .SetupAsync(sid, request.FallbackSecret, request.PreferredMethod, ctx.CancellationToken)
            .ConfigureAwait(false);

        await _audit.AppendAsync(
            AuditActor.User,
            AuditAction.ConfigChange,
            AuditResult.Ok,
            detail: "Authentication credential configured.",
            ct: ctx.CancellationToken).ConfigureAwait(false);

        return ctx.Request.Ok(_credentials.StatusFor(sid, _agent.IsAgentPresent));
    }

    private async Task<IpcEnvelope> VerifyPinAsync(RequestContext ctx)
    {
        var request = ctx.PayloadAs<AuthVerifyRequest>();

        if (request is null || string.IsNullOrEmpty(request.Secret))
        {
            return ctx.Request.Fail(ErrorCodes.BadRequest, "No PIN or password was supplied.");
        }

        if (ctx.ClientUserSid is not { } sid)
        {
            return ctx.Request.Fail(ErrorCodes.Unauthorized, "The calling user could not be identified.");
        }

        var result = await _credentials
            .VerifyAsync(sid, request.Secret, ctx.CancellationToken)
            .ConfigureAwait(false);

        if (result.Verified)
        {
            // Success is not audited as an event of its own; the unlock it authorises is, which is the
            // thing the user would actually look for in the log.
            result.AuthorizationToken = _authTokens.GenerateForSid(sid);
            return ctx.Request.Ok(result);
        }

        await _audit.AppendAsync(
            AuditActor.User,
            AuditAction.AuthFail,
            AuditResult.Denied,
            appId: request.AppId,
            detail: result.LockedOut ? "Locked out after repeated failures." : "Incorrect PIN or password.",
            ct: ctx.CancellationToken).ConfigureAwait(false);

        // A failed verification is a successful *response* carrying a failure result, not a transport
        // error — the client needs the attempts-remaining and lockout fields to render the prompt, and
        // an error envelope would carry none of them.
        return ctx.Request.Fail(
            result.LockedOut ? ErrorCodes.AuthLockedOut : ErrorCodes.AuthFailed,
            result.Reason ?? "Verification failed.",
            new Dictionary<string, object?>
            {
                ["attemptsRemaining"] = result.AttemptsRemaining,
                ["lockedUntilUtc"] = result.LockedUntilUtc,
            });
    }

    private async Task<IpcEnvelope> VerifyWindowsHelloAsync(RequestContext ctx)
    {
        if (ctx.ClientUserSid is not { } sid)
        {
            return ctx.Request.Fail(ErrorCodes.Unauthorized, "The calling user could not be identified.");
        }

        var request = ctx.PayloadAs<AuthVerifyRequest>() ?? new AuthVerifyRequest();
        var result = await _agent.VerifyWindowsHelloAsync(request, ctx.CancellationToken).ConfigureAwait(false);

        if (result is not { Verified: true })
        {
            return ctx.Request.Fail(ErrorCodes.AuthFailed, result?.Reason ?? "Windows Hello did not verify you.");
        }

        await _credentials.NoteExternalSuccessAsync(sid, ctx.CancellationToken).ConfigureAwait(false);
        result.AuthorizationToken = _authTokens.GenerateForSid(sid);
        return ctx.Request.Ok(result);
    }

    private async Task<IpcEnvelope> StartSessionAsync(RequestContext ctx)
    {
        var request = ctx.PayloadAs<StartSessionRequest>();

        if (request is null || string.IsNullOrWhiteSpace(request.AppId))
        {
            return ctx.Request.Fail(ErrorCodes.BadRequest, "An appId is required.");
        }

        if (!RequireAuthenticatedSession(request.AuthorizationToken, ctx.ClientUserSid))
        {
            return ctx.Request.Fail(ErrorCodes.Unauthorized, "Authenticate again to unlock this app.");
        }

        var rule = _policy.FindByAppId(request.AppId);

        if (rule is null)
        {
            return ctx.Request.Fail(ErrorCodes.NotFound, "That app has no AppGuardian rule.");
        }

        var session = _sessions.Grant(request.AppId, rule.Lock, new StartSessionOptions
        {
            DurationSeconds = request.DurationSeconds,
            Mode = request.Mode,
            ProcessId = request.ProcessId,
            // The server derives authorization from the consumed grant, never from a client claim.
            Method = AuthMethod.Pin,
        });

        await _audit.AppendAsync(
            AuditActor.User,
            AuditAction.Unlock,
            AuditResult.Ok,
            appId: request.AppId,
            ruleId: rule.RuleId,
            detail: $"Unlocked after verified authentication; {DescribeSession(session)}.",
            ct: ctx.CancellationToken).ConfigureAwait(false);

        return ctx.Request.Ok(session);
    }

    private async Task<IpcEnvelope> EndSessionAsync(RequestContext ctx)
    {
        var request = ctx.PayloadAs<EndSessionRequest>();

        var ended = string.IsNullOrWhiteSpace(request?.AppId)
            ? _sessions.EndAll("locked by request")
            : _sessions.End(request!.AppId!, "locked by request") ? 1 : 0;

        if (ended > 0)
        {
            await _audit.AppendAsync(
                AuditActor.User,
                AuditAction.Lock,
                AuditResult.Ok,
                appId: request?.AppId,
                detail: ended == 1 ? "App re-locked." : $"{ended} app(s) re-locked.",
                ct: ctx.CancellationToken).ConfigureAwait(false);
        }

        return ctx.Request.Ok(new { endedCount = ended });
    }

    private async Task<IpcEnvelope> ResetAuthAsync(RequestContext ctx)
    {
        if (ctx.ClientUserSid is not { } sid)
        {
            return ctx.Request.Fail(ErrorCodes.Unauthorized, "The calling user could not be identified.");
        }

        var reset = await _credentials.ResetAsync(sid, ctx.CancellationToken).ConfigureAwait(false);

        // Sessions die with the credential. Leaving them alive would mean a reset silently left
        // whatever was unlocked at the time unlocked, with no credential left to re-lock it against.
        _sessions.EndAll("credential reset");

        await _audit.AppendAsync(
            AuditActor.User,
            AuditAction.ConfigChange,
            reset ? AuditResult.Ok : AuditResult.Denied,
            detail: reset
                ? "Credential reset by an administrator. All protection is unauthenticated until setup runs again."
                : "Credential reset requested but no credential existed.",
            ct: ctx.CancellationToken).ConfigureAwait(false);

        return ctx.Request.Ok(new { reset });
    }

    private const int MinSecretLength = 4;

    private bool RequireAuthenticatedSession(string? token, string? clientUserSid) =>
        PipeSecurityFactory.RequireAuthenticatedSession(_authTokens.TryConsume, token, clientUserSid);

    private static string DescribeSession(UnlockSession session) =>
        session.Mode == TempUnlockMode.UntilAppClose
            ? "until the app closes"
            : $"expires {session.ExpiresUtc:HH:mm}";

    private enum RuleSection
    {
        Lock,
        Hide,
    }

    // ---- apps handlers ----

    private async Task<IpcEnvelope> ListInstalledAsync(RequestContext ctx)
    {
        var request = ctx.PayloadAs<ListInstalledRequest>() ?? new ListInstalledRequest();

        var apps = await _discovery.ListInstalledAsync(ctx.CancellationToken).ConfigureAwait(false);

        var result = Project(apps, request.Filter, request.IncludeIcons);

        return ctx.Request.Ok(result);
    }

    private Task<IpcEnvelope> ListRunningAsync(RequestContext ctx)
    {
        var request = ctx.PayloadAs<ListInstalledRequest>() ?? new ListInstalledRequest();

        // Built from the monitor's live snapshot rather than from the discovery cache, because the
        // point of this message is "what is running right now" and the cache is up to five minutes old.
        var apps = _monitor.ListRunning()
            .Select(running => new DiscoveredApp
            {
                Identity = new AppIdentity
                {
                    // The process name, not the window title: titles change as the user works
                    // ("Untitled - Notepad" becomes "budget.txt - Notepad") and a picker row whose
                    // label moves is a picker row the user cannot find twice.
                    DisplayName = running.ProcessName,
                    ExecutablePath = running.ExecutablePath,
                    PackageFamilyName = running.PackageFamilyName,
                }.WithDerivedId(),
                IsRunning = true,
                ProcessIds = new List<int> { running.ProcessId },
                Source = DiscoverySource.RunningProcess,
                UnsupportedReason = running.IsElevated
                    ? "This app runs with administrator rights, so AppGuardian cannot lock or restrict it."
                    : null,
            })

            // One entry per app, not per process: a browser with fifteen renderer processes should be
            // one row in the picker with fifteen PIDs behind it.
            .GroupBy(app => app.Identity.AppId, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group.First();
                first.ProcessIds = group.SelectMany(a => a.ProcessIds).Distinct().ToList();
                return first;
            })
            .ToList();

        return Task.FromResult(ctx.Request.Ok(Project(apps, request.Filter, includeIcons: false)));
    }

    private async Task<IpcEnvelope> ResolveIdentityAsync(RequestContext ctx)
    {
        var request = ctx.PayloadAs<ResolveIdentityRequest>();

        if (request is null ||
            (string.IsNullOrWhiteSpace(request.ExecutablePath) &&
             string.IsNullOrWhiteSpace(request.PackageFamilyName) &&
             request.ProcessId is null))
        {
            return ctx.Request.Fail(
                ErrorCodes.BadRequest,
                "Supply an executable path, a package family name, or a process id.");
        }

        var identity = await _discovery.ResolveAsync(request, ctx.CancellationToken).ConfigureAwait(false);

        return identity is null
            ? ctx.Request.Fail(ErrorCodes.NotFound, "That app could not be identified.")
            : ctx.Request.Ok(identity);
    }

    private async Task<IpcEnvelope> AddManualAsync(RequestContext ctx)
    {
        var request = ctx.PayloadAs<ResolveIdentityRequest>();

        if (request is null || string.IsNullOrWhiteSpace(request.ExecutablePath))
        {
            return ctx.Request.Fail(ErrorCodes.BadRequest, "An executable path is required.");
        }

        // Existence is checked here rather than left to the enforcement loop, because a typo'd path
        // would otherwise become a rule that silently never matches anything and looks like a bug in
        // the locking.
        if (!File.Exists(request.ExecutablePath))
        {
            return ctx.Request.Fail(
                ErrorCodes.NotFound,
                "No file exists at that path.",
                new Dictionary<string, object?> { ["executablePath"] = request.ExecutablePath });
        }

        var identity = await _discovery.ResolveAsync(request, ctx.CancellationToken).ConfigureAwait(false)
                       ?? new AppIdentity
                       {
                           DisplayName = Path.GetFileNameWithoutExtension(request.ExecutablePath),
                           ExecutablePath = request.ExecutablePath,
                       }.WithDerivedId();

        // The app is invalidated, not the rule created: adding manually only makes the app *pickable*.
        // Creating a rule here would protect an app the user has not yet configured, which is the
        // opposite of least surprise.
        _discovery.Invalidate();

        return ctx.Request.Ok(new DiscoveredApp
        {
            Identity = identity,
            Source = DiscoverySource.ManuallyAdded,
            RuleId = _policy.FindByAppId(identity.AppId)?.RuleId,
        });
    }

    private async Task<IpcEnvelope> GetIconAsync(RequestContext ctx)
    {
        var request = ctx.PayloadAs<GetIconRequest>();

        if (request is null || string.IsNullOrWhiteSpace(request.AppId))
        {
            return ctx.Request.Fail(ErrorCodes.BadRequest, "An appId is required.");
        }

        var icon = await _discovery
            .GetIconBase64Async(request.AppId, ctx.CancellationToken)
            .ConfigureAwait(false);

        // A missing icon is an Ok response with a null icon, not E_NOT_FOUND: the app exists, it just
        // has no extractable icon, and the UI's placeholder is the correct outcome either way.
        return ctx.Request.Ok(new GetIconResult { AppId = request.AppId, IconBase64 = icon });
    }

    private AppListResult Project(IEnumerable<DiscoveredApp> apps, string? filter, bool includeIcons)
    {
        var query = apps.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(filter))
        {
            query = query.Where(app =>
                app.Identity.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                (app.Identity.ExecutablePath?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var list = query
            .OrderBy(app => app.Identity.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        foreach (var app in list)
        {
            // Stamped at projection time so the picker can show which apps already have rules without
            // a second round trip, and so the discovery cache never holds policy state that could go
            // stale behind a rule change.
            app.RuleId = _policy.FindByAppId(app.Identity.AppId)?.RuleId;

            if (!includeIcons)
            {
                app.Identity.IconBase64 = null;
            }
        }

        return new AppListResult
        {
            Apps = list,

            // Icons are never inlined, whatever the request asks: two hundred 2 KB PNGs would exceed
            // the 256 KB envelope cap (SC-15c). The flag tells the UI to fetch them individually
            // instead of leaving it to wonder why they are missing.
            IconsOmitted = true,
        };
    }

    // ---- rule handlers (lock.setRule, hide.setRule, power.setProfile) ----

    private async Task<IpcEnvelope> SetRuleAsync(RequestContext ctx, RuleSection section)
    {
        var request = ctx.PayloadAs<SetRuleRequest>();

        if (request is null)
        {
            return ctx.Request.Fail(ErrorCodes.BadRequest, "An app identity is required.");
        }

        if (!RequireAuthenticatedSession(request.AuthorizationToken, ctx.ClientUserSid))
        {
            return ctx.Request.Fail(ErrorCodes.Unauthorized, "Authenticate again before changing protection rules.");
        }

        if (string.IsNullOrWhiteSpace(request.Identity.AppId))
        {
            // Derived rather than rejected when the caller supplied a path: the appId is a function of
            // the path, and making the UI compute it would duplicate the derivation rule in two places.
            if (request.Identity.ExecutablePath is null && request.Identity.PackageFamilyName is null)
            {
                return ctx.Request.Fail(ErrorCodes.BadRequest, "An app identity is required.");
            }

            request.Identity = request.Identity.WithDerivedId();
        }

        // Only the section this message owns is passed through. A lock.setRule carrying a power payload
        // is a client bug, and honouring it would let one page of the dashboard silently overwrite
        // another's settings.
        var lockSettings = section == RuleSection.Lock ? request.Lock : null;
        var hideSettings = section == RuleSection.Hide ? request.Hide : null;

        if (lockSettings is null && hideSettings is null)
        {
            return ctx.Request.Fail(
                ErrorCodes.BadRequest,
                section == RuleSection.Lock
                    ? "A lock section is required for lock.setRule."
                    : "A hide section is required for hide.setRule.");
        }

        lockSettings?.Normalize();

        var existed = _policy.FindByAppId(request.Identity.AppId) is not null;

        var rule = await _policy.UpsertAsync(
            request.Identity,
            request.Identity.DisplayName,
            lockSettings,
            hideSettings,
            powerSettings: null,
            ctx.CancellationToken).ConfigureAwait(false);

        // Turning locking off must also drop any live unlock session, otherwise the session outlives
        // the rule and a later re-enable would find the app already unlocked.
        if (lockSettings is { Enabled: false })
        {
            _sessions.End(rule.Identity.AppId, "locking disabled");
        }

        await _audit.AppendAsync(
            AuditActor.User,
            AuditAction.ConfigChange,
            AuditResult.Ok,
            appId: rule.Identity.AppId,
            ruleId: rule.RuleId,
            detail: DescribeRuleChange(section, lockSettings, hideSettings),
            ct: ctx.CancellationToken).ConfigureAwait(false);

        // No policy.changed push here: PolicyChangeBroadcaster is hooked to PolicyStore.Changed and
        // fires for every mutation path. Publishing from both would deliver the event twice and make
        // the dashboard re-fetch twice per save.
        return ctx.Request.Ok(new RuleResult { Rule = rule, Created = !existed });
    }

    private async Task<IpcEnvelope> SetPowerProfileAsync(RequestContext ctx)
    {
        var request = ctx.PayloadAs<SetPowerProfileRequest>();

        if (request is null || string.IsNullOrWhiteSpace(request.Identity.AppId))
        {
            return ctx.Request.Fail(ErrorCodes.BadRequest, "An app identity is required.");
        }

        if (!RequireAuthenticatedSession(request.AuthorizationToken, ctx.ClientUserSid))
        {
            return ctx.Request.Fail(ErrorCodes.Unauthorized, "Authenticate again before changing power restrictions.");
        }

        var power = new PowerSettings
        {
            Profile = request.Profile,
            ReducePriority = request.ReducePriority,
            EcoQos = request.EcoQos,
        };

        // Asking for EcoQoS on a build that does not have it is answered now, not silently dropped at
        // enforcement time. The rule is still saved — the CPU cap and priority parts work — but the
        // response says which part will not take effect.
        string? unsupported = null;

        if (request.EcoQos && !_powerController.SupportsEcoQos)
        {
            unsupported = "This version of Windows does not support efficiency mode. "
                          + "The CPU limit and priority settings will still be applied.";
        }

        var existed = _policy.FindByAppId(request.Identity.AppId) is not null;

        var rule = await _policy.UpsertAsync(
            request.Identity,
            request.Identity.DisplayName,
            lockSettings: null,
            hideSettings: null,
            powerSettings: power,
            ctx.CancellationToken).ConfigureAwait(false);

        // Removing the profile has to release live throttles here rather than waiting for the
        // enforcement pass, because the user's mental model is that the slider takes effect at once.
        if (request.Profile is null)
        {
            foreach (var pid in RunningPidsFor(rule.Identity.AppId))
            {
                _powerController.RemoveThrottle(pid);
            }
        }

        await _audit.AppendAsync(
            AuditActor.User,
            AuditAction.ConfigChange,
            AuditResult.Ok,
            appId: rule.Identity.AppId,
            ruleId: rule.RuleId,
            detail: request.Profile is null
                ? "Power restriction removed."
                : $"Power profile set to {request.Profile}.",
            ct: ctx.CancellationToken).ConfigureAwait(false);

        // No policy.changed push here: PolicyChangeBroadcaster is hooked to PolicyStore.Changed and
        // fires for every mutation path. Publishing from both would deliver the event twice and make
        // the dashboard re-fetch twice per save.
        return ctx.Request.Ok(new RuleResult { Rule = rule, Created = !existed });
    }

    private static string DescribeRuleChange(
        RuleSection section,
        LockSettings? lockSettings,
        HideSettings? hideSettings) => section switch
    {
        RuleSection.Lock => lockSettings!.Enabled ? "Locking enabled." : "Locking disabled.",
        _ => hideSettings!.Enabled ? "Hiding enabled." : "Hiding disabled.",
    };

    // ---- lock state / power status ----

    private async Task<IpcEnvelope> UnlockAsync(RequestContext ctx)
    {
        var request = ctx.PayloadAs<UnlockRequest>();

        if (request is null || string.IsNullOrWhiteSpace(request.AppId))
        {
            return ctx.Request.Fail(ErrorCodes.BadRequest, "An appId is required.");
        }

        var rule = _policy.FindByAppId(request.AppId);

        if (rule is null)
        {
            return ctx.Request.Fail(ErrorCodes.NotFound, "That app has no AppGuardian rule.");
        }

        if (!RequireAuthenticatedSession(request.AuthorizationToken, ctx.ClientUserSid))
        {
            return ctx.Request.Fail(ErrorCodes.Unauthorized, "Authenticate again to unlock this app.");
        }

        var session = _sessions.Grant(request.AppId, rule.Lock, new StartSessionOptions
        {
            DurationSeconds = request.DurationSeconds,
            ProcessId = request.ProcessId,
            Method = request.Method,
        });

        await _audit.AppendAsync(
            AuditActor.User,
            AuditAction.Unlock,
            AuditResult.Ok,
            appId: request.AppId,
            ruleId: rule.RuleId,
            detail: $"Unlocked from the dashboard; {DescribeSession(session)}.",
            ct: ctx.CancellationToken).ConfigureAwait(false);

        return ctx.Request.Ok(session);
    }

    private Task<IpcEnvelope> GetLockStateAsync(RequestContext ctx)
    {
        var running = _monitor.ListRunning();

        var entries = _policy.Current.Rules
            .Where(rule => rule.Lock.Enabled)
            .Select(rule =>
            {
                var pids = PidsFor(running, rule.Identity.AppId);

                return new LockStateEntry
                {
                    AppId = rule.Identity.AppId,
                    DisplayName = rule.Identity.DisplayName,

                    // "Locked" here means "will demand authentication", which is the inverse of having
                    // a live session — not whether an overlay happens to be on screen right now.
                    IsLocked = !_sessions.IsUnlocked(rule.Identity.AppId),
                    IsRunning = pids.Count > 0,
                    UnlockExpiresUtc = _sessions.Get(rule.Identity.AppId)?.ExpiresUtc,
                };
            })
            .OrderBy(entry => entry.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return Task.FromResult(ctx.Request.Ok(new LockStateResult { Entries = entries }));
    }

    /// <summary>
    /// <c>hide.reveal</c> — brings a hidden window back. FR-604, FR-606.
    /// </summary>
    /// <remarks>
    /// Forwarded to the agent, which owns the window and the desktop. The service is in the middle because
    /// the dashboard only holds one pipe, to the service (SC-02); a dashboard that dialled the agent
    /// directly would need a second connection and its own reconnect logic for a process that restarts with
    /// the session.
    /// <para>
    /// Not audited here. The agent is the only party that knows whether the window actually moved, and it
    /// emits the <c>Reveal</c> entry itself over <c>system.audit</c> — auditing on both sides would
    /// double-count every reveal in the log the user reads.
    /// </para>
    /// </remarks>
    private async Task<IpcEnvelope> RevealAsync(RequestContext ctx)
    {
        var request = ctx.PayloadAs<RevealRequest>();

        if (request is null || string.IsNullOrWhiteSpace(request.AppId))
        {
            return ctx.Request.Fail(ErrorCodes.BadRequest, "A reveal request needs an appId.");
        }

        var result = await _agent.RevealAsync(request, ctx.CancellationToken).ConfigureAwait(false);

        // Ok even when the reveal failed, with the reason in the payload. A transport-level failure would
        // tell the dashboard "the service is unreachable", which is the wrong thing to show a user whose
        // service answered perfectly well and whose agent is the part that is missing.
        return ctx.Request.Ok(result);
    }

    private Task<IpcEnvelope> GetPowerStatusAsync(RequestContext ctx)
    {
        var running = _monitor.ListRunning();
        var active = _powerController.ActiveThrottles;

        var entries = _policy.Current.Rules
            .Where(rule => rule.Power.Profile is not null)
            .Select(rule =>
            {
                var pids = PidsFor(running, rule.Identity.AppId);
                var spec = PowerProfileMap.Resolve(rule.Power.Profile!.Value);

                return new PowerStatusEntry
                {
                    AppId = rule.Identity.AppId,
                    DisplayName = rule.Identity.DisplayName,
                    Profile = rule.Power.Profile,

                    // Throttled means the kernel object actually exists for at least one live PID, not
                    // merely that a profile is configured. The two differ whenever an app is not running
                    // or the cap could not be applied, and conflating them would show the user a
                    // restriction that is not in force.
                    IsThrottled = pids.Any(active.ContainsKey),
                    ProcessIds = pids,
                    CpuRateCapPercent = spec.CpuRateCapPercent,
                    UnsupportedReason = rule.Identity.IsPackaged
                        ? "This is a Microsoft Store app. CPU limits cannot be applied because Store apps share a host process with others."
                        : null,
                };
            })
            .OrderBy(entry => entry.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return Task.FromResult(ctx.Request.Ok(new PowerStatusResult
        {
            Entries = entries,
            EcoQosSupported = _powerController.SupportsEcoQos,
            OnBattery = _powerController.IsOnBattery(),
        }));
    }

    private List<int> RunningPidsFor(string appId) => PidsFor(_monitor.ListRunning(), appId);

    private static List<int> PidsFor(IReadOnlyList<RunningApp> running, string appId) => running
        .Where(app => string.Equals(
            AppIdentity.DeriveAppId(app.ExecutablePath, app.PackageFamilyName),
            appId,
            StringComparison.OrdinalIgnoreCase))
        .Select(app => app.ProcessId)
        .ToList();

    // ---- policy handlers ----

    private Task<IpcEnvelope> GetPolicyAsync(RequestContext ctx) =>
        Task.FromResult(ctx.Request.Ok(new PolicySnapshot
        {
            Policy = _policy.Current,
            IntegrityHash = _policy.Current.IntegrityHash,
        }));

    private async Task<IpcEnvelope> DeleteRuleAsync(RequestContext ctx)
    {
        var request = ctx.PayloadAs<DeleteRuleRequest>();

        if (request is null || string.IsNullOrWhiteSpace(request.RuleId))
        {
            return ctx.Request.Fail(ErrorCodes.BadRequest, "A ruleId is required.");
        }

        if (!RequireAuthenticatedSession(request.AuthorizationToken, ctx.ClientUserSid))
        {
            return ctx.Request.Fail(ErrorCodes.Unauthorized, "Authenticate again before deleting a protection rule.");
        }

        // Captured before the delete, because afterwards there is no rule to look the appId up from and
        // the throttle teardown below needs it.
        var rule = _policy.Current.FindByRuleId(request.RuleId);

        var deleted = await _policy.DeleteAsync(request.RuleId, ctx.CancellationToken).ConfigureAwait(false);

        if (!deleted)
        {
            // Idempotent by design: deleting an already-deleted rule is the outcome the caller wanted,
            // and the write-dedup cache in the dispatcher can legitimately replay this message.
            return ctx.Request.Ok(new { deleted = false });
        }

        if (rule is not null)
        {
            // Everything the rule was doing has to be undone here. Leaving a CPU cap or an unlock
            // session behind after the rule is gone would be a restriction with nothing in the UI to
            // explain or remove it.
            foreach (var pid in RunningPidsFor(rule.Identity.AppId))
            {
                _powerController.RemoveThrottle(pid);
            }

            _sessions.End(rule.Identity.AppId, "rule deleted");
        }

        await _audit.AppendAsync(
            AuditActor.User,
            AuditAction.ConfigChange,
            AuditResult.Ok,
            appId: rule?.Identity.AppId,
            ruleId: request.RuleId,
            detail: "Rule deleted. Any hidden windows must be revealed from the hidden apps page.",
            ct: ctx.CancellationToken).ConfigureAwait(false);

        // See SetRuleAsync: PolicyChangeBroadcaster owns the policy.changed push.
        return ctx.Request.Ok(new { deleted = true });
    }

    private async Task<IpcEnvelope> SetPausedAsync(RequestContext ctx)
    {
        var request = ctx.PayloadAs<SetPausedRequest>();

        if (request is null)
        {
            return ctx.Request.Fail(ErrorCodes.BadRequest, "A paused flag is required.");
        }

        if (!RequireAuthenticatedSession(request.AuthorizationToken, ctx.ClientUserSid))
        {
            return ctx.Request.Fail(ErrorCodes.Unauthorized, "Authenticate again before changing protection state.");
        }

        await _policy.SetPausedAsync(request.Paused, ctx.CancellationToken).ConfigureAwait(false);

        if (request.Paused)
        {
            // Pausing releases throttles but deliberately does not reveal hidden windows: revealing
            // them would move the user's windows around as a side effect of a settings toggle. The
            // hidden apps page is the one place that moves windows.
            foreach (var pid in _powerController.ActiveThrottles.Keys.ToList())
            {
                _powerController.RemoveThrottle(pid);
            }

            _sessions.EndAll("protection paused");
        }

        await _audit.AppendAsync(
            AuditActor.User,
            AuditAction.ConfigChange,
            AuditResult.Ok,
            detail: request.Paused
                ? "Protection paused. Locking, hiding, and CPU limits are not being enforced."
                : "Protection resumed.",
            ct: ctx.CancellationToken).ConfigureAwait(false);

        // See SetRuleAsync: PolicyChangeBroadcaster owns the policy.changed push.
        return ctx.Request.Ok(new { paused = request.Paused });
    }

    // ---- system handlers ----

    private Task<IpcEnvelope> GetSystemStatusAsync(RequestContext ctx)
    {
        var rules = _policy.Current.Rules;

        // Degradation is reported from whichever component is degraded, with the store first: a policy
        // that failed to load is a more serious problem than a monitor that fell back to polling, and
        // showing only one reason means showing the worse one.
        var degradedReason = _policy.IsDegraded ? _policy.DegradedReason
            : _monitor.IsDegraded ? _monitor.DegradedReason
            : null;

        var status = new SystemStatus
        {
            ServiceRunning = true,
            AgentRunning = _agent.IsAgentPresent,
            ProtectedCount = rules.Count(rule => rule.Enabled && rule.HasAnyProtection),
            LockedCount = rules.Count(rule =>
                rule.Lock.Enabled && !_sessions.IsUnlocked(rule.Identity.AppId)),
            HiddenCount = rules.Count(rule => rule.Hide.Enabled),
            ThrottledCount = _powerController.ActiveThrottles.Count,

            // Virtual desktop support is an agent-side probe (ADR-002). With no agent there is no
            // answer, and false is the honest one — hiding genuinely will not work right now.
            VirtualDesktopSupported = _agent.IsAgentPresent,
            WindowsHelloAvailable = _agent.IsAgentPresent,
            ProtectionPaused = _policy.Current.ProtectionPaused,
            DegradedReason = degradedReason,
            ServiceUptime = DateTimeOffset.UtcNow - _startedUtc,
            ServiceVersion = ApiVersion.Current,
        };

        return Task.FromResult(ctx.Request.Ok(status));
    }

    private async Task<IpcEnvelope> GetAuditLogAsync(RequestContext ctx)
    {
        var request = ctx.PayloadAs<GetAuditLogRequest>() ?? new GetAuditLogRequest();

        // Clamped rather than rejected: an oversized request is almost always a UI paging bug, and
        // failing the call would leave the audit page blank instead of merely showing fewer rows. The
        // cap exists because the whole reply must fit the 256 KB envelope (SC-15c).
        var limit = Math.Clamp(request.MaxEntries, 1, AuditLog.MaxReadLimit);

        var entries = await _audit
            .ReadRecentAsync(limit, request.SinceUtc, ctx.CancellationToken)
            .ConfigureAwait(false);

        var filtered = request.ActionFilter is { } action
            ? entries.Where(entry => entry.Action == action).ToList()
            : entries.ToList();

        return ctx.Request.Ok(new AuditLogResult
        {
            Entries = filtered,
            Truncated = entries.Count >= limit,
        });
    }

    private Task<IpcEnvelope> PingAsync(RequestContext ctx) =>
        Task.FromResult(ctx.Request.Ok(new PingResult
        {
            Component = PolicyConstants.ServiceName,
            Version = ApiVersion.Current,
            Uptime = DateTimeOffset.UtcNow - _startedUtc,
        }));

    private async Task<IpcEnvelope> AppendAuditAsync(RequestContext ctx)
    {
        var request = ctx.PayloadAs<AuditRequest>();

        if (request?.Entry is null)
        {
            return ctx.Request.Fail(ErrorCodes.BadRequest, "An audit entry is required.");
        }

        // The actor is overwritten, not trusted: this message exists so the agent can flush the events
        // it buffered while the service was down (FR-903), and letting a caller stamp its own actor
        // would make the log's attribution meaningless.
        request.Entry.Actor = AuditActor.Agent;

        await _audit.AppendAsync(request.Entry, ctx.CancellationToken).ConfigureAwait(false);

        return ctx.Request.Ok(new { accepted = true });
    }
}
