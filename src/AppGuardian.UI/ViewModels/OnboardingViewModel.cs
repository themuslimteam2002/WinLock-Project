using System.Diagnostics;
using System.Runtime.Versioning;
using AppGuardian.Shared.Models;
using AppGuardian.Shared.Storage;
using AppGuardian.UI.Mvvm;
using AppGuardian.UI.Services;
using Microsoft.Extensions.Logging;

namespace AppGuardian.UI.ViewModels;

/// <summary>
/// The four screens of first-run setup.
/// </summary>
/// <remarks>
/// The numeric values are load-bearing: <see cref="OnboardingViewModel.StepIndex"/> is this value plus
/// one, and the progress rail lights segment N once the index has reached N.
/// </remarks>
public enum OnboardingStep
{
    /// <summary>What the product does, and what it does not do. No input.</summary>
    Welcome = 0,

    /// <summary>What Windows has to be doing for protection to work. Read-only, with links out.</summary>
    Permissions = 1,

    /// <summary>Windows Hello preference and the fallback PIN. The only step that writes anything.</summary>
    Security = 2,

    /// <summary>Confirmation, and the one button that leaves setup.</summary>
    Ready = 3,
}

/// <summary>
/// Carries the step that the wizard just moved to. Used by <see cref="OnboardingViewModel.StepChanged"/>
/// so the view can react to transitions without holding a reference to the view model.
/// </summary>
public sealed class StepChangedEventArgs(OnboardingStep step) : EventArgs
{
    /// <summary>The step now active.</summary>
    public OnboardingStep Step { get; } = step;
}

