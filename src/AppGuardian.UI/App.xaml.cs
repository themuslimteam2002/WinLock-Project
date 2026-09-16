using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using AppGuardian.UI.Mvvm;
using AppGuardian.UI.Services;
using AppGuardian.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AppGuardian.UI;

/// <summary>
/// Application entry point: container, single-instance gate, and the top-level exception net.
/// </summary>
/// <remarks>
/// A plain <see cref="ServiceCollection"/> rather than a generic host. A host exists to run background
/// services and own a lifetime, and this process has neither — it is a window with a pipe client behind it,
/// and every long-running job in the product lives in the service or the agent by design. Bringing
/// <c>IHostedService</c> in here would add a second lifetime to reason about alongside WPF's own.
/// </remarks>
[SupportedOSPlatform("windows")]
public partial class App : Application
{
    /// <summary>
    /// Mutex name for the single-instance gate.
    /// </summary>
    /// <remarks>
    /// The <c>Global\</c> prefix makes the mutex visible across all terminal-services sessions on the
    /// machine. The dashboard is per-user conceptually, but a Global mutex is used so that two
    /// quick launches in the same user session — the realistic duplicate-launch scenario — are caught
    /// regardless of whether the first window is attached to the physical console session or a
    /// Fast User Switched session. A second user who switched in gets a separate window because
    /// the mutex is only checked, not enforced, and the signal message is broadcast to all top-level
    /// windows: only the dashboard running under that user's token responds to its own
    /// <c>RegisterWindowMessage</c> name.
    /// </remarks>
    private const string InstanceMutexName = "Global\\AppGuardian.UI.SingleInstance";

    /// <summary>
    /// Atom for the registered window message that signals the running dashboard to restore itself.
    /// Obtained once at type initialization via <see cref="RegisterWindowMessage"/>.
    /// </summary>
    private static readonly uint WM_APPVISIT = RegisterWindowMessage("AppGuardian.ShowDashboard");

