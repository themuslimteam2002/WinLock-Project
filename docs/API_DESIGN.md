# API Design

## AppGuardian for Windows

| Field | Value |
|---|---|
| **Project Name** | AppGuardian for Windows |
| **Document Version** | 0.2.0 |
| **Document Status** | Baselined for AI-assisted implementation |
| **Scope** | Local (in-process + IPC) API design — **not** a public web API |
| **Companion Docs** | `docs/SRS.md` (v0.2.0), `docs/PROJECT_PLAN.md` (v0.2.0), `docs/DECISIONS.md` |
| **Date** | 2026-08-24 |
| **Supersedes** | API Design v0.1.0 |

### Document Purpose
Defines the internal application APIs, IPC contracts, service boundaries, data models, OS-integration interfaces, error model, and versioning rules for AppGuardian. Every contract here traces to an SRS requirement so implementation stays verifiable.

---

## Revision History

| Version | Date | Summary |
|---|---|---|
| 0.1.0 | 2026-08-xx | Initial IPC sketch (pipe names, envelope). |
| 0.2.0 | 2026-08-24 | Completed envelope (request/response/event/error), full message catalog with trace IDs, canonical data models, pipe ACL specification, auth/session model, versioning & compatibility rules, timeouts/retries, and OS-integration interface contracts. |

---

# 1. Architecture Overview

## 1.1 Components

**AppGuardian.Shared** — DTOs, enums, constants, message contracts, interface contracts. The single source of truth for wire types; referenced by all other projects.

**AppGuardian.Service** (Windows Service, SYSTEM) — policy storage access, process monitoring, process throttling, power/QoS enforcement, watchdog, and the primary message-broker endpoint. Authoritative owner of persisted policy.

**AppGuardian.Agent** (user session) — Windows Hello UI, lock overlay, window hooks, virtual-desktop operations, user notifications. Owns everything that must touch the interactive desktop (Session 0 isolation, SRS CN-1).

**AppGuardian.UI** (WPF dashboard) — rule management, status display, settings, logs. A client only; holds no authoritative state.

## 1.2 Communication Model

| Channel | Direction | Purpose |
|---|---|---|
| UI → Service | request/response | read/write rules, query status, audit log |
| UI → Agent | request/response | trigger auth prompt, overlay control, reveal flow |
| Agent → Service | request/response | fetch policy, report lock/hide events, request throttle |
| Service → Agent | event (push) | "locked app launched", "apply hide", "policy changed" |

**Transport:** Named Pipes (message mode). **Serialization:** UTF-8 JSON. **Styles:** request/response and service-to-client events. (SRS FR-203, FR-204.)

```
        ┌────────────┐   req/resp    ┌──────────────┐
        │ AppGuardian│──────────────▶│ AppGuardian  │
        │    .UI     │◀──────────────│  .Service    │
        └────────────┘               └──────┬───────┘
              │ req/resp                     │ req/resp + events
              ▼                              ▼
        ┌────────────┐   events      ┌──────────────┐
        │ AppGuardian│◀──────────────│  (Service is │
        │  .Agent    │──────────────▶│   broker)    │
        └────────────┘   req/resp    └──────────────┘
```

---

# 2. IPC Transport Design

## 2.1 Pipe Names
- Service pipe: `\\.\pipe\appguardian.service.v1`
- Agent pipe: `\\.\pipe\appguardian.agent.v1`

The `.v1` suffix is the **transport version** (see §7). Both pipes are created in **message mode** (`PipeTransmissionMode.Message`) so each envelope is a discrete message.

## 2.2 Security Requirements (SRS FR-203, NFR-S2)
- Pipe DACL restricts access to `SYSTEM`, `Administrators`, and the **current interactive user** (for the agent pipe). Everything else is denied.
- The service sets the pipe `PipeSecurity` explicitly at creation — never relies on defaults.
- The server validates caller identity via `GetNamedPipeClientProcessId` / token inspection where a privileged operation is requested; write/policy-mutating calls require an Administrator or SYSTEM caller.
- **No remote access:** pipes are local-only (`PipeOptions.None`, no `\\server\pipe\...` form). Network pipe access is rejected by construction.
- Malformed or oversized messages (> 256 KB default) are rejected with `E_BAD_REQUEST` and logged.

## 2.3 Message Envelope

All three message kinds share a common header. `type` is a dot-namespaced verb; `messageId` is a client-generated UUID; `correlationId` links a response/event to its originating request; `apiVersion` is the semantic contract version (§7).

### 2.3.1 Request
```json
{
  "apiVersion": "1.0",
  "kind": "request",
  "messageId": "8f0c2f5e-5a44-4e2b-9f2b-2f9b9d1c7a01",
  "type": "auth.verifyWindowsHello",
  "timestamp": "2026-08-24T14:19:25.227Z",
  "payload": {}
}
```

