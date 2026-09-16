using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AppGuardian.UI.Services;

namespace AppGuardian.UI.Controls;

/// <summary>
/// Code-behind for the toast overlay. Handles dismiss clicks and toast-level click-to-dismiss.
/// </summary>
/// <remarks>
/// No business logic here — just input routing. The <see cref="ToastService"/> owns the queue and
/// the timers; this control is a view of that queue.
/// </remarks>
public partial class ToastHost : UserControl
{
    public ToastHost()
    {
        InitializeComponent();
    }

    /// <summary>Clicking the dismiss ✕ button.</summary>
    private void OnDismissClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ToastItem item })
        {
            item.Dismiss();
        }
    }

    /// <summary>Clicking anywhere on the toast body also dismisses it.</summary>
    private void OnToastClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ToastItem item })
        {
            item.Dismiss();
        }
    }
}
