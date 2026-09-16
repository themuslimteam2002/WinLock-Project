using System.Collections.ObjectModel;
using System.Runtime.Versioning;
using AppGuardian.Shared.Models;
using AppGuardian.UI.Mvvm;
using AppGuardian.UI.Services;
using Microsoft.Extensions.Logging;

namespace AppGuardian.UI.ViewModels;

/// <summary>
/// The local audit log. FR-803, FR-508.
/// </summary>
/// <remarks>
/// Read-only by design. The log is the record of what AppGuardian did on this PC, and a dashboard that
/// could delete entries would make it worthless for the one job it has — telling the owner whether someone
/// tried to get into a locked app while they were away.
/// <para>
/// The service holds the file; nothing here reads %ProgramData% directly. That is what keeps the log's ACL
/// meaningful (ADR-005).
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ActivityViewModel : PageViewModel
{
    /// <summary>
    /// How many entries to request.
    /// </summary>
    /// <remarks>
    /// Two hundred, matching the contract default. It is a few days of ordinary use, it fits inside the
    /// 256 KB envelope with room to spare, and a user who needs more than that is looking for a specific
    /// incident and is better served by the filter than by a longer list.
    /// </remarks>
    private const int PageSize = 200;

    private readonly GuardianClient _client;
    private readonly ILogger<ActivityViewModel> _log;

    private AuditAction? _filter;
    private bool _truncated;
    private bool _failuresOnly;

    public ActivityViewModel(GuardianClient client, ILogger<ActivityViewModel> log)
    {
        _client = client;
        _log = log;

        RefreshCommand = new AsyncRelayCommand(
            () => LoadAsync(CancellationToken.None),
            onError: ex => _log.LogWarning(ex, "The activity log could not be refreshed."));
    }

    public override string Title => "Activity";

    public ObservableCollection<AuditRowViewModel> Entries { get; } = new();

    public AsyncRelayCommand RefreshCommand { get; }

    public bool IsEmpty => Entries.Count == 0;

    /// <summary>Available action filters, with the "everything" option first.</summary>
    public IReadOnlyList<AuditFilterOption> FilterOptions { get; } = new List<AuditFilterOption>
    {
        new(null, "All activity"),
        new(AuditAction.Unlock, "Unlocks"),
        new(AuditAction.AuthFail, "Failed attempts"),
        new(AuditAction.Lock, "Locks"),
        new(AuditAction.Hide, "Hides"),
        new(AuditAction.Reveal, "Reveals"),
        new(AuditAction.ConfigChange, "Setting changes"),
        new(AuditAction.Unsupported, "Apps that could not be controlled"),
    };

    public AuditAction? Filter
    {
        get => _filter;
        set
        {
            if (Set(ref _filter, value))
            {
                _ = LoadAsync(CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// Narrows to denied and failed outcomes, locally.
    /// </summary>
    /// <remarks>
    /// Applied client-side because the contract's filter takes an action, not a result, and "show me what
    /// went wrong" spans several actions. Filtering the fetched page is enough — the alternative is a
    /// contract change for a convenience toggle.
    /// </remarks>
    public bool FailuresOnly
    {
        get => _failuresOnly;
        set
        {
            if (Set(ref _failuresOnly, value))
            {
                _ = LoadAsync(CancellationToken.None);
            }
        }
    }

    /// <summary>True when older entries exist beyond what was fetched.</summary>
    public bool Truncated
    {
        get => _truncated;
        private set => Set(ref _truncated, value);
    }

    public string TruncationNote =>
        $"Showing the most recent {PageSize} entries. Older activity is still in the log file on this PC.";

    public override async Task LoadAsync(CancellationToken ct)
    {
        IsBusy = true;

        try
        {
            var result = await _client.GetAuditLogAsync(PageSize, _filter, ct).ConfigureAwait(true);

            if (result is null)
            {
                ShowError(
                    "The activity log could not be read. It is stored on this PC and nothing has been "
                    + "lost — AppGuardian's background service may still be starting.");
                return;
            }

            IEnumerable<AuditEntry> entries = result.Entries;

            if (_failuresOnly)
            {
                entries = entries.Where(e => e.Result != AuditResult.Ok);
            }

            CollectionSync.Replace(
                Entries,

                // Newest first. The service appends, so the file order is oldest-first, and a log that
                // opens on last week's events makes the user scroll to find today's.
                entries
                    .OrderByDescending(e => e.Utc)
                    .Select(e => new AuditRowViewModel(e))
                    .ToList());

            Truncated = result.Truncated;

            OnPropertyChanged(nameof(IsEmpty));

            if (Entries.Count == 0)
            {
                ShowInfo(
                    _filter is null && !_failuresOnly
                        ? "Nothing has happened yet. Activity appears here as apps are locked, unlocked "
                          + "and hidden."
                        : "Nothing matches that filter.");
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
}

/// <summary>One filter choice.</summary>
public sealed record AuditFilterOption(AuditAction? Action, string Label);

/// <summary>One audit entry, phrased for a person rather than for a log parser. FR-803.</summary>
public sealed class AuditRowViewModel : ObservableObject
{
    private readonly AuditEntry _entry;

    public AuditRowViewModel(AuditEntry entry) => _entry = entry;

    /// <summary>Local time, because a user comparing this against their own memory thinks in local time.</summary>
    public DateTimeOffset When => _entry.Utc.ToLocalTime();

    public string WhenText => When.ToString("ddd d MMM, HH:mm:ss");

    public string ActionText => _entry.Action switch
    {
        AuditAction.Lock => "Locked",
        AuditAction.Unlock => "Unlocked",
        AuditAction.Hide => "Hidden",
        AuditAction.Reveal => "Brought back",
        AuditAction.ApplyPower => "Limit applied",
        AuditAction.RemovePower => "Limit removed",
        AuditAction.AuthFail => "Failed unlock attempt",
        AuditAction.ConfigChange => "Setting changed",
        AuditAction.Unsupported => "Could not be controlled",
        _ => _entry.Action.ToString(),
    };

    public string ResultText => _entry.Result switch
    {
        AuditResult.Ok => "Done",
        AuditResult.Denied => "Refused",
        AuditResult.Error => "Failed",
        _ => _entry.Result.ToString(),
    };

    public bool IsProblem => _entry.Result != AuditResult.Ok;

    /// <summary>Who acted. Distinguishing the components is what makes an unexpected lock diagnosable.</summary>
    public string ActorText => _entry.Actor switch
    {
        AuditActor.User => "You",
        AuditActor.Service => "AppGuardian",
        AuditActor.Agent => "AppGuardian on your desktop",
        _ => _entry.Actor.ToString(),
    };

    public string? AppId => _entry.AppId;

    public string Detail => _entry.Detail;

    /// <summary>
    /// True when this entry was written from a client's buffer after a service outage.
    /// </summary>
    /// <remarks>
    /// Surfaced rather than hidden. Its timestamp came from a process that could not reach the service, so
    /// it is less trustworthy than the rest of the log, and anyone reading the log as evidence of when
    /// something happened needs to see that distinction.
    /// </remarks>
    public bool WasBuffered => _entry.BufferedUtc is not null;

    public string? BufferedNote => WasBuffered
        ? "Recorded while the service was unavailable; the time may be approximate."
        : null;
}
