# Software Requirements Specification (SRS)

## AppGuardian for Windows

| Field | Value |
|---|---|
| **Project Name** | AppGuardian for Windows |
| **Document Version** | 0.2.0 |
| **Document Status** | Baselined draft for AI-assisted implementation |
| **Project Owner** | Hafiz |
| **Author** | Hafiz (with AI-assisted authoring) |
| **Date** | 2026-08-24 |
| **Classification** | Public / Open Source |
| **Supersedes** | SRS v0.1.0 |
| **Target Standard** | Structured after ISO/IEC/IEEE 29148:2018 |

### Development Model
AI-assisted development using:
- **Claude Code** — primary implementation, refactoring, and test authoring.
- **Antigravity** — UI/UX design, iconography, and documentation support.

---

## Revision History

| Version | Date | Author | Summary of Changes |
|---|---|---|---|
| 0.1.0 | 2026-08-xx | Hafiz | Initial draft SRS. |
| 0.2.0 | 2026-08-24 | Hafiz | Industrial-grade enhancement: added revision history, definitions/acronyms, requirement ID & priority scheme, per-requirement rationale/preconditions/acceptance, measurable NFRs, C4-style architecture, persisted data model, Named-Pipe IPC protocol spec, STRIDE threat model, verification test cases, full requirements traceability matrix (RTM), milestone plan, and expanded Definition of Done. |

---

## Table of Contents

1. Introduction
2. Overall Description & Product Scope
3. Target Users & Stakeholders
4. Assumptions, Constraints & Dependencies
5. User Roles and Permissions
6. Functional Requirements
7. Non-Functional Requirements
8. System Architecture
9. Data Model & Persistence
10. IPC Protocol Specification
11. Security & Threat Model (STRIDE)
12. Permissions and OS Integration
13. Risks and Mitigations
14. Verification & Acceptance Test Cases
15. Requirements Traceability Matrix
16. Delivery Plan & Milestones
17. Acceptance Criteria
18. Definition of Done
- Appendix A: Glossary & Acronyms
- Appendix B: Requirement Conventions
- Appendix C: Open Questions

---

# 1. Introduction

## 1.1 Purpose
This document defines the complete, verifiable software requirements for **AppGuardian for Windows**, a free and open-source Windows desktop utility that lets users:

1. **Lock** selected applications behind Windows Hello or a custom PIN/password.
2. **Hide** selected applications inside a dedicated hidden Virtual Desktop/workspace so they are not visible on the main desktop.
3. **Restrict** selected applications from excessive battery/CPU drain via process throttling, priority control, and power/QoS policies.

The document is written to be directly consumable by AI coding agents and human contributors. Every functional requirement carries a stable identifier, a priority, a rationale, and a testable acceptance condition so that implementation and verification can be automated and traced.

## 1.2 Intended Audience
- **Implementers** (Claude Code, human contributors) — authoritative source of behavior.
- **Reviewers / QA** — basis for test case derivation and acceptance sign-off.
- **Project Owner (Hafiz)** — scope control and acceptance authority.
- **Downstream users / packagers** — understanding of capabilities and documented limitations.

## 1.3 Product Summary
AppGuardian is a **local-first** Windows system utility installed with administrative privileges. It comprises:
- A main dashboard UI (WPF).
- A user-session background agent.
- A Windows Service for policy enforcement and monitoring.
- A direct-download installer (Inno Setup).

It requires **no cloud accounts, license keys, telemetry, or internet connectivity** for any core functionality.

## 1.4 Target Platform
- Windows 10 64-bit and Windows 11 64-bit.
- **Primary target:** Windows 11 23H2 or later.
- **Minimum supported target:** Windows 10 22H2 (build 19045).
- x64 architecture. ARM64 is **out of scope** for the MVP (see §2.2).

## 1.5 Distribution Model
- Direct download from website or local installer distribution.
- Installer requires administrative privileges.
- Installs the main application, user-session agent, and Windows Service.

## 1.6 Licensing Model
- Free, open source, no license key, no paid activation.
- **Suggested license: MIT.**

## 1.7 References
- ISO/IEC/IEEE 29148:2018 — Requirements engineering.
- Microsoft Win32 API documentation (Process, Window, Job Object, Power Throttling).
- Microsoft `IVirtualDesktopManager` / undocumented Virtual Desktop COM interfaces.
- Windows Hello / `Windows.Security.Credentials.UI` and `KeyCredentialManager`.
- EcoQoS / `SetProcessInformation(ProcessPowerThrottling)`.

