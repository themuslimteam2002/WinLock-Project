# Project Plan

## AppGuardian for Windows

| Field | Value |
|---|---|
| **Project Name** | AppGuardian for Windows |
| **Document Version** | 0.2.0 |
| **Document Status** | Baselined plan for AI-assisted execution |
| **Project Lead** | Hafiz |
| **Timeline** | 30 days / 4 weeks (with a 2-day buffer, see §1.3) |
| **Companion Docs** | `docs/SRS.md` (v0.2.0), `docs/API_DESIGN.md`, `docs/DECISIONS.md` |
| **Date** | 2026-08-24 |
| **Supersedes** | Project Plan v0.1.0 |

### Development Approach
AI-assisted agile development using **Claude Code** (implementation, debugging, tests) and **Antigravity** (UI/UX, design, docs, packaging). Work proceeds in one-week iterations, each ending in a demonstrable, testable increment mapped to SRS requirement IDs.

### Primary Goal
Deliver a stable Windows MVP in one month with app lock, hidden virtual-desktop app hiding, battery/CPU restriction, an admin installer, and an open-source local-first architecture — with every `Must` (M) requirement in the SRS passing its mapped test case.

---

## Revision History

| Version | Date | Author | Summary |
|---|---|---|---|
| 0.1.0 | 2026-08-xx | Hafiz | Initial project plan. |
| 0.2.0 | 2026-08-24 | Hafiz | Industrial enhancement: SRS traceability on every task, exit criteria as measurable gates, effort estimates & critical path, dependency ordering, RACI, per-week risk burn-down, definition-of-ready/done, branching & CI conventions, rollback plan, buffer/contingency, and a consolidated tracking checklist. |

---

# 0. How to Read This Plan

- Tasks are tagged with the SRS requirement(s) they satisfy, e.g. `[FR-101]`, so implementation and verification stay traceable.
- Each milestone has **entry (Definition of Ready)** and **exit (Definition of Done)** gates. A milestone is not "done" until its exit gate is objectively met.
- Priority tiers mirror the SRS: **M** = Must (MVP-blocking), **S** = Should, **C** = Could. When time is short, cut C then S; never cut M.
- Effort is a rough estimate in ideal engineering days (`d`) to expose the critical path, not a commitment.

---

# 1. Milestones

## 1.1 Milestone Overview

| ID | Milestone | Target | Primary SRS coverage | Gate type |
|---|---|---|---|---|
| M1 | Foundation Complete | End of Week 1 | FR-200..206, FR-203 IPC, §9 store, FR-100/101 skeleton | Roundtrip demo |
| M2 | Authentication + App Lock Core | End of Week 2 | FR-300..308, FR-400..405, FR-500..508 | Lock/unlock demo |
| M3 | Hidden Desktop + Battery Control | End of Week 3 | FR-600..608, FR-700..708, FR-800/804/805 | Hide+throttle demo |
| M4 | Stabilization and Release | End of Week 4 | FR-103/104/106, FR-801..807, all NFRs | Clean-machine install + test pass |

## 1.2 Milestone Detail

### M1 — Foundation Complete (End of Week 1)
Deliverables: repository initialized; solution structure; `/docs` integrated; basic WPF shell; service skeleton; agent skeleton; Named-Pipe IPC skeleton working; installer skeleton; atomic policy store scaffolding (§9).
**Exit gate (all must hold):** UI launches; service installs and reports `RUNNING`; agent starts; an IPC request→response roundtrip completes end-to-end (UI→service→UI) with the §10 envelope.

### M2 — Authentication + App Lock Core (End of Week 2)
Deliverables: first-run onboarding; Windows Hello check + verification; PIN/password fallback with salted hashing; local unlock-session state; installed & running app lists; app lock rule creation UI; foreground detection; lock overlay; temporary-unlock timeout; lock event logging.
**Exit gate:** user configures auth; a selected app can be locked; overlay appears on launch/focus ≤ 1 s; unlock works via Hello or PIN/password; failed attempts are rate-limited.

### M3 — Hidden Desktop + Battery Control (End of Week 3)
Deliverables: virtual-desktop abstraction layer (versioned adapter, CN-2); hidden workspace creation; move-to-hidden behavior; hide rule model; hide-on-launch; reveal/unhide flow; hidden app list UI; power profiles; Job Object CPU throttling; priority reduction; EcoQoS where available; runtime apply/remove; dashboard hidden/power status.
**Exit gate:** selected apps move into the hidden workspace and can be revealed; selected running apps are throttled per profile without restart; dashboard reflects state.

