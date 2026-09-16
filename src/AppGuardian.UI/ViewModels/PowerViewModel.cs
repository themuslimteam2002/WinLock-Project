using System.Collections.ObjectModel;
using System.Runtime.Versioning;
using AppGuardian.Shared.Contracts;
using AppGuardian.Shared.Models;
using AppGuardian.Shared.Security;
using AppGuardian.UI.Mvvm;
using AppGuardian.UI.Services;
using Microsoft.Extensions.Logging;

namespace AppGuardian.UI.ViewModels;

/// <summary>
/// CPU and battery restrictions, and what they are actually doing. FR-704, FR-707.
/// </summary>
/// <remarks>
/// Reports enforcement rather than intent. A power rule can be set and still not be in force — the target
/// is not running, the process already belongs to another job object (ADR-008), or the OS build has no
/// EcoQoS — and a page that showed only the rule would let a user believe they had capped an app that was
/// running unrestricted. Every row therefore carries both the rule and the live status.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class PowerViewModel : PageViewModel
{
    private readonly GuardianClient _client;
    private readonly ILogger<PowerViewModel> _log;

    private bool _ecoQosSupported = true;
    private bool _onBattery;

    public PowerViewModel(GuardianClient client, ILogger<PowerViewModel> log)
    {
        _client = client;
        _log = log;

        RemoveCommand = new AsyncRelayCommand(
            parameter => RemoveAsync(parameter as PowerRowViewModel),
            parameter => parameter is PowerRowViewModel,
            ex => _log.LogWarning(ex, "The power rule could not be removed."));

        RefreshCommand = new AsyncRelayCommand(
            () => LoadAsync(CancellationToken.None),
            onError: ex => _log.LogWarning(ex, "The power status could not be refreshed."));
    }

    public override string Title => "Battery and CPU";

    public ObservableCollection<PowerRowViewModel> Rows { get; } = new();

    public AsyncRelayCommand RemoveCommand { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public bool IsEmpty => Rows.Count == 0;

    /// <summary>False on hardware or Windows builds without EcoQoS. FR-703.</summary>
    public bool EcoQosSupported
    {
        get => _ecoQosSupported;
        private set
        {
            if (Set(ref _ecoQosSupported, value))
            {
                OnPropertyChanged(nameof(CapabilityDisclosure));
            }
        }
    }

    public bool OnBattery
    {
        get => _onBattery;
        private set => Set(ref _onBattery, value);
    }

    /// <summary>
    /// The honest scope of what these limits can do. FR-707.
    /// </summary>
    /// <remarks>
    /// Leads with the limitation, because the feature name promises more than the mechanism delivers: a
    /// CPU rate cap constrains scheduling, not memory, disk, GPU or network, and it cannot touch a process
    /// that another job object already owns.
    /// </remarks>
    public string CapabilityDisclosure
    {
        get
        {
            var text =
                "These limits cap how much processor time an app gets. They do not limit memory, disk, "
                + "graphics or network use, and they cannot restrict an app that Windows has already "
                + "placed under another program's control.";

            if (!EcoQosSupported)
            {
                text += " This PC does not support Windows' efficiency mode, so the efficiency option is "
                        + "ignored here and only the CPU cap and priority take effect.";
            }

            return text;
        }
    }

    public override async Task LoadAsync(CancellationToken ct)
    {
        IsBusy = true;

        try
        {
            var statusTask = _client.GetPowerStatusAsync(ct);
            var policyTask = _client.GetPolicyAsync(ct);

            var status = await statusTask.ConfigureAwait(true);
            var policy = await policyTask.ConfigureAwait(true);

            if (policy is null)
            {
                ShowError(
                    "The battery and CPU settings could not be read. AppGuardian's background service may "
                    + "still be starting.");
                return;
            }

            if (status is not null)
            {
                EcoQosSupported = status.EcoQosSupported;
                OnBattery = status.OnBattery;
            }

            var live = new Dictionary<string, PowerStatusEntry>(StringComparer.Ordinal);

            foreach (var entry in status?.Entries ?? new List<PowerStatusEntry>())
            {
                live[entry.AppId] = entry;
            }

            var rows = policy.Policy.Rules
                .Where(r => r.Power.Profile is not null)
                .OrderBy(r => r.Identity.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .Select(r => new PowerRowViewModel(
                    r,
                    live.GetValueOrDefault(r.Identity.AppId),
                    EcoQosSupported))
                .ToList();

            CollectionSync.Replace(Rows, rows);

            OnPropertyChanged(nameof(IsEmpty));

            if (rows.Count == 0)
            {
                ShowInfo(
                    "No apps are limited yet. Choose an app on the Apps page and pick a battery profile "
                    + "for it.");
            }
            else if (status is null)
            {
                // The rules are known but their live state is not. Said out loud, because the rows below
                // will show "not applied" for every app and that would otherwise read as a real finding.
                ShowError(
                    "Your limits are shown below, but AppGuardian could not check whether they are "
                    + "currently in force.");
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

    private async Task RemoveAsync(PowerRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        // A null profile is how the contract expresses "no power rule", which is distinct from an explicit
        // Balanced — the latter is a recorded decision to leave the app alone.
        var settings = new PowerSettings { Profile = null, ReducePriority = false, EcoQos = false };

        var result = await _client.SetPowerProfileAsync(row.Identity, settings).ConfigureAwait(true);

        if (!result.Succeeded)
        {
            ShowError(result.Message);
            return;
        }

        ShowInfo($"{row.DisplayName} is no longer limited.");

        await LoadAsync(CancellationToken.None).ConfigureAwait(true);
    }
}

/// <summary>One limited app: what was asked for, and what is actually in force. FR-704.</summary>
public sealed class PowerRowViewModel : ObservableObject
{
    private readonly AppRule _rule;
    private readonly PowerStatusEntry? _live;
    private readonly bool _ecoQosSupported;

    public PowerRowViewModel(AppRule rule, PowerStatusEntry? live, bool ecoQosSupported)
    {
        _rule = rule;
        _live = live;
        _ecoQosSupported = ecoQosSupported;
    }

    public AppIdentity Identity => _rule.Identity;

    public string AppId => _rule.Identity.AppId;

    public string DisplayName => string.IsNullOrWhiteSpace(_rule.Identity.DisplayName)
        ? AppId
        : _rule.Identity.DisplayName;

    /// <summary>
    /// The recorded profile, or null when the rule carries no power decision at all.
    /// </summary>
    /// <remarks>
    /// Nullable, deliberately. An earlier version collapsed null into <c>PowerProfile.Balanced</c> with
    /// <c>?? PowerProfile.Balanced</c>, which made the two states indistinguishable on this page even though
    /// the contract keeps them apart everywhere else — <c>PowerViewModel.RemoveAsync</c> writes null to mean
    /// "no rule" and Balanced to mean "a recorded decision to leave this app alone". The collapse also gave
    /// the null case Balanced's description from <see cref="PowerProfileMap"/>, so a row with no power rule
    /// claimed to have one.
    /// </remarks>
    public PowerProfile? Profile => _rule.Power.Profile;

    /// <summary>The profile used for lookups that need a concrete member; null behaves like Balanced.</summary>
    private PowerProfile EffectiveProfile => Profile ?? PowerProfile.Balanced;

    public string ProfileLabel => Profile switch
    {
        null => "No rule",
        PowerProfile.Balanced => "Balanced — no cap",
        PowerProfile.LowImpact => "Low impact",
        PowerProfile.StrictSavings => "Strict savings",
        _ => Profile.Value.ToString(),
    };

    /// <summary>The profile's own words, from the shared map rather than duplicated here.</summary>
    public string ProfileDescription => Profile is { } profile
        ? PowerProfileMap.Resolve(profile).Description
        : "No power rule is recorded for this app. Windows schedules it normally.";

    public bool IsRunning => _live?.ProcessIds.Count > 0;

    public bool IsThrottled => _live?.IsThrottled == true;

    public int? CpuRateCapPercent => _live?.CpuRateCapPercent
                                    ?? PowerProfileMap.Resolve(EffectiveProfile).CpuRateCapPercent;

    /// <summary>Why the limit is not in force, when it is not. ADR-008, FR-704.</summary>
    public string? UnsupportedReason => _live?.UnsupportedReason;

    /// <summary>
    /// One line for the state column.
    /// </summary>
    /// <remarks>
    /// "Not running" and "could not be applied" are kept apart. The first is expected and needs no action;
    /// the second means the user's setting will never take effect and they need to know which it is.
    /// </remarks>
    public string StateText
    {
        get
        {
            if (_live is null)
            {
                return "State unknown";
            }

            if (UnsupportedReason is { } reason)
            {
                return reason;
            }

            if (!IsRunning)
            {
                return "Not running — the limit applies when it starts";
            }

            if (!IsThrottled)
            {
                return "Running without the limit applied";
            }

            var suffix = _rule.Power.EcoQos && !_ecoQosSupported
                ? " (efficiency mode is not available on this PC)"
                : string.Empty;

            return CpuRateCapPercent is { } cap
                ? $"Limited to about {cap}% CPU{suffix}"
                : $"Limited{suffix}";
        }
    }

    /// <summary>True when the user's setting is not achieving what it says. Drives a warning icon.</summary>
    public bool NeedsAttention =>
        UnsupportedReason is not null || (IsRunning && !IsThrottled);
}