---

# 2. Overall Description & Product Scope

## 2.1 In Scope (MVP)

### 2.1.1 Application Locking
- Detect installed desktop applications.
- Allow the user to select apps to lock.
- Lock selected apps on launch or focus.
- Unlock via Windows Hello or a custom PIN/password fallback.

### 2.1.2 Application Hiding
- Hide selected apps from the main desktop workspace.
- Run/move hidden apps into a dedicated hidden Virtual Desktop/workspace.
- Reveal/unhide flow from the dashboard.
- Prevent normal visibility on the main desktop where technically possible (best-effort).

### 2.1.3 Battery/CPU Restriction
- Detect running processes/apps.
- Apply power profiles to selected apps.
- Restrict CPU via Job Objects or equivalent.
- Apply lower priority or EcoQoS where supported.
- Show basic battery/CPU impact information.

### 2.1.4 Security and Settings
- Local authentication setup, Windows Hello enrollment/check, PIN/password fallback.
- Admin-protected settings, local policy storage.
- Audit/history log for lock/unlock/hide actions.

### 2.1.5 Installation and Maintenance
- Direct-download installer, automatic service installation, agent startup registration, clean uninstallation.

## 2.2 Out of Scope for MVP
Cloud synchronization; online account login; license key validation; telemetry/analytics; hiding apps from Task Manager; kernel-level process hiding; blocking Ctrl+Alt+Del; globally blocking Task Manager; enterprise MDM; full multi-user session support; anti-cheat/DRM-protected app support; guaranteed hiding from all advanced system tools; ARM64 builds; localization beyond English.

## 2.3 Product Perspective
AppGuardian is a standalone, self-contained utility with no server-side component. It integrates with the OS through documented Win32/WinRT APIs and (for virtual desktops) through a versioned abstraction layer over partially undocumented COM interfaces.

---

# 3. Target Users & Stakeholders

## 3.1 Primary User
A local Windows user seeking privacy protection, app locking, workspace hiding, and battery/CPU control on a personal machine.

## 3.2 Administrator User
The person who installs the app and grants admin permissions (often the same as the primary user on a consumer device).

## 3.3 Stakeholder Register

| Stakeholder | Interest | Influence |
|---|---|---|
| Hafiz (Owner) | Scope, quality, acceptance | Final authority |
| End users | Privacy, reliability, low overhead | Adoption |
| Contributors | Clean, modular, documented code | Maintenance |
| AV vendors | Non-malicious behavior signals | Can block distribution |

## 3.4 Non-Root / Non-Enterprise Assumption
The MVP assumes a standard consumer Windows machine, a local administrator available for installation, and no domain/enterprise policy enforcement.

---

# 4. Assumptions, Constraints & Dependencies

## 4.1 Assumptions
- The user has administrator rights during installation.
- The app runs primarily for the interactive logged-in user.
- Hidden virtual desktop behavior is implemented via Windows desktop APIs or an accepted wrapper.
- Windows Hello may be unavailable, so a fallback PIN/password is **mandatory**.
- Some target apps may be impossible to lock/hide (exclusive fullscreen, anti-cheat, elevated context, system processes).

## 4.2 Constraints
- **CN-1** Windows Services cannot show UI on the user desktop (Session 0 isolation) — all UI lives in the agent/UI processes.
- **CN-2** Virtual Desktop management is partially undocumented and may change between Windows builds — must be isolated behind a versioned adapter.
- **CN-3** Hiding is best-effort, not absolute.
- **CN-4** Battery restriction is throttling-based, not a guarantee of zero battery usage.
- **CN-5** Elevated/protected apps may be uncontrollable by AppGuardian running at the same or lower integrity level.
- **CN-6** MVP timeline is approximately one month — scope is strictly bounded by §2.

## 4.3 Dependencies
- .NET 8 runtime (or self-contained publish).
- Windows Hello APIs for biometric/PIN verification.
- Win32 APIs (process, window hooks, Job Objects, power throttling).
- Windows Virtual Desktop COM interfaces or a compatible wrapper.
- Inno Setup for installer creation.

---

# 5. User Roles and Permissions