### M4 — Stabilization and Release (End of Week 4)
Deliverables: high-priority bug fixes; health indicators; error handling; reboot-persistence validation; install/uninstall/repair validation; Win10/Win11 testing; multi-monitor/DPI testing; edge-case testing; README; build instructions; release notes; troubleshooting guide; signed final installer; public repo structure.
**Exit gate:** installer works from a clean machine; all MVP (M) features usable and passing §14 SRS test cases; no critical crashes; all measurable NFR thresholds met; Hafiz approves.

## 1.3 Buffer & Contingency
Days 29–30 are reserved as a **hardening buffer** — no new features, only defect burn-down and release prep. If M2 or M3 slips, the first response is to drop `Could` items, then `Should` items, per §4.3; `Must` scope is protected.

---

# 2. Weekly Execution Plan

Legend: `[FR-xxx]` = SRS trace, `~Nd` = est. ideal days, `(M/S/C)` = priority, `★` = on critical path.

## Week 1 — Project Foundation
**Objective:** Stand up the base architecture and communication layer.

| Task | Trace | Pri | Est |
|---|---|---|---|
| ★ Create Git repository + branch protection on `main` | — | M | ~0.25d |
| Add `/docs` (SRS, Project Plan, API Design, DECISIONS) | — | M | ~0.25d |
| ★ Create .NET 8 solution + projects (UI, Agent, Service, Shared) | FR-200..202, 206 | M | ~0.5d |
| Set up MVVM structure for WPF UI | FR-200 | M | ~0.5d |
| Basic dashboard shell (nav, empty states) | FR-800 | M | ~0.5d |
| ★ Named-Pipe IPC server/client skeleton + envelope | FR-203 | M | ~1d |
| Shared DTO/message contracts in `AppGuardian.Shared` | FR-206 | M | ~0.5d |
| ★ Local policy/data store layer (atomic writes) | §9, NFR-R4 | M | ~0.75d |
| Windows Service skeleton (SCM lifecycle, heartbeat) | FR-202, 205 | M | ~0.75d |
| User-session agent skeleton (autostart) | FR-201, 102 | M | ~0.5d |
| Inno Setup installer skeleton | FR-100, 101 | M | ~0.5d |
| ★ Validate service install/uninstall locally (VM) | FR-101, 103 | M | ~0.5d |

**Exit criteria:** UI launches; service installs; agent starts; IPC roundtrip works (matches M1 gate).

## Week 2 — Authentication and App Lock
**Objective:** Secure unlock system + core app lock.

| Task | Trace | Pri | Est |
|---|---|---|---|
| First-run onboarding (mandatory setup) | FR-300 | M | ~0.5d |
| Windows Hello availability check | FR-301 | M | ~0.25d |
| ★ Windows Hello verification flow | FR-301 | M | ~0.75d |
| ★ Fallback PIN/password setup | FR-302 | M | ~0.5d |
| ★ Secure hashing (Argon2id/PBKDF2, salted) | FR-303/304, NFR-S1 | M | ~0.5d |
| Local session unlock state + timeout | FR-305, 308 | M | ~0.5d |
| Failed-attempt rate limiting/backoff | FR-306 | M | ~0.25d |
| Installed app listing | FR-400, 404 | M | ~0.75d |
| Running process listing | FR-401 | M | ~0.5d |
| App identity resolution (path/PFN/AUMID/hash) | FR-402 | M | ~0.5d |
| Manual add via file picker | FR-403 | M | ~0.25d |
| App rule selection UI | FR-500, 801 | M | ~0.75d |
| ★ Foreground app detection (WinEvent hooks) | FR-501 | M | ~0.75d |
| ★ Lock trigger logic + per-app state | FR-501, 506 | M | ~0.5d |
| ★ Lock overlay window (top-most, multi-monitor) | FR-502/503/507 | M | ~1d |
| Lock event logging | FR-508, 803 | M | ~0.25d |

**Exit criteria:** matches M2 gate.

## Week 3 — App Hiding and Battery Restriction
**Objective:** Hidden desktop workspace + power engine.

