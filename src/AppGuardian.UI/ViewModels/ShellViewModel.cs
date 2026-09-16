using System.Collections.ObjectModel;
using System.Runtime.Versioning;
using System.Windows.Threading;
using AppGuardian.Shared.Models;
using AppGuardian.Shared.Storage;
using AppGuardian.UI.Mvvm;
using AppGuardian.UI.Services;
using Microsoft.Extensions.Logging;

namespace AppGuardian.UI.ViewModels;

/// <summary>
/// The shell: navigation, the status strip, and the global pause switch. FR-804, FR-805, FR-806.
/// </summary>
/// <remarks>
/// Owns the only timer in the dashboard. Each page could poll for itself, but five pages each running
/// their own timer would put five times the traffic on a pipe whose whole purpose is to stay out of the
/// way of the one-second lock budget (NFR-P4). The shell polls, and pages are told to re-read.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ShellViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// Status refresh interval.
    /// </summary>
    /// <remarks>
    /// Five seconds, not one. Nothing on the status strip changes fast enough to warrant more, and the
    /// service answers system.status by walking its rule set and process table — cheap, but not free,
    /// and it is doing that on behalf of a window the user may have left open all day.
    /// </remarks>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly GuardianClient _client;
    private readonly Func<PageId, PageViewModel> _pages;
    private readonly ILogger<ShellViewModel> _log;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<PageId, PageViewModel> _cache = new();

    // Read once at startup to decide whether the onboarding gate should be raised at all. This is a local
    // per-user preference (settings.json), not a service fact, so loading it here avoids a pipe round trip
    // before the first navigation decision and keeps the gate deterministic on a cold start.
    private readonly AtomicJsonStore<UserSettings> _settings =
        new(StoragePaths.SettingsFile);

    private CancellationTokenSource _pageLoad = new();
    private PageViewModel? _current;
    private PageId _currentId = PageId.Apps;
    private SystemStatus? _status;
    private AuthStatus? _auth;
    private bool _serviceReachable = true;
    private bool _onboardingRequired;
    private bool _hasCompletedOnboarding;
    private bool _disposed;

    public ShellViewModel(
        GuardianClient client,
        Func<PageId, PageViewModel> pages,
        ILogger<ShellViewModel> log)
    {
        _client = client;
        _pages = pages;
        _log = log;

        NavigateCommand = new RelayCommand(
            parameter =>
            {
                if (parameter is PageId id)
                {
                    Navigate(id);
                }
            });

        RefreshCommand = new AsyncRelayCommand(
            () => RefreshAsync(CancellationToken.None),
            onError: ex => _log.LogWarning(ex, "A manual refresh failed."));

        TogglePauseCommand = new AsyncRelayCommand(
            TogglePauseAsync,
            () => _serviceReachable,
            ex => _log.LogWarning(ex, "The pause switch could not be changed."));

        LockNowCommand = new AsyncRelayCommand(
            LockNowAsync,
            () => _serviceReachable && _status?.LockedCount != _status?.ProtectedCount,
            ex => _log.LogWarning(ex, "Lock-now failed."));

        _timer = new DispatcherTimer { Interval = PollInterval };
        _timer.Tick += async (_, _) => await SafeRefreshAsync().ConfigureAwait(true);

        // Pushed changes are handled on the dispatcher, because this arrives on the pipe's read pump and
        // every binding target below lives on the UI thread.
        _client.PolicyChanged += OnPolicyChanged;
    }

    public ObservableCollection<NavigationItem> NavigationItems { get; } = new()
    {
        new NavigationItem(PageId.Apps, "Apps", "Choose what to protect"),
        new NavigationItem(PageId.Locked, "Locked", "Locked apps and active unlocks"),
        new NavigationItem(PageId.Hidden, "Hidden", "Apps on the hidden desktop"),
        new NavigationItem(PageId.Power, "Battery", "CPU and battery restrictions"),
        new NavigationItem(PageId.Activity, "Activity", "Local audit log"),
        new NavigationItem(PageId.Settings, "Settings", "Credentials and diagnostics"),
    };

    public RelayCommand NavigateCommand { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand TogglePauseCommand { get; }

    public AsyncRelayCommand LockNowCommand { get; }

    public PageViewModel? Current
    {
        get => _current;
        private set => Set(ref _current, value);
    }

    public PageId CurrentId
    {
        get => _currentId;
        private set
        {
            if (Set(ref _currentId, value))
            {
                foreach (var item in NavigationItems)
                {
                    item.IsSelected = item.Id == value;
                }
            }
        }
    }

    public SystemStatus? Status
    {
        get => _status;
        private set
        {
            if (Set(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusHeadline));
                OnPropertyChanged(nameof(StatusDetail));
                OnPropertyChanged(nameof(IsDegraded));
                OnPropertyChanged(nameof(IsPaused));
                OnPropertyChanged(nameof(PauseButtonText));
                OnPropertyChanged(nameof(HidingIsFullStrength));
                LockNowCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public AuthStatus? Auth
    {
        get => _auth;
        private set
        {
            if (Set(ref _auth, value))
            {
                // A completed onboarding suppresses the gate even if the service probe says the credential
                // is missing — that state is decided by what the user last did here, not by whether the
                // credential survived a reboot. The wizard's own OnboardingViewModel re-raises the gate from
                // live state its next LoadAsync, so a cleared credential still sends the user back through.
                OnboardingRequired = !_hasCompletedOnboarding && value is not null && !value.IsConfigured;
            }
        }
    }

    /// <summary>
    /// True until a credential exists. FR-300 makes onboarding mandatory, so the shell hides navigation
    /// entirely rather than letting the user create rules that could never be enforced.
    /// </summary>
    public bool OnboardingRequired
    {
        get => _onboardingRequired;
        private set
        {
            if (Set(ref _onboardingRequired, value) && value)
            {
                Navigate(PageId.Onboarding);
            }
        }
    }

    public bool ServiceReachable
    {
        get => _serviceReachable;
        private set
        {
            if (Set(ref _serviceReachable, value))
            {
                OnPropertyChanged(nameof(StatusHeadline));
                OnPropertyChanged(nameof(StatusDetail));
                OnPropertyChanged(nameof(IsDegraded));
                TogglePauseCommand.RaiseCanExecuteChanged();
                LockNowCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsPaused => _status?.ProtectionPaused == true;

    public string PauseButtonText => IsPaused ? "Resume protection" : "Pause protection";

    /// <summary>False when hiding is running on the minimise fallback. FR-806, ADR-007.</summary>
    public bool HidingIsFullStrength => _status?.VirtualDesktopSupported == true;

    public bool IsDegraded => !ServiceReachable || _status?.IsFullyOperational != true;

    public string StatusHeadline
    {
        get
        {
            if (!ServiceReachable)
            {
                return "Not protecting";
            }

            if (IsPaused)
            {
                return "Paused";
            }

            return _status?.IsFullyOperational == true ? "Protecting" : "Partly protecting";
        }
    }

    /// <summary>
    /// One line explaining the headline.
    /// </summary>
    /// <remarks>
    /// Ordered by severity, and only the worst problem is shown. A user looking at a stack of four
    /// warnings does not know which one to act on; a user looking at one does.
    /// </remarks>
    public string StatusDetail
    {
        get
        {
            if (!ServiceReachable)
            {
                return "AppGuardian's background service is not responding. Your rules are saved, but "
                       + "nothing is being enforced right now.";
            }

            if (IsPaused)
            {
                return "You paused protection. Locks, hiding and battery limits are all off until you "
                       + "resume.";
            }

            // The service's own words. It knows which of several possible degradations is in force, and
            // paraphrasing it here would eventually contradict it.
            if (!string.IsNullOrWhiteSpace(_status?.DegradedReason))
            {
                return _status!.DegradedReason!;
            }

            if (_status?.AgentRunning != true)
            {
                return "The part of AppGuardian that runs on your desktop is not running, so apps cannot "
                       + "be locked or hidden. Signing out and back in usually starts it.";
            }

            var protectedCount = _status?.ProtectedCount ?? 0;

            return protectedCount == 0
                ? "No apps are protected yet. Pick one from Apps to get started."
                : $"{protectedCount} app{(protectedCount == 1 ? "" : "s")} protected.";
        }
    }

    /// <summary>Loads status once, then navigates to the right first page.</summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        // The local completion flag is read first, before the service probe: if the wizard already finished
        // on a previous launch, the gate is dismissed immediately and the user lands on the app picker even
        // if the service is briefly unreachable on this start. The probe that follows still sets
        // OnboardingRequired from live auth state, so a credential that was cleared away from this machine
        // re-raises the gate and sends the user back through setup.
        if (ReadHasCompletedOnboarding())
        {
            _hasCompletedOnboarding = true;
            OnPropertyChanged(nameof(OnboardingRequired));
        }

        await RefreshAsync(ct).ConfigureAwait(true);

        // Onboarding wins over any remembered page. Deciding this after the first status read rather than
        // before it means the user never sees the app picker flash up and then be replaced.
        Navigate(OnboardingRequired ? PageId.Onboarding : PageId.Apps);

        _timer.Start();
    }

    /// <summary>
    /// Reads the persisted onboarding-complete flag so the shell knows whether the wizard gate is still
    /// required on this first visit. FR-300: onboarding is mandatory exactly once.
    /// </summary>
    /// <remarks>
    /// Loaded from <c>settings.json</c> rather than re-derived from service auth state, because the flag
    /// survives a credential loss that the service has already forgotten about — the user completed the
    /// wizard but the credential was since cleared, and they should be sent through setup again rather
    /// than dropped on the app picker with no way to lock anything. A missing or unreadable settings
    /// file defaults to "not completed," which is the safe direction: the wizard runs.
    /// </remarks>
    private bool ReadHasCompletedOnboarding()
    {
        try
        {
            // Synchronous load over an atomic store: settings.json is sub-KB and on local disk, so the
            // blocking read is shorter than a context switch to an async continuation would be, and this runs
            // once, before the window is shown.
            var loaded = _settings.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();

            return loaded.IsFailure ? false : loaded.Value.HasCompletedOnboarding;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Onboarding state could not be read; assuming setup is required.");
            return false;
        }
    }

    public void Navigate(PageId id)
    {
        // Onboarding is a gate, not a page in the rotation: leaving it before a credential exists would
        // let the user set rules that FR-300 says cannot be enforced.
        if (OnboardingRequired && id != PageId.Onboarding)
        {
            return;
        }

        if (Current is IGuardNavigation guard && guard.BlockNavigationReason is { } reason)
        {
            _log.LogDebug("Navigation away from {Page} was blocked: {Reason}", _currentId, reason);
            return;
        }

        if (!_cache.TryGetValue(id, out var page))
        {
            page = _pages(id);
            _cache[id] = page;

            // Onboarding raises Completed when the user finishes setup. Subscribing here — only when the
            // page is first created, so the handler cannot accumulate across revisits — and not in the
            // constructor, because _pages(id) is a delegate that the shell receives and the page does not
            // exist to wire up before it does.
            if (id == PageId.Onboarding && page is OnboardingViewModel onboarding)
            {
                onboarding.Completed += OnOnboardingCompleted;
            }
        }

        Current = page;
        CurrentId = id;

        // The previous page's load is abandoned rather than awaited. Its results would be written into a
        // view that is no longer on screen, and on a slow service that write could land after the user
        // has already navigated twice more.
        _pageLoad.Cancel();
        _pageLoad.Dispose();
        _pageLoad = new CancellationTokenSource();

        _ = LoadPageAsync(page, _pageLoad.Token);
    }

    /// <summary>Re-reads the current page. Called after a change that other pages may have caused.</summary>
    public Task ReloadCurrentAsync() =>
        Current is null ? Task.CompletedTask : LoadPageAsync(Current, _pageLoad.Token);

    private async Task LoadPageAsync(PageViewModel page, CancellationToken ct)
    {
        try
        {
            await page.LoadAsync(ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Expected on navigation.
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "{Page} could not be loaded.", page.Title);
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        var status = await _client.GetStatusAsync(ct).ConfigureAwait(true);

        ServiceReachable = status is not null;

        if (status is not null)
        {
            Status = status;
        }

        // Auth status is only read while onboarding is unresolved or the user is locked out. Polling it
        // every five seconds otherwise would put the failed-attempt counter on the wire far more often
        // than anything reads it.
        if (_auth is null || !_auth.IsConfigured || _auth.IsLockedOut)
        {
            var auth = await _client.GetAuthStatusAsync(ct).ConfigureAwait(true);

            if (auth is not null)
            {
                Auth = auth;
            }
        }
    }

    private async Task SafeRefreshAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await RefreshAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // A throwing timer tick would leave the strip frozen on stale text with no indication that it
            // had stopped updating, which is worse than a wrong value: it looks current.
            _log.LogDebug(ex, "A status refresh failed.");
            ServiceReachable = false;
        }
    }

    private async Task TogglePauseAsync()
    {
        var target = !IsPaused;

        var result = await _client.SetPausedAsync(target).ConfigureAwait(true);

        if (!result.Succeeded)
        {
            _log.LogWarning("The pause switch was refused: {Message}", result.Message);
        }

        // Re-read rather than assume. The service may refuse, or may pause and then report a further
        // degradation, and the strip has to show what is actually true.
        await RefreshAsync(CancellationToken.None).ConfigureAwait(true);
    }

    private async Task LockNowAsync()
    {
        // Ends every session for every app, which is what "lock everything now" means. FR-806.
        var result = await _client.EndSessionAsync(null).ConfigureAwait(true);

        if (!result.Succeeded)
        {
            _log.LogWarning("Lock-now was refused: {Message}", result.Message);
        }

        await RefreshAsync(CancellationToken.None).ConfigureAwait(true);
        await ReloadCurrentAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Onboarding finished. Drops the gate first so the setter's navigation-on-true is harmless, then
    /// advances to the apps page and asks it to refresh — the service is now configured and the cache
    /// may have missed that fact.
    /// </summary>
    private void OnOnboardingCompleted(object? sender, EventArgs e)
    {
        OnboardingRequired = false;
        Navigate(PageId.Apps);
        ReloadCurrentAsync();
    }

    private void OnPolicyChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        // Marshalled explicitly: this arrives on the pipe read pump, and touching a bound collection from
        // there throws an InvalidOperationException that surfaces nowhere near its cause.
        _ = System.Windows.Application.Current?.Dispatcher.InvokeAsync(async () =>
        {
            await SafeRefreshAsync().ConfigureAwait(true);
            await ReloadCurrentAsync().ConfigureAwait(true);
        });
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        _client.PolicyChanged -= OnPolicyChanged;
        _pageLoad.Cancel();
        _pageLoad.Dispose();

        // Detach from OnboardingViewModel.Completed if the page was created during this session. Safe to
        // call unconditionally: OnOnboardingCompleted only matters while the gate is up, and once Dispose
        // has run the handler can no longer reach a live page anyway.
        if (_cache.TryGetValue(PageId.Onboarding, out var page) && page is OnboardingViewModel onboarding)
        {
            onboarding.Completed -= OnOnboardingCompleted;
        }
    }
}

/// <summary>The pages the shell can show.</summary>
public enum PageId
{
    Onboarding,
    Apps,
    Locked,
    Hidden,
    Power,
    Activity,
    Settings,
}

/// <summary>One entry in the shell's navigation list.</summary>
public sealed class NavigationItem : ObservableObject
{
    private bool _isSelected;

    public NavigationItem(PageId id, string label, string description)
    {
        Id = id;
        Label = label;
        Description = description;
    }

    public PageId Id { get; }

    public string Label { get; }

    /// <summary>Tooltip text. Also read by narrators, which is why it is a sentence and not a keyword.</summary>
    public string Description { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }
}
