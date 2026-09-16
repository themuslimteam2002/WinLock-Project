using System.Collections.Concurrent;
using System.Diagnostics;
using AppGuardian.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AppGuardian.Service.Locking;

/// <summary>
/// Tracks which apps currently have a temporary unlock. SRS FR-305, FR-308.
/// </summary>
/// <remarks>
/// The single authority on "is this app unlocked right now", queried by the process monitor on every
/// launch and by the foreground path on every focus change. In-memory only, per the reasoning on
/// <see cref="UnlockSession"/>: a service restart re-locks everything, which is the safe default
/// while SRS Appendix C question 2 is unanswered.
/// <para>
/// Expiry is checked on read rather than driven by a timer per session. A hundred timers would be a
/// hundred chances to leak one; a comparison against the clock cannot leak.
/// </para>
/// </remarks>
public sealed class UnlockSessionTracker
{
    private readonly ConcurrentDictionary<string, UnlockSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<UnlockSessionTracker> _log;
    private readonly TimeProvider _clock;

    public UnlockSessionTracker(ILogger<UnlockSessionTracker> log, TimeProvider? clock = null)
    {
        _log = log;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Raised when a session ends, so the agent can re-arm the lock for that app.</summary>
    public event EventHandler<UnlockSession>? SessionEnded;

    /// <summary>
    /// Grants a session. FR-305.
    /// </summary>
    public UnlockSession Grant(string appId, LockSettings settings, StartSessionOptions options)
    {
        var mode = options.Mode ?? settings.TempUnlockMode;

        UnlockSession session;

        if (mode == TempUnlockMode.UntilAppClose)
        {
            if (options.ProcessId is null or <= 0)
            {
                // Falling back to a bounded duration rather than refusing: an unlock the user asked
                // for should not fail because the caller could not name a PID, and a session with no
                // expiry and no process to watch would never end.
                _log.LogWarning(
                    "Until-app-close unlock requested for {AppId} without a process id; " +
                    "granting a timed session instead.",
                    appId);

                session = UnlockSession.ForDuration(appId, EffectiveSeconds(settings, options), options.Method);
            }
            else
            {
                session = UnlockSession.UntilClose(appId, options.ProcessId.Value, options.Method);
            }
        }
        else
        {
            session = UnlockSession.ForDuration(appId, EffectiveSeconds(settings, options), options.Method);
        }

        // Replace rather than extend: re-authenticating restarts the clock, which is what a user
        // expects, and avoids an unbounded session built from repeated small extensions.
        _sessions[appId] = session;

        _log.LogInformation(
            "Unlocked {AppId} via {Method} ({Mode}, expires {Expiry}).",
            appId,
            options.Method,
            session.Mode,
            session.ExpiresUtc?.ToString("O") ?? "on app close");

        return session;
    }

    /// <summary>True when the app currently has a valid session. Expired sessions are reaped here.</summary>
    public bool IsUnlocked(string appId)
    {
        if (!_sessions.TryGetValue(appId, out var session))
        {
            return false;
        }

        if (IsStillValid(session))
        {
            return true;
        }

        End(appId, "expired");
        return false;
    }

    public UnlockSession? Get(string appId) =>
        _sessions.TryGetValue(appId, out var session) && IsStillValid(session) ? session : null;

    public IReadOnlyList<UnlockSession> Active()
    {
        Sweep();
        return _sessions.Values.ToList();
    }

    /// <summary>Ends one session. FR-305 lock-now.</summary>
    public bool End(string appId, string reason = "ended")
    {
        if (!_sessions.TryRemove(appId, out var session))
        {
            return false;
        }

        _log.LogInformation("Unlock session for {AppId} {Reason}.", appId, reason);

        SessionEnded?.Invoke(this, session);

        return true;
    }

    /// <summary>
    /// Ends every session. Used by lock-all and by the pause/resume transition.
    /// </summary>
    /// <remarks>
    /// Resuming protection must clear sessions, otherwise an app unlocked before the pause would stay
    /// unlocked afterwards and the user would believe protection had resumed when it had not.
    /// </remarks>
    public int EndAll(string reason = "ended")
    {
        var count = 0;

        foreach (var appId in _sessions.Keys.ToArray())
        {
            if (End(appId, reason))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Ends any until-app-close session whose process has exited. Called from the process monitor's
    /// exit event and from the periodic sweep, because a process that exits while the monitor is
    /// briefly unavailable would otherwise leave the app unlocked indefinitely.
    /// </summary>
    public void NoteProcessExited(int processId)
    {
        foreach (var (appId, session) in _sessions.ToArray())
        {
            if (session.Mode == TempUnlockMode.UntilAppClose && session.ProcessId == processId)
            {
                End(appId, "app closed");
            }
        }
    }

    /// <summary>Reaps expired and orphaned sessions. Called on a timer by the enforcement loop.</summary>
    public void Sweep()
    {
        foreach (var (appId, session) in _sessions.ToArray())
        {
            if (!IsStillValid(session))
            {
                End(appId, session.Mode == TempUnlockMode.UntilAppClose ? "app closed" : "expired");
            }
        }
    }

    private bool IsStillValid(UnlockSession session)
    {
        if (session.Mode == TempUnlockMode.UntilAppClose)
        {
            return session.ProcessId is not null && IsProcessAlive(session.ProcessId.Value);
        }

        return session.IsActive(_clock.GetUtcNow());
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // No such process — it exited.
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static int EffectiveSeconds(LockSettings settings, StartSessionOptions options)
    {
        var seconds = options.DurationSeconds ?? settings.TempUnlockSeconds;

        // Clamped server-side as well as in the store, because the duration arrives from a client
        // and FR-305's 60–3600 range is a policy limit, not a UI convenience.
        return Math.Clamp(seconds, LockSettings.MinTempUnlockSeconds, LockSettings.MaxTempUnlockSeconds);
    }
}

/// <summary>Options for granting a session, resolved from the request and the rule.</summary>
public sealed class StartSessionOptions
{
    public int? DurationSeconds { get; init; }

    /// <summary>Null falls back to the rule's configured mode.</summary>
    public TempUnlockMode? Mode { get; init; }

    public int? ProcessId { get; init; }

    public AuthMethod Method { get; init; } = AuthMethod.Pin;
}