/// <summary>
/// First-run setup, as a four-step wizard. FR-300, FR-301, FR-801.
/// </summary>
/// <remarks>
/// Mandatory, and deliberately so: every lock in the product resolves to "prove you are the owner", and
/// without a credential there is nothing to prove it against. A fallback PIN or password is required even
/// when Windows Hello is available, because Hello stops working the moment a fingerprint reader fails or a
/// camera is covered, and an unlock path with no fallback is a lock the owner cannot open either.
/// <para>
/// Four steps rather than one form. The single form this replaces asked for a PIN in the first thing the
/// user ever saw, with the explanation of why underneath it, and said nothing about what the product does
/// with the machine. Splitting it lets the first screen make the local-first claim before asking for
/// anything, and lets the second screen report what Windows is actually doing — which is the one part of
/// setup AppGuardian cannot do on the user's behalf.
/// </para>
/// <para>
/// Only <see cref="OnboardingStep.Security"/> writes. <see cref="OnboardingStep.Welcome"/> and
/// <see cref="OnboardingStep.Ready"/> are text, and <see cref="OnboardingStep.Permissions"/> reads status
/// and opens Windows Settings. So Back is free everywhere it is offered: nothing done on an earlier step
/// has to be undone.
/// </para>
/// <para>
/// The secret never leaves this page as anything but a single <c>auth.setup</c> call. It is not held after
/// that call returns, not logged, and not echoed back into the confirm box.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class OnboardingViewModel : PageViewModel
{
    /// <summary>
    /// FR-301: at least six characters.
    /// </summary>
    /// <remarks>
    /// The floor is low on purpose. The threat this defends against is a family member picking up an
    /// unlocked machine, not an offline attack — the store is DPAPI-protected per-machine and the backoff
    /// in FR-306 caps online guessing at a handful of attempts per hour. A twelve-character minimum would
    /// push users towards writing it down, which is a real weakness in exchange for a theoretical one.
    /// </remarks>
    public const int MinimumSecretLength = 6;

    /// <summary>Windows Settings, startup apps. Where the desktop agent's run-at-logon entry lives.</summary>
    public const string StartupAppsUri = "ms-settings:startupapps";

    /// <summary>Windows Settings, sign-in options. Where Windows Hello is enrolled.</summary>
    public const string SignInOptionsUri = "ms-settings:signinoptions";

    /// <summary>Windows Settings, notifications.</summary>
    public const string NotificationsUri = "ms-settings:notifications";

    /// <summary>Windows Settings, per-app overlay/focus assist. Where overlay-style locks are granted.</summary>
    /// <remarks>
    /// <see cref="ms-settings:apps&quiet=0"/> opens Apps &amp; features filtered to the foreground. There is
    /// no Windows "overlay permission" toggle — AppGuardian's overlay is a topmost window launched from the
    /// interactive agent, the same way the lock screen is — so this link lands on the page where the user can
    /// confirm AppGuardian itself is set to run at logon and is allowed to display over other windows. It is
    /// named "Overlay" in the wizard because that is what the user is reasoning about, not because Windows
    /// has a gate called that.
    /// </remarks>
    public const string OverlayAppsUri = "ms-settings:startupapps";

    /// <summary>Windows Settings, battery saver. Where CPU throttling is configured.</summary>
    /// <remarks>Per the premise correction in <c>UI_UX_UPGRADE.md</c>, AppGuardian applies EcoQoS itself and
    /// holds no battery grant; this link simply lands the user on the page that shows whether the OS is
    /// already treating the device as on-battery, which is the one piece of Windows state this step reports.</remarks>
    public const string BatterySaverUri = "ms-settings:batterysaver";

    /// <summary>
    /// The only URIs <see cref="OpenSettingsCommand"/> will launch.
    /// </summary>
    /// <remarks>
    /// The command's parameter comes from a XAML literal today, so the check never fires. It is here
    /// because the alternative is a shell-execute whose argument is whatever a binding produced, and a
    /// later edit that binds the parameter to data would turn this method into exactly that sink without
    /// anything in the diff looking wrong.
    /// </remarks>
    private static readonly HashSet<string> AllowedSettingsUris =
        new(StringComparer.Ordinal)
        {
            StartupAppsUri, SignInOptionsUri, NotificationsUri, OverlayAppsUri, BatterySaverUri,
        };

    private readonly GuardianClient _client;
    private readonly ILogger<OnboardingViewModel> _log;

    private OnboardingStep _step = OnboardingStep.Welcome;
    private string _secret = string.Empty;
    private string _confirm = string.Empty;
    private int _strengthScore;
    private string _strengthLevel = string.Empty;
    private bool _helloAvailable;
    private bool _preferHello = true;
    private bool _serviceReady;
    private bool _agentReady;
    private bool _completed;

    // ---- permissions (step 2) ----
    // The real Windows-side prerequisites are status, not grants. These two mirrors exist because the WPF
    // command-parameter contract is a single RelayCommand: GrantOverlayAccessCommand and
    // GrantBatteryAccessCommand hand the allow-listed URI straight to OpenSettings, keeping one code path
    // for the shell rather than letting XAML spell the URI literal that the allow-list silently drops.
    private bool _isOverlayGranted;
    private bool _isBatteryGranted;

    private readonly AtomicJsonStore<UserSettings> _settingsStore;

    public OnboardingViewModel(
        GuardianClient client,
        ILogger<OnboardingViewModel> log)
        : this(
            client,
            log,
            new AtomicJsonStore<UserSettings>(StoragePaths.SettingsFile))
    {
    }

    /// <summary>Test seam: lets a test point the settings file at a temporary location.</summary>
    internal OnboardingViewModel(
        GuardianClient client,
        ILogger<OnboardingViewModel> log,
        AtomicJsonStore<UserSettings> settingsStore)
    {
        _client = client;
        _log = log;
        _settingsStore = settingsStore;

        NextCommand = new RelayCommand(
            () => Step = Step + 1,
            () => _step is OnboardingStep.Welcome or OnboardingStep.Permissions);

        BackCommand = new RelayCommand(
            () => Step = Step - 1,
            () => _step is OnboardingStep.Permissions or OnboardingStep.Security);

        // The EmptyState CTA on the Locked/Hidden/Power pages links out to the Apps page. Onboarding is a
        // gate, so that link is a no-op here — but the shell's RelayCommand is the same shape, so exposing one
        // keeps the EmptyState's binding target stable rather than nulling it on this page.
        NavigateCommand = new RelayCommand(
            _ => { },
            _ => false);

        FinishCommand = new AsyncRelayCommand(
            FinishAsync,
            () => _step == OnboardingStep.Security && CanSubmit,
            ex =>
            {
                _log.LogWarning(ex, "Setup could not be completed.");
                ShowError("Your PIN could not be saved. AppGuardian's background service is not "
                          + "responding — it starts with Windows, so try again in a moment.");
            });

        EnterDashboardCommand = new RelayCommand(
            Release,
            () => _step == OnboardingStep.Ready);

        RecheckCommand = new AsyncRelayCommand(
            () => CheckPrerequisitesAsync(CancellationToken.None),
            onError: ex => _log.LogWarning(ex, "The prerequisite check failed."));

        OpenSettingsCommand = new RelayCommand(OpenSettings);

        // ---- permission rows that link out rather than grant ----
        // Named "Grant" because the wizard's copy calls them that, but they resolve to an OpenSettings call
        // — Windows owns the switch, AppGuardian can only land the user on the right page (see the premise
        // note in UI_UX_UPGRADE.md: there is no AppGuardian-owned Accessibility/Overlay permission gate).
        GrantOverlayAccessCommand = new RelayCommand(
            () => OpenSettings(OverlayAppsUri));

        GrantBatteryAccessCommand = new RelayCommand(
            () => OpenSettings(BatterySaverUri));
    }

    /// <summary>
    /// Raised once, when the user chooses to leave setup.
    /// </summary>
    /// <remarks>
    /// Not raised when the credential is written — that advances to <see cref="OnboardingStep.Ready"/>
    /// instead. The shell tears this page down in response, so raising it from <see cref="FinishAsync"/>
    /// would destroy the page mid-await and skip the confirmation step the user never got to read.
    /// <para>
    /// <c>ShellViewModel</c> is the subscriber, and something has to be: its <c>OnboardingRequired</c>
    /// navigates only on the transition to true, so nothing else in the app moves the user off this page.
    /// Until this was wired up, finishing setup left the user parked here — the five-second poll cleared
    /// the gate but never navigated.
    /// </para>
    /// </remarks>
    public event EventHandler? Completed;

    /// <summary>
    /// Raised after <see cref="Step"/> changes, carrying the new step. Lets the view auto-focus the relevant
    /// control (e.g. the PIN box on entering <see cref="OnboardingStep.Security"/>) without the view model
    /// reaching into the visual tree. FR-300.
    /// </summary>
    public event EventHandler<StepChangedEventArgs>? StepChanged;

    /// <summary>Advances one step. Enabled on the two steps that have a step after them.</summary>
    public RelayCommand NextCommand { get; }

    /// <summary>
    /// Returns one step. Enabled on the two steps that have a step before them.
    /// </summary>
    /// <remarks>
    /// Deliberately absent on <see cref="OnboardingStep.Ready"/>: the credential is written by then, and
    /// a Back that returned to the PIN form would imply the PIN could still be changed there. It cannot —
    /// changing it afterwards happens in Settings, which asks for the old one first.
    /// </remarks>
    public RelayCommand BackCommand { get; }

    /// <summary>
    /// No-op on this page: the shell's own navigation command is the binding target the EmptyState CTA uses,
    /// and it must resolve to a non-null command so the button does not throw.
    /// </summary>
    public RelayCommand NavigateCommand { get; }

    /// <summary>Writes the credential. Enabled only on Security, and only once the form is valid.</summary>
    public AsyncRelayCommand FinishCommand { get; }

    /// <summary>Leaves setup for the dashboard. The only control on the last step.</summary>
    public RelayCommand EnterDashboardCommand { get; }

    /// <summary>Re-runs the prerequisite probe behind the Permissions step.</summary>
    public AsyncRelayCommand RecheckCommand { get; }

    /// <summary>Opens one Windows Settings page. The URI arrives as the command parameter.</summary>
    public RelayCommand OpenSettingsCommand { get; }

    /// <summary>Links to the Windows Settings page for overlay access.</summary>
    public RelayCommand GrantOverlayAccessCommand { get; }

    /// <summary>Links to the Windows Settings page for battery saver configuration.</summary>
    public RelayCommand GrantBatteryAccessCommand { get; }

    // ---- permission status indicators (step 2) ----
    // Kept as bools even though the only Windows-side answers these four map to are "is the service/agent
    // running" and "is Hello enrolled": the wizard presents them as a status rail so the shell can render a
    // consistent badge per row without a converter per property. The value is the badge; the explanatory
    // sentence below it says whether the row still matters.

    /// <summary>True when the background service answered the status call (mirrors ServiceReady).</summary>
    public bool IsAccessibilityGranted => ServiceReady;

    /// <summary>
    /// True when the desktop agent is running in this session (mirrors AgentReady).
    /// </summary>
    /// <remarks>There is no Windows "overlay permission" toggle, so this row tracks the agent whose window
    /// is what draws the lock prompt. The link on the row opens the startup-settings page where that state
    /// is decided.</remarks>
    public bool IsOverlayGranted
    {
        get => _isOverlayGranted;
        private set
        {
            if (Set(ref _isOverlayGranted, value))
            {
                OnPropertyChanged(nameof(OverlayStatus));
            }
        }
    }

    /// <summary>True when the service reports battery/saver state is readable (mirrors ServiceReady).</summary>
    /// <remarks>AppGuardian holds no battery grant; this reflects whether the OS-side state the battery row
    /// reports is available, and the link lands the user on battery-saver settings.</remarks>
    public bool IsBatteryGranted
    {
        get => _isBatteryGranted;
        private set
        {
            if (Set(ref _isBatteryGranted, value))
            {
                OnPropertyChanged(nameof(BatteryStatus));
            }
        }
    }

    public string AccessibilityStatus => ServiceReady ? StatusReady : "Not ready";

    public string OverlayStatus => _isOverlayGranted ? StatusReady : "Not running";

    public string BatteryStatus => _isBatteryGranted ? StatusReady : "Not available";

    /// <summary>
    /// Whether the permission check is in flight. Drives the skeleton loader on the Permissions step.
    /// </summary>
    /// <remarks>Bound to the skeleton's Visibility through <c>BoolToVisible</c>; the EmptyState-style badge
    /// rows stay collapsed while this is true, so a not-yet-read service does not flash red on first visit.
    /// </remarks>
    public bool IsLoading => ServiceReady || AgentReady || _helloAvailable || _completed;

    // ---- PIN strength (step 3) ----
    // Reported as a count out of six for the segmented meter, plus a label word. The view binds
    // StrengthScore/StrengthLevel already; these aliases sit beside the PasswordBox so the copy matrix has
    // a place to land the per-digit length feedback the brief asks for.
    public string PinStrength => StrengthLevel;

    public string PinStrengthLabel => StrengthScore switch
    {
        >= 4 => "Strong",
        3 => "Good",
        2 => "Fair",
        1 => "Weak",
        _ => "Too short",
    };

    /// <summary>
    /// Per-step, because the shell renders this as the page heading.
    /// </summary>
    /// <remarks>
    /// One fixed title across four screens would leave the heading disagreeing with the content on three
    /// of them. The Welcome title is the local-first claim itself, which is the whole reason that step
    /// exists.
    /// </remarks>
    public override string Title => _step switch
    {
        OnboardingStep.Permissions => "What AppGuardian needs from Windows",
        OnboardingStep.Security => "Choose how you unlock",
        OnboardingStep.Ready => "You're set up",
        _ => "Everything stays on this PC",
    };

    /// <summary>Which of the four screens is showing.</summary>
    /// <remarks>
    /// The setter raises every property derived from it, in one place. Spreading those raises over the
    /// call sites that move the step is how a panel ends up still visible after the step behind it
    /// changed, and that bug is invisible until someone clicks Back.
    /// </remarks>
    public OnboardingStep Step
    {
        get => _step;
        private set
        {
            // Clamped rather than trusted. Next and Back are CanExecute-gated to the interior steps, so
            // neither can run off the end today; the clamp means a later caller cannot park the wizard on
            // a step with no panel, which renders as a blank page rather than as an error anyone notices.
            var next = (OnboardingStep)Math.Clamp(
                (int)value,
                (int)OnboardingStep.Welcome,
                (int)OnboardingStep.Ready);

            if (!Set(ref _step, next))
            {
                return;
            }

            OnPropertyChanged(nameof(StepIndex));
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(IsWelcomeStep));
            OnPropertyChanged(nameof(IsPermissionsStep));
            OnPropertyChanged(nameof(IsSecurityStep));
            OnPropertyChanged(nameof(IsReadyStep));

            NextCommand.RaiseCanExecuteChanged();
            BackCommand.RaiseCanExecuteChanged();
            FinishCommand.RaiseCanExecuteChanged();
            EnterDashboardCommand.RaiseCanExecuteChanged();

            StepChanged?.Invoke(this, new(next));
        }
    }

    /// <summary>
    /// One-based step number, for the progress rail.
    /// </summary>
    /// <remarks>
    /// One-based because a human reads it: segment 1 lights on the first screen. The rail's four segments
    /// each ask <c>IntAtLeast</c> whether this has reached 1, 2, 3 and 4, so the enum's numeric values and
    /// this offset have to stay in step with the markup.
    /// </remarks>
    public int StepIndex => (int)_step + 1;

    /// <summary>True on the Welcome step.</summary>
    /// <remarks>
    /// Four bools rather than four <c>DataTrigger</c>s comparing <see cref="Step"/>. The panels need a
    /// <see cref="System.Windows.Visibility"/>, and reaching one from the enum means chaining
    /// <c>EnumEquals</c> into <c>BoolToVisible</c> — which WPF has no syntax for inside a single binding.
    /// The trigger route costs about eight lines of style per panel; this costs one attribute.
    /// </remarks>
    public bool IsWelcomeStep => _step == OnboardingStep.Welcome;

    /// <summary>True on the Permissions step.</summary>
    public bool IsPermissionsStep => _step == OnboardingStep.Permissions;

    /// <summary>True on the Security step, the only one that writes anything.</summary>
    public bool IsSecurityStep => _step == OnboardingStep.Security;

    /// <summary>True on the final step.</summary>
    public bool IsReadyStep => _step == OnboardingStep.Ready;

    // ---- step 2: what Windows is actually doing ----

    /// <summary>
    /// True when the background service answered the status call.
    /// </summary>
    /// <remarks>
    /// This is the one prerequisite with no Settings link, because there is nothing for the user to switch
    /// on: the service is installed to start with Windows, and a machine that has just finished the
    /// installer can reach this screen a second or two before the service is listening. So the row offers
    /// "Check again" rather than a way out to Windows.
    /// </remarks>
    public bool ServiceReady
    {
        get => _serviceReady;
        private set
        {
            if (Set(ref _serviceReady, value))
            {
                OnPropertyChanged(nameof(ServiceStatus));
                OnPropertyChanged(nameof(IsAccessibilityGranted));
                OnPropertyChanged(nameof(AccessibilityStatus));
                OnPropertyChanged(nameof(IsBatteryGranted));
                OnPropertyChanged(nameof(BatteryStatus));
                OnPropertyChanged(nameof(IsLoading));
            }
        }
    }

    /// <summary>
    /// True when the desktop agent is running in this session.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="ServiceReady"/>, and not derivable from it. The service holds the policy
    /// and the credential; the agent is what draws the lock overlay and moves hidden windows, and it runs
    /// per interactive session from a run-at-logon entry the user can switch off in Windows Settings. A
    /// machine with a healthy service and no agent enforces nothing — which is exactly the state this row
    /// exists to surface, and the reason its link goes to <see cref="StartupAppsUri"/>.
    /// </remarks>
    public bool AgentReady
    {
        get => _agentReady;
        private set
        {
            if (Set(ref _agentReady, value))
            {
                OnPropertyChanged(nameof(AgentStatus));
                OnPropertyChanged(nameof(IsOverlayGranted));
                OnPropertyChanged(nameof(OverlayStatus));
                OnPropertyChanged(nameof(IsLoading));
            }
        }
    }

    /// <summary>
    /// The badge word for a prerequisite that is satisfied. Green, through <c>Badge.Status</c>.
    /// </summary>
    /// <remarks>
    /// The four status strings below are a closed vocabulary, because <c>Badge.Status</c> in Theme.xaml
    /// colours itself by matching them literally: this value is green, <see cref="StatusOptional"/> is
    /// amber, and every other string is red. So the negative cases can say what is wrong in the user's
    /// words — "Not responding", "Not running" — and get the right colour for free. A fifth state means
    /// editing that style too, not just adding a string here.
    /// </remarks>
    public const string StatusReady = "Ready";

    /// <summary>The badge word for a prerequisite that improves things but is not required. Amber.</summary>
    public const string StatusOptional = "Optional";

    /// <summary>
    /// The words in the service row's badge.
    /// </summary>
    /// <remarks>
    /// A string rather than a bool plus a converter, because this is the accessible text for the row: a
    /// coloured border announces nothing, and the badge's own content is what a screen reader reads. It is
    /// also why every negative value is a sentence fragment a user can act on rather than "False".
    /// </remarks>
    public string ServiceStatus => _serviceReady ? StatusReady : "Not responding";

    /// <summary>The words in the agent row's badge.</summary>
    public string AgentStatus => _agentReady ? StatusReady : "Not running";

    /// <summary>
    /// The words in the Windows Hello row's badge.
    /// </summary>
    /// <remarks>
    /// "Optional" rather than "Not available", because Hello genuinely is optional here: the PIN on the
    /// next step stands on its own, and a machine with no fingerprint reader and no IR camera is not
    /// misconfigured. Amber says there is something the user could improve without implying they must.
    /// </remarks>
    public string HelloStatus => _helloAvailable ? StatusReady : StatusOptional;

    /// <summary>
    /// The words in the notifications row's badge. Always <see cref="StatusOptional"/>.
    /// </summary>
    /// <remarks>
    /// Constant, because this is the one row AppGuardian cannot measure. Windows exposes no per-app answer
    /// to "are my notifications enabled" that a desktop process should rely on, and the honest thing to do
    /// with a prerequisite we cannot read is to say what it is for and link to the page — not to guess and
    /// then be wrong in green.
    /// </remarks>
    public string NotificationsStatus => StatusOptional;

    // ---- step 3: the credential (FR-301, FR-302) ----

    /// <summary>
    /// Whether this machine has Windows Hello enrolled.
    /// </summary>
    /// <remarks>
    /// Read from the service rather than probed here, so this screen and the component that will actually
    /// perform the verification agree about what exists.
    /// <para>
    /// Setting it false forces <see cref="PreferHello"/> false too. A ticked "use Windows Hello" box on a
    /// machine without Hello would send <c>AuthMethod.WindowsHello</c> to a service that cannot honour it,
    /// and the user would discover that at their first lock prompt instead of here.
    /// </para>
    /// </remarks>
    public bool HelloAvailable
    {
        get => _helloAvailable;
        private set
        {
            if (!Set(ref _helloAvailable, value))
            {
                return;
            }

            if (!value)
            {
                PreferHello = false;
            }

            OnPropertyChanged(nameof(HelloStatus));
            OnPropertyChanged(nameof(HelloExplanation));
            OnPropertyChanged(nameof(IsAccessibilityGranted));
            OnPropertyChanged(nameof(AccessibilityStatus));
            OnPropertyChanged(nameof(IsLoading));
        }
    }

    /// <summary>
    /// Whether Windows Hello is tried first. Two-way bound to a checkbox.
    /// </summary>
    /// <remarks>
    /// This chooses which method is tried first, not whether a fallback exists. The PIN is collected either
    /// way, because FR-301 requires the fallback unconditionally — see the class remarks.
    /// </remarks>
    public bool PreferHello
    {
        get => _preferHello;
        set => Set(ref _preferHello, value);
    }

    /// <summary>The sentence under the Hello checkbox. Changes with what the machine supports.</summary>
    public string HelloExplanation => _helloAvailable
        ? "Windows Hello is ready on this PC. AppGuardian will ask for your face, fingerprint or Windows "
          + "PIN first, and fall back to the PIN below if that does not work."
        : "Windows Hello is not set up on this PC, so AppGuardian will use the PIN below. You can add "
          + "Hello later in Windows sign-in options — AppGuardian will pick it up on its own.";

    /// <summary>
    /// The fallback PIN or password, pushed here by the view's <c>PasswordChanged</c> handler.
    /// </summary>
    /// <remarks>
    /// Pushed rather than bound, because <see cref="System.Windows.Controls.PasswordBox.Password"/> is not a
    /// dependency property — deliberately, so a password cannot be reached by walking the visual tree.
    /// <para>
    /// Held only until <see cref="FinishAsync"/> returns, which clears it and the confirm entry in its
    /// <c>finally</c>. It is never logged, and never copied into the confirm box.
    /// </para>
    /// </remarks>
    public string Secret
    {
        get => _secret;
        set
        {
            if (!Set(ref _secret, value))
            {
                return;
            }

            var (score, level) = Measure(_secret);

            // The explicit-name overload, because the assignment happens here rather than inside each
            // property's own setter — CallerMemberName would raise "Secret" three times.
            Set(ref _strengthScore, score, nameof(StrengthScore));
            Set(ref _strengthLevel, level, nameof(StrengthLevel));

            Revalidate();
        }
    }

    /// <summary>The confirmation entry. Never pre-filled from <see cref="Secret"/>.</summary>
    public string Confirm
    {
        get => _confirm;
        set
        {
            if (Set(ref _confirm, value))
            {
                Revalidate();
            }
        }
    }

    /// <summary>
    /// Strength as a count out of four, for the segmented meter.
    /// </summary>
    /// <remarks>
    /// A count rather than a percentage, and four segments rather than a bar: the measurement is discrete,
    /// and a bar filled to 63% claims a precision it does not have. Computed inside <see cref="Secret"/>'s
    /// setter so the meter cannot fall out of step with the box.
    /// </remarks>
    public int StrengthScore => _strengthScore;

    /// <summary>
    /// The word beside the meter: "Too short", "Weak", "Fair", "Good", "Strong", or empty when nothing has
    /// been typed.
    /// </summary>
    /// <remarks>
    /// This is the accessible statement of strength — four Borders carry no automation text worth reading,
    /// and the view marks this a polite live region so a change is announced without interrupting typing.
    /// <c>Strength.Label</c> and <c>Strength.Segment.Fill</c> both colour themselves by matching these words
    /// literally, so the vocabulary is closed exactly as the badge one is, and an unrecognised value stays
    /// red — the safe direction for a claim about a secret.
    /// </remarks>
    public string StrengthLevel => _strengthLevel;

    /// <summary>
    /// True when both entries match and the secret is long enough.
    /// </summary>
    /// <remarks>
    /// This gates <see cref="FinishCommand"/>. Before it did, the primary button on this step was enabled
    /// against an untouched form and failed on click with a validation message — the precise defect class
    /// this pass exists to remove. The message under the boxes still says what is missing, so a disabled
    /// button is never unexplained.
    /// </remarks>
    public bool CanSubmit =>
        _secret.Length >= MinimumSecretLength && string.Equals(_secret, _confirm, StringComparison.Ordinal);

    /// <summary>True once the credential has been written. Read by the shell's navigation gate.</summary>
    public bool IsCompleted => _completed;

    /// <summary>
    /// Reads what Windows and the service are doing, then decides whether setup is needed at all.
    /// </summary>
    /// <remarks>
    /// Setup is mandatory but not repeatable. A machine that already has a credential should not be asked
    /// for another one, so an already-configured reply skips to the end and leaves immediately — the
    /// alternative traps a returning user on a wizard with nothing to do.
    /// </remarks>
    public override async Task LoadAsync(CancellationToken ct)
    {
        IsBusy = true;

        try
        {
            await CheckPrerequisitesAsync(ct).ConfigureAwait(true);

            if (_completed)
            {
                Release();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// The probe behind the Permissions step, and behind "Check again".
    /// </summary>
    /// <remarks>
    /// Both calls in one method, because "Check again" has to refresh all four rows and the Hello row is
    /// answered by the auth call rather than the status one.
    /// <para>
    /// A null reply raises no banner. Reaching this screen a second after the installer finishes is normal,
    /// the affected row already reads "Not responding", and a banner would be the same sentence twice. The
    /// banner is reserved for the one failure that actually blocks the user: the credential write.
    /// </para>
    /// </remarks>
    private async Task CheckPrerequisitesAsync(CancellationToken ct)
    {
        var status = await _client.GetStatusAsync(ct).ConfigureAwait(true);

        ServiceReady = status is not null && status.ServiceRunning;
        AgentReady = status is not null && status.AgentRunning;

        // The grant indicators track the real Windows-side state rather than any AppGuardian switch:
        // there is no "accessibility/overlay/battery permission" to flip, so the rows report service health
        // and agent presence and link out to the one Settings page each state is decided on.
        IsOverlayGranted = status is not null && status.AgentRunning;
        IsBatteryGranted = status is not null;

        var auth = await _client.GetAuthStatusAsync(ct).ConfigureAwait(true);

        if (auth is null)
        {
            return;
        }

        HelloAvailable = auth.WindowsHelloAvailable;

        if (auth.IsConfigured && !_completed)
        {
            MarkConfigured();
        }
    }

    /// <summary>
    /// Opens one Windows Settings page.
    /// </summary>
    /// <remarks>
    /// A link out rather than a "Grant" button, because AppGuardian cannot change any of these four things
    /// on the user's behalf — Windows owns all of them. A button labelled "Grant" would promise otherwise
    /// and then produce a Settings window the user has to finish the job in anyway.
    /// <para>
    /// The target is checked against <see cref="AllowedSettingsUris"/> first; that field says why a check
    /// which cannot fail today is worth having.
    /// </para>
    /// </remarks>
    private void OpenSettings(object? parameter)
    {
        if (parameter?.ToString() is not { } uri || !AllowedSettingsUris.Contains(uri))
        {
            _log.LogWarning("A Settings link was ignored: its target is not on the allow-list.");
            return;
        }

        try
        {
            // UseShellExecute is required, not stylistic: ms-settings: is a protocol handler rather than an
            // executable, and Process.Start cannot resolve one without asking the shell.
            using var opened = Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Windows Settings could not be opened.");

            ShowError("Windows Settings would not open. You can get to the same place from Start, then "
                      + "Settings.");
        }
    }

    /// <summary>The reason the credential cannot be written, or null when it can.</summary>
    private string? Validate()
    {
        if (_secret.Length < MinimumSecretLength)
        {
            return $"Use at least {MinimumSecretLength} characters.";
        }

        return string.Equals(_secret, _confirm, StringComparison.Ordinal)
            ? null
            : "The two entries do not match.";
    }

    /// <summary>
    /// Updates the button and the banner after either box changes.
    /// </summary>
    /// <remarks>
    /// Only the mismatch is reported in the banner. Length is already stated permanently and in place by the
    /// strength label beside the meter, which sits next to the box rather than at the top of the page, so a
    /// banner saying it too would be the same sentence twice in the worse of the two positions.
    /// <para>
    /// The mismatch waits until the confirm box is at least as long as the secret. Reporting it from the
    /// first keystroke means flagging every prefix of a correct entry as wrong, which trains the user to
    /// ignore the banner.
    /// </para>
    /// </remarks>
    private void Revalidate()
    {
        var mismatch = _secret.Length > 0
            && _confirm.Length >= _secret.Length
            && !string.Equals(_secret, _confirm, StringComparison.Ordinal);

        if (mismatch)
        {
            ShowError("The two entries do not match.");
        }
        else
        {
            ClearMessage();
        }

        OnPropertyChanged(nameof(CanSubmit));
        FinishCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Writes the credential, then advances to the last step. FR-301.
    /// </summary>
    /// <remarks>
    /// Re-validates rather than trusting <see cref="CanSubmit"/>. The command is gated on it, but a keystroke
    /// can land between the gate being evaluated and this running, and this is the one call on the page that
    /// persists anything.
    /// </remarks>
    private async Task FinishAsync()
    {
        if (Validate() is { } problem)
        {
            ShowError(problem);
            return;
        }

        IsBusy = true;

        CallResult result;

        try
        {
            // Hello is only requested when the machine actually has it. PreferHello alone is not enough:
            // HelloAvailable can go false underneath a ticked box if enrolment is removed mid-setup.
            var method = _preferHello && _helloAvailable ? AuthMethod.WindowsHello : AuthMethod.Pin;

            result = await _client.SetUpAuthAsync(method, _secret, CancellationToken.None)
                .ConfigureAwait(true);
        }
        finally
        {
            // Cleared whether or not the write succeeded, and before the outcome is reported. Clearing runs
            // through Secret's setter, which re-runs validation and resets the banner — so reporting has to
            // come after this, or it is overwritten by its own cleanup. On success the secret is stored and
            // this copy is redundant; on failure retyping six characters is cheaper than keeping a password
            // in a live object for the lifetime of the window.
            Secret = string.Empty;
            Confirm = string.Empty;
            IsBusy = false;
        }

        if (result.Succeeded)
        {
            MarkConfigured();
            return;
        }

        ShowError(result.Message
                  ?? "Your PIN could not be saved. Nothing has changed — try again in a moment.");
    }

    /// <summary>
    /// Records that a credential now exists, moves to the last step, and persists the first-run flag so the
    /// shell can skip this wizard on launch. FR-300.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Release"/> so that writing the credential and leaving setup are two events.
    /// They used to be one, which meant the confirmation the user needed most — that protection is on, and
    /// what it does not cover — flashed past on the way to the dashboard.
    /// <para>
    /// No success banner. The Ready step's own copy says it, in the place the user is already looking.
    /// </para>
    /// <para>
    /// <see cref="HasCompletedOnboarding"/> is written through the same <c>UserSettings</c> store the theme
    /// uses, because it is a local UI preference with no service-side meaning — the service is what actually
    /// remembers whether a credential exists, and the shell re-checks that. A setting that lost a disk write
    /// therefore re-runs a wizard the user has already finished, which is the safe failure direction.
    /// </para>
    /// </remarks>
    private void MarkConfigured()
    {
        if (!Set(ref _completed, true, nameof(IsCompleted)))
        {
            return;
        }

        _ = PersistOnboardingCompleteAsync();

        Step = OnboardingStep.Ready;
    }

    /// <summary>
    /// Marks the onboarding gate as completed in <c>settings.json</c>, so the shell can skip the wizard on
    /// the next launch.
    /// </summary>
    /// <remarks>
    /// A load-then-mutate-then-save over the live settings object, because <see cref="UserSettings"/> carries
    /// other fields written by <see cref="ThemeService"/> and a blind new-instance write would drop them.
    /// The store is atomic on disk, so a crash mid-write leaves either the old document or this one.
    /// </remarks>
    private async Task PersistOnboardingCompleteAsync()
    {
        try
        {
            var loaded = await _settingsStore.LoadAsync().ConfigureAwait(true);

            var settings = loaded.Value;
            settings.HasCompletedOnboarding = true;

            await _settingsStore.SaveAsync(settings).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Logged, not surfaced. A failed write means the gate is re-evaluated next launch, which just
            // re-opens a wizard the user already finished — annoying, not data-loss.
            _log.LogWarning(ex, "First-run completion could not be persisted.");
        }
    }

    /// <summary>
    /// Leaves setup. The shell listens for this and navigates to the dashboard.
    /// </summary>
    /// <remarks>
    /// The only user-reachable caller is <see cref="EnterDashboardCommand"/>, which is gated on the last
    /// step; <see cref="LoadAsync"/> also calls it when the credential already existed, so a returning user
    /// passes straight through.
    /// </remarks>
    private void Release() => Completed?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Scores a candidate secret out of four, with the word that goes beside the meter.
    /// </summary>
    /// <remarks>
    /// Length first, variety second, because length is what costs an attacker and variety is what users
    /// reach for instead. A ten-character phrase scores above a six-character mix of cases, digits and
    /// symbols, which is the correct order.
    /// <para>
    /// Deliberately not an entropy estimate. A real one needs a dictionary and a keyboard-layout model, and a
    /// number with a decimal point would imply a precision four segments do not have. It is a hint — and the
    /// class remarks explain why a modest PIN here is not the threat it appears to be: the store is
    /// DPAPI-protected per machine, and FR-306 caps online guessing at a handful of attempts an hour.
    /// </para>
    /// <para>
    /// Static, and taking the string rather than reading the field, so it can be reasoned about without a
    /// view model in front of you.
    /// </para>
    /// </remarks>
    private static (int Score, string Level) Measure(string secret)
    {
        if (secret.Length == 0)
        {
            // Empty is unanswered, not weak. A red "Weak" against an untouched box criticises the user for
            // something they have not done yet.
            return (0, string.Empty);
        }

        if (secret.Length < MinimumSecretLength)
        {
            // Score 0 leaves every segment unlit, so the meter agrees with the label instead of showing one
            // lit segment beside the words "Too short".
            return (0, "Too short");
        }

        var score = 1
            + (secret.Length >= 10 ? 1 : 0)
            + (secret.Length >= 14 ? 1 : 0)
            + (CharacterClasses(secret) >= 3 ? 1 : 0);

        // Long is not the same as hard. Clamped rather than zeroed: the entry still meets the minimum, and a
        // twenty-character secret reported as "Too short" would look like a broken meter.
        if (IsTrivial(secret))
        {
            score = 1;
        }

        return score switch
        {
            >= 4 => (4, "Strong"),
            3 => (3, "Good"),
            2 => (2, "Fair"),
            _ => (1, "Weak"),
        };
    }

    /// <summary>How many of lower case, upper case, digit and everything else appear.</summary>
    private static int CharacterClasses(string text)
    {
        var lower = false;
        var upper = false;
        var digit = false;
        var other = false;

        foreach (var character in text)
        {
            if (char.IsLower(character))
            {
                lower = true;
            }
            else if (char.IsUpper(character))
            {
                upper = true;
            }
            else if (char.IsDigit(character))
            {
                digit = true;
            }
            else
            {
                other = true;
            }
        }

        return (lower ? 1 : 0) + (upper ? 1 : 0) + (digit ? 1 : 0) + (other ? 1 : 0);
    }

    /// <summary>
    /// True for entries that are long without being hard: one repeated character, or a straight run up or
    /// down the keyboard.
    /// </summary>
    /// <remarks>
    /// Two patterns, and no dictionary. "password" is not caught here, and adding a handful of words would be
    /// worse than being clear about the limit — it would make the meter look like it knows more than it does.
    /// </remarks>
    private static bool IsTrivial(string text)
    {
        var allSame = true;
        var ascending = true;
        var descending = true;

        for (var i = 1; i < text.Length; i++)
        {
            var previous = text[i - 1];
            var current = text[i];

            if (current != previous)
            {
                allSame = false;
            }

            if (current != previous + 1)
            {
                ascending = false;
            }

            if (current != previous - 1)
            {
                descending = false;
            }
        }

        return allSame || ascending || descending;
    }
}
