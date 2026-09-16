using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;

namespace AppGuardian.UI.Services;

/// <summary>
/// Global, non-intrusive notification overlay. Complements the per-page <c>ShowInfo</c>/<c>ShowError</c>
/// banners on <see cref="Mvvm.PageViewModel"/> for actions whose feedback belongs outside the current page.
/// </summary>
/// <remarks>
/// A lock-now from the status strip, a rule saved on the Apps page that the Locked page should acknowledge,
/// or a service disconnect that the user needs to see regardless of which page they are on — these are the
/// cases a page-scoped banner cannot serve. The toast is the right answer because it occupies no layout
/// space, auto-dismisses, and does not steal focus.
/// <para>
/// The queue is capped at <see cref="MaxVisible"/>. More than three stacked messages are not read; they are
/// scrolled past. When a fourth arrives the oldest is evicted immediately rather than queued behind the
/// current three, so the newest — the one the user just caused — is always visible.
/// </para>
/// </remarks>
public sealed class ToastService
{
    /// <summary>Maximum toasts shown at once. Beyond this the oldest is evicted.</summary>
    public const int MaxVisible = 3;

    /// <summary>How long a toast stays on screen before auto-dismissing.</summary>
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(4);

    /// <summary>Bound by the <c>ToastHost</c> overlay.</summary>
    public ObservableCollection<ToastItem> Items { get; } = new();

    public void ShowSuccess(string message) => Show(message, ToastLevel.Success);

    public void ShowWarning(string message) => Show(message, ToastLevel.Warning);

    public void ShowError(string message) => Show(message, ToastLevel.Error);

    public void ShowInfo(string message) => Show(message, ToastLevel.Info);

    private void Show(string message, ToastLevel level)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        // Marshal to the UI thread. This can be called from a pipe callback or a ViewModel on any thread.
        if (Application.Current?.Dispatcher is not { } dispatcher)
        {
            return;
        }

        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => Show(message, level));
            return;
        }

        // Evict oldest when full. The newest message is the one the user just caused and should always
        // be visible; the one from four seconds ago has already been read or will not be.
        while (Items.Count >= MaxVisible)
        {
            Items.RemoveAt(0);
        }

        var item = new ToastItem(message, level, DefaultDuration, Remove);
        Items.Add(item);
    }

    private void Remove(ToastItem item)
    {
        if (Application.Current?.Dispatcher is not { } dispatcher)
        {
            return;
        }

        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => Remove(item));
            return;
        }

        Items.Remove(item);
    }
}

/// <summary>The severity of a toast, which drives colour.</summary>
public enum ToastLevel
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>One toast in the queue. Owns its own dismiss timer.</summary>
public sealed class ToastItem : IDisposable
{
    private readonly Action<ToastItem> _remove;
    private readonly DispatcherTimer _timer;
    private bool _disposed;

    public ToastItem(string message, ToastLevel level, TimeSpan duration, Action<ToastItem> remove)
    {
        Message = message;
        Level = level;
        _remove = remove;

        _timer = new DispatcherTimer { Interval = duration };
        _timer.Tick += (_, _) => Dismiss();
        _timer.Start();
    }

    public string Message { get; }

    public ToastLevel Level { get; }

    /// <summary>User-triggered dismiss, or the timer expired.</summary>
    public void Dismiss()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _remove(this);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _timer.Stop();
        }
    }
}
