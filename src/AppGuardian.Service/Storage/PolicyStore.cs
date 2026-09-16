using AppGuardian.Shared.Models;
using AppGuardian.Shared.Storage;
using Microsoft.Extensions.Logging;

namespace AppGuardian.Service.Storage;

/// <summary>
/// Owns <c>policy.json</c>. SRS FR-800..803, §9.1.
/// </summary>
/// <remarks>
/// The service is the only writer, which is what makes the in-memory copy authoritative between
/// saves and lets readers avoid touching the disk on every rule lookup — the process monitor consults
/// the policy on every process start, so a file read there would be a real cost.
/// <para>
/// Every mutation persists before returning, so a crash cannot lose an acknowledged change (NFR-S4
/// as restated in SC-08: a crash never mutates policy).
/// </para>
/// </remarks>
public sealed class PolicyStore
{
    private readonly AtomicJsonStore<PolicyDocument> _store;
    private readonly ILogger<PolicyStore> _log;
    private readonly SemaphoreSlim _mutate = new(1, 1);

    private PolicyDocument _document = new();
    private volatile bool _degraded;
    private string? _degradedReason;

    public PolicyStore(ILogger<PolicyStore> log, string? path = null)
    {
        _log = log;
        _store = new AtomicJsonStore<PolicyDocument>(path ?? StoragePaths.PolicyFile);
    }

    /// <summary>Raised after a successful mutation, so the dispatcher can push <c>policy.changed</c>.</summary>
    public event EventHandler<PolicyDocument>? Changed;

    /// <summary>True when the stored policy could not be read and defaults are in use.</summary>
    public bool IsDegraded => _degraded;

    public string? DegradedReason => _degradedReason;

    public async Task InitializeAsync(CancellationToken ct)
    {
        var result = await _store.LoadAsync(ct).ConfigureAwait(false);

        _document = result.Value;

        if (result.IsFailure)
        {
            // Deliberately not self-healing. Writing defaults over a corrupt policy would silently
            // unprotect every app the user had configured, and the user would have no signal. The
            // service reports degraded status and the dashboard surfaces it (FR-806).
            _degraded = true;
            _degradedReason = result.Error;
            _log.LogError("Policy could not be loaded: {Reason}. Running with no rules and reporting degraded.", result.Error);
            return;
        }

        if (!result.WasExisting)
        {
            _log.LogInformation("No policy file yet; starting with an empty rule set.");
            return;
        }

        VerifyIntegrity();

        _log.LogInformation(
            "Loaded {Count} rule(s); protection {State}.",
            _document.Rules.Count,
            _document.ProtectionPaused ? "paused" : "active");
    }

    /// <summary>
    /// Current document. Treat as read-only; mutations go through the methods below so they are
    /// serialized and persisted.
    /// </summary>
    public PolicyDocument Current => _document;

    public AppRule? FindByAppId(string appId) => _document.FindByAppId(appId);

    /// <summary>Rules that should currently be enforced. Empty while protection is paused.</summary>
    public IReadOnlyList<AppRule> ActiveRules() => _document.ActiveRules.ToList();