| Role | Runs as | Capabilities |
|---|---|---|
| **Installer/Admin** | Elevated | Install/uninstall; register/start service; configure protected settings; define app rules. |
| **Standard Interactive User** | User session | Use dashboard; authenticate (Hello/PIN); lock/unlock; hide/reveal; apply battery profiles. |
| **Background Service** | LocalSystem | Enforce policies; monitor processes; apply throttling; watchdog/recovery. |
| **User-Session Agent** | User session | Render overlays; prompt auth; window hooks; virtual desktop operations. |

Privilege principle: the service holds the highest privilege and performs no UI; the agent performs all UI and window manipulation within the user's session. Cross-boundary requests are authenticated over IPC (§10).

---

# 6. Functional Requirements

**Priority key:** `M` = Must (MVP-blocking), `S` = Should (high value, may slip), `C` = Could (nice-to-have). Verification methods: `T` = Test, `D` = Demonstration, `I` = Inspection, `A` = Analysis. See Appendix B.

## 6.1 Installation and Removal

| ID | Pri | Requirement | Rationale / Acceptance |
|---|---|---|---|
| FR-100 | M | Provide a Windows installer requiring administrative privileges. | Service + startup registration need elevation. **Accept:** launching without elevation triggers UAC; cancelling aborts install. `T` |
| FR-101 | M | Install and register a Windows Service named `AppGuardian.Service` (start type: Automatic). | **Accept:** `sc query AppGuardian.Service` returns `RUNNING` post-install. `T` |
| FR-102 | M | Register the user-session agent to auto-start after user login (per-user Run key or Task Scheduler logon trigger). | **Accept:** agent process present after logon. `T` |
| FR-103 | M | Uninstaller shall stop+remove the service, remove startup entries, remove application files, and optionally remove local user data (user prompted). | **Accept:** post-uninstall, service absent, no autostart, program files removed; data removal honors user choice. `T` |
| FR-104 | S | Support repair/re-registration of the service and startup entries. | **Accept:** running repair restores a manually-deleted service. `D` |
| FR-105 | S | Installer shall verify OS build ≥ minimum target (§1.4) and abort with a clear message otherwise. | Prevents unsupported installs. `T` |
| FR-106 | C | Sign installer and binaries with a code-signing certificate when available. | Reduces AV/SmartScreen friction (Risk 4). `I` |

## 6.2 Application Architecture Requirements

| ID | Pri | Requirement |
|---|---|---|
| FR-200 | M | Provide a WPF dashboard application for configuration and monitoring. |
| FR-201 | M | Provide a user-session agent for UI overlays, auth prompts, window hooks, and virtual desktop operations. |
| FR-202 | M | Provide a Windows Service for policy storage access, process monitoring, power enforcement, and watchdog/recovery. |
| FR-203 | M | UI, agent, and service shall communicate over local Named Pipes with JSON message envelopes (§10). |
| FR-204 | M | All core functionality shall work fully offline with no network calls. |
| FR-205 | S | Each component shall expose a health/heartbeat endpoint over IPC for status indicators (FR-804/805). |
| FR-206 | S | Shared models, contracts, and DTOs shall live in a single `AppGuardian.Shared` assembly referenced by all components. |

## 6.3 Authentication Requirements

| ID | Pri | Requirement | Acceptance |
|---|---|---|---|
| FR-300 | M | On first run, require the user to configure a preferred auth method and a fallback PIN/password. | Setup cannot be skipped; app unusable until complete. `T` |
| FR-301 | M | Support Windows Hello verification where available (`KeyCredentialManager` / `UserConsentVerifier`). | `D` |
| FR-302 | M | Support a custom PIN/password when Hello is unavailable or fails. | `T` |
| FR-303 | M | Store only salted, hashed credential data locally (PBKDF2/Argon2id, per-credential random salt, ≥ 100k iterations or memory-hard params). | Inspect storage: no reversible secret present. `I` |
| FR-304 | M | Never store PINs/passwords in plain text or reversibly encrypted form. | `I` |
| FR-305 | M | Support temporary unlock sessions with a configurable timeout (default 5 min, range 1–60 min or "until app close"). | `T` |
| FR-306 | M | Rate-limit failed auth: after N failures (default 5) enforce a backoff (default 30s, escalating). | `T` |
| FR-307 | S | Provide PIN/password reset via: local administrator action, secure in-dashboard reset after OS-level admin (UAC) confirmation, or a reset/repair tool. | `D` |
| FR-308 | S | Lock/hide/reveal operations shall each require a fresh auth check unless within an active unlock session. | `T` |

## 6.4 App Discovery Requirements

