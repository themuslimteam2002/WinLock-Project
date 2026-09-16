using System.Runtime.Versioning;
using AppGuardian.Service.Ipc;
using AppGuardian.Service.Locking;
using AppGuardian.Service.Power;
using AppGuardian.Service.Monitoring;
using AppGuardian.Service.Storage;
using AppGuardian.Shared.Contracts;
using AppGuardian.Shared.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AppGuardian.Service.Enforcement;

/// <summary>
/// The loop that makes policy real: reacts to launches, applies and releases throttles, expires unlock
/// sessions, and heartbeats the agent. SRS §7, FR-201, FR-205.
/// </summary>
/// <remarks>
/// Event-driven for the parts with a latency budget and reconciled on a timer for everything else.
/// A launch has one second to reach an overlay (NFR-P4), which only an event can meet; a CPU cap that
/// is a few seconds late is invisible. The reconciliation pass exists because events are lossy — a
/// process that started while the WMI watcher was reconnecting would otherwise stay unrestricted for
/// the rest of its life.
/// <para>
/// Nothing in here throws out to the host. A background service that faults takes the whole process
/// with it under the default <c>BackgroundServiceExceptionBehavior</c>, which would mean one unreadable
/// process handle could stop all protection.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class EnforcementService : BackgroundService
{
    private readonly PolicyStore _policy;
    private readonly UnlockSessionTracker _sessions;
    private readonly JobObjectPowerController _power;
    private readonly WmiProcessMonitor _monitor;
    private readonly AgentBridge _agent;
    private readonly AuditLog _audit;
    private readonly ILogger<EnforcementService> _log;

    /// <summary>PIDs already announced to the agent, so a reconciliation pass does not re-overlay.</summary>
    private readonly HashSet<int> _announced = new();

    public EnforcementService(
        PolicyStore policy,
        UnlockSessionTracker sessions,
        JobObjectPowerController power,
        WmiProcessMonitor monitor,
        AgentBridge agent,
        AuditLog audit,
        ILogger<EnforcementService> log)
    {
        _policy = policy;
        _sessions = sessions;
        _power = power;
        _monitor = monitor;
        _agent = agent;
        _audit = audit;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Subscribed before the monitor starts, so no launch that happens during startup is missed.
        _monitor.AppLaunched += OnAppLaunched;
        _monitor.AppExited += OnAppExited;
        _sessions.SessionEnded += OnSessionEnded;

        try
        {
            await _monitor.StartAsync(stoppingToken).ConfigureAwait(false);

            // One immediate pass, so restarting the service re-applies restrictions to apps that are
            // already running rather than waiting out a reconciliation interval.
            await ReconcileAsync(stoppingToken).ConfigureAwait(false);

            using var timer = new PeriodicTimer(ReconcileInterval);

            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await ReconcileAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            _monitor.AppLaunched -= OnAppLaunched;
            _monitor.AppExited -= OnAppExited;
            _sessions.SessionEnded -= OnSessionEnded;

            await _monitor.StopAsync().ConfigureAwait(false);
        }
    }

    // ---- event paths ----

    private void OnAppLaunched(object? sender, AppLaunchedEventArgs e)
    {
        // Fire-and-forget on purpose: this runs on a WMI callback thread, and blocking it would stall
        // delivery of every subsequent launch event. The continuation swallows faults for the same
        // reason the class-level note gives.
        _ = HandleLaunchAsync(e);
    }

    private async Task HandleLaunchAsync(AppLaunchedEventArgs e)
    {
        try
        {
            if (_policy.Current.ProtectionPaused)
            {
                return;
            }

            var appId = e.AppId ?? AppIdentity.DeriveAppId(e.ExecutablePath, e.PackageFamilyName);
            var rule = _policy.FindByAppId(appId);

            if (rule is null || !rule.Enabled || !rule.HasAnyProtection)
            {
                return;
            }

            using var cts = new CancellationTokenSource(HandlerBudget);

            // Locking first, then hiding, then power. The order matters for the user: the overlay must
            // be up before anything slower happens, or a locked app is briefly interactive.
            if (rule.Lock.Enabled && !_sessions.IsUnlocked(appId))
            {
                await AnnounceLockAsync(rule, e.ProcessId, cts.Token).ConfigureAwait(false);
            }

            if (rule.Hide.Enabled)
            {
                await HideAsync(rule, e.ProcessId, cts.Token).ConfigureAwait(false);
            }

            if (rule.Power.Profile is not null)
            {
                await ApplyPowerAsync(rule, e.ProcessId, cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Enforcement for PID {Pid} failed.", e.ProcessId);
        }
    }

    private void OnAppExited(object? sender, AppExitedEventArgs e)
    {
        lock (_announced)
        {
            _announced.Remove(e.ProcessId);
        }

        // The throttle is dropped here rather than left to the sweep so the job object goes away with
        // the process it was created for; the sweep is only a backstop for exits we never saw.
        _power.RemoveThrottle(e.ProcessId);

        // An until-close unlock session ends with the process it was granted for. Without this, closing
        // and relaunching a locked app inside the session window would skip the prompt.
        _sessions.NoteProcessExited(e.ProcessId);
    }

    private void OnSessionEnded(object? sender, UnlockSession session)
    {
        // Re-locking a running app is the agent's job (it owns the overlay), and there is nothing to do
        // here beyond letting it know the app is protected again. Best-effort: if the agent is gone the
        // app is still locked as far as the next launch is concerned.
        var rule = _policy.FindByAppId(session.AppId);

        if (rule is null || !rule.Lock.Enabled || session.ProcessId is not { } pid)
        {
            return;
        }

        lock (_announced)
        {
            _announced.Remove(pid);
        }

        _ = AnnounceLockAsync(rule, pid, CancellationToken.None);
    }

    // ---- actions ----

    private async Task AnnounceLockAsync(AppRule rule, int pid, CancellationToken ct)
    {
        lock (_announced)
        {
            // Dedup by PID, not by app: two windows of the same app are two processes and both need
            // covering, but the reconciliation pass must not re-show an overlay the user is looking at.
            if (!_announced.Add(pid))
            {
                return;
            }
        }

        try
        {
            await _agent.PublishAppLaunchedAsync(
                new AppLaunchedEvent
                {
                    AppId = rule.Identity.AppId,
                    ProcessId = pid,
                    ExecutablePath = rule.Identity.ExecutablePath,
                },
                ct).ConfigureAwait(false);

            if (!_agent.IsAgentPresent)
            {
                // Audited as Unsupported rather than Error: nothing malfunctioned, the component that
                // draws the overlay simply is not there. The distinction matters when the user reads
                // the log trying to work out why an app was not locked.
                await _audit.AppendAsync(
                    AuditActor.Service,
                    AuditAction.Unsupported,
                    AuditResult.Error,
                    appId: rule.Identity.AppId,
                    ruleId: rule.RuleId,
                    detail: "The app started but AppGuardian's desktop helper is not running, so the lock screen could not be shown.",
                    ct: ct).ConfigureAwait(false);

                lock (_announced)
                {
                    // Forgotten so the next reconciliation retries once the agent is back. Keeping it
                    // would mean an agent restart never recovers the lock for an app already running.
                    _announced.Remove(pid);
                }

                return;
            }

            await _audit.AppendAsync(
                AuditActor.Service,
                AuditAction.Lock,
                AuditResult.Ok,
                appId: rule.Identity.AppId,
                ruleId: rule.RuleId,
                detail: $"Lock screen requested for process {pid}.",
                ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Could not announce lock for PID {Pid}.", pid);

            lock (_announced)
            {
                _announced.Remove(pid);
            }
        }
    }

    private async Task HideAsync(AppRule rule, int pid, CancellationToken ct)
    {
        var result = await _agent.MoveToHiddenAsync(
            new MoveToHiddenRequest
            {
                AppId = rule.Identity.AppId,
                ProcessId = pid,
            },
            ct).ConfigureAwait(false);

        await _audit.AppendAsync(
            AuditActor.Service,
            AuditAction.Hide,
            result.Succeeded ? AuditResult.Ok : AuditResult.Error,
            appId: rule.Identity.AppId,
            ruleId: rule.RuleId,
            detail: result.Detail ?? (result.Succeeded
                ? result.UsedFallback
                    ? "Window hidden by minimising it, because virtual desktops are unavailable on this system."
                    : "Window moved to the hidden desktop."
                : "The window could not be hidden."),
            ct: ct).ConfigureAwait(false);
    }

    private async Task ApplyPowerAsync(AppRule rule, int pid, CancellationToken ct)
    {
        var result = _power.ApplyThrottle(
            pid,
            rule.Power.Profile!.Value,
            rule.Power.ReducePriority,
            rule.Power.EcoQos);

        // Only failures and partial applications are audited. A successful throttle on every launch of
        // a chatty app would flood the log and push the entries the user actually wants off the end of
        // the 500-entry read window.
        if (result.Succeeded && result.UnsupportedReason is null)
        {
            return;
        }

        await _audit.AppendAsync(
            AuditActor.Service,
            result.Succeeded ? AuditAction.ApplyPower : AuditAction.Unsupported,
            result.Succeeded ? AuditResult.Ok : AuditResult.Error,
            appId: rule.Identity.AppId,
            ruleId: rule.RuleId,
            detail: result.UnsupportedReason ?? "The CPU limit could not be applied.",
            ct: ct).ConfigureAwait(false);
    }

    // ---- reconciliation ----

    /// <summary>
    /// Brings live state back in line with policy. FR-201, NFR-R2.
    /// </summary>
    /// <remarks>
    /// Three jobs, in this order: expire sessions, release restrictions that no longer apply, then apply
    /// the ones that are missing. Releasing before applying matters when the user has just changed a
    /// profile — otherwise the old job object is still holding the old cap when the new one is created.
    /// </remarks>
    private async Task ReconcileAsync(CancellationToken ct)
    {
        try
        {
            _sessions.Sweep();
            _power.SweepExited();

            await _agent.PingAsync(ct).ConfigureAwait(false);

            if (_policy.Current.ProtectionPaused)
            {
                // Paused means paused: every throttle comes off. Hidden windows are deliberately left
                // where they are, because revealing them would move the user's windows as a side effect
                // of a setting they toggled somewhere else.
                foreach (var pid in _power.ActiveThrottles.Keys.ToList())
                {
                    _power.RemoveThrottle(pid);
                }

                return;
            }

            var running = _monitor.ListRunning();

            // Built once and reused: ListRunning enumerates every process on the machine, and calling it
            // per rule would make the pass O(rules × processes) against NFR-P2's 2% idle budget.
            var pidsByApp = running
                .GroupBy(app => AppIdentity.DeriveAppId(app.ExecutablePath, app.PackageFamilyName),
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(app => app.ProcessId).ToList(),
                    StringComparer.OrdinalIgnoreCase);

            var shouldBeThrottled = new Dictionary<int, AppRule>();

            foreach (var rule in _policy.ActiveRules())
            {
                if (rule.Power.Profile is null ||
                    !pidsByApp.TryGetValue(rule.Identity.AppId, out var pids))
                {
                    continue;
                }

                foreach (var pid in pids)
                {
                    shouldBeThrottled[pid] = rule;
                }
            }

            foreach (var pid in _power.ActiveThrottles.Keys.ToList())
            {
                // A throttle with no matching rule means the rule was deleted, disabled, or repointed
                // while the app kept running. Left alone it would be a cap with nothing in the UI to
                // explain it.
                if (!shouldBeThrottled.ContainsKey(pid))
                {
                    _power.RemoveThrottle(pid);
                }
            }

            foreach (var (pid, rule) in shouldBeThrottled)
            {
                // ApplyThrottle short-circuits an identical re-apply, so this is cheap on the common
                // pass where nothing has changed.
                await ApplyPowerAsync(rule, pid, ct).ConfigureAwait(false);
            }

            await ReconcileLocksAsync(pidsByApp, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Swallowed so one bad pass does not end the loop. A permanent problem shows up as a
            // repeating warning rather than as protection that silently stopped.
            _log.LogWarning(ex, "Reconciliation pass failed.");
        }
    }

    private async Task ReconcileLocksAsync(
        Dictionary<string, List<int>> pidsByApp,
        CancellationToken ct)
    {
        var live = new HashSet<int>();

        foreach (var rule in _policy.ActiveRules())
        {
            if (!rule.Lock.Enabled ||
                !pidsByApp.TryGetValue(rule.Identity.AppId, out var pids))
            {
                continue;
            }

            foreach (var pid in pids)
            {
                live.Add(pid);
            }

            if (_sessions.IsUnlocked(rule.Identity.AppId))
            {
                continue;
            }

            foreach (var pid in pids)
            {
                // Catches launches the monitor missed — a process that started while the WMI watcher was
                // reconnecting, or while the agent was down. AnnounceLockAsync dedups, so an app already
                // showing an overlay is untouched.
                await AnnounceLockAsync(rule, pid, ct).ConfigureAwait(false);
            }
        }

        lock (_announced)
        {
            // Pruned against live PIDs because exit events are lossy too, and a stale entry would
            // suppress the overlay for a future process that happened to reuse the number.
            _announced.RemoveWhere(pid => !live.Contains(pid));
        }
    }

    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan HandlerBudget = TimeSpan.FromSeconds(10);
}