| Task | Trace | Pri | Est |
|---|---|---|---|
| ★ Virtual desktop abstraction layer (versioned) | FR-600, CN-2, Risk 1 | M | ~1d |
| ★ Hidden workspace creation (`GuardianHidden`) | FR-600 | M | ~0.5d |
| ★ Move-to-hidden-desktop behavior | FR-602 | M | ~0.75d |
| Hide app rule model + persistence | FR-601, 605 | M | ~0.5d |
| Hide-on-launch behavior + re-apply on restart | FR-602, 608 | M | ~0.5d |
| Reveal/unhide + return-to-main flow | FR-604, 606 | M | ~0.5d |
| Hidden app list UI + disclosure | FR-607 | M | ~0.5d |
| Power/battery profiles (Balanced/Low/Strict) | FR-700 | M | ~0.5d |
| ★ Job Object CPU throttling | FR-701, 706 | M | ~1d |
| Priority reduction option | FR-702 | S | ~0.25d |
| EcoQoS support where available | FR-703 | S | ~0.5d |
| Running-app restriction controls + disclosure | FR-704/707 | M | ~0.5d |
| Dashboard hidden/power status | FR-800, 804, 805 | M | ~0.5d |

**Exit criteria:** matches M3 gate.

## Week 4 — Stabilization, Packaging, and Release
**Objective:** Ship a stable MVP.

| Task | Trace | Pri | Est |
|---|---|---|---|
| Fix high-priority bugs | — | M | ~1.5d |
| Service/agent health indicators | FR-804/805, NFR-R1/R2 | M | ~0.5d |
| Error handling + fail-safe behavior | NFR-S4 | M | ~0.5d |
| ★ Reboot persistence validation | FR-605/705, NFR-R3 | M | ~0.5d |
| ★ Install/uninstall/repair validation | FR-103/104 | M | ~0.5d |
| Test on Windows 10 22H2 | NFR-Compat | M | ~0.5d |
| Test on Windows 11 23H2+ | NFR-Compat | M | ~0.5d |
| Multi-monitor + DPI scaling tests | FR-507, NFR-Compat | M | ~0.5d |
| Locked-app edge cases (fullscreen, admin, UWP) | FR-509 | M | ~0.5d |
| Hidden desktop fallback behavior | Risk 1/6 | M | ~0.5d |
| README, build instructions, release notes, troubleshooting | FR-DoD | M | ~0.75d |
| Code-sign + produce final installer | FR-106 | S | ~0.5d |
| Prepare public repo structure (MIT LICENSE) | §7.8 | M | ~0.25d |

**Exit criteria:** matches M4 gate.

---

# 3. Task Ownership Model (RACI)

| Activity | Hafiz | Claude Code | Antigravity |
|---|---|---|---|
| Product decisions / scope | A/R | C | I |
| Final acceptance | A/R | I | I |
| Implementation & debugging | I | A/R | C |
| Test authoring & execution | I | A/R | I |
| UI/UX flow & design | C | C | A/R |
| Documentation | A | R | R |
| Installer / packaging | I | R | C |

R=Responsible, A=Accountable, C=Consulted, I=Informed.

---

# 4. Development Rules

## 4.1 Documentation-First
All major implementation follows `docs/SRS.md`, `docs/PROJECT_PLAN.md`, `docs/API_DESIGN.md`. New architectural decisions are recorded in `docs/DECISIONS.md` (ADR format: context, decision, consequences).

## 4.2 Incremental Build Order (dependency-correct)
1. Shared contracts → 2. IPC → 3. service/agent skeleton → 4. auth → 5. app lock → 6. hide desktop → 7. power control → 8. installer polish. Each stage must be demonstrable before the next begins.

## 4.3 No Scope Expansion
During the 30-day MVP, do **not** add cloud sync, telemetry, licensing, plugin systems, theme marketplace, or enterprise controls. Descoping order under pressure: cut `Could`, then `Should`; protect all `Must`.

## 4.4 Definition of Ready (per task)
A task may start only when: its SRS trace is identified, dependencies are merged, and its exit condition is stated. 

## 4.5 Definition of Done (per task)
Code merged to a feature branch; unit tests (where applicable) pass; the mapped SRS acceptance/test case passes; checklist (§9) updated; no new `Must` regressions.

## 4.6 AI / Version-Control Workflow
- Each major feature on its own branch (`feat/lock-overlay`, `feat/virtual-desktop`, …); PR into `main`.
- CI on every PR: build all four projects, run unit tests, run analyzers/format check.
- `main` is always buildable; releases are tagged (`v0.x.y`).
- Claude Code must not silently change scope — scope changes go through Hafiz and `DECISIONS.md`.
- Each completed task updates the §9 checklist.

---

# 5. Testing Plan