| ID | Pri | Requirement |
|---|---|---|
| FR-400 | M | List installed desktop applications selectable for protection (from registry uninstall keys, Start Menu shortcuts, and known package sources). |
| FR-401 | M | List currently running processes/apps. |
| FR-402 | M | Identify applications using one or more of: executable full path, Package Family Name, AppUserModelID, and file hash (SHA-256) where useful. |
| FR-403 | M | Allow manual selection of an executable file via file picker. |
| FR-404 | M | Display app name, icon (if available), path/package identity, and running status. |
| FR-405 | S | De-duplicate discovered entries and merge running/installed views into one selectable list. |
| FR-406 | C | Cache discovery results and refresh on demand and on process-start events. |

## 6.5 App Lock Requirements

| ID | Pri | Requirement | Acceptance |
|---|---|---|---|
| FR-500 | M | User can mark selected apps as locked. | `T` |
| FR-501 | M | On launch or focus of a locked app, AppGuardian triggers the lock flow. | Lock overlay appears on focus. `T` |
| FR-502 | M | Agent displays a secure lock overlay/window covering the target. | `D` |
| FR-503 | S | Lock UI is displayed top-most above normal windows where technically possible. | `D` |
| FR-504 | M | Target app remains inaccessible until authentication succeeds. | Input to target blocked while locked. `T` |
| FR-505 | M | Allow temporary unlock for a configurable duration (ties to FR-305). | `T` |
| FR-506 | M | Track lock state per app/process. | `A` |
| FR-507 | S | Lock overlay supports multi-monitor environments. | `D` |
| FR-508 | M | Log lock/unlock events locally (FR-803). | `I` |
| FR-509 | S | If overlay cannot cover a target (e.g., exclusive fullscreen), fall back to minimizing/suspending the process and disclose the limitation. | `D` |

## 6.6 App Hide Requirements

| ID | Pri | Requirement | Acceptance |
|---|---|---|---|
| FR-600 | M | Create/use a dedicated hidden Virtual Desktop/workspace named `GuardianHidden` (or equivalent). | `D` |
| FR-601 | M | User can mark selected apps as hidden. | `T` |
| FR-602 | M | On launch/detection of a hidden app, agent attempts to move its windows to the hidden workspace. | `D` |
| FR-603 | S | Best-effort to keep hidden windows invisible on the main desktop. | `D` |
| FR-604 | M | User can reveal hidden apps from the dashboard after authentication. | `T` |
| FR-605 | M | Hidden app rules persist across restarts. | `T` |
| FR-606 | M | Support unhide, reveal temporarily, and return-to-main-desktop actions. | `T` |
| FR-607 | M | Clearly disclose that hiding is best-effort and may not hide from Task Manager or advanced tools. | UI disclosure present. `I` |
| FR-608 | S | On agent restart, re-apply hide rules to matching already-running windows. | `T` |

## 6.7 Battery/CPU Restriction Requirements

| ID | Pri | Requirement | Acceptance |
|---|---|---|---|
| FR-700 | M | Provide power profiles: Balanced, Low Impact, Strict Savings. | `T` |
| FR-701 | M | Apply CPU throttling to selected apps via Job Objects (`JOBOBJECT_CPU_RATE_CONTROL`) or equivalent. | `T` |
| FR-702 | S | Optionally reduce process priority for selected apps. | `T` |
| FR-703 | S | Apply EcoQoS / power-throttling (`SetProcessInformation`) where supported. | `D` |
| FR-704 | M | Show running apps and their restriction status. | `D` |
| FR-705 | M | Battery restriction rules persist across restarts. | `T` |
| FR-706 | M | Apply/remove restrictions on running apps at runtime without restarting them. | `T` |
| FR-707 | M | Disclose that actual savings depend on workload, hardware, and Windows behavior. | `I` |
| FR-708 | C | Auto-apply a stricter profile when the device is on battery and relax on AC. | `D` |

## 6.8 Dashboard and Settings Requirements

| ID | Pri | Requirement |
|---|---|---|
| FR-800 | M | Dashboard home shows protected/hidden/locked/battery-restricted apps plus service and agent status. |
| FR-801 | M | User can create, edit, enable, disable, and delete app rules. |
| FR-802 | M | Settings page: authentication, timeout, theme, startup behavior, logging, reset/uninstall utilities. |
| FR-803 | M | Audit Log page shows recent local events with timestamp, action, target, and outcome. |
| FR-804 | M | Dashboard shows whether the Windows Service is running. |
| FR-805 | M | Dashboard shows whether the user-session agent is running. |
| FR-806 | S | Provide a global enable/disable ("pause protection") toggle guarded by authentication. |
| FR-807 | C | Export/import rules as a local JSON file (no cloud). |

