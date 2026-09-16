using System.Collections.ObjectModel;
using System.Runtime.Versioning;
using AppGuardian.Shared.Contracts;
using AppGuardian.UI.Mvvm;
using AppGuardian.UI.Services;
using Microsoft.Extensions.Logging;

namespace AppGuardian.UI.ViewModels;

/// <summary>
/// Locked apps and any active temporary unlock. FR-506, FR-806.
/// </summary>
/// <remarks>
/// The only page that polls on its own, at one-second resolution, because it is the only page showing a
/// value that changes every second — the countdown on an active unlock. Elsewhere the shell's five-second
/// poll is enough.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class LockedAppsViewModel : PageViewModel, IDisposable
{
    private readonly GuardianClient _client;
    private readonly ILogger<LockedAppsViewModel> _log;
    private readonly System.Windows.Threading.DispatcherTimer _tick;

    private bool _disposed;

    public LockedAppsViewModel(GuardianClient client, ILogger<LockedAppsViewModel> log)
    {
        _client = client;
        _log = log;

        LockNowCommand = new AsyncRelayCommand(
            parameter => LockNowAsync(parameter as LockRowViewModel),
            parameter => parameter is LockRowViewModel { HasActiveUnlock: true },
            ex => _log.LogWarning(ex, "The app could not be re-locked."));

        RefreshCommand = new AsyncRelayCommand(
            () => LoadAsync(CancellationToken.None),
            onError: ex => _log.LogWarning(ex, "The lock state could not be refreshed."));

        // Only the countdown text is recomputed on this tick — no pipe traffic. Re-reading state every
        // second would put sixty round trips a minute on the pipe to learn something the client can work
        // out from the expiry it already has.
        _tick = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };

        _tick.Tick += (_, _) =>
        {
            var expired = false;

            foreach (var row in Rows)
            {
                row.RefreshCountdown();
                expired |= row.JustExpired;
            }

            // A read is only issued when a countdown reaches zero, which is the one moment the service's
            // view and this page's view are guaranteed to have diverged.
            if (expired && !_disposed)
            {
                _ = LoadAsync(CancellationToken.None);
            }
        };
    }

    public override string Title => "Locked apps";

    public ObservableCollection<LockRowViewModel> Rows { get; } = new();

    public AsyncRelayCommand LockNowCommand { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public bool IsEmpty => Rows.Count == 0;

    public override async Task LoadAsync(CancellationToken ct)
    {
        IsBusy = true;

        try
        {
            var state = await _client.GetLockStateAsync(ct).ConfigureAwait(true);

            if (state is null)
            {
                ShowError(
                    "The lock state could not be read. Your rules are unchanged, but AppGuardian cannot "
                    + "confirm what is locked right now.");
                return;
            }

            CollectionSync.Replace(
                Rows,
                state.Entries
                    .OrderByDescending(e => e.UnlockExpiresUtc is not null)
                    .ThenBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .Select(e => new LockRowViewModel(e))
                    .ToList());

            OnPropertyChanged(nameof(IsEmpty));

            ClearMessage();

            if (Rows.Count > 0)
            {
                _tick.Start();
            }
            else
            {
                _tick.Stop();
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LockNowAsync(LockRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        var result = await _client.EndSessionAsync(row.AppId).ConfigureAwait(true);

        if (!result.Succeeded)
        {
            ShowError(result.Message);
            return;
        }

        ShowInfo($"{row.DisplayName} is locked again.");

        await LoadAsync(CancellationToken.None).ConfigureAwait(true);
    }

    public void Dispose()
    {
        _disposed = true;
        _tick.Stop();
    }
}

/// <summary>One app's lock state, with a live countdown. FR-506.</summary>
public sealed class LockRowViewModel : ObservableObject
{
    private readonly LockStateEntry _entry;
    private string _countdown = string.Empty;
    private bool _wasActive;

    public LockRowViewModel(LockStateEntry entry)
    {
        _entry = entry;
        _wasActive = HasActiveUnlock;
        RefreshCountdown();
    }

    public string AppId => _entry.AppId;

    public string DisplayName => _entry.DisplayName;

    public bool IsRunning => _entry.IsRunning;

    public bool IsLocked => _entry.IsLocked;

    public bool HasActiveUnlock =>
        _entry.UnlockExpiresUtc is { } expiry && expiry > DateTimeOffset.UtcNow;

    /// <summary>Set for one tick when an unlock window closes, so the page knows to re-read once.</summary>
    public bool JustExpired { get; private set; }

    public string Countdown
    {
        get => _countdown;
        private set => Set(ref _countdown, value);
    }

    /// <summary>
    /// State in the user's words rather than the model's.
    /// </summary>
    /// <remarks>
    /// "Unlocked for 4:12" is the only line on this page a user actually acts on — it is what tells them
    /// how long they have before the app asks again — so it leads with the number.
    /// </remarks>
    public string StateText
    {
        get
        {
            if (HasActiveUnlock)
            {
                return $"Unlocked for {Countdown}";
            }

            if (!_entry.IsLocked)
            {
                return "Not locked";
            }

            return _entry.IsRunning ? "Locked, running" : "Locked";
        }
    }

    public void RefreshCountdown()
    {
        var active = HasActiveUnlock;

        JustExpired = _wasActive && !active;
        _wasActive = active;

        if (!active)
        {
            Countdown = string.Empty;
            OnPropertyChanged(nameof(HasActiveUnlock));
            OnPropertyChanged(nameof(StateText));
            return;
        }

        var remaining = _entry.UnlockExpiresUtc!.Value - DateTimeOffset.UtcNow;

        // Rounded up, not down. A user watching "0:00" for a whole second before the lock returns would
        // reasonably conclude the countdown had stalled.
        var seconds = (int)Math.Ceiling(remaining.TotalSeconds);

        Countdown = $"{seconds / 60}:{seconds % 60:00}";

        OnPropertyChanged(nameof(StateText));
    }
}
