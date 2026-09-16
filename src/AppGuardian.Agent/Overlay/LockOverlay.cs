using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Threading;
using AppGuardian.Agent.Auth;
using AppGuardian.Shared.Contracts;
using AppGuardian.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AppGuardian.Agent.Overlay;

/// <summary>
/// Shows and hides the lock overlay across every monitor. FR-502, FR-503, FR-507.
/// </summary>
/// <remarks>
/// All window work is marshalled onto the WPF dispatcher. Callers arrive from three different threads —
/// the WinEvent hook, the pipe read pump, and the reconcile timer — and none of them may touch a
/// <see cref="Window"/> directly; doing so throws at best and corrupts visual state at worst.
/// <para>
/// Authentication happens here rather than in the window so the window stays a view: it collects a secret
/// and raises an event, and this class decides what the result means.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class LockOverlay : ILockOverlay, IDisposable
{
    private readonly IUnlockAuthenticator _auth;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<LockOverlay> _log;

    private readonly List<LockOverlayWindow> _windows = new();
    private readonly object _gate = new();

    private AppIdentity? _app;
    private bool _helloAvailable;
    private CancellationTokenSource? _attemptCts;

    public event EventHandler<OverlayUnlockedEventArgs>? Unlocked;

    public event EventHandler? Dismissed;

    public LockOverlay(IUnlockAuthenticator auth, Dispatcher dispatcher, ILogger<LockOverlay> log)
    {
        _auth = auth;
        _dispatcher = dispatcher;
        _log = log;
    }

    public bool IsVisible
    {
        get
        {
            lock (_gate)
            {
                return _windows.Count > 0;
            }
        }
    }

    public void Show(AppIdentity app, IReadOnlyList<MonitorInfo> monitors)
    {
        // InvokeAsync, not Invoke. Show is called from the WinEvent hook thread, and a synchronous
        // dispatcher call from there would deadlock the moment the UI thread was itself waiting on
        // anything that the hook thread had to deliver.
        _dispatcher.InvokeAsync(() => ShowCore(app, monitors));
    }

    private void ShowCore(AppIdentity app, IReadOnlyList<MonitorInfo> monitors)
    {
        try
        {
            lock (_gate)
            {
                // Idempotent per the interface contract. A second launch event for an app already covered
                // refreshes topmost instead of stacking a second set of windows, which would leave the
                // user dismissing overlays one at a time.
                if (_windows.Count > 0 && _app?.AppId == app.AppId)
                {
                    foreach (var window in _windows)
                    {
                        window.ReassertTopmost();
                    }

                    return;
                }

                CloseAllCore();

                _app = app;

                // Queried once per show, not once per process. Hello can be enrolled or removed while the
                // agent is running, and offering a button that then fails is worse than not offering it.
                _helloAvailable = _auth.IsHelloAvailableAsync().GetAwaiter().GetResult();

                var promptMonitor = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];

                foreach (var monitor in monitors)
                {
                    var window = new LockOverlayWindow(
                        app,
                        monitor,
                        isPrimaryPrompt: ReferenceEquals(monitor, promptMonitor),
                        _helloAvailable);

                    window.SecretSubmitted += OnSecretSubmitted;
                    window.HelloRequested += OnHelloRequested;
                    window.DismissRequested += OnDismissRequested;

                    _windows.Add(window);
                    window.Show();
                }

                _windows.FirstOrDefault(w => w.IsPrimaryPrompt)?.Activate();
            }
        }
        catch (Exception ex)
        {
            // A failure to draw the overlay must not leave a half-covered screen. Everything comes down
            // and the app is left unlocked, which is visible to the user, rather than leaving a window
            // that captures input with no way to authenticate.
            _log.LogError(ex, "The lock overlay for {AppId} could not be shown.", app.AppId);
            CloseAll();
        }
    }

    public void Hide() => _dispatcher.InvokeAsync(CloseAll);

    private void CloseAll()
    {
        lock (_gate)
        {
            CloseAllCore();
        }
    }

    private void CloseAllCore()
    {
        _attemptCts?.Cancel();
        _attemptCts?.Dispose();
        _attemptCts = null;

        foreach (var window in _windows)
        {
            window.SecretSubmitted -= OnSecretSubmitted;
            window.HelloRequested -= OnHelloRequested;
            window.DismissRequested -= OnDismissRequested;

            try
            {
                window.Close();
            }
            catch (InvalidOperationException)
            {
                // Already closing — the user hit Alt+F4 or the session is ending. Nothing to do.
            }
        }

        _windows.Clear();
        _app = null;
    }

    /// <summary>Re-asserts topmost on every overlay window. Called when the foreground changes.</summary>
    public void ReassertTopmost()
    {
        _dispatcher.InvokeAsync(() =>
        {
            lock (_gate)
            {
                foreach (var window in _windows)
                {
                    window.ReassertTopmost();
                }
            }
        });
    }

    private void OnSecretSubmitted(object? sender, string secret)
    {
        if (sender is not LockOverlayWindow window || _app is null)
        {
            return;
        }

        var appId = _app.AppId;
        var cts = ResetAttempt();

        window.SetBusy(true);

        // Async void is deliberately avoided; the continuation is scheduled back onto the dispatcher and
        // every fault is handled inside, so nothing escapes to the unhandled-exception handler.
        _ = Task.Run(async () =>
        {
            PinAttemptOutcome outcome;

            try
            {
                outcome = await _auth.VerifySecretAsync(secret, appId, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "A PIN attempt for {AppId} failed unexpectedly.", appId);
                outcome = PinAttemptOutcome.Unavailable("Something went wrong. Try again in a moment.");
            }

            await _dispatcher.InvokeAsync(() =>
            {
                window.SetBusy(false);

                if (outcome.Verified)
                {
                    RaiseUnlocked(appId, AuthMethod.Pin);
                    return;
                }

                window.ShowMessage(MessageFor(outcome));
            });
        });
    }

    private void OnHelloRequested(object? sender, EventArgs e)
    {
        if (sender is not LockOverlayWindow window || _app is null)
        {
            return;
        }

        var appId = _app.AppId;
        var displayName = _app.DisplayName;
        var cts = ResetAttempt();

        window.SetBusy(true);

        _ = Task.Run(async () =>
        {
            VerifyResult result;

            try
            {
                result = await _auth
                    .VerifyHelloAsync($"Unlock {displayName}", cts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Windows Hello verification for {AppId} failed unexpectedly.", appId);
                result = VerifyResult.Unavailable("Windows Hello could not be used. Use your PIN instead.");
            }

            var sessionStarted = false;

            if (result.Succeeded)
            {
                // The service is told separately, because Hello was verified here and the service has no
                // way to observe it. If this fails the unlock is not honoured: dismissing the overlay
                // while the service still believes the app is locked would re-lock it a second later and
                // look like the PIN had been ignored.
                sessionStarted = await _auth
                    .StartSessionAsync(appId, AuthMethod.WindowsHello, cts.Token)
                    .ConfigureAwait(false);
            }

            await _dispatcher.InvokeAsync(() =>
            {
                window.SetBusy(false);

                if (result.Succeeded && sessionStarted)
                {
                    RaiseUnlocked(appId, AuthMethod.WindowsHello);
                    return;
                }

                if (result.Succeeded)
                {
                    window.ShowMessage(
                        "You were verified, but AppGuardian's background service did not respond. "
                        + "Try again in a moment.");

                    return;
                }

                if (result.Outcome == VerifyOutcome.Cancelled)
                {
                    // Silent. The user dismissed the Windows prompt themselves and knows what happened;
                    // an error message here would read as a failure they had not caused.
                    return;
                }

                window.ShowMessage(result.Detail ?? "Windows Hello did not verify you.");
            });
        });
    }

    private void OnDismissRequested(object? sender, EventArgs e)
    {
        CloseAll();

        // Raised after the windows are down, so a handler that queries IsVisible sees the settled state.
        Dismissed?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseUnlocked(string appId, AuthMethod method)
    {
        CloseAllCore();

        Unlocked?.Invoke(this, new OverlayUnlockedEventArgs { AppId = appId, Method = method });
    }

    private CancellationTokenSource ResetAttempt()
    {
        lock (_gate)
        {
            // One attempt at a time. Without this, a user who clicks Hello and then types a PIN has two
            // verifications racing, and whichever loses would report a failure over the winner's success.
            _attemptCts?.Cancel();
            _attemptCts?.Dispose();
            _attemptCts = new CancellationTokenSource();

            return _attemptCts;
        }
    }

    private static string MessageFor(PinAttemptOutcome outcome)
    {
        if (outcome.LockedOut)
        {
            var until = outcome.LockedUntilUtc?.ToLocalTime();

            return until is null
                ? "Too many incorrect attempts. Try again shortly."
                : $"Too many incorrect attempts. Try again after {until:t}.";
        }

        if (outcome.ServiceUnavailable)
        {
            return outcome.Message ?? "AppGuardian's background service is not responding.";
        }

        var text = outcome.Message ?? "That PIN is not correct.";

        // The remaining count is shown only when it is genuinely low. Displaying "4 attempts remaining"
        // after a single typo reads as an accusation; showing it at 2 is a useful warning.
        return outcome.AttemptsRemaining is > 0 and <= 2
            ? $"{text} {outcome.AttemptsRemaining} attempt(s) left before a short lockout."
            : text;
    }

    public void Dispose()
    {
        if (_dispatcher.CheckAccess())
        {
            CloseAll();
        }
        else
        {
            _dispatcher.Invoke(CloseAll);
        }
    }
}
