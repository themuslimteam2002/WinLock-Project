using System.Collections.ObjectModel;
using System.Runtime.Versioning;
using AppGuardian.Shared.Contracts;
using AppGuardian.Shared.Models;
using AppGuardian.UI.Mvvm;
using AppGuardian.UI.Services;
using Microsoft.Extensions.Logging;

namespace AppGuardian.UI.ViewModels;

/// <summary>
/// The app picker and the rule editor for the selected app. FR-800, FR-802, FR-509.
/// </summary>
/// <remarks>
/// One page rather than two. A separate "add app" wizard followed by a rules list would make the common
/// case — protect this one app — a four-screen journey, and the user would have to name the app twice.
/// The list is the picker; selecting a row opens its rule beside it.
/// <para>
/// Every toggle saves immediately rather than behind an OK button. A half-applied rule that exists only in
/// this window is a rule the user believes is protecting them.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class AppsViewModel : PageViewModel
{
    private readonly GuardianClient _client;
    private readonly ILogger<AppsViewModel> _log;

    private readonly List<AppRowViewModel> _all = new();

    private string _filter = string.Empty;
    private AppRowViewModel? _selected;
    private bool _showProtectedOnly;
    private bool _iconsOmitted;

    public AppsViewModel(GuardianClient client, ILogger<AppsViewModel> log)
    {
        _client = client;
        _log = log;

        RefreshCommand = new AsyncRelayCommand(
            () => LoadAsync(CancellationToken.None),
            onError: ex => _log.LogWarning(ex, "The app list could not be refreshed."));

        RemoveRuleCommand = new AsyncRelayCommand(
            RemoveRuleAsync,
            () => _selected?.RuleId is not null,
            ex => _log.LogWarning(ex, "The rule could not be removed."));
    }

    public override string Title => "Apps";

    public ObservableCollection<AppRowViewModel> Apps { get; } = new();

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand RemoveRuleCommand { get; }

    /// <summary>
    /// Free-text filter, applied locally.
    /// </summary>
    /// <remarks>
    /// Filtered in memory rather than by re-querying the service on each keystroke: the enumeration walks
    /// the registry, the Start Menu and the packaged-app catalogue, which is far too slow to run per
    /// character, and the full list is only a few hundred rows.
    /// </remarks>
    public string Filter
    {
        get => _filter;
        set
        {
            if (Set(ref _filter, value))
            {
                ApplyFilter();
            }
        }
    }

    public bool ShowProtectedOnly
    {
        get => _showProtectedOnly;
        set
        {
            if (Set(ref _showProtectedOnly, value))
            {
                ApplyFilter();
            }
        }
    }

    public AppRowViewModel? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                RemoveRuleCommand.RaiseCanExecuteChanged();

                // Icons are fetched one row at a time, on selection, rather than for the whole list.
                // Several hundred base64 PNGs in one response would blow the 256 KB envelope cap, and the
                // service strips them anyway (SC-15c).
                if (value is not null)
                {
                    _ = LoadIconAsync(value);
                }
            }
        }
    }

    public bool HasSelection => _selected is not null;

    /// <summary>True when the service had to strip icons to fit the envelope. Disclosed, not hidden.</summary>
    public bool IconsOmitted
    {
        get => _iconsOmitted;
        private set => Set(ref _iconsOmitted, value);
    }

    public override async Task LoadAsync(CancellationToken ct)
    {
        IsBusy = true;

        try
        {
            // Both are needed: the installed list is the catalogue, and the policy is what says which of
            // those rows already has a rule. Requested together rather than sequentially because the
            // installed enumeration is the slow one and there is no reason for the policy read to wait.
            var listTask = _client.ListInstalledAsync(null, ct);
            var policyTask = _client.GetPolicyAsync(ct);

            var list = await listTask.ConfigureAwait(true);
            var policy = await policyTask.ConfigureAwait(true);

            if (list is null)
            {
                ShowError(
                    "The list of installed apps could not be read. AppGuardian's background service may "
                    + "still be starting.");
                return;
            }

            var rules = policy?.Policy.Rules ?? new List<AppRule>();
            var byAppId = new Dictionary<string, AppRule>(StringComparer.Ordinal);

            foreach (var rule in rules)
            {
                byAppId[rule.Identity.AppId] = rule;
            }

            var previousSelection = _selected?.AppId;

            _all.Clear();

            foreach (var app in list.Apps.OrderBy(a => a.Identity.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            {
                byAppId.Remove(app.Identity.AppId, out var rule);
                _all.Add(new AppRowViewModel(app, rule, SaveAsync));
            }

            // Rules whose target was not discovered still appear. An app uninstalled, moved, or simply
            // missed by discovery would otherwise leave a rule the user cannot see, cannot edit, and
            // cannot delete — while the service goes on enforcing it.
            foreach (var orphan in byAppId.Values)
            {
                _all.Add(new AppRowViewModel(
                    new DiscoveredApp
                    {
                        Identity = orphan.Identity,
                        Source = DiscoverySource.ManuallyAdded,
                        UnsupportedReason =
                            "This app was not found on this PC. The rule is still in force if it comes back.",
                    },
                    orphan,
                    SaveAsync));
            }

            IconsOmitted = list.IconsOmitted;

            ApplyFilter();

            Selected = previousSelection is null
                ? null
                : Apps.FirstOrDefault(a => a.AppId == previousSelection);

            if (_all.Count == 0)
            {
                ShowInfo("No apps were found. You can still add one by browsing for its .exe.");
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

    /// <summary>Adds an app the picker did not find, by path. FR-403.</summary>
    public async Task<bool> AddByPathAsync(string executablePath, CancellationToken ct = default)
    {
        // Resolved by the service rather than constructed here: only the service can read the version
        // resource for a display name, and deriving the appId in two places would eventually produce two
        // different ids for one app.
        var identity = await _client.ResolveAsync(executablePath, ct).ConfigureAwait(true);

        if (identity is null)
        {
            ShowError("That file could not be identified as an application.");
            return false;
        }

        var existing = _all.FirstOrDefault(a => a.AppId == identity.AppId);

        if (existing is not null)
        {
            // Already present — selected instead of duplicated, which is what the user wanted anyway.
            Selected = Apps.FirstOrDefault(a => a.AppId == identity.AppId) ?? existing;
            ShowInfo($"{identity.DisplayName} is already in the list.");
            return true;
        }

        var row = new AppRowViewModel(
            new DiscoveredApp { Identity = identity, Source = DiscoverySource.ManuallyAdded },
            null,
            SaveAsync);

        _all.Add(row);
        ApplyFilter();
        Selected = row;
        ClearMessage();

        return true;
    }

    private void ApplyFilter()
    {
        IEnumerable<AppRowViewModel> query = _all;

        if (!string.IsNullOrWhiteSpace(_filter))
        {
            var needle = _filter.Trim();

            query = query.Where(a =>
                a.DisplayName.Contains(needle, StringComparison.CurrentCultureIgnoreCase)
                || (a.ExecutablePath?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        if (_showProtectedOnly)
        {
            query = query.Where(a => a.IsProtected);
        }

        CollectionSync.Replace(Apps, query.ToList());
    }

    /// <summary>
    /// Persists one facet of a rule.
    /// </summary>
    /// <remarks>
    /// One call per facet, carrying only the section that changed. Sending the whole rule would make each
    /// toggle overwrite whatever another page saved a moment earlier — a lost update the user would only
    /// notice when a battery limit they set silently disappeared.
    /// </remarks>
    private async Task SaveAsync(AppRowViewModel row, RuleFacet facet)
    {
        IsBusy = true;

        try
        {
            var result = facet switch
            {
                RuleFacet.Lock => await _client
                    .SetLockRuleAsync(row.Identity, row.LockSettingsSnapshot())
                    .ConfigureAwait(true),
                RuleFacet.Hide => await _client
                    .SetHideRuleAsync(row.Identity, row.HideSettingsSnapshot())
                    .ConfigureAwait(true),
                RuleFacet.Power => await _client
                    .SetPowerProfileAsync(row.Identity, row.PowerSettingsSnapshot())
                    .ConfigureAwait(true),
                _ => throw new ArgumentOutOfRangeException(nameof(facet)),
            };

            if (!result.Succeeded)
            {
                // Reverted, not left showing the change. A toggle that stays on after the save failed is
                // the worst possible outcome here: the user believes an app is locked when it is not.
                row.RevertFrom(row.SavedRule);
                ShowError(result.Message);
                return;
            }

            row.AdoptSaved(result.Value?.Rule);
            RemoveRuleCommand.RaiseCanExecuteChanged();

            ShowInfo(
                result.Value?.Created == true
                    ? $"{row.DisplayName} is now protected."
                    : "Saved.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RemoveRuleAsync()
    {
        if (_selected?.RuleId is not { } ruleId)
        {
            return;
        }

        var name = _selected.DisplayName;

        var result = await _client.DeleteRuleAsync(ruleId).ConfigureAwait(true);

        if (!result.Succeeded)
        {
            ShowError(result.Message);
            return;
        }

        _selected.AdoptSaved(null);
        RemoveRuleCommand.RaiseCanExecuteChanged();
        ApplyFilter();

        ShowInfo($"{name} is no longer protected.");
    }

    private async Task LoadIconAsync(AppRowViewModel row)
    {
        if (row.IconBase64 is not null)
        {
            return;
        }

        try
        {
            var icon = await _client.GetIconAsync(row.AppId).ConfigureAwait(true);

            if (icon?.IconBase64 is { } data)
            {
                row.IconBase64 = data;
            }
        }
        catch (Exception ex)
        {
            // A missing icon is cosmetic. Reporting it would put an error banner over a working page.
            _log.LogDebug(ex, "No icon could be fetched for {AppId}.", row.AppId);
        }
    }
}

/// <summary>Which section of a rule a save applies to.</summary>
public enum RuleFacet
{
    Lock,
    Hide,
    Power,
}
