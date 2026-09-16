# Compatibility Test Matrix — AppGuardian for Windows v0.1.0

## Overview

This matrix defines the environments that must be validated before release. Each cell should be marked with:
- ✅ **Pass** — all QA checklist items passed
- ⚠️ **Partial** — passes with known limitations noted
- ❌ **Fail** — blocking issues found
- ⬜ **Not Tested** — not yet validated

---

## 1. Operating System Matrix

| Test Case | Win 10 22H2 (19045) | Win 11 23H2 (22631) | Win 11 24H2+ |
|---|---|---|---|
| **Installer: Clean install** | ⬜ | ⬜ | ⬜ |
| **Installer: Silent install** | ⬜ | ⬜ | ⬜ |
| **Installer: Upgrade** | ⬜ | ⬜ | ⬜ |
| **Installer: Uninstall** | ⬜ | ⬜ | ⬜ |
| **Service: Register + start** | ⬜ | ⬜ | ⬜ |
| **Service: Auto-restart on crash** | ⬜ | ⬜ | ⬜ |
| **Agent: Auto-start at logon** | ⬜ | ⬜ | ⬜ |
| **Auth: Windows Hello** | ⬜ | ⬜ | ⬜ |
| **Auth: PIN/password** | ⬜ | ⬜ | ⬜ |
| **Lock: Overlay on focus** | ⬜ | ⬜ | ⬜ |
| **Lock: Multi-monitor** | ⬜ | ⬜ | ⬜ |
| **Lock: Persist across reboot** | ⬜ | ⬜ | ⬜ |
| **Hide: Virtual desktop move** | ⬜ | ⬜ | ⬜ |
| **Hide: VD fallback** | ⬜ | ⬜ | ⬜ |
| **Hide: Persist across reboot** | ⬜ | ⬜ | ⬜ |
| **Power: Job Object throttle** | ⬜ | ⬜ | ⬜ |
| **Power: EcoQoS** | ⬜ | ⬜ | ⬜ |
| **UI: Dark theme** | ⬜ | ⬜ | ⬜ |
| **UI: Light theme** | ⬜ | ⬜ | ⬜ |
| **UI: System theme follow** | ⬜ | ⬜ | ⬜ |

### OS-Specific Notes

| OS | Known Considerations |
|---|---|
| Win 10 22H2 | Virtual Desktop COM interfaces differ from Win 11. Adapter must detect build. EcoQoS not available. |
| Win 11 23H2 | Primary target. All features expected to work. |
| Win 11 24H2+ | Virtual Desktop API may have changed. Adapter must handle gracefully. |

---

## 2. Hardware Matrix

| Test Case | Laptop (battery) | Desktop (no battery) | Multi-monitor | High-DPI (≥150%) |
|---|---|---|---|---|
| **Install + service start** | ⬜ | ⬜ | ⬜ | ⬜ |
| **Dashboard renders correctly** | ⬜ | ⬜ | ⬜ | ⬜ |
| **Lock overlay positioning** | ⬜ | ⬜ | ⬜ | ⬜ |
| **Power profile apply** | ⬜ | ⬜ | ⬜ | ⬜ |
| **Power profile with battery** | ⬜ | N/A | ⬜ | ⬜ |
| **DPI scaling (100%)** | ⬜ | ⬜ | ⬜ | N/A |
| **DPI scaling (125%)** | ⬜ | ⬜ | ⬜ | ⬜ |
| **DPI scaling (150%)** | ⬜ | ⬜ | ⬜ | ⬜ |
| **DPI scaling (200%)** | ⬜ | ⬜ | ⬜ | ⬜ |
| **Mixed DPI (monitor 1 ≠ monitor 2)** | N/A | N/A | ⬜ | ⬜ |

### Hardware-Specific Notes

| Config | Known Considerations |
|---|---|
| Laptop with battery | Power profiles should show measurable CPU reduction in Task Manager |
| Desktop without battery | Power profiles still apply CPU throttling; battery-specific UX may be irrelevant |
| Multi-monitor | Lock overlay must appear on the correct monitor; DPI may differ per display |
| High DPI | PMv2 manifest ensures per-monitor scaling; verify text and controls aren't clipped |