---

# 7. Non-Functional Requirements

Each NFR is measurable and verifiable.

## 7.1 Performance
| ID | Requirement | Metric |
|---|---|---|
| NFR-P1 | Dashboard cold start | ≤ 3 s on a 4-core/8 GB SSD machine. |
| NFR-P2 | Idle process-monitoring overhead | ≤ 2% average CPU on target hardware. |
| NFR-P3 | Idle memory footprint (service + agent) | ≤ 150 MB combined. |
| NFR-P4 | Lock trigger latency after focus/launch | ≤ 1 s where technically possible. |
| NFR-P5 | IPC round-trip (local pipe) | ≤ 100 ms p95. |

## 7.2 Security
| ID | Requirement |
|---|---|
| NFR-S1 | No plain-text credentials stored (FR-303/304). |
| NFR-S2 | Named-Pipe ACLs restrict access to the interactive user and LocalSystem; reject other principals. |
| NFR-S3 | Settings and policy store are protected against casual tampering (ACL + integrity check). |
| NFR-S4 | On service/agent unavailability, the app fails safe (protected apps remain locked/hidden; no silent unprotect). |
| NFR-S5 | No dynamic code download or execution; no unsigned plugin loading. |

## 7.3 Privacy
| ID | Requirement |
|---|---|
| NFR-Pr1 | No telemetry or analytics by default (or ever, in MVP). |
| NFR-Pr2 | No outbound network calls required for any feature. |
| NFR-Pr3 | All data stored locally under a documented path. |

## 7.4 Reliability & Availability
| ID | Requirement |
|---|---|
| NFR-R1 | Service auto-recovers from crashes (SCM recovery actions: restart). |
| NFR-R2 | Agent restarts if terminated, where the session permits. |
| NFR-R3 | Rules survive reboot (persisted, FR-605/705). |
| NFR-R4 | Policy store writes are atomic (write-temp-then-rename) to survive power loss. |

## 7.5 Usability
Simple onboarding; clear permission/service status indicators; easy app-selection UI; clear, actionable error messages; every best-effort limitation disclosed in-context.

## 7.6 Compatibility
Windows 10/11 64-bit; light/dark theme; multi-monitor; per-monitor DPI awareness (PMv2).

## 7.7 Maintainability
Modular codebase; strict separation of UI/agent/service/shared; AI-readable docs in `/docs`; unit tests for shared logic; conventional commits.

## 7.8 Open-Source Compliance
No proprietary license keys; public-ready source; MIT LICENSE, README, and reproducible build instructions.

---

# 8. System Architecture

## 8.1 Components (C4 — Container level)

```
+-------------------+        Named Pipe        +---------------------+
|  AppGuardian.UI   |<------------------------>|  AppGuardian.Agent  |
|  (WPF dashboard)  |        \GuardianUI        | (user session)      |
+---------+---------+                           +----------+----------+
          |                                                |
          |  Named Pipe \GuardianCtl                       | Named Pipe
          |                                                | \GuardianSvc
          v                                                v
+----------------------------------------------------------------------+
|                     AppGuardian.Service (LocalSystem)                 |
|  policy store | process monitor | power/CPU enforcement | watchdog   |
+----------------------------------------------------------------------+
                          |
                          v
             +--------------------------+
             |  AppGuardian.Shared      |
             |  models/contracts/DTOs   |
             +--------------------------+
```

- **AppGuardian.UI** — WPF management console.
- **AppGuardian.Agent** — overlays, Windows Hello UI, virtual desktop ops, window/focus hooks (WinEvent hooks).
- **AppGuardian.Service** — policy enforcement, process monitoring, CPU/power restriction, persistence, watchdog.
- **AppGuardian.Shared** — models, contracts, IPC DTOs, constants (referenced by all).

## 8.2 Responsibility Split (why three processes)
Session 0 isolation (CN-1) forbids service UI; virtual desktop and overlay APIs must run in the interactive session (agent); highest-privilege enforcement (Job Objects, service control) must run as LocalSystem (service). The UI is a thin client over both.

