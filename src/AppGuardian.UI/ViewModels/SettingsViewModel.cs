using System.Runtime.Versioning;
using AppGuardian.Shared.Models;
using AppGuardian.UI.Mvvm;
using AppGuardian.UI.Services;
using Microsoft.Extensions.Logging;

namespace AppGuardian.UI.ViewModels;

/// <summary>
/// Credentials, disclosures, appearance, and diagnostics. FR-802, FR-307, FR-607, FR-707.
/// </summary>
/// <remarks>
/// SPEC CONFLICT SC-13: FR-802 asks for a settings surface but the project plan has no task for one, so
/// this page's scope is inferred from the functional requirements it has to satisfy rather than from a
/// plan line. It is deliberately kept to what the requirements name — changing the credential, choosing
/// an appearance, seeing what each best-effort feature really guarantees, and reading version and health
/// information — rather than growing preferences nobody asked for.
/// <para>
/// Appearance is editable here and nothing else in <see cref="UserSettings"/> is. The difference is that
/// the theme is a purely local UI concern that <see cref="ThemeService"/> can apply and persist to the
/// per-user <c>settings.json</c> without any IPC message; launch-at-logon and the rest need the service
/// to act on them and the contract defines no message for that, so a switch here would appear to save a
/// preference that never left the process.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class SettingsViewModel : PageViewModel
{
    private readonly GuardianClient _client;
    private readonly ThemeService _theme;
    private readonly ILogger<SettingsViewModel> _log;

    private AuthStatus? _auth;
    private SystemStatus? _status;
    private string _newSecret = string.Empty;
    private string _confirmSecret = string.Empty;
    private bool _preferHello;
    private bool _resetArmed;

    public SettingsViewModel(GuardianClient client, ThemeService theme, ILogger<SettingsViewModel> log)
    {
        _client = client;
        _theme = theme;
        _log = log;

        ChangeSecretCommand = new AsyncRelayCommand(
            ChangeSecretAsync,
            () => CanChangeSecret,
            ex =>
            {
                _log.LogWarning(ex, "The PIN could not be changed.");
                ShowError("The PIN could not be changed. Check that AppGuardian's service is running.");
            });

        // Parameterised rather than three commands, because the three radio buttons differ only in which
        // AppTheme they carry and a command each would be three identical bodies.
        SetThemeCommand = new AsyncRelayCommand(
            parameter => ApplyThemeAsync(parameter),
            onError: ex =>
            {
                _log.LogWarning(ex, "The appearance could not be changed.");
                ShowError("The appearance could not be changed.");
            });

        ArmResetCommand = new RelayCommand(() => ResetArmed = true);

        CancelResetCommand = new RelayCommand(() => ResetArmed = false);

        ResetCommand = new AsyncRelayCommand(
            ResetAsync,
            () => _resetArmed,
            ex => _log.LogWarning(ex, "The credential could not be reset."));

        RefreshCommand = new AsyncRelayCommand(
            () => LoadAsync(CancellationToken.None),
            onError: ex => _log.LogWarning(ex, "Settings could not be refreshed."));
    }

    public override string Title => "Settings";

    public AsyncRelayCommand ChangeSecretCommand { get; }

    public AsyncRelayCommand SetThemeCommand { get; }

    public RelayCommand ArmResetCommand { get; }

    public RelayCommand CancelResetCommand { get; }

    public AsyncRelayCommand ResetCommand { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    // ---- appearance (FR-802) ----

    /// <summary>
    /// The stored preference, which may be <see cref="AppTheme.System"/>.
    /// </summary>
    /// <remarks>
    /// Read straight from the service rather than mirrored into a field. There is one authoritative copy
    /// of this value, in <see cref="ThemeService"/>, and a backing field here would be a second one to
    /// keep in step for no benefit — the page is a singleton and the theme changes only through it.
    /// </remarks>
    public AppTheme Theme => _theme.Preference;

    /// <summary>Three bools rather than a bound enum, so each radio button binds to one of them.</summary>
    public bool ThemeIsSystem => Theme == AppTheme.System;

    public bool ThemeIsLight => Theme == AppTheme.Light;

    public bool ThemeIsDark => Theme == AppTheme.Dark;

    /// <summary>What "follow Windows" currently resolves to, so the option is not a guess.</summary>
    public string ThemeSystemText =>
        $"Follow Windows — currently {(_theme.Resolve(AppTheme.System) == AppTheme.Light ? "light" : "dark")}";

    // ---- credentials (FR-307) ----

    public string NewSecret
    {
        get => _newSecret;
        set
        {
            if (Set(ref _newSecret, value))
            {
                ChangeSecretCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(SecretProblem));
            }
        }
    }

    public string ConfirmSecret
    {
        get => _confirmSecret;
        set
        {
            if (Set(ref _confirmSecret, value))
            {
                ChangeSecretCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(SecretProblem));
            }
        }
    }

    public bool PreferHello
    {
        get => _preferHello;
        set => Set(ref _preferHello, value);
    }

    public bool HelloAvailable => _auth?.WindowsHelloAvailable == true;

    public string HelloStateText => HelloAvailable
        ? "Windows Hello is available on this PC and will be offered first."
        : "Windows Hello is not set up on this PC. Enrol a face, fingerprint or Windows PIN in Windows "
          + "Settings, then come back here to switch to it.";

    public bool CanChangeSecret =>
        _newSecret.Length >= OnboardingViewModel.MinimumSecretLength && _newSecret == _confirmSecret;

    public string? SecretProblem
    {
        get
        {
            if (_newSecret.Length == 0)
            {
                return null;
            }

            if (_newSecret.Length < OnboardingViewModel.MinimumSecretLength)
            {
                return $"Use at least {OnboardingViewModel.MinimumSecretLength} characters.";
            }

            return _confirmSecret.Length > 0 && _newSecret != _confirmSecret
                ? "The two entries do not match."
                : null;
        }
    }

    /// <summary>Lockout state, so a user waiting out a backoff can see how long is left. FR-306.</summary>
    public bool IsLockedOut => _auth?.IsLockedOut == true;

    public string? LockoutText
    {
        get
        {
            if (_auth?.LockedUntilUtc is not { } until)
            {
                return null;
            }

            var remaining = until - DateTimeOffset.UtcNow;

            if (remaining <= TimeSpan.Zero)
            {
                return null;
            }

            // Rounded up to the minute. A precise countdown here would need a timer for a value nobody
            // acts on second by second, and "less than a minute" is the useful answer at the low end.
            var minutes = (int)Math.Ceiling(remaining.TotalMinutes);

            return minutes <= 1
                ? "Too many failed attempts. Unlocking is paused for less than a minute."
                : $"Too many failed attempts. Unlocking is paused for about {minutes} minutes.";
        }
    }

    // ---- reset (deliberately two-step) ----

    /// <summary>
    /// True once the user has asked to reset, which reveals the confirm button.
    /// </summary>
    /// <remarks>
    /// A two-step gesture rather than a modal dialog. Resetting clears the credential and every lock rule
    /// depending on it, and it is the one destructive action in the product — but it is also the only way
    /// back for someone who has forgotten their PIN, so it must not be hidden.
    /// </remarks>
    public bool ResetArmed
    {
        get => _resetArmed;
        private set
        {
            if (Set(ref _resetArmed, value))
            {
                ResetCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ResetWarning =>
        "Resetting clears your PIN and turns off every lock, so any app that is currently locked will open "
        + "normally until you set a new PIN. Hidden apps are brought back. Your rules are kept but are not "
        + "enforced until you finish setting up again.";

    // ---- disclosures (FR-607, FR-707) ----

    public bool VirtualDesktopSupported => _status?.VirtualDesktopSupported == true;

    public string HideDisclosure => VirtualDesktopSupported
        ? "Hidden apps are moved to a separate desktop. They stay visible in Task Manager, and their files "
          + "remain on this PC — this hides windows, it does not encrypt or protect data."
        : "This PC cannot use the separate hidden desktop, so hiding minimises windows and keeps them off "
          + "the taskbar. Anyone using this PC could restore them.";

    public string LockDisclosure =>
        "The lock is a full-screen prompt shown when you switch to a protected app. It is not a Windows "
        + "security boundary: someone with administrator access to this PC, or with access to the files "
        + "directly, can bypass it. It protects against someone picking up your unlocked PC.";

    public string PowerDisclosure =>
        "Battery limits cap processor time only. They cannot limit memory, disk, graphics or network use, "
        + "and some apps cannot be limited at all — those are marked on the Battery page.";

    public string PrivacyStatement =>
        "AppGuardian works entirely on this PC. It makes no network connections, sends no telemetry, and "
        + "keeps its settings and activity log in ProgramData on this machine.";

    // ---- diagnostics ----

    public string? ServiceVersion => _status?.ServiceVersion;

    public string? AgentVersion => _status?.AgentVersion;

    public bool AgentRunning => _status?.AgentRunning == true;

    public string AgentStateText => AgentRunning
        ? "Running"
        : "Not running. Apps cannot be locked or hidden until it starts — signing out and back in usually "
          + "fixes this.";

    public string? ServiceUptimeText => _status?.ServiceUptime is { } uptime
        ? uptime.TotalHours >= 1
            ? $"{(int)uptime.TotalHours} h {uptime.Minutes} min"
            : $"{uptime.Minutes} min"
        : null;

    public override async Task LoadAsync(CancellationToken ct)
    {
        IsBusy = true;

        try
        {
            var authTask = _client.GetAuthStatusAsync(ct);
            var statusTask = _client.GetStatusAsync(ct);

            _auth = await authTask.ConfigureAwait(true);
            _status = await statusTask.ConfigureAwait(true);

            if (_auth is not null)
            {
                PreferHello = _auth.PreferredMethod == AuthMethod.WindowsHello;
            }

            // Every derived property is announced together rather than through per-field setters. There
            // are a dozen of them, they all come from these two reads, and individual backing fields would
            // be a dozen places for the two to drift apart.
            OnPropertyChanged(nameof(HelloAvailable));
            OnPropertyChanged(nameof(HelloStateText));
            OnPropertyChanged(nameof(IsLockedOut));
            OnPropertyChanged(nameof(LockoutText));
            OnPropertyChanged(nameof(VirtualDesktopSupported));
            OnPropertyChanged(nameof(HideDisclosure));
            OnPropertyChanged(nameof(ServiceVersion));
            OnPropertyChanged(nameof(AgentVersion));
            OnPropertyChanged(nameof(AgentRunning));
            OnPropertyChanged(nameof(AgentStateText));
            OnPropertyChanged(nameof(ServiceUptimeText));

            // Re-read on every visit, not just after a change: the user may have switched Windows itself
            // between light and dark since this page was last shown, which changes what "Follow Windows"
            // resolves to even though our own preference is untouched.
            OnPropertyChanged(nameof(Theme));
            OnPropertyChanged(nameof(ThemeIsSystem));
            OnPropertyChanged(nameof(ThemeIsLight));
            OnPropertyChanged(nameof(ThemeIsDark));
            OnPropertyChanged(nameof(ThemeSystemText));

            if (_auth is null || _status is null)
            {
                ShowError(
                    "Some settings could not be read because AppGuardian's background service is not "
                    + "responding.");
            }
            else
            {
                ClearMessage();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Applies an appearance choice. The command parameter is an <see cref="AppTheme"/> or its name.
    /// </summary>
    /// <remarks>
    /// Accepts a string as well as the enum because XAML's <c>CommandParameter="Dark"</c> passes a string
    /// and requiring <c>{x:Static}</c> in three places would be noise. An unparseable value is ignored
    /// rather than thrown on: it can only come from a typo in the view, and crashing the settings page is
    /// a poor way to report one.
    /// </remarks>
    private async Task ApplyThemeAsync(object? parameter)
    {
        var preference = parameter switch
        {
            AppTheme theme => theme,
            string name when Enum.TryParse<AppTheme>(name, ignoreCase: true, out var parsed) => parsed,
            _ => (AppTheme?)null,
        };

        if (preference is null)
        {
            _log.LogWarning("Ignoring an unrecognised theme parameter '{Parameter}'.", parameter);
            return;
        }

        if (preference == _theme.Preference)
        {
            return;
        }

        await _theme.ApplyAsync(preference.Value).ConfigureAwait(true);

        OnPropertyChanged(nameof(Theme));
        OnPropertyChanged(nameof(ThemeIsSystem));
        OnPropertyChanged(nameof(ThemeIsLight));
        OnPropertyChanged(nameof(ThemeIsDark));
        OnPropertyChanged(nameof(ThemeSystemText));
    }

    private async Task ChangeSecretAsync()
    {
        IsBusy = true;

        try
        {
            var method = PreferHello && HelloAvailable ? AuthMethod.WindowsHello : AuthMethod.Pin;

            // The same auth.setup call as onboarding. The service treats a setup call on a configured
            // store as a replacement, which is exactly what a PIN change is; a separate change message
            // would need its own hashing path and would be a second place for the parameters to diverge.
            var result = await _client.SetUpAuthAsync(method, _newSecret).ConfigureAwait(true);

            if (!result.Succeeded)
            {
                ShowError(result.Message);
                return;
            }

            ShowInfo("Your PIN has been changed.");

            await LoadAsync(CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            // Cleared on every path. A PIN left in a bound property stays in the heap for as long as the
            // window is open.
            NewSecret = string.Empty;
            ConfirmSecret = string.Empty;

            IsBusy = false;
        }
    }

    private async Task ResetAsync()
    {
        var result = await _client.ResetAuthAsync().ConfigureAwait(true);

        ResetArmed = false;

        if (!result.Succeeded)
        {
            ShowError(result.Message);
            return;
        }

        ShowInfo("Your PIN has been cleared. Set a new one to start protecting apps again.");

        await LoadAsync(CancellationToken.None).ConfigureAwait(true);
    }
}