    /// <summary>
    /// Creates or updates a rule. FR-801.
    /// </summary>
    /// <remarks>
    /// Sections are merged rather than replaced: a null <c>lock</c>, <c>hide</c>, or <c>power</c> on
    /// the request means "leave unchanged". Without that, the dashboard's power page would wipe the
    /// lock settings every time it saved, because it only knows about its own section.
    /// </remarks>
    public async Task<AppRule> UpsertAsync(
        AppIdentity identity,
        string? displayName,
        LockSettings? lockSettings,
        HideSettings? hideSettings,
        PowerSettings? powerSettings,
        CancellationToken ct)
    {
        await _mutate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            if (string.IsNullOrEmpty(identity.AppId))
            {
                identity.WithDerivedId();
            }

            var rule = _document.FindByAppId(identity.AppId);

            if (rule is null)
            {
                rule = new AppRule
                {
                    RuleId = Guid.NewGuid().ToString(),
                    Identity = identity,
                };

                _document.Rules.Add(rule);
            }
            else
            {
                // Refresh the identity in place: an app that moved or was reinstalled keeps its
                // appId only if the derivation still matches, so anything else here (display name,
                // file hash, icon) is the newer information.
                rule.Identity = identity;
            }

            if (!string.IsNullOrWhiteSpace(displayName))
            {
                rule.Identity.DisplayName = displayName;
            }

            if (lockSettings is not null)
            {
                lockSettings.Normalize();
                rule.Lock = lockSettings;
            }

            if (hideSettings is not null)
            {
                rule.Hide = hideSettings;
            }

            if (powerSettings is not null)
            {
                rule.Power = powerSettings;
            }

            rule.Touch();

            await PersistAsync(ct).ConfigureAwait(false);

            _log.LogInformation("Rule {RuleId} saved for {App}.", rule.RuleId, rule.Identity.DisplayName);

            return rule;
        }
        finally
        {
            _mutate.Release();
        }
    }

    /// <summary>Deletes a rule by id. Returns false when it was already gone.</summary>
    public async Task<bool> DeleteAsync(string ruleId, CancellationToken ct)
    {
        await _mutate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            var rule = _document.FindByRuleId(ruleId);

            if (rule is null)
            {
                return false;
            }

            _document.Rules.Remove(rule);

            await PersistAsync(ct).ConfigureAwait(false);

            _log.LogInformation("Rule {RuleId} deleted.", ruleId);

            return true;
        }
        finally
        {
            _mutate.Release();
        }
    }

    /// <summary>
    /// Pauses or resumes all enforcement. FR-803.
    /// </summary>
    /// <remarks>
    /// A single flag rather than per-rule disabling, so resuming restores exactly the previous
    /// configuration. Pausing is persisted, which is a deliberate choice: a user who paused
    /// protection to install something should not have it silently return on the next reboot without
    /// them noticing, and the dashboard shows a persistent paused banner (FR-806).
    /// </remarks>
    public async Task SetPausedAsync(bool paused, CancellationToken ct)
    {
        await _mutate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            if (_document.ProtectionPaused == paused)
            {
                return;
            }

            _document.ProtectionPaused = paused;

            await PersistAsync(ct).ConfigureAwait(false);

            _log.LogWarning("Protection {State}.", paused ? "PAUSED" : "resumed");
        }
        finally
        {
            _mutate.Release();
        }
    }

    private async Task PersistAsync(CancellationToken ct)
    {
        _document.UpdatedUtc = DateTimeOffset.UtcNow;
        _document.IntegrityHash = ComputeHash(_document);

        await _store.SaveAsync(_document, ct).ConfigureAwait(false);

        // Cleared on a successful write: whatever was wrong with the file on load, the file on disk
        // is now one we wrote and can read.
        _degraded = false;
        _degradedReason = null;

        Changed?.Invoke(this, _document);
    }

    private void VerifyIntegrity()
    {
        if (string.IsNullOrEmpty(_document.IntegrityHash))
        {
            // Absent on a document written by an older build, or by hand. Not treated as tampering.
            return;
        }

        var expected = ComputeHash(_document);

        if (!string.Equals(expected, _document.IntegrityHash, StringComparison.OrdinalIgnoreCase))
        {
            // Not fatal, and not a security boundary — a local admin can recompute this hash as
            // easily as we can (see the residual risk in SRS §11). It is a tamper *signal*: it tells
            // the user something edited the file outside AppGuardian, which is worth surfacing
            // because the likely cause is a partial hand-edit that dropped rules.
            _log.LogWarning(
                "Policy integrity hash does not match. The file was modified outside AppGuardian. " +
                "Rules are still loaded; review them in the dashboard.");
        }
    }

    private static string ComputeHash(PolicyDocument document) =>
        // The hash covers the rules and the paused flag only. Including the hash field itself would
        // be self-referential, and including timestamps would make it change on every load.
        AtomicJsonStore<PolicyDocument>.ComputeIntegrityHash(new
        {
            document.SchemaVersion,
            document.Rules,
            document.ProtectionPaused,
        });
}