## 8.3 Key Runtime Flows (summary)
- **Lock flow:** agent WinEvent hook detects foreground change → checks policy (cached from service) → shows overlay → collects auth → on success starts unlock session and dismisses overlay.
- **Hide flow:** agent detects target window → moves to `GuardianHidden` virtual desktop → records state.
- **Throttle flow:** service process monitor detects target PID → assigns to a Job Object with CPU rate cap / EcoQoS per profile.

---

# 9. Data Model & Persistence

## 9.1 Storage Location & Format
- Machine-wide policy: `%ProgramData%\AppGuardian\policy.json` (ACL: Admins/LocalSystem write; Users read).
- Per-user settings: `%LOCALAPPDATA%\AppGuardian\settings.json`.
- Audit log: `%ProgramData%\AppGuardian\audit.log` (append-only, rotated).
- Credential store: `%ProgramData%\AppGuardian\credentials.dat` (salted hashes only).
- All JSON writes are atomic (NFR-R4).

## 9.2 Core Entities (logical schema)

```jsonc
// AppRule
{
  "id": "guid",
  "displayName": "string",
  "identity": {
    "exePath": "string|null",
    "packageFamilyName": "string|null",
    "aumid": "string|null",
    "sha256": "string|null"
  },
  "lock":   { "enabled": true, "tempUnlockMinutes": 5 },
  "hide":   { "enabled": false },
  "power":  { "profile": "Balanced|LowImpact|StrictSavings|null" },
  "enabled": true,
  "createdUtc": "iso8601",
  "updatedUtc": "iso8601"
}
```

```jsonc
// AuthConfig
{ "method": "WindowsHello|Pin|Password",
  "fallbackConfigured": true,
  "hash": { "algo": "Argon2id|PBKDF2", "salt": "base64", "params": {}, "digest": "base64" },
  "failCount": 0, "lockedUntilUtc": "iso8601|null" }
```

```jsonc
// AuditEntry
{ "tsUtc": "iso8601", "actor": "user|service|agent",
  "action": "lock|unlock|hide|reveal|applyPower|removePower|authFail|configChange",
  "targetRuleId": "guid|null", "outcome": "success|failure|blocked", "detail": "string" }
```

## 9.3 Retention
Audit log rotates at a configurable size (default 5 MB, keep 3 files). No PII beyond app identities and local usernames.

---

# 10. IPC Protocol Specification

## 10.1 Transport
- Windows Named Pipes, message mode, one pipe per server role: `\\.\pipe\GuardianSvc` (service), `\\.\pipe\GuardianAgent` (agent).
- ACL: allow interactive user SID + LocalSystem; deny others (NFR-S2).

## 10.2 Envelope
```jsonc
{
  "v": 1,
  "id": "guid",              // correlation id
  "type": "request|response|event",
  "op": "GetPolicy|SetRule|ApplyPower|ShowLock|MoveToHidden|Heartbeat|...",
  "payload": { },            // op-specific DTO from AppGuardian.Shared
  "ts": "iso8601"
}
```

## 10.3 Rules
- Every `request` receives a `response` with the same `id` or a timeout error (NFR-P5: p95 ≤ 100 ms).
- Unauthorized ops (e.g., rule change without an active auth session) return `{ "error": "unauthorized" }`.
- `event` messages (heartbeat, process-start, status change) are one-way, best-effort.
- Unknown `op` or `v` mismatch → `{ "error": "unsupported" }`; never crash the peer.

---

# 11. Security & Threat Model (STRIDE)

| Threat | Category | Vector | Mitigation |
|---|---|---|---|
| Impersonating the service pipe | Spoofing | Rogue process opens a named pipe | Server-side pipe ACL + verify client token is interactive user/LocalSystem. |
| Editing `policy.json` to unprotect apps | Tampering | Local file write | ProgramData ACL (admin-only write) + integrity hash; service rejects mismatches (NFR-S3). |
| Denying an audit action | Repudiation | User claims no unlock | Append-only, timestamped audit log (FR-803). |
| Reading stored credentials | Info Disclosure | Reading credential file | Only salted hashes stored (FR-303/304); no reversible secret. |
| Killing service/agent to bypass | DoS / bypass | Task Manager terminate | Fail-safe: protected apps stay locked/hidden when enforcers are down (NFR-S4); watchdog restart (NFR-R1/R2). |
| Elevating via AppGuardian | Elevation of Privilege | Malicious IPC to LocalSystem service | Strict op allow-list, input validation, no shell/dynamic exec (NFR-S5). |