## 5.1 Functional Tests
Install/uninstall/repair; service start/stop; agent startup; auth setup; lock/unlock; hide/reveal; throttle apply/remove; reboot persistence. (Maps to SRS §14 TC-01..TC-15.)

## 5.2 OS Compatibility Tests
Windows 10 22H2; Windows 11 23H2+; x64 only.

## 5.3 UI Tests
Dark/light theme; multi-monitor; high DPI (PMv2); long app names; empty states; error states.

## 5.4 Security Tests
No plain-text PIN/password (inspect store); unauthorized IPC rejected (NFR-S2); fail-safe when service stopped (NFR-S4); failed-unlock throttling (FR-306).

## 5.5 Edge Case Tests
Target already running; target runs as admin (integrity mismatch); target is UWP/packaged; app updated and path changed (identity re-match); hidden-desktop API unavailable (fallback); service/agent stopped unexpectedly (watchdog recovery).

## 5.6 Test Tracking
Each test case records: build tag, OS, pass/fail, defect link. A milestone gate requires zero open `Critical`/`High` defects on its `Must` scope.

---

# 6. Risk Management

| Risk | Impact | Likelihood | Owner | Mitigation & trigger |
|---|---|---|---|---|
| Virtual Desktop implementation breaks | High | High | Claude Code | Isolate behind versioned interface; fallback mode; feature-detect at runtime. **Trigger:** spike in Week 3 day 1; if adapter unstable by day 3, ship reduced hide (minimize/off-screen) and disclose. |
| Service/UI complexity exceeds timeline | High | Medium | Hafiz | Cut C→S polish; protect Must. **Trigger:** M2 gate missed by >1 day. |
| Some apps cannot be controlled | Medium | Medium | Claude Code | Mark unsupported in UI + log reason (FR-509). |
| Installer issues | Medium | Medium | Antigravity | Test install/uninstall on clean VM by **Week 2**, not Week 4. |
| Windows Hello unavailable on test HW | Low | Medium | Claude Code | PIN/password fallback is mandatory path; test both. |
| AV/SmartScreen flags binaries | Medium | Medium | Hafiz | No stealth/kernel tricks; code-sign (FR-106); clear identity. |

**Rollback plan:** every release is a tagged build with its installer archived; if a release regresses, revert to the previous tag's installer.

---

# 7. Release Deliverables

- [ ] Source code repository (public-ready)
- [ ] README.md
- [ ] LICENSE (MIT)
- [ ] `/docs` folder (SRS, Plan, API, DECISIONS)
- [ ] Signed installer executable
- [ ] Build instructions (reproducible)
- [ ] Release notes
- [ ] Troubleshooting guide

---

# 8. MVP Success Definition

The MVP is successful when it: installs cleanly with admin rights; runs as a Windows Service + user-session agent; locks apps via Windows Hello or fallback PIN/password; hides apps into a hidden virtual desktop/workspace; applies basic CPU/battery restriction; remains stable for daily use; is ready for open-source publication — **and** all `Must` SRS requirements pass their mapped test cases with NFR thresholds met.

---

# 9. Consolidated Tracking Checklist

**M1** ☐ repo ☐ solution ☐ docs ☐ WPF shell ☐ service skel ☐ agent skel ☐ IPC roundtrip ☐ store ☐ installer skel
**M2** ☐ onboarding ☐ Hello ☐ PIN fallback ☐ hashing ☐ unlock session ☐ installed list ☐ running list ☐ rule UI ☐ foreground detect ☐ overlay ☐ timeout ☐ logging
**M3** ☐ VD adapter ☐ hidden workspace ☐ move-to-hidden ☐ hide rules ☐ reveal ☐ hidden UI ☐ profiles ☐ Job Object throttle ☐ priority ☐ EcoQoS ☐ dashboard status
**M4** ☐ bug fixes ☐ health indicators ☐ error handling ☐ reboot persist ☐ install/uninstall/repair ☐ Win10 ☐ Win11 ☐ multi-mon/DPI ☐ edge cases ☐ README ☐ build docs ☐ release notes ☐ signed installer ☐ Hafiz approval

---

> **Note on sourcing:** The referenced Qwen chat (`chat.qwen.ai/s/01cc1855-...`) could not be retrieved — that host is blocked by this environment's network allowlist, so no external content could be pulled in. This enhancement is derived from the plan you provided and the enhanced SRS v0.2.0. If that conversation contains additional decisions (owners, dates, tool choices), paste the relevant text and I will integrate it.