---

## 3. User Scenario Matrix

| Test Case | Admin User | Standard User (admin install) | Service Stopped Manually | Agent Killed Manually |
|---|---|---|---|---|
| **Dashboard opens** | ⬜ | ⬜ | ⬜ | ⬜ |
| **Create lock rule** | ⬜ | ⬜ | ⬜ (expect error) | ⬜ |
| **Lock overlay appears** | ⬜ | ⬜ | ⬜ | ⬜ (expect no overlay) |
| **Unlock works** | ⬜ | ⬜ | ⬜ | ⬜ |
| **Hide app** | ⬜ | ⬜ | ⬜ (expect error) | ⬜ |
| **Power profile apply** | ⬜ | ⬜ | ⬜ (expect error) | ⬜ |
| **Warning banner appears** | N/A | N/A | ⬜ (must appear) | ⬜ (must appear) |
| **Health page shows "Stopped"** | N/A | N/A | ⬜ (must show) | ⬜ (must show) |
| **Agent auto-restarts (watchdog)** | N/A | N/A | N/A | ⬜ |

### Scenario-Specific Notes

| Scenario | Expected Behavior |
|---|---|
| Service stopped manually | Dashboard shows warning banner. New rules cannot be created. Existing lock overlays may not trigger. |
| Agent killed manually | Service watchdog should detect and restart the agent. Lock overlays stop appearing until agent restarts. |
| Standard user after admin install | All features should work. The service runs as SYSTEM, the agent and UI run as the interactive user. |

---

## 4. Windows Hello Availability Matrix

| Hello Hardware | Auth Setup | Unlock Flow | Fallback |
|---|---|---|---|
| Fingerprint available | ⬜ Hello toggle enabled | ⬜ Fingerprint prompt appears | ⬜ PIN fallback works |
| Face recognition available | ⬜ Hello toggle enabled | ⬜ Face prompt appears | ⬜ PIN fallback works |
| No biometric hardware | ⬜ Hello toggle disabled + info notice | ⬜ PIN-only UI | ⬜ N/A |
| Hello configured then removed | ⬜ Detection updates on next launch | ⬜ Falls back to PIN | ⬜ PIN works |

---

## 5. .NET Runtime Matrix

| Scenario | Expected Behavior | Result |
|---|---|---|
| .NET 8 Desktop Runtime installed | Installer proceeds normally | ⬜ |
| .NET 8 Desktop Runtime NOT installed | Installer shows download prompt | ⬜ |
| .NET 8 SDK installed (no separate runtime) | Runtime included in SDK, installer proceeds | ⬜ |
| .NET 9+ installed (no .NET 8) | Installer checks for 8.x specifically | ⬜ |

---

## 6. Target Applications Matrix

Test locking/hiding/throttling with these representative apps:

| App Type | Lock | Hide | Power | Notes |
|---|---|---|---|---|
| Standard Win32 app (e.g., Notepad) | ⬜ | ⬜ | ⬜ | Baseline case |
| Electron app (e.g., VS Code) | ⬜ | ⬜ | ⬜ | Multi-process |
| UWP/MSIX app (e.g., Calculator) | ⬜ | ⬜ | ⬜ | Different process model |
| Browser (e.g., Chrome, Edge) | ⬜ | ⬜ | ⬜ | Many child processes |
| Game (non-fullscreen) | ⬜ | ⬜ | ⬜ | |
| Game (exclusive fullscreen) | ⬜ | ⬜ | ⬜ | Expected: overlay may not cover |
| Elevated app (Run as Admin) | ⬜ | ⬜ | ⬜ | Expected: may resist |
| System process (e.g., explorer.exe) | ⬜ | ⬜ | ⬜ | Expected: should warn/refuse |

---

## Sign-Off

| Environment | Tester | Date | Overall Status |
|---|---|---|---|
| Win 10 22H2 | | | ⬜ |
| Win 11 23H2 | | | ⬜ |
| Win 11 24H2+ | | | ⬜ |
| Multi-monitor | | | ⬜ |
| High DPI | | | ⬜ |