**Documented residual risk:** a local administrator can always disable protection (uninstall, stop service, edit ACLs). AppGuardian is a privacy/convenience tool, **not** an anti-tamper or anti-admin security boundary; this is disclosed to users.

---

# 12. Permissions and OS Integration

## 12.1 Required Privileges
Administrator for installation; service registration; process inspection/control for selected apps; startup registration for the agent.

## 12.2 Windows APIs Expected
Process enumeration; WinEvent/window event hooks; foreground window detection; Job Objects (CPU rate control); power throttling / EcoQoS (`SetProcessInformation`); Windows Hello (`KeyCredentialManager`, `UserConsentVerifier`); Virtual Desktop COM interfaces or wrapper (behind versioned adapter, CN-2).

---

# 13. Risks and Mitigations

| # | Risk | Impact | Likelihood | Mitigation |
|---|---|---|---|---|
| 1 | Virtual Desktop API instability across builds | High | High | Versioned abstraction layer; per-build adapters; graceful fallback; feature-detect at runtime. |
| 2 | Service cannot show UI (Session 0) | High | Certain | Dedicated user-session agent owns all UI/overlay. |
| 3 | Some apps cannot be locked/hidden | Medium | Medium | Document limits; exclude unsupported apps gracefully (FR-509). |
| 4 | AV flags the utility | Medium | Medium | No stealth/kernel tricks; clear identity; code-sign (FR-106). |
| 5 | One-month timeline is tight | High | High | Strict MVP scope; no backend/licensing/telemetry; priority tiers (M/S/C). |
| 6 | Overlay bypass via exclusive fullscreen/anti-cheat | Medium | Medium | Fallback to suspend/minimize; disclose limitation. |
| 7 | Data loss on power failure | Low | Low | Atomic writes (NFR-R4). |

---

# 14. Verification & Acceptance Test Cases

| TC | Covers | Steps | Expected |
|---|---|---|---|
| TC-01 | FR-100/101/102 | Run installer as non-admin, then as admin. | UAC prompt; after install, service `RUNNING` and agent auto-starts at logon. |
| TC-02 | FR-300/301/302 | First run; configure Hello (if present) + fallback PIN. | Setup mandatory; both methods verified; app usable after. |
| TC-03 | FR-303/304 | Inspect `credentials.dat`. | Only salted hash present; no reversible secret. |
| TC-04 | FR-306 | Enter wrong PIN 5×. | Backoff enforced; auth blocked for configured window. |
| TC-05 | FR-500/501/504 | Lock Notepad; launch and focus it. | Overlay appears ≤ 1 s; input blocked until auth. |
| TC-06 | FR-505/305 | Unlock temporarily (5 min); refocus within window. | No re-prompt within timeout; re-prompt after expiry. |
| TC-07 | FR-507 | Lock app on secondary monitor. | Overlay renders on correct monitor. |
| TC-08 | FR-600/602/603 | Hide an app; launch it. | Window moved to `GuardianHidden`; absent from main desktop. |
| TC-09 | FR-604/606 | Reveal hidden app from dashboard after auth. | Window returns to main desktop. |
| TC-10 | FR-700/701/706 | Apply Strict Savings to a running CPU-heavy app. | CPU share drops per Job Object cap without restarting app. |
| TC-11 | FR-605/705/R3 | Set lock+hide+power rules; reboot. | All rules re-applied after reboot. |
| TC-12 | FR-804/805 | Stop service manually. | Dashboard shows service down; NFR-S4 fail-safe holds. |
| TC-13 | FR-103 | Uninstall, choose to remove data. | Service gone, no autostart, files+data removed. |
| TC-14 | FR-607/707 | Open hide and power pages. | Best-effort limitation disclosures visible. |
| TC-15 | NFR-P4/P5 | Measure lock latency and IPC round-trip. | ≤ 1 s lock; ≤ 100 ms p95 IPC. |

---

# 15. Requirements Traceability Matrix

| Requirement group | Acceptance §17 item | Test case(s) |
|---|---|---|
| FR-100..106 | 1, 11 | TC-01, TC-13 |
| FR-300..308 | 2 | TC-02, TC-03, TC-04 |
| FR-400..406 | 3 | (covered by lock/hide selection TCs) |
| FR-500..509 | 3, 4 | TC-05, TC-06, TC-07 |
| FR-600..608 | 5, 6, 7 | TC-08, TC-09, TC-14 |
| FR-700..708 | 8 | TC-10, TC-14 |
| FR-800..807 | 9 | TC-12 |
| Rules persistence (FR-605/705) | 10 | TC-11 |
| NFRs (P/S/R) | — | TC-12, TC-15 |

