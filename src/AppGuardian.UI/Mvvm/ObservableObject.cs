using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace AppGuardian.UI.Mvvm;

/// <summary>
/// Minimal <see cref="INotifyPropertyChanged"/> base.
/// </summary>
/// <remarks>
/// Hand-written rather than taken from CommunityToolkit.Mvvm. The project ships as source that a
/// reviewer is expected to read end to end (SRS §1.3), and the toolkit's source generators would put
/// half of every view model in files that are not in the repository.
/// </remarks>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Assigns and raises only when the value actually changed.</summary>
    /// <returns>True when a change was raised, so callers can chain dependent updates.</returns>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

/// <summary>
/// An <see cref="ICommand"/> over a synchronous action.
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// An <see cref="ICommand"/> over an async operation, with the button disabled while it runs.
/// </summary>
/// <remarks>
/// Every command in this dashboard is a pipe round trip, so re-entrancy is the default hazard rather
/// than an edge case: a double-clicked "Save" would send two writes and a double-clicked "Reveal" would
/// reveal and then re-hide. The running flag is what makes that impossible, and it is more reliable
/// than asking every view to remember to bind IsEnabled.
/// <para>
/// Exceptions are handed to <paramref name="onError"/> rather than allowed to escape. An unobserved
/// exception in a void async handler tears the process down, and losing the dashboard because a pipe
/// call failed would be a worse outcome than the failure itself.
/// </para>
/// </remarks>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private readonly Action<Exception>? _onError;
    private bool _running;

    public AsyncRelayCommand(
        Func<object?, Task> execute,
        Func<object?, bool>? canExecute = null,
        Action<Exception>? onError = null)
    {
        _execute = execute;
        _canExecute = canExecute;
        _onError = onError;
    }

    public AsyncRelayCommand(
        Func<Task> execute,
        Func<bool>? canExecute = null,
        Action<Exception>? onError = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute(), onError)
    {
    }

    public event EventHandler? CanExecuteChanged;

    public bool IsRunning => _running;

    public bool CanExecute(object? parameter) =>
        !_running && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _running = true;
        RaiseCanExecuteChanged();

        try
        {
            await _execute(parameter).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is a normal outcome here — the user navigated away, or a newer request
            // superseded this one. Nothing to report.
        }
        catch (Exception ex)
        {
            _onError?.Invoke(ex);
        }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Base for the dashboard's pages: a busy flag, a message line, and a load hook.
/// </summary>
/// <remarks>
/// Every page in this application does the same three things — read state from the service over the
/// pipe, show something while that is in flight, and explain itself when the read fails. Putting that
/// here keeps each page's own code about its own subject.
/// </remarks>
public abstract class PageViewModel : ObservableObject
{
    private bool _isBusy;
    private string? _message;
    private bool _messageIsError;

    /// <summary>Page title, shown in the shell header rather than duplicated in each view.</summary>
    public abstract string Title { get; }

    public bool IsBusy
    {
        get => _isBusy;
        protected set => Set(ref _isBusy, value);
    }

    /// <summary>Status or error text for this page. Null hides the banner entirely.</summary>
    public string? Message
    {
        get => _message;
        protected set => Set(ref _message, value);
    }

    /// <summary>Drives the banner's colour. Kept separate from the text so an empty string is not a state.</summary>
    public bool MessageIsError
    {
        get => _messageIsError;
        protected set => Set(ref _messageIsError, value);
    }

    /// <summary>Called by the shell on navigation and again whenever policy changes.</summary>
    public virtual Task LoadAsync(CancellationToken ct) => Task.CompletedTask;

    protected void ShowError(string? text)
    {
        MessageIsError = true;
        Message = text;
    }

    protected void ShowInfo(string? text)
    {
        MessageIsError = false;
        Message = text;
    }

    protected void ClearMessage() => Message = null;
}

/// <summary>
/// Marks a page that must not be left while it holds unsaved work.
/// </summary>
public interface IGuardNavigation
{
    /// <summary>Null to allow navigation; a user-facing reason to block it.</summary>
    string? BlockNavigationReason { get; }
}

/// <summary>Small helper for keeping an observable collection in sync without clearing it.</summary>
/// <remarks>
/// A Clear-then-Add refresh resets scroll position and selection on every poll, which on a page that
/// refreshes itself makes the list unusable. This replaces items in place instead.
/// </remarks>
public static class CollectionSync
{
    public static void Replace<T>(
        System.Collections.ObjectModel.ObservableCollection<T> target,
        IReadOnlyList<T> source)
    {
        for (var i = 0; i < source.Count; i++)
        {
            if (i < target.Count)
            {
                if (!EqualityComparer<T>.Default.Equals(target[i], source[i]))
                {
                    target[i] = source[i];
                }
            }
            else
            {
                target.Add(source[i]);
            }
        }

        while (target.Count > source.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }
}