### 2.3.2 Response
```json
{
  "apiVersion": "1.0",
  "kind": "response",
  "messageId": "b2e7c1a0-1111-4f0a-9c33-77e0aa2b0c44",
  "correlationId": "8f0c2f5e-5a44-4e2b-9f2b-2f9b9d1c7a01",
  "timestamp": "2026-08-24T14:19:25.401Z",
  "success": true,
  "payload": { "verified": true, "method": "windowsHello" },
  "error": null
}
```

### 2.3.3 Event (Service → Agent, push)
```json
{
  "apiVersion": "1.0",
  "kind": "event",
  "messageId": "c9d1...",
  "type": "lock.appLaunched",
  "timestamp": "2026-08-24T14:19:26.010Z",
  "payload": { "appId": "sha256:ab12...", "processId": 4820, "windowHandle": 133742 }
}
```

### 2.3.4 Error object
```json
{
  "code": "E_AUTH_FAILED",
  "message": "Windows Hello verification was cancelled by the user.",
  "retryable": true,
  "details": { "attemptsRemaining": 4 }
}
```

## 2.4 Timeouts, Retries, Ordering
- Default request timeout: **5 s** (auth prompts: **60 s**, since they involve a human). On timeout the client raises `E_TIMEOUT`.
- Clients may retry **idempotent** requests (all `*.get*`, `*.list*`, `*.status`) up to 2 times with backoff. Non-idempotent writes must not be blindly retried — use `messageId` for server-side dedup.
- Events are best-effort; the Agent reconciles against `policy.get` on reconnect (no assumption of guaranteed delivery/order).

---

# 3. Error Model

| Code | Meaning | Retryable |
|---|---|---|
| `E_BAD_REQUEST` | Malformed envelope / schema violation | No |
| `E_UNSUPPORTED_VERSION` | `apiVersion` not supported by server | No |
| `E_UNAUTHORIZED` | Caller lacks required privilege | No |
| `E_AUTH_FAILED` | Hello/PIN verification failed | Yes |
| `E_AUTH_LOCKED_OUT` | Rate limit tripped (FR-306) | Yes (after cooldown) |
| `E_NOT_FOUND` | Rule/app not found | No |
| `E_CONFLICT` | Concurrent modification / duplicate rule | Maybe |
| `E_UNSUPPORTED_TARGET` | App cannot be locked/hidden/throttled (FR-509) | No |
| `E_VD_UNAVAILABLE` | Virtual Desktop API not usable (Risk 1) | No |
| `E_SERVICE_UNAVAILABLE` | Service down; client fails safe (NFR-S4) | Yes |
| `E_TIMEOUT` | Request timed out | Yes |
| `E_INTERNAL` | Unexpected server fault | Maybe |

Servers never leak stack traces or secrets in `message`; diagnostic detail goes to the local log only.

---

# 4. Message Catalog

Each entry: `type` — direction — SRS trace.

## 4.1 Auth (`auth.*`)
| type | dir | trace |
|---|---|---|
| `auth.getStatus` | UI/Agent→Service | FR-300, 308 |
| `auth.setup` | UI→Service | FR-300, 302, 303 |
| `auth.verifyWindowsHello` | UI/Agent→Agent | FR-301 |
| `auth.verifyPin` | UI/Agent→Service | FR-302, 303, 306 |
| `auth.startSession` | →Service | FR-305, 308 |
| `auth.endSession` | →Service | FR-305 |
| `auth.reset` | UI→Service (admin) | FR-307 |

## 4.2 Apps (`apps.*`)
| type | dir | trace |
|---|---|---|
| `apps.listInstalled` | UI→Service | FR-400, 404 |
| `apps.listRunning` | UI→Service | FR-401 |
| `apps.resolveIdentity` | UI→Service | FR-402 |
| `apps.addManual` | UI→Service | FR-403 |

## 4.3 Lock (`lock.*`)
| type | dir | trace |
|---|---|---|
| `lock.setRule` | UI→Service | FR-500, 801 |
| `lock.appLaunched` (event) | Service→Agent | FR-501 |
| `lock.showOverlay` | Service→Agent | FR-502, 503, 507 |
| `lock.unlock` | Agent→Service | FR-504, 505 |
| `lock.getState` | UI→Service | FR-506 |

## 4.4 Hide (`hide.*`)
| type | dir | trace |
|---|---|---|
| `hide.setRule` | UI→Service | FR-601, 605 |
| `hide.ensureWorkspace` | Agent | FR-600 |
| `hide.moveToHidden` | Service→Agent | FR-602, 603 |
| `hide.reveal` | UI→Agent | FR-604, 606 |

## 4.5 Power (`power.*`)
| type | dir | trace |
|---|---|---|
| `power.setProfile` | UI→Service | FR-700, 705 |
| `power.applyThrottle` | Service | FR-701, 706 |
| `power.removeThrottle` | Service | FR-706 |
| `power.getStatus` | UI→Service | FR-704 |

## 4.6 System (`system.*`)
| type | dir | trace |
|---|---|---|
| `system.status` | UI→Service/Agent | FR-800, 804, 805 |
| `system.getAuditLog` | UI→Service | FR-803 |
| `system.ping` | any | NFR-R1/R2 heartbeat |

