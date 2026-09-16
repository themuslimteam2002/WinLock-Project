using System.Runtime.Versioning;
using AppGuardian.Shared.Contracts;
using AppGuardian.Shared.Models;
using AppGuardian.Shared.Security;
using AppGuardian.UI.Mvvm;

namespace AppGuardian.UI.ViewModels;

/// <summary>
/// One row in the app picker, and the editor for that app's rule. FR-800, FR-802, FR-509.
/// </summary>
/// <remarks>
/// Holds two copies of the rule: the toggles the user is looking at, and <see cref="SavedRule"/> — what
/// the service last confirmed. Without the second copy a failed save would have no state to fall back to,
/// and the row would go on showing a lock that does not exist.
/// <para>
/// Each setter saves through the callback rather than raising an event for the page to handle. The saving
/// is the point of the toggle; separating them would allow a build where the toggle moves and nothing
/// happens.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class AppRowViewModel : ObservableObject
{
    private readonly Func<AppRowViewModel, RuleFacet, Task> _save;

    private bool _lockEnabled;
    private int _tempUnlockSeconds = LockSettings.DefaultTempUnlockSeconds;
    private bool _hideEnabled;
    private PowerProfile? _powerProfile;
    private bool _reducePriority;
    private bool _ecoQos;
    private string? _iconBase64;
    private bool _suppressSave;

    public AppRowViewModel(
        DiscoveredApp app,
        AppRule? rule,
        Func<AppRowViewModel, RuleFacet, Task> save)
    {
        Discovered = app;
        Identity = app.Identity;
        _save = save;

        AdoptSaved(rule);
    }

    public DiscoveredApp Discovered { get; }

    public AppIdentity Identity { get; }

    /// <summary>The last state the service confirmed. Null when no rule exists.</summary>
    public AppRule? SavedRule { get; private set; }

    public string AppId => Identity.AppId;

    public string DisplayName => string.IsNullOrWhiteSpace(Identity.DisplayName)
        ? System.IO.Path.GetFileNameWithoutExtension(Identity.ExecutablePath) ?? "Unknown app"
        : Identity.DisplayName;

    public string? ExecutablePath => Identity.ExecutablePath;

    public string? RuleId => SavedRule?.RuleId;

    public bool IsRunning => Discovered.IsRunning;

    public bool IsPackaged => Identity.IsPackaged;

    /// <summary>
    /// The reason this app will resist control, if there is one. FR-509.
    /// </summary>
    /// <remarks>
    /// Shown on the row before the user sets anything, not raised as an error after the first save. An
    /// elevated or protected process cannot be locked at all, and finding that out only when the lock
    /// fails to appear would read as the product being broken rather than as a documented limit.
    /// </remarks>
    public string? UnsupportedReason => Discovered.UnsupportedReason;

    public bool IsSupported => Discovered.UnsupportedReason is null;

    public string SourceLabel => Discovered.Source switch
    {
        DiscoverySource.RegistryUninstall => "Installed program",
        DiscoverySource.StartMenuShortcut => "Start menu",
        DiscoverySource.AppxPackage => "Microsoft Store app",
        DiscoverySource.RunningProcess => "Running now",
        DiscoverySource.ManuallyAdded => "Added by you",
        _ => string.Empty,
    };

    public string? IconBase64
    {
        get => _iconBase64;
        set => Set(ref _iconBase64, value);
    }

    public bool IsProtected => SavedRule?.HasAnyProtection == true;

    // ---- lock ----

    public bool LockEnabled
    {
        get => _lockEnabled;
        set
        {
            if (Set(ref _lockEnabled, value))
            {
                OnPropertyChanged(nameof(TempUnlockEditable));
                Save(RuleFacet.Lock);
            }
        }
    }

    /// <summary>
    /// Temporary unlock window, in minutes for display. FR-305.
    /// </summary>
    /// <remarks>
    /// Stored in seconds (ADR-009, SC-04) and shown in minutes, because a user thinking "give me five
    /// minutes" should not have to type 300. The conversion lives here so nothing else in the dashboard
    /// has to know both units.
    /// </remarks>
    public int TempUnlockMinutes
    {
        get => Math.Max(1, _tempUnlockSeconds / 60);
        set
        {
            var seconds = Math.Clamp(
                value * 60,
                LockSettings.MinTempUnlockSeconds,
                LockSettings.MaxTempUnlockSeconds);

            if (Set(ref _tempUnlockSeconds, seconds, nameof(TempUnlockMinutes)))
            {
                Save(RuleFacet.Lock);
            }
        }
    }

    public bool TempUnlockEditable => _lockEnabled;

    // ---- hide ----

    public bool HideEnabled
    {
        get => _hideEnabled;
        set
        {
            if (Set(ref _hideEnabled, value))
            {
                Save(RuleFacet.Hide);
            }
        }
    }

    // ---- power ----

    public PowerProfile? PowerProfile
    {
        get => _powerProfile;
        set
        {
            if (Set(ref _powerProfile, value))
            {
                OnPropertyChanged(nameof(PowerOptionsEditable));
                OnPropertyChanged(nameof(PowerSummary));

                // The profile's own defaults are applied when one is first chosen, so a user who picks
                // "Low impact" and saves gets the behaviour the label promises rather than a profile with
                // both of its mechanisms switched off.
                if (value is not null && !_reducePriority && !_ecoQos)
                {
                    _suppressSave = true;
                    ReducePriority = value != Shared.Models.PowerProfile.Balanced;
                    EcoQos = value != Shared.Models.PowerProfile.Balanced;
                    _suppressSave = false;
                }

                Save(RuleFacet.Power);
            }
        }
    }

    public bool PowerOptionsEditable => _powerProfile is not null;

    public bool ReducePriority
    {
        get => _reducePriority;
        set
        {
            if (Set(ref _reducePriority, value))
            {
                OnPropertyChanged(nameof(PowerSummary));
                Save(RuleFacet.Power);
            }
        }
    }

    public bool EcoQos
    {
        get => _ecoQos;
        set
        {
            if (Set(ref _ecoQos, value))
            {
                OnPropertyChanged(nameof(PowerSummary));
                Save(RuleFacet.Power);
            }
        }
    }

    /// <summary>Plain-language description of what the chosen profile will actually do. FR-704.</summary>
    public string PowerSummary
    {
        get
        {
            // Worded as "no rule", not "no limit". Balanced is also an uncapped state, and calling both of
            // them "no limit" made the two indistinguishable in the UI even though the contract keeps them
            // apart: null means the policy file carries nothing for this app, Balanced means it carries a
            // recorded decision to leave it alone.
            if (_powerProfile is null)
            {
                return "No rule. Nothing is recorded for this app and Windows schedules it normally.";
            }

            var spec = PowerProfileMap.Resolve(_powerProfile.Value);

            var parts = new List<string>();

            if (spec.CpuRateCapPercent is { } percent)
            {
                parts.Add($"capped at about {percent}% of total CPU");
            }

            if (_reducePriority)
            {
                parts.Add("run at lower priority");
            }

            if (_ecoQos)
            {
                parts.Add("marked as background work where Windows supports it");
            }

            return parts.Count == 0
                ? "No effective limit — turn on at least one of the options below."
                : "This app will be " + string.Join(", ", parts) + ".";
        }
    }

    // ---- snapshots for the save path ----

    public LockSettings LockSettingsSnapshot()
    {
        var settings = new LockSettings
        {
            Enabled = _lockEnabled,
            TempUnlockSeconds = _tempUnlockSeconds,
            TempUnlockMode = SavedRule?.Lock.TempUnlockMode ?? TempUnlockMode.Duration,
        };

        settings.Normalize();

        return settings;
    }

    public HideSettings HideSettingsSnapshot() => new() { Enabled = _hideEnabled };

    public PowerSettings PowerSettingsSnapshot() => new()
    {
        Profile = _powerProfile,
        ReducePriority = _reducePriority,
        EcoQos = _ecoQos,
    };

    /// <summary>Takes the service's confirmed rule as the new truth.</summary>
    public void AdoptSaved(AppRule? rule)
    {
        SavedRule = rule;
        RevertFrom(rule);

        OnPropertyChanged(nameof(SavedRule));
        OnPropertyChanged(nameof(RuleId));
        OnPropertyChanged(nameof(IsProtected));
    }

    /// <summary>
    /// Resets the editable state from a rule, without saving.
    /// </summary>
    /// <remarks>
    /// The suppression flag is what makes this safe to call from the save path's failure branch. Without
    /// it, resetting a toggle would fire another save, which could fail, which would reset again.
    /// </remarks>
    public void RevertFrom(AppRule? rule)
    {
        _suppressSave = true;

        try
        {
            LockEnabled = rule?.Lock.Enabled ?? false;
            _tempUnlockSeconds = rule?.Lock.TempUnlockSeconds ?? LockSettings.DefaultTempUnlockSeconds;
            OnPropertyChanged(nameof(TempUnlockMinutes));

            HideEnabled = rule?.Hide.Enabled ?? false;

            PowerProfile = rule?.Power.Profile;
            ReducePriority = rule?.Power.ReducePriority ?? false;
            EcoQos = rule?.Power.EcoQos ?? false;
        }
        finally
        {
            _suppressSave = false;
        }
    }

    private void Save(RuleFacet facet)
    {
        if (_suppressSave)
        {
            return;
        }

        // Fire-and-forget by necessity: this runs from a property setter that a binding is driving, and a
        // setter cannot be awaited. The callback reports its own failures, so nothing is swallowed here.
        _ = _save(this, facet);
    }
}
