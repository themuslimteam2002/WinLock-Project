using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Media.Animation;
using AppGuardian.Shared.Models;
using AppGuardian.Shared.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace AppGuardian.UI.Services;

/// <summary>
/// Applies and persists the dashboard's light/dark palette. FR-802, <see cref="UserSettings.Theme"/>.
/// </summary>
/// <remarks>
/// Colour lives in two interchangeable dictionaries, <c>Views/Palette.Dark.xaml</c> and
/// <c>Views/Palette.Light.xaml</c>, and this class swaps whichever one sits at
/// <see cref="PaletteSlot"/> in the application's merged dictionaries. Every palette key is referenced
/// from <c>Theme.xaml</c> and the views through <c>DynamicResource</c>, which is what makes the swap
/// take effect on windows that are already open — a <c>StaticResource</c> is resolved once at load and
/// would hold the old brush until the process restarted.
/// <para>
/// Replacing one dictionary rather than clearing and rebuilding the whole collection is deliberate:
/// <c>Theme.xaml</c>'s styles are keyed by <c>StaticResource</c> from every view, and tearing them out
/// even momentarily would throw on any visual that re-resolved in between.
/// </para>
/// <para>
/// The preference is stored in the per-user <c>settings.json</c>, not in policy. It is a UI choice with
/// no security meaning, so it does not belong in the machine-wide document the service defends, and
/// keeping it out means the dashboard can save it without an IPC round trip or an unlock session.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ThemeService
{
    /// <summary>
    /// Index of the palette in <c>Application.Resources.MergedDictionaries</c>.
    /// </summary>
    /// <remarks>
    /// Zero, and <c>App.xaml</c> lists the palette first for that reason. An index is fragile — adding a
    /// dictionary above it in <c>App.xaml</c> would silently make this replace the wrong one — so
    /// <see cref="Apply"/> checks that the dictionary it is about to replace really is a palette before
    /// touching it, and falls back to inserting rather than overwriting something else.
    /// </remarks>
    private const int PaletteSlot = 0;

    private const string DarkPalettePath = "Views/Palette.Dark.xaml";
    private const string LightPalettePath = "Views/Palette.Light.xaml";

    /// <summary>A key present in every palette and in no other dictionary. Used to identify the slot.</summary>
    private const string PaletteMarkerKey = "Surface.Page";

    private readonly AtomicJsonStore<UserSettings> _store;
    private readonly ILogger<ThemeService> _log;

    private UserSettings _settings = new();

    public ThemeService(ILogger<ThemeService> log)
        : this(new AtomicJsonStore<UserSettings>(StoragePaths.SettingsFile), log)
    {
    }

    /// <summary>Test seam: lets a test point the store at a temporary file.</summary>
    public ThemeService(AtomicJsonStore<UserSettings> store, ILogger<ThemeService> log)
    {
        _store = store;
        _log = log;
    }

    /// <summary>The user's stored choice, which may be <see cref="AppTheme.System"/>.</summary>
    public AppTheme Preference => _settings.Theme;

    /// <summary>Raised after a successful <see cref="ApplyAsync"/>, so a view model can refresh its labels.</summary>
    public event EventHandler? ThemeChanged;

    /// <summary>
    /// Reads the stored preference and applies it. Called once at startup, before the window is shown.
    /// </summary>
    /// <remarks>
    /// A failure here is not fatal and is not surfaced to the user. The palette merged by
    /// <c>App.xaml</c> is already a valid theme, so the worst outcome of an unreadable settings file is
    /// that the dashboard opens in dark and the user sets it again.
    /// </remarks>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            var load = await _store.LoadAsync(ct).ConfigureAwait(true);

            _settings = load.Value;

            if (load.IsFailure)
            {
                _log.LogWarning("Settings could not be read ({Error}); using defaults.", load.Error);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Settings could not be read; using defaults.");
        }

        Apply(Resolve(_settings.Theme));
    }

    /// <summary>Applies a preference and writes it back to <c>settings.json</c>.</summary>
    public async Task ApplyAsync(AppTheme preference, CancellationToken ct = default)
    {
        Apply(Resolve(preference));

        _settings.Theme = preference;

        try
        {
            await _store.SaveAsync(_settings, ct).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Applied but not saved. Reverting the visual change would be worse: the user asked for this
            // and can see that it happened, so the honest failure is that it will not survive a restart.
            _log.LogWarning(ex, "The theme was applied but could not be saved.");
        }

        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Turns a preference into the palette actually shown, reading Windows' own setting for
    /// <see cref="AppTheme.System"/>.
    /// </summary>
    public AppTheme Resolve(AppTheme preference) => preference switch
    {
        AppTheme.Light => AppTheme.Light,
        AppTheme.Dark => AppTheme.Dark,
        _ => ReadSystemTheme(),
    };

    /// <summary>
    /// Windows' own app-mode setting.
    /// </summary>
    /// <remarks>
    /// Read from the registry rather than from a WinRT UISettings call, because the registry value is
    /// available on Windows 10 22H2 and needs no dependency, and this is one read at startup rather than
    /// something on a hot path. <c>AppsUseLightTheme</c> is 0 for dark and 1 for light; a missing value
    /// means an older build or a policy-managed machine, and dark is the documented default.
    /// </remarks>
    private AppTheme ReadSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

            return key?.GetValue("AppsUseLightTheme") is int value && value != 0
                ? AppTheme.Light
                : AppTheme.Dark;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "The system theme could not be read; defaulting to dark.");
            return AppTheme.Dark;
        }
    }

    /// <summary>Swaps the palette dictionary in place, with a brief opacity cross-fade.</summary>
    private void Apply(AppTheme resolved)
    {
        if (Application.Current is not { } app)
        {
            // No Application in a unit test host. Resolve and persistence are still testable.
            return;
        }

        var path = resolved == AppTheme.Light ? LightPalettePath : DarkPalettePath;

        var palette = new ResourceDictionary
        {
            // A relative pack URI, so it resolves the same whether the assembly is loaded from the
            // install folder or from a test host's directory.
            Source = new Uri(path, UriKind.Relative),
        };

        var merged = app.Resources.MergedDictionaries;

        // Cross-fade: dim the window briefly to mask the instant at which every brush changes. Only
        // when the window is already visible — the initial apply at startup should not animate.
        var window = app.MainWindow;
        var shouldAnimate = window is { IsVisible: true };

        if (shouldAnimate)
        {
            BeginCrossFade(window!);
        }

        if (merged.Count > PaletteSlot && merged[PaletteSlot].Contains(PaletteMarkerKey))
        {
            merged[PaletteSlot] = palette;
        }
        else
        {
            // App.xaml has been changed and the slot no longer holds a palette. Inserting is safe —
            // a duplicate palette below the structural dictionaries still resolves — and is better than
            // replacing Theme.xaml and losing every style in the app.
            _log.LogWarning(
                "Merged dictionary {Slot} is not a palette; inserting instead of replacing.", PaletteSlot);

            merged.Insert(Math.Min(PaletteSlot, merged.Count), palette);
        }
    }

    /// <summary>
    /// A quick dim-and-recover (1.0 → 0.88 → 1.0 over 250 ms). Fire-and-forget; the window stays
    /// fully interactive throughout.
    /// </summary>
    private static void BeginCrossFade(Window window)
    {
        var animation = new DoubleAnimation
        {
            From = 1.0,
            To = 0.88,
            Duration = new Duration(TimeSpan.FromMilliseconds(125)),
            AutoReverse = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };

        window.BeginAnimation(UIElement.OpacityProperty, animation);
    }
}