    private ServiceProvider? _services;
    private Mutex? _instanceGate;
    private HwndSource? _messageHook;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Single instance. Two dashboards would each poll the service every five seconds and, worse, each
        // hold an independent copy of the rule list — the user would edit whichever window they happened to
        // be looking at and the other would quietly overwrite it on its next save.
        _instanceGate = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirstInstance);

        if (!isFirstInstance)
        {
            // No message box. The user double-clicked the tray icon or the shortcut and expects a window;
            // an error dialog telling them the program is already running is noise. Instead, broadcast a
            // registered-window message to every top-level window on the desktop; the running dashboard
            // recognizes its own message id and brings itself forward.
            BroadcastShowDashboard();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        _services = BuildServices();

        // Before the window is constructed, so the first frame is already in the user's palette. The
        // await is on the dispatcher, which is why this method is async void from here on: a
        // .GetAwaiter().GetResult() would deadlock against the same dispatcher the store's continuation
        // wants, and showing the window first would flash dark before flipping to light.
        var theme = _services.GetRequiredService<ThemeService>();

        theme.InitializeAsync(CancellationToken.None).ContinueWith(
            _ => ShowShell(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>
    /// Builds and shows the main window. Split out of <see cref="OnStartup"/> so the theme can be
    /// applied first without making startup itself <c>async void</c>.
    /// </summary>
    private void ShowShell()
    {
        if (_services is null)
        {
            return;
        }

        var shell = _services.GetRequiredService<ShellViewModel>();
        var toast = _services.GetRequiredService<ToastService>();

        var window = new MainWindow { DataContext = shell };

        // The toast overlay has its own DataContext — it binds to ToastService.Items, not to the shell.
        // Set here because the overlay is a named element, not a page resolved through templates.
        window.ToastOverlay.DataContext = toast;

        MainWindow = window;

        // Install a window-message hook so that when a second launcher fires the
        // AppGuardian.ShowDashboard registered message, this process can restore its window.
        var helper = new WindowInteropHelper(window);
        _messageHook = HwndSource.FromHwnd(helper.Handle);
        if (_messageHook != null)
        {
            _messageHook.AddHook(WndProc);
        }

        window.Show();

        // Shown before the first read completes, deliberately. The status strip starts on "Checking…" and
        // fills in a moment later; holding the window back until the pipe answers would make a service that
        // is still starting look like an application that failed to launch.
        _ = shell.InitializeAsync(CancellationToken.None);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // The shell owns a timer and the client owns a named-pipe connection. Disposing the provider runs
        // both, in reverse registration order.
        if (_services is not null)
        {
            var client = _services.GetService<GuardianClient>();

            _services.Dispose();

            // GuardianClient is IAsyncDisposable, which the container cannot await from a synchronous
            // Dispose. Waiting here is bounded and happens after the window is gone.
            client?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        }

        _messageHook?.RemoveHook(WndProc);
        _messageHook?.Dispose();

        _instanceGate?.ReleaseMutex();
        _instanceGate?.Dispose();

        base.OnExit(e);
    }

    /// <summary>
    /// Low-level window proc that dispatches the registered-window message back to
    /// <see cref="OnShowDashboardMessage"/>.
    /// </summary>
    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == (int)WM_APPVISIT)
        {
            var app = Application.Current as App;
            app?.OnShowDashboardMessage();
            handled = true;
            return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Broadcasts a registered-window message asking the running dashboard to restore itself.
    /// </summary>
    /// <remarks>
    /// <see cref="SendMessage"/> with <c>HWND_BROADCAST</c> delivers to all top-level windows. Only the
    /// dashboard process has registered a handler for the <c>AppGuardian.ShowDashboard</c> message, so
    /// other applications simply ignore it. This is the cross-process activation channel that SC-13
    /// left as a placeholder.
    /// </remarks>
    private static void BroadcastShowDashboard()
    {
        SendMessageW(
            HWND_BROADCAST,
            WM_APPVISIT,
            new IntPtr(GetCurrentProcessId()),
            IntPtr.Zero);
    }

    /// <summary>
    /// Intercepts the broadcast message and restores the main window if the process is running.
    /// </summary>
    /// <remarks>
    /// Registered as a message hook so the dashboard responds when a second launcher fires
    /// <see cref="BroadcastShowDashboard"/> instead of shutting down silently.
    /// </remarks>
    private void OnShowDashboardMessage()
    {
        if (MainWindow is not { } window)
        {
            return;
        }

        // Restore first, then bring to front. A window that is minimized needs Restore() before
        // Activate will surface it; Activate alone on a minimized window activates then immediately
        // re-minimizes.
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
        window.Show();
    }

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.AddDebug();

            // Warning, not Information. The dashboard has no log file of its own — the audit log is the
            // service's, and adding a second log here would create a record of the user's activity that
            // nothing in the product manages or rotates (FR-803).
            builder.SetMinimumLevel(LogLevel.Warning);
        });

        // Singleton: one pipe connection for the process. A client per page would open six connections to
        // a service that reconnects on demand anyway, and each would need its own policy.changed
        // subscription.
        services.AddSingleton<GuardianClient>();

        // Owns settings.json and the palette swap. Singleton because it holds the loaded UserSettings —
        // a second instance would write back a copy that never saw the first one's changes.
        services.AddSingleton<ThemeService>();

        // Global toast overlay. Singleton because there is one overlay for the whole window, and its
        // ObservableCollection must be the same instance the ToastHost binds to.
        services.AddSingleton<ToastService>();

        // Pages are singletons because the shell caches them across navigation — a user who filters the app
        // list, visits Settings and comes back expects their filter to still be there. They are cheap; the
        // state they hold is refreshed on every LoadAsync.
        services.AddSingleton<OnboardingViewModel>();
        services.AddSingleton<AppsViewModel>();
        services.AddSingleton<LockedAppsViewModel>();
        services.AddSingleton<HiddenAppsViewModel>();
        services.AddSingleton<PowerViewModel>();
        services.AddSingleton<ActivityViewModel>();
        services.AddSingleton<SettingsViewModel>();

        // The factory the shell's constructor takes. A delegate rather than an injected dictionary of every
        // page, so the shell does not have to name each view model type it might navigate to and a new page
        // is one arm of this switch.
        services.AddSingleton<Func<PageId, PageViewModel>>(provider => id => id switch
        {
            PageId.Onboarding => provider.GetRequiredService<OnboardingViewModel>(),
            PageId.Apps => provider.GetRequiredService<AppsViewModel>(),
            PageId.Locked => provider.GetRequiredService<LockedAppsViewModel>(),
            PageId.Hidden => provider.GetRequiredService<HiddenAppsViewModel>(),
            PageId.Power => provider.GetRequiredService<PowerViewModel>(),
            PageId.Activity => provider.GetRequiredService<ActivityViewModel>(),
            PageId.Settings => provider.GetRequiredService<SettingsViewModel>(),

            // Unreachable through the navigation list, which is built from this same enum. Thrown rather
            // than defaulted, because silently landing on the app picker would hide the mistake.
            _ => throw new ArgumentOutOfRangeException(nameof(id), id, "No page is registered for this id."),
        });

        services.AddSingleton<ShellViewModel>();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Last resort for an exception that escaped a command handler.
    /// </summary>
    /// <remarks>
    /// Handled rather than allowed to terminate. This process holds no state that a crash would corrupt —
    /// the policy file belongs to the service — but killing the window is how a user loses their only way to
    /// un-hide a window they have hidden. Staying up with a visible failure is strictly better than
    /// vanishing.
    /// </remarks>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var logger = _services?.GetService<ILogger<App>>();

        logger?.LogError(e.Exception, "An unhandled exception reached the dispatcher.");

        MessageBox.Show(
            "Something went wrong in AppGuardian's window. Your apps are still protected — protection runs "
            + "in a background service that is not affected by this.\n\n"
            + "If the window is not behaving, close and reopen it.\n\n"
            + e.Exception.Message,
            "AppGuardian",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

        e.Handled = true;
    }

    // --- P/Invoke for cross-process single-instance signaling ---

    private const IntPtr HWND_BROADCAST = (IntPtr)0xffff;

    /// <summary>
    /// Registers a window message so every process that calls the function with the same name
    /// receives the same message identifier.
    /// </summary>
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = false, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpProcName);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = false)]
    private static extern IntPtr SendMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = false)]
    private static extern uint GetCurrentProcessId();
}