---

# 16. Delivery Plan & Milestones (~4 weeks)

| Week | Milestone | Key deliverables |
|---|---|---|
| 1 | Foundation | Solution skeleton (UI/Agent/Service/Shared), IPC over Named Pipes (§10), policy store (§9), installer stub. |
| 2 | Auth + Lock | First-run setup, Hello + PIN fallback, credential hashing, lock flow + overlay (multi-monitor). |
| 3 | Hide + Power | Virtual desktop adapter + hide flow, Job Object throttling + profiles, dashboard status. |
| 4 | Harden + Ship | Audit log, settings, repair/uninstall, disclosures, test pass (§14), docs, code-sign, MVP build. |

---

# 17. Acceptance Criteria

The MVP is accepted when:
1. Installer installs app, agent, and service successfully.
2. User can configure Windows Hello and a fallback PIN/password.
3. User can select an app and lock it.
4. A locked app requires authentication to access.
5. User can hide an app into a hidden virtual desktop/workspace.
6. Hidden app is not visible on the main desktop under normal conditions.
7. User can reveal hidden apps from the dashboard.
8. User can apply CPU/battery restriction profiles to selected running apps.
9. Service and agent status are visible in the dashboard.
10. Rules persist after reboot.
11. Uninstaller removes app components cleanly.

All `Must` (M) requirements pass their mapped test cases (§15) and all measurable NFRs (§7) meet their thresholds.

---

# 18. Definition of Done

The project is complete when:
- All MVP `Must` functional requirements are implemented and pass §14 tests.
- App works on Windows 10 22H2 and Windows 11 23H2+ test machines.
- Installer and uninstaller work correctly, including repair.
- No known crash bugs in core flows; fail-safe behavior verified.
- README, `/docs`, build instructions, and limitation disclosures complete.
- Source is clean, modular, and public-ready under MIT.
- No license system or telemetry present; no required network calls.
- All measurable NFR thresholds met.
- Hafiz approves the final MVP build.

---

# Appendix A: Glossary & Acronyms

| Term | Meaning |
|---|---|
| AUMID | Application User Model ID — identifies an app for shell/taskbar. |
| EcoQoS | Energy-efficient Quality of Service scheduling hint. |
| IPC | Inter-Process Communication. |
| Job Object | Win32 kernel object grouping processes for resource limits (e.g., CPU rate). |
| LocalSystem | Highest-privilege built-in service account. |
| MVP | Minimum Viable Product. |
| PFN | Package Family Name (MSIX/UWP identity). |
| Session 0 | Isolated non-interactive session where services run (no user UI). |
| STRIDE | Threat categories: Spoofing, Tampering, Repudiation, Info disclosure, DoS, Elevation. |
| Virtual Desktop | Windows workspace feature used to host the hidden `GuardianHidden` desktop. |
| Windows Hello | Windows biometric/PIN authentication framework. |

# Appendix B: Requirement Conventions
- **Priority:** M (Must / MVP-blocking), S (Should), C (Could).
- **Verification:** T (Test), D (Demonstration), I (Inspection), A (Analysis).
- Requirement IDs are stable; deprecated IDs are never reused.
- "shall" = mandatory, "should" = recommended, "may" = optional.

# Appendix C: Open Questions
1. Which credential KDF is preferred for the reference build — Argon2id (memory-hard) or PBKDF2 (FIPS-friendly)? (Default: Argon2id.)
2. Should temporary unlock persist across app restart within the timeout window, or reset on process exit?
3. Is code-signing certificate procurement in budget for the MVP (affects Risk 4)?
4. Preferred Virtual Desktop approach: undocumented COM interfaces vs. a maintained third-party wrapper (licensing must remain MIT-compatible)?

> **Note on sourcing:** The referenced Qwen chat (`chat.qwen.ai/s/cc796123-...`) could not be retrieved — that host is blocked by this environment's network allowlist, so no external content could be pulled in. This enhancement is derived solely from the SRS you provided plus Windows platform domain knowledge. If that conversation contains additional decisions (e.g., answers to the open questions above), paste the relevant text and I will fold it in.
