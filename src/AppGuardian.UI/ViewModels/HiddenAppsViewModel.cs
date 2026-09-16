using System.Collections.ObjectModel;
using System.Runtime.Versioning;
using AppGuardian.Shared.Models;
using AppGuardian.UI.Mvvm;
using AppGuardian.UI.Services;
using Microsoft.Extensions.Logging;

namespace AppGuardian.UI.ViewModels;

/// <summary>
/// Apps on the hidden desktop, and the only way back from it. FR-604, FR-606, FR-607, FR-806.
/// </summary>
/// <remarks>
/// This page is a safety mechanism as much as a feature. Hiding moves a window somewhere the user cannot
/// reach through the taskbar or Alt+Tab, so if this list were wrong or this page were missing, the window
/// would be gone for good. Everything here is built around making that impossible: the list comes from the
/// service rather than from anything the dashboard remembers, and "Bring back" is always available even
/// when the rule stays in force.
/// <para>
/// The fallback disclosure at the top is not optional (FR-607, ADR-007). On a Windows build where the
/// virtual desktop interfaces are unavailable, hiding degrades to minimising, which any user can undo from
/// the taskbar — a materially weaker promise than the one the feature name makes.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class HiddenAppsViewModel : PageViewModel
{
    private readonly GuardianClient _client;
    private readonly ILogger<HiddenAppsViewModel> _log;

    private bool _usesRealDesktops = true;
    private string? _workspaceName;

    public HiddenAppsViewModel(GuardianClient client, ILogger<HiddenAppsViewModel> log)
    {
        _client = client;
        _log = log;

        RevealCommand = new AsyncRelayCommand(
            parameter => RevealAsync(parameter as HiddenRowViewModel, permanent: false),
            parameter => parameter is HiddenRowViewModel,
            ex => _log.LogWarning(ex, "The app could not be revealed."));

        StopHidingCommand = new AsyncRelayCommand(
            parameter => RevealAsync(parameter as HiddenRowViewModel, permanent: true),
            parameter => parameter is HiddenRowViewModel,
            ex => _log.LogWarning(ex, "Hiding could not be turned off."));

        RevealAllCommand = new AsyncRelayCommand(
            RevealAllAsync,
            () => Rows.Count > 0,
            ex => _log.LogWarning(ex, "Not every app could be revealed."));

        RefreshCommand = new AsyncRelayCommand(
            () => LoadAsync(CancellationToken.None),
            onError: ex => _log.LogWarning(ex, "The hidden app list could not be refreshed."));
    }

    public override string Title => "Hidden apps";

    public ObservableCollection<HiddenRowViewModel> Rows { get; } = new();

    public AsyncRelayCommand RevealCommand { get; }

    public AsyncRelayCommand StopHidingCommand { get; }

    public AsyncRelayCommand RevealAllCommand { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public bool IsEmpty => Rows.Count == 0;

    /// <summary>False when hiding is running on the minimise fallback. ADR-007, FR-607.</summary>
    public bool UsesRealDesktops
    {
        get => _usesRealDesktops;
        private set
        {
            if (Set(ref _usesRealDesktops, value))
            {
                OnPropertyChanged(nameof(StrengthDisclosure));
            }
        }
    }

    public string? WorkspaceName
    {
        get => _workspaceName;
        private set => Set(ref _workspaceName, value);
    }

    /// <summary>
    /// What hiding actually guarantees on this machine, in plain words.
    /// </summary>
    /// <remarks>
    /// Both branches state the limit rather than only the capability. Neither mode hides an app from Task
    /// Manager or from someone who knows the file is there, and a user relying on this for real privacy
    /// needs to know that before they rely on it, not after.
    /// </remarks>
    public string StrengthDisclosure => UsesRealDesktops
        ? $"Hidden apps are moved to a separate desktop called {WorkspaceName ?? PolicyConstants.HiddenWorkspaceName}. "
          + "They will not appear in the taskbar or in Alt+Tab, but they are still visible in Task Manager "
          + "and their files are still on this PC."
        : "This PC does not support the separate hidden desktop, so AppGuardian minimises hidden windows "
          + "and keeps them off the taskbar instead. Anyone using this PC could restore them, so treat "
          + "this as tidying rather than privacy.";

    public override async Task LoadAsync(CancellationToken ct)
    {
        IsBusy = true;

        try
        {
            // Status carries the fallback flag; policy carries the rules. Both are needed because a rule
            // can be set while nothing is hidden right now, and the page has to show those separately.
            var statusTask = _client.GetStatusAsync(ct);
            var policyTask = _client.GetPolicyAsync(ct);

            var status = await statusTask.ConfigureAwait(true);
            var policy = await policyTask.ConfigureAwait(true);

            if (policy is null)
            {
                ShowError(
                    "The list of hidden apps could not be read. Nothing has been lost — try again in a "
                    + "moment, or use Bring back everything once the service responds.");
                return;
            }

            if (status is not null)
            {
                UsesRealDesktops = status.VirtualDesktopSupported;
            }

            WorkspaceName = PolicyConstants.HiddenWorkspaceName;

            var rows = policy.Policy.Rules
                .Where(r => r.Hide.Enabled)
                .OrderBy(r => r.Identity.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .Select(r => new HiddenRowViewModel(r))
                .ToList();

            CollectionSync.Replace(Rows, rows);

            OnPropertyChanged(nameof(IsEmpty));
            RevealAllCommand.RaiseCanExecuteChanged();

            if (rows.Count == 0)
            {
                ShowInfo("Nothing is set to be hidden. Turn hiding on for an app from the Apps page.");
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

    /// <summary>
    /// Brings one app back.
    /// </summary>
    /// <remarks>
    /// Two variants of the same operation, because FR-606 asks for both and they mean different things: a
    /// temporary reveal leaves the rule in force so the app is hidden again next launch, while stopping
    /// hiding is a policy change. Offering only the first would leave a user unable to undo the feature;
    /// offering only the second would make a quick look at a hidden app a settings change.
    /// </remarks>
    private async Task RevealAsync(HiddenRowViewModel? row, bool permanent)
    {
        if (row is null)
        {
            return;
        }

        // The agent is what actually moves the window, so a reveal is attempted first and the rule is only
        // changed once it succeeded. Clearing the rule first and then failing would leave the window on a
        // desktop nothing is tracking any more.
        var reveal = await _client.RevealAsync(row.AppId).ConfigureAwait(true);

        if (!reveal.Succeeded)
        {
            ShowError(reveal.Message);
            return;
        }

        if (!permanent)
        {
            ShowInfo(
                $"{row.DisplayName} is back on your desktop. It will be hidden again the next time it "
                + "starts.");
            return;
        }

        var settings = new HideSettings { Enabled = false };

        var saved = await _client.SetHideRuleAsync(row.Identity, settings).ConfigureAwait(true);

        if (!saved.Succeeded)
        {
            // Honest about the split outcome. The window is visible, which is what the user cared about,
            // but the rule survived and will hide it again — saying only "done" would be a lie the user
            // discovers tomorrow.
            ShowError(
                $"{row.DisplayName} is back on your desktop, but the hide rule could not be turned off, "
                + $"so it will be hidden again next time it starts. {saved.Message}");
            return;
        }

        ShowInfo($"{row.DisplayName} will no longer be hidden.");

        await LoadAsync(CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>The escape hatch. FR-604.</summary>
    private async Task RevealAllAsync()
    {
        var failures = new List<string>();

        // Sequential, deliberately. Each reveal ends with a window being restored and focused, and firing
        // a dozen of those concurrently produces a fight over the foreground that leaves focus somewhere
        // arbitrary.
        foreach (var row in Rows.ToList())
        {
            var result = await _client.RevealAsync(row.AppId).ConfigureAwait(true);

            if (!result.Succeeded)
            {
                failures.Add(row.DisplayName);
            }
        }

        if (failures.Count == 0)
        {
            ShowInfo("Every hidden app is back on your desktop. Their rules are unchanged.");
            return;
        }

        ShowError(
            $"{failures.Count} app{(failures.Count == 1 ? "" : "s")} could not be brought back: "
            + string.Join(", ", failures)
            + ". They may have closed while hidden, in which case starting them again will show them "
            + "normally.");
    }
}

/// <summary>One hidden app. FR-604.</summary>
public sealed class HiddenRowViewModel : ObservableObject
{
    private readonly AppRule _rule;

    public HiddenRowViewModel(AppRule rule) => _rule = rule;

    public AppIdentity Identity => _rule.Identity;

    public string AppId => _rule.Identity.AppId;

    public string DisplayName => string.IsNullOrWhiteSpace(_rule.Identity.DisplayName)
        ? AppId
        : _rule.Identity.DisplayName;

    public string? ExecutablePath => _rule.Identity.ExecutablePath;

    /// <summary>
    /// True when the rule exists but is switched off at the master level.
    /// </summary>
    /// <remarks>
    /// Shown rather than filtered out. A rule that is configured to hide but disabled looks identical to
    /// no rule at all from the user's side, and the difference matters the moment they re-enable it.
    /// </remarks>
    public bool IsRuleDisabled => !_rule.Enabled;
}