---

# 5. Canonical Data Models

```jsonc
// AppIdentity — how an app is uniquely referenced (FR-402)
{
  "appId": "sha256:ab12...",        // stable derived key
  "displayName": "Example App",
  "executablePath": "C:\\Program Files\\Example\\app.exe",
  "packageFamilyName": null,          // set for UWP/MSIX
  "appUserModelId": null,
  "fileHash": "sha256:...",           // optional, for tamper/update detection
  "iconBase64": null
}

// AppRule — persisted policy for one app (FR-500/601/700, 605/705)
{
  "ruleId": "9f...",
  "identity": { /* AppIdentity */ },
  "lock": { "enabled": true, "tempUnlockSeconds": 300 },
  "hide": { "enabled": false, "workspace": "GuardianHidden" },
  "power": { "profile": "LowImpact", "reducePriority": true, "ecoQos": true },
  "enabled": true,
  "createdUtc": "2026-08-24T14:00:00Z",
  "updatedUtc": "2026-08-24T14:00:00Z"
}

// PowerProfile enum: "Balanced" | "LowImpact" | "StrictSavings"   (FR-700)

// UnlockSession (FR-305, 308)
{ "appId": "sha256:...", "expiresUtc": "2026-08-24T14:25:00Z" }

// AuditEntry (FR-508, 803)
{ "id": "...", "utc": "...", "actor": "user|system",
  "action": "lock|unlock|hide|reveal|throttle|authFail",
  "appId": "sha256:...", "result": "ok|denied|error", "detail": "" }

// SystemStatus (FR-800, 804, 805)
{ "serviceRunning": true, "agentRunning": true,
  "protectedCount": 3, "lockedCount": 2, "hiddenCount": 1, "throttledCount": 2,
  "virtualDesktopSupported": true, "windowsHelloAvailable": true }
```

**Credential storage note (FR-303/304, NFR-S1):** credentials are stored only as a salted hash (Argon2id preferred, PBKDF2-HMAC-SHA256 fallback) plus per-credential random salt; the raw PIN/password never crosses IPC in cleartext beyond the one-time `auth.setup`/`auth.verifyPin` payload over the local, ACL-restricted pipe, and is never persisted.

---

# 6. OS Integration Interface Contracts

Defined in `AppGuardian.Shared` as interfaces so OS specifics stay behind adapters (supports Risk 1 mitigation and NFR-Maintainability).

```csharp
public interface IWindowsHelloVerifier {            // FR-301
    Task<bool> IsAvailableAsync();
    Task<VerifyResult> VerifyAsync(string reason, CancellationToken ct);
}

public interface IProcessMonitor {                  // FR-401, 501
    event EventHandler<AppLaunchedEventArgs> AppLaunched;
    IReadOnlyList<RunningApp> ListRunning();
}

public interface IPowerController {                 // FR-701..703, 706
    void ApplyThrottle(int pid, PowerProfile p);
    void RemoveThrottle(int pid);
    bool SupportsEcoQos { get; }
}

public interface IVirtualDesktopAdapter {           // FR-600..606, Risk 1
    bool IsSupported { get; }                        // feature-detect at runtime
    DesktopId EnsureHidden(string name);
    void MoveWindow(IntPtr hwnd, DesktopId target);
}

public interface ILockOverlay {                     // FR-502/503/507
    void Show(AppIdentity app, IReadOnlyList<MonitorInfo> monitors);
    void Hide();
}
```

Each adapter must degrade gracefully: `IVirtualDesktopAdapter.IsSupported == false` ⇒ callers surface `E_VD_UNAVAILABLE` and the UI discloses the fallback (SRS FR-607).

---

# 7. Versioning & Compatibility

- **Transport version** (`.v1` in the pipe name) changes only on a breaking wire-framing change — old and new pipes can coexist during migration.
- **Contract version** (`apiVersion` in the envelope) is semantic: **minor** bumps add optional fields/messages (backward-compatible); **major** bumps may remove/rename. Servers accept any minor within the same major and reject others with `E_UNSUPPORTED_VERSION`.
- Unknown fields in a payload are **ignored**, not rejected, to allow forward-compatible additions.
- All three components ship from one repo/version, so at release they always share the same contract; the version negotiation exists for in-development safety and future upgrades.

---

# 8. Traceability Summary

Every message type in §4 and every model in §5 carries an SRS `FR-`/`NFR-` reference. The Project Plan's task list references these same IDs, so a reviewer can follow **requirement → API contract → implementation task → test case** end to end.

---

> **Note:** This enhancement is derived from the API draft you provided (which was truncated mid–message-envelope) plus the enhanced SRS v0.2.0 and Project Plan v0.2.0. I completed the envelope with response/event/error kinds and added the full message catalog, data models, security ACLs, error model, and versioning rules. If your source draft continued past the envelope with specific messages you'd already defined, paste that text and I'll reconcile names/fields.
