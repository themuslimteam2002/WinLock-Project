using System.Windows.Media.Animation;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AppGuardian.Shared.Contracts;
using AppGuardian.Shared.Models;

namespace AppGuardian.Agent.Overlay;

/// <summary>
/// One covering window on one monitor. FR-502, FR-503, FR-507.
/// </summary>
/// <remarks>
/// Built in code rather than XAML. The overlay is the one window whose geometry, topmost-ness and input
/// capture matter more than its styling, and a code-only window keeps the whole behaviour — including the
/// parts that are easy to break, like re-asserting topmost — in one readable file instead of split across
/// a markup file and a partial class.
/// <para>
/// Only the monitor holding the locked window carries the input controls; the others are plain covers
/// (<see cref="IsPrimaryPrompt"/> false). Repeating the PIN box on every monitor would give the user four
/// places to type and no indication which one is live.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class LockOverlayWindow : Window
{
    private readonly PasswordBox? _secretBox;
    private readonly TextBlock? _messageBlock;
    private readonly Button? _helloButton;
    private readonly Button? _pinButton;

    internal event EventHandler<string>? SecretSubmitted;

    internal event EventHandler? HelloRequested;

    internal event EventHandler? DismissRequested;

    internal bool IsPrimaryPrompt { get; }

    internal LockOverlayWindow(AppIdentity app, MonitorInfo monitor, bool isPrimaryPrompt, bool helloAvailable)
    {
        IsPrimaryPrompt = isPrimaryPrompt;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        AllowsTransparency = false;

        // Nearly opaque rather than fully. A hint of the app underneath tells the user which window they
        // are being asked to unlock, which a flat panel does not; full transparency would let them read
        // the content the lock exists to protect.
        Background = new SolidColorBrush(Color.FromRgb(0x12, 0x14, 0x18));
        Opacity = 0.97;

        // Manual placement in device-independent units. WindowStartupLocation and Screen bounds both go
        // wrong on mixed-DPI setups, leaving a strip of the real window visible at one edge.
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = monitor.X / monitor.DpiScale;
        Top = monitor.Y / monitor.DpiScale;
        Width = monitor.Width / monitor.DpiScale;
        Height = monitor.Height / monitor.DpiScale;

        if (!isPrimaryPrompt)
        {
            var content = BuildSecondaryContent(app);
            Content = content;

            // Secondary covers start fully transparent and fade in so they do not flash on the
            // secondary monitor while the user is already looking at the primary prompt.
            content.Opacity = 0;

            Loaded += (_, _) =>
            {
                Activate();
                BeginEntranceAnimation(content);
            };

            PreviewKeyDown += PreviewEscapeHandler;
            return;
        }

        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 420,
            // RenderTransform and Opacity are set for the entrance slide-up animation. The panel
            // starts faded and lowered; BeginEntranceAnimation fades it to 1 and raises it.
            RenderTransform = new TranslateTransform(0, 24),
            Opacity = 0,
        };

        panel.Children.Add(new TextBlock
        {
            Text = app.DisplayName,
            FontSize = 26,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8),
        });

        panel.Children.Add(new TextBlock
        {
            Text = "This app is locked by AppGuardian.",
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0xBE, 0xC8)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 28),
        });

        if (helloAvailable)
        {
            _helloButton = new Button
            {
                Content = "Unlock with Windows Hello",
                Padding = new Thickness(18, 10, 18, 10),
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 12),
                // Focused by default because it is the faster path and the one most users will want; the
                // PIN box is one Tab away for anyone who prefers it.
                IsDefault = true,
            };

            _helloButton.Click += (_, _) => HelloRequested?.Invoke(this, EventArgs.Empty);
            panel.Children.Add(_helloButton);
        }

        _secretBox = new PasswordBox
        {
            FontSize = 16,
            Padding = new Thickness(10, 8, 10, 8),
            MaxLength = 128,
            Margin = new Thickness(0, 0, 0, 10),
        };

        // Enter submits. Without this the only way in is the button, which is a poor fit for a control the
        // user will most often reach by typing.
        _secretBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Submit();
                e.Handled = true;
            }
        };

        panel.Children.Add(new TextBlock
        {
            Text = "Or enter your AppGuardian PIN",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x92, 0x9E)),
            Margin = new Thickness(0, 4, 0, 6),
        });

        panel.Children.Add(_secretBox);

        _pinButton = new Button
        {
            Content = "Unlock",
            Padding = new Thickness(18, 8, 18, 8),
            FontSize = 14,
        };

        _pinButton.Click += (_, _) => Submit();
        panel.Children.Add(_pinButton);

        _messageBlock = new TextBlock
        {
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x9A, 0x8A)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 14, 0, 0),
            Visibility = Visibility.Collapsed,
        };

        panel.Children.Add(_messageBlock);

        panel.Children.Add(new TextBlock
        {
            // Stated, not hidden. A lock the user cannot back out of is a lock that traps them when
            // authentication is broken, and pretending Escape does nothing would not stop anyone who
            // simply pressed it.
            Text = "Press Esc to leave this app locked.",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x72, 0x7E)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 24, 0, 0),
        });

        Content = panel;

        Loaded += (_, _) =>
        {
            Activate();
            BeginEntranceAnimation(panel);

            // Focus is set after Loaded, not in the constructor: a control that is not yet in the visual
            // tree silently refuses focus, and the user would find themselves typing into nothing.
            if (helloAvailable)
            {
                _helloButton?.Focus();
            }
            else
            {
                _secretBox?.Focus();
            }
        };

        PreviewKeyDown += PreviewEscapeHandler;
    }

    private void PreviewEscapeHandler(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DismissRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Plays the slide-and-fade entrance animation on the overlay content.
    /// </summary>
    /// <remarks>
    /// The storyboard is created in code rather than referenced from Animations.xaml because this window
    /// lives in the Agent project, which does not reference the UI project's resource dictionaries. The
    /// animation mirrors <c>LockOverlayEnter</c> from Animations.xaml: a 250 ms fade from 0 to 1 opacity
    /// and a slide-up from a 24 px translate, both with a cubic ease-out.
    /// </remarks>
    private void BeginEntranceAnimation(UIElement content)
    {
        var opacityAnimation = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(250),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        var slideAnimation = new DoubleAnimation
        {
            From = 24,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(250),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        content.BeginAnimation(OpacityProperty, opacityAnimation);

        // Only animate the slide if the content has a RenderTransform we can target.
        // The StackPanel panel gets a TranslateTransform; the secondary TextBlock does not.
        if (content.RenderTransform is TranslateTransform transform)
        {
            transform.BeginAnimation(TranslateTransform.YProperty, slideAnimation);
        }
    }

    private static UIElement BuildSecondaryContent(AppIdentity app) => new TextBlock
    {
        Text = $"{app.DisplayName} is locked. Unlock it on your main display.",
        FontSize = 15,
        Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x92, 0x9E)),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 400,
    };

    private void Submit()
    {
        var secret = _secretBox?.Password ?? string.Empty;

        if (secret.Length == 0)
        {
            ShowMessage("Enter your PIN.");
            return;
        }

        SecretSubmitted?.Invoke(this, secret);
    }

    /// <summary>Shows an error and clears the entry box.</summary>
    internal void ShowMessage(string message)
    {
        if (_messageBlock is null)
        {
            return;
        }

        _messageBlock.Text = message;
        _messageBlock.Visibility = Visibility.Visible;

        // Cleared on every failure so a wrong entry is not partially reused, and because leaving a
        // rejected secret on screen invites the user to edit one character and try again, burning
        // attempts against the FR-306 lockout budget.
        _secretBox?.Clear();
        _secretBox?.Focus();
    }

    /// <summary>Disables input while a verification is in flight.</summary>
    internal void SetBusy(bool busy)
    {
        if (_secretBox is not null)
        {
            _secretBox.IsEnabled = !busy;
        }

        if (_pinButton is not null)
        {
            _pinButton.IsEnabled = !busy;
        }

        if (_helloButton is not null)
        {
            _helloButton.IsEnabled = !busy;
        }
    }

    /// <summary>
    /// Re-asserts topmost.
    /// </summary>
    /// <remarks>
    /// Windows does not guarantee the overlay stays on top: another topmost window, a UAC prompt, or the
    /// locked app itself raising its own window can end up above it. Called from the coordinator whenever
    /// the foreground changes while the overlay is up, because an overlay that is not on top is not a
    /// lock at all.
    /// </remarks>
    internal void ReassertTopmost()
    {
        Topmost = false;
        Topmost = true;
        Activate();
    }
}
