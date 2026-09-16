namespace AppGuardian.Shared.Ipc;

/// <summary>
/// Dot-namespaced message type constants. API Design §4, with SRS traceability on each entry.
/// </summary>
/// <remarks>
/// SPEC CONFLICT SC-02: the SRS §10.2 uses flat PascalCase ops (<c>GetPolicy</c>, <c>SetRule</c>).
/// We use the API Design dot-namespaced form.
/// SPEC CONFLICT SC-03: the <c>policy.*</c> namespace appears in no API Design catalog table, yet
/// §2.4 requires the agent to reconcile against <c>policy.get</c> on reconnect. Added here.
/// </remarks>
public static class MessageTypes
{
    // ---- auth.* (API Design §4.1) ----

    /// <summary>UI/Agent → Service. FR-300, FR-308.</summary>
    public const string AuthGetStatus = "auth.getStatus";

    /// <summary>UI → Service. FR-300, FR-302, FR-303.</summary>
    public const string AuthSetup = "auth.setup";

    /// <summary>
    /// UI → Agent. FR-301.
    /// </summary>
    /// <remarks>
    /// SPEC CONFLICT SC-10: API Design §4.1 lists the direction as "UI/Agent→Agent", which is not
    /// an IPC call in the Agent→Agent case. Resolved per ADR/SC-10: the Agent renders the Hello
    /// prompt (it must — Session 0 isolation) then calls <see cref="AuthStartSession"/> on the
    /// Service, which remains the sole owner of session state.
    /// </remarks>
    public const string AuthVerifyWindowsHello = "auth.verifyWindowsHello";

    /// <summary>UI/Agent → Service. FR-302, FR-303, FR-306.</summary>
    public const string AuthVerifyPin = "auth.verifyPin";

    /// <summary>→ Service. FR-305, FR-308.</summary>
    public const string AuthStartSession = "auth.startSession";

    /// <summary>→ Service. FR-305.</summary>
    public const string AuthEndSession = "auth.endSession";

    /// <summary>UI → Service. Requires an elevated caller. FR-307.</summary>
    public const string AuthReset = "auth.reset";

    // ---- apps.* (API Design §4.2) ----

    /// <summary>UI → Service. FR-400, FR-404. Icons omitted; see SC-15c.</summary>
    public const string AppsListInstalled = "apps.listInstalled";

    /// <summary>UI → Service. FR-401.</summary>
    public const string AppsListRunning = "apps.listRunning";

    /// <summary>UI → Service. FR-402.</summary>
    public const string AppsResolveIdentity = "apps.resolveIdentity";

    /// <summary>UI → Service. FR-403.</summary>
    public const string AppsAddManual = "apps.addManual";

    /// <summary>
    /// UI → Service. FR-404. Not in the API Design catalog; added to keep list responses under
    /// the 256 KB envelope cap (SC-15c).
    /// </summary>
    public const string AppsGetIcon = "apps.getIcon";

    // ---- lock.* (API Design §4.3) ----

    /// <summary>UI → Service. FR-500, FR-801.</summary>
    public const string LockSetRule = "lock.setRule";

    /// <summary>Event. Service → Agent. FR-501.</summary>
    public const string LockAppLaunched = "lock.appLaunched";

    /// <summary>Service → Agent. FR-502, FR-503, FR-507.</summary>
    public const string LockShowOverlay = "lock.showOverlay";

    /// <summary>Agent → Service. FR-504, FR-505.</summary>
    public const string LockUnlock = "lock.unlock";

    /// <summary>UI → Service. FR-506.</summary>
    public const string LockGetState = "lock.getState";

    // ---- hide.* (API Design §4.4) ----

    /// <summary>UI → Service. FR-601, FR-605.</summary>
    public const string HideSetRule = "hide.setRule";

    /// <summary>Agent-local. FR-600.</summary>
    public const string HideEnsureWorkspace = "hide.ensureWorkspace";

    /// <summary>Service → Agent. FR-602, FR-603.</summary>
    public const string HideMoveToHidden = "hide.moveToHidden";

    /// <summary>UI → Agent. FR-604, FR-606.</summary>
    public const string HideReveal = "hide.reveal";

    // ---- power.* (API Design §4.5) ----

    /// <summary>UI → Service. FR-700, FR-705.</summary>
    public const string PowerSetProfile = "power.setProfile";

    /// <summary>Service-local. FR-701, FR-706.</summary>
    public const string PowerApplyThrottle = "power.applyThrottle";

    /// <summary>Service-local. FR-706.</summary>
    public const string PowerRemoveThrottle = "power.removeThrottle";

    /// <summary>UI → Service. FR-704.</summary>
    public const string PowerGetStatus = "power.getStatus";

    // ---- policy.* (SC-03: absent from the API Design catalog) ----

    /// <summary>Agent/UI → Service. Full policy snapshot. Referenced by API Design §2.4.</summary>
    public const string PolicyGet = "policy.get";

    /// <summary>Event. Service → Agent/UI. Policy changed; recipients should re-fetch.</summary>
    public const string PolicyChanged = "policy.changed";

    /// <summary>UI → Service. Delete a rule. FR-801.</summary>
    public const string PolicyDeleteRule = "policy.deleteRule";

    /// <summary>UI → Service. Global pause/resume, auth-guarded. FR-806.</summary>
    public const string PolicySetPaused = "policy.setPaused";

    // ---- system.* (API Design §4.6) ----

    /// <summary>UI → Service/Agent. FR-800, FR-804, FR-805.</summary>
    public const string SystemStatus = "system.status";

    /// <summary>UI → Service. FR-803.</summary>
    public const string SystemGetAuditLog = "system.getAuditLog";

    /// <summary>Any. Heartbeat. FR-205, NFR-R1, NFR-R2.</summary>
    public const string SystemPing = "system.ping";

    /// <summary>
    /// Agent/UI → Service. Append an audit entry. FR-508.
    /// </summary>
    /// <remarks>
    /// SPEC CONFLICT SC-12 / ADR-005: the Service is the only process that holds a handle to
    /// audit.log, because a user-privilege agent cannot append to an admin-only-write file and
    /// loosening the ACL would let any user process forge entries.
    /// </remarks>
    public const string SystemAudit = "system.audit";

    /// <summary>
    /// True when a request of this type may be safely retried after a timeout
    /// (API Design §2.4: all <c>*.get*</c>, <c>*.list*</c>, <c>*.status</c> are idempotent).
    /// Non-idempotent writes must not be blindly retried; the server deduplicates by messageId.
    /// </summary>
    public static bool IsIdempotent(string type) =>
        type.Contains(".get", StringComparison.Ordinal) ||
        type.Contains(".list", StringComparison.Ordinal) ||
        type.EndsWith(".status", StringComparison.Ordinal) ||
        type == SystemPing;
}
