# Specification Conflict Register

| Field | Value |
|---|---|
| **Project** | AppGuardian for Windows |
| **Date** | 2026-08-24 |
| **Reviewed documents** | `docs/SRS.md` v0.2.0, `docs/API_DESIGN.md` v0.2.0, `docs/PROJECT_PLAN.md` v0.2.0 |
| **Status** | Open — awaiting rulings from Hafiz |

Every conflict below has a recommendation, but none has been resolved unilaterally. Where the
implementation had to pick something in order to compile, the column **Provisional code choice**
records what the scaffold currently does so a ruling can be applied as a mechanical change.
Each conflict is numbered `SC-nn` and referenced from `docs/DECISIONS.md`.

---

## SC-01 — Named Pipe names disagree (~~blocking~~ **RESOLVED 2026-08-25**)

> **Ruling (owner, 2026-08-25).** The Service's names win: `\\.\pipe\appguardian.service.v1` and
> `\\.\pipe\appguardian.agent.v1`. The SRS names are dead. Recorded as ADR-014. No code change was
> needed — `PipeNames` already held exactly these two constants and every call site reads them — so the
> only edit was removing the "awaiting a ruling" note from `PipeNames.cs`. SRS §8.1 and §10.1 are now
> known-wrong documentation to fix in the next spec revision.

| Source | Value |
|---|---|
| SRS §8.1 diagram | `\GuardianUI`, `\GuardianCtl`, `\GuardianSvc` — **three** pipes |
| SRS §10.1 | `\\.\pipe\GuardianSvc`, `\\.\pipe\GuardianAgent` — **two** pipes |
| API Design §2.1 | `\\.\pipe\appguardian.service.v1`, `\\.\pipe\appguardian.agent.v1` — **two** pipes |

Three different naming schemes across two documents, and the SRS contradicts itself internally:
§8.1 shows a dedicated UI↔Agent pipe (`\GuardianUI`) plus a separate UI→Service control pipe
(`\GuardianCtl`), while §10.1 describes only a service pipe and an agent pipe. The API Design
message catalog (§4.4 `hide.reveal` is UI→Agent) requires the UI to reach the agent directly, which
is consistent with §10.1's two-pipe model — the UI just connects to the agent's pipe rather than
having a private one.

**Recommendation:** adopt the API Design names. The `.v1` suffix is load-bearing — API Design §7
defines transport versioning as a change to the pipe name so old and new pipes can coexist during
migration, which the SRS names cannot express. Correct SRS §8.1 and §10.1 to match.

**Provisional code choice:** `appguardian.service.v1` / `appguardian.agent.v1`, defined as the only
two constants in `PipeNames`. This is now the ruled choice, not a provisional one.

**Related provisional choice — the dashboard holds one pipe, not two.** The catalog's "UI→Agent"
direction for `hide.reveal` (§4.4) would require the dashboard to open a second connection, to the
agent. It does not: `GuardianClient` connects only to `appguardian.service.v1`, and the service
forwards `hide.reveal` to the agent through `AgentBridge`. The reason is lifetime — the agent restarts
with the interactive session, so a dashboard that dialled it directly would need its own reconnect
loop for the one call that is the user's recovery path out of a hidden window. `auth.verifyWindowsHello`
and `hide.ensureWorkspace` remain registered on the agent's pipe with no cross-process caller: the
agent invokes both in-process (`LockOverlay`, `HideCoordinator`), which is what SC-10 already
recommends for the first. If a ruling restores the direct UI→Agent path, the change is a second
`PipeClient` in the dashboard plus removing one forwarding handler.

---

## SC-02 — Envelope schema is incompatible between documents (~~blocking~~ **RESOLVED 2026-08-25**)

> **Ruling (owner, 2026-08-25).** The Service's envelope wins as the single source of truth for all three
> peers: `AppGuardian.Shared.Ipc.IpcEnvelope`, the API Design §2.3 form. Recorded as ADR-014, which
> supersedes ADR-009's PROVISIONAL status for this conflict. No behavioural change was needed — the UI
> client and the agent dispatcher both reach the wire only through `PipeClient`/`PipeServer`/
> `MessageDispatcher`, so there was never a second schema that could drift. SRS §10.2 is now known-wrong
> documentation to reduce to a pointer in the next spec revision.

SRS §10.2 and API Design §2.3 define different wire formats for the same messages:

| Concern | SRS §10.2 | API Design §2.3 |
|---|---|---|
| Version field | `"v": 1` (integer) | `"apiVersion": "1.0"` (semver string) |
| Message kind | `"type": "request\|response\|event"` | `"kind": "request\|response\|event"` |
| Operation | `"op": "GetPolicy"` (PascalCase, flat) | `"type": "policy.get"` (dot-namespaced, camelCase) |
| Message id | `"id"` (also serves as correlation id) | `"messageId"` + separate `"correlationId"` |
| Timestamp | `"ts"` | `"timestamp"` |
| Errors | `{ "error": "unauthorized" }` (bare string) | structured object with `code`/`message`/`retryable`/`details` |
| Success flag | absent | `"success": true` on responses |

These are not stylistic variants; a client written to one will fail against a server written to the
other. Note also that the SRS reuses a single `id` for both identity and correlation, which makes
server-side deduplication of retried writes (API Design §2.4) impossible to implement.

**Recommendation:** adopt the API Design envelope wholesale and rewrite SRS §10.2/§10.3 to
reference it rather than restate it. The API Design version is strictly more capable (structured
errors, separate correlation id, semantic versioning) and the SRS is the document that should
defer on wire-level detail.

**Provisional code choice:** API Design envelope. SRS §10.3's *rules* (every request gets a response
or a timeout error; unknown `op`/version never crashes the peer; events are one-way best-effort)
are all preserved — those do not conflict, only the field names do.

---

## SC-03 — Operation names in the SRS are absent from the API message catalog

SRS §10.2 gives example ops `GetPolicy`, `SetRule`, `ApplyPower`, `ShowLock`, `MoveToHidden`,
`Heartbeat`. The API Design catalog (§4) has no `policy.*` namespace at all, and names the others
`power.applyThrottle`, `lock.showOverlay`, `hide.moveToHidden`, `system.ping`. Additionally the
agent is specified to fetch policy from the service (API Design §1.2, and §2.4 says the agent
"reconciles against `policy.get` on reconnect") but `policy.get` appears in **no** catalog table.

**Recommendation:** add a `policy.*` namespace to API Design §4 (`policy.get`, `policy.changed`
event) and drop the SRS's illustrative op list in favour of a pointer to the catalog. A message
referenced in the timeout/retry rules but missing from the catalog is a real gap, not just a
naming mismatch.

**Provisional code choice:** `policy.get` and `policy.changed` are implemented, marked in code as
filling this gap.

---

## SC-04 — Temporary unlock unit and default disagree

| Source | Field | Value |
|---|---|---|
| SRS §9.2 `AppRule` | `tempUnlockMinutes` | `5` |
| SRS FR-305 | — | default 5 min, range 1–60 min, or "until app close" |
| API Design §5 `AppRule` | `tempUnlockSeconds` | `300` |

Same concept, different field name and unit, so the persisted policy file shape differs between
the two documents. Separately, FR-305's third option — "until app close" — is not representable in
either schema, since both fields are plain numbers with no sentinel or discriminator.

**Recommendation:** use `tempUnlockSeconds` (finer granularity, no rounding when the UI offers
minute values) and add an explicit `tempUnlockMode: "duration" | "untilAppClose"` so FR-305's
third option is expressible. This also interacts with SRS Appendix C question 2 (does a temporary
unlock survive process restart within its window?) — the two must be answered together.

**Provisional code choice:** `tempUnlockSeconds` plus `tempUnlockMode`; `untilAppClose` is modelled
and persisted but the agent-side enforcement is stubbed pending the Appendix C ruling.

---

## SC-05 — `AppRule` shape differs in three further ways

Beyond SC-04, the two `AppRule` definitions do not agree:

| Field | SRS §9.2 | API Design §5 |
|---|---|---|
| Key name | `id` | `ruleId` |
| Display name | `displayName` at rule level | inside `identity` |
| Hide block | `{ enabled }` | `{ enabled, workspace }` |
| Power block | `{ profile }` only | `{ profile, reducePriority, ecoQos }` |
| Identity fields | `exePath`, `packageFamilyName`, `aumid`, `sha256` | `executablePath`, `packageFamilyName`, `appUserModelId`, `fileHash`, plus `appId`, `iconBase64` |
| Nullable profile | `"...\|null"` | non-null `"LowImpact"` |

The API Design version is a superset and is the only one that carries `appId` — the "stable derived
key" that every other message in the catalog uses to reference an app. The SRS schema has no such
key, so rules there can only be addressed by GUID while events reference apps by `appId`.

**Recommendation:** the API Design §5 models are canonical; SRS §9.2 should be reduced to a prose
pointer. A single schema definition in one document, referenced from the other, prevents this class
of drift entirely.

**Provisional code choice:** API Design §5 field names throughout, since `AppGuardian.Shared` is the
single source of truth for wire types (API Design §1.1) and those types are also what get persisted.

---

## SC-06 — How `appId` is derived is never specified

API Design §5 shows `"appId": "sha256:ab12..."` as a "stable derived key" and `"fileHash":
"sha256:..."` as a separate optional field, but never says **what** is hashed to produce `appId`.
This matters more than it looks:

- If `appId` derives from the file *contents*, every app update changes the identity and silently
  orphans the user's rules — which is exactly the "app updated and path changed" edge case the
  Project Plan §5.5 requires to keep working.
- If it derives from the executable *path*, the rule survives updates but breaks when the user
  moves or reinstalls the app to a different location.
- For UWP/MSIX apps there is no meaningful single executable path at all, so the derivation must
  differ by app kind.

**Recommendation:** define `appId` as a hash over a normalised *identity* string, not file content —
lowercased `packageFamilyName` when present, else the lowercased canonical executable path — and
keep `fileHash` purely as a tamper/update *signal* that never participates in identity. Document
the derivation in API Design §5 so all three components compute it identically.

**Provisional code choice:** implemented exactly as recommended above, isolated in one function so a
different ruling is a single-method change.

---

## SC-07 — Credential KDF: documents state a preference, SRS still lists it as open

SRS FR-303 permits "PBKDF2/Argon2id, ≥ 100k iterations or memory-hard params". API Design §5 says
"Argon2id preferred, PBKDF2-HMAC-SHA256 fallback". SRS Appendix C question 1 nonetheless lists the
choice as open with Argon2id as the default. The Project Plan Week 2 task says "Argon2id/PBKDF2".

The practical problem: **.NET 8 has no built-in Argon2id.** `Rfc2898DeriveBytes` (PBKDF2) ships in
the BCL; Argon2id requires a third-party NuGet package (e.g. Konscious.Security.Cryptography or
libsodium bindings). That collides with two other requirements — NFR-S5 forbids unsigned plugin
loading and dynamic code, and SRS §7.8 requires MIT-compatible licensing and reproducible builds.
Adding a native-dependency crypto package is a real supply-chain decision, not a detail.

**Recommendation:** ship PBKDF2-HMAC-SHA256 at 600,000 iterations (current OWASP guidance, well
above FR-303's 100k floor) using only the BCL, and treat Argon2id as a post-MVP enhancement behind
the already-designed `algo` discriminator in the stored hash record. This keeps the MVP
dependency-free and FIPS-friendly, and the stored-format discriminator means migrating later does
not invalidate existing credentials.

**Provisional code choice:** PBKDF2 as described, behind an `IKeyDerivation` abstraction with the
algorithm name persisted in the credential record so Argon2id can be added without a migration.
**This deviates from the stated Appendix C default of Argon2id and needs an explicit ruling.**

---

## SC-08 — Fail-safe (NFR-S4) is not implementable as written

NFR-S4: "On service/agent unavailability, the app fails safe (protected apps remain locked/hidden;
no silent unprotect)." The STRIDE table (§11) repeats this as the mitigation for "killing
service/agent to bypass". But the enforcement mechanisms are *owned* by those processes:

- The lock overlay is a window owned by the agent process. When the agent dies, its windows are
  destroyed by the OS and the target app becomes accessible. Nothing remains to block input.
- Hidden windows live on a virtual desktop. The desktop persists, but with no agent there is
  nothing to move newly-launched windows onto it.
- Job Object CPU caps **do** survive, because the limit lives on the kernel object, not in the
  service — this is the one case that genuinely fails safe.

So "protected apps remain locked" is achievable only if the *watchdog wins the race* — the service
restarts the agent (NFR-R2) faster than the user can interact with the unprotected app. That is a
mitigation, not a guarantee, and the SRS's own §11 residual-risk note already concedes that a local
administrator can always disable protection.

**Recommendation:** restate NFR-S4 as two verifiable claims: (a) no policy is ever *deleted or
disabled* as a result of a crash — protection resumes on restart with rules intact; and (b) the
watchdog restarts a dead enforcer within a stated budget (suggest ≤ 5 s), with the gap disclosed to
the user. TC-12 currently asserts "NFR-S4 fail-safe holds" after manually stopping the service,
which will fail against the requirement as literally written and pass against the restatement.

**Provisional code choice:** watchdog with a 5 s budget, plus a hard rule that a crash never mutates
policy. The residual gap is disclosed in the UI text.

---

## SC-09 — Pipe ACL and the privilege model contradict each other

API Design §2.2 requires that "write/policy-mutating calls require an Administrator or SYSTEM
caller". But SRS §5 grants the **Standard Interactive User** the capability to "lock/unlock;
hide/reveal; apply battery profiles", and FR-801 lets the user "create, edit, enable, disable, and
delete app rules" from the dashboard — which is a policy mutation. The dashboard is a normal
user-session process (API Design §1.1), so it is not an Administrator caller.

Taken together the two rules make the primary use case impossible: a non-elevated user could never
create a rule, yet that is the main thing the dashboard is for.

**Recommendation:** replace caller-*elevation* checks with caller-*authentication* checks for
user-facing mutations. The pipe ACL already limits callers to the interactive user, SYSTEM, and
Administrators; within that boundary, policy mutations should require an active unlock session
(which FR-308 already mandates) rather than an elevated token. Reserve genuine
Administrator-or-SYSTEM enforcement for the small set of operations that bypass authentication
itself — `auth.reset` (FR-307, which the catalog already marks "admin") and uninstall/repair.

**Provisional code choice:** as recommended — unlock-session gating for user mutations,
elevation required only for `auth.reset`. This is a deliberate departure from a literal reading of
API Design §2.2 and needs a ruling.

---

## SC-10 — `auth.verifyWindowsHello` is routed to the wrong process

API Design §4.1 lists `auth.verifyWindowsHello` with direction "UI/Agent→Agent". A message from the
Agent *to the Agent* is not an IPC call, and the table gives no way to distinguish the in-process
case from the UI's cross-process case. Meanwhile `auth.verifyPin` is routed "UI/Agent→Service".

Splitting the two verification methods across two different servers means the unlock-session state
(FR-305, owned by the service per §4.1 `auth.startSession`) is updated from two places, and the
agent must make a second hop to the service after a successful Hello verification — which the
catalog does not describe.

**Recommendation:** keep Hello *UI rendering* in the agent (it must — Session 0 isolation, CN-1)
but have the agent report the verification result to the service, which remains the sole owner of
session state. Document that as `auth.verifyWindowsHello` (UI→Agent) followed by
`auth.startSession` (Agent→Service), and note the in-process shortcut explicitly.

**Provisional code choice:** as recommended; the agent calls `auth.startSession` on the service
after a successful Hello verification.

---

## SC-11 — Two audit entry schemas

| Field | SRS §9.2 | API Design §5 |
|---|---|---|
| id | absent | `id` |
| timestamp | `tsUtc` | `utc` |
| actor | `user\|service\|agent` | `user\|system` |
| action | includes `applyPower`, `removePower`, `configChange` | includes `throttle`; omits the other three |
| target | `targetRuleId` (GUID) | `appId` (hash) |
| outcome | `outcome: success\|failure\|blocked` | `result: ok\|denied\|error` |

Every field name differs, the actor and outcome enumerations have different cardinality, and the two
disagree on whether an entry points at a rule or an app. Since the audit log is append-only and
persisted (§9.1), picking the wrong one now means a migration later.

**Recommendation:** API Design §5 field names, but keep the SRS's richer enumerations — the
three-way actor (`user`/`service`/`agent`) is genuinely useful when diagnosing which component
performed an action, and `configChange` is needed for FR-803's "outcome" coverage of settings edits.
Record both `ruleId` and `appId`; they answer different questions and both are cheap.

**Provisional code choice:** as recommended — API Design names, SRS enumerations, both identifiers.

---

## SC-12 — Storage paths and locations are underspecified or inconsistent

SRS §9.1 puts the credential store at `%ProgramData%\AppGuardian\credentials.dat` with ProgramData
ACLs (admin-only write). Two problems:

1. **Per-user credentials in a machine-wide location.** FR-300 configures auth per user, and §2.2
   lists full multi-user support as out of scope — but a single machine-wide credential file means
   two users on one machine share one PIN, which is not what FR-300 describes. The SRS never says
   whether the credential record is keyed by user SID.
2. **The audit log is written by two processes with different privileges.** §9.1 puts
   `audit.log` under ProgramData with admin-only write, but FR-508 has lock/unlock events logged
   (agent, user privilege) and §9.2's `actor` enum includes `agent`. A user-privilege process cannot
   append to an admin-only-write file.

Additionally, API Design §5's `SystemStatus` exposes `virtualDesktopSupported` and
`windowsHelloAvailable`, but SRS §9 never persists these — fine if they are computed per-request,
which should be stated.

**Recommendation:** key the credential record by user SID within the machine-wide file (preserves
the admin-only-write ACL that NFR-S3 wants, while making FR-300 per-user as described). Route **all**
audit writes through the service over IPC so exactly one privileged process owns the file — the
agent sends an audit event rather than writing directly. State in §9 that `SystemStatus` is computed,
not persisted.

**Provisional code choice:** as recommended. Audit writes are service-only; the agent has no file
handle to `audit.log`.

---

## SC-13 — Requirements with no owning task, and tasks with no requirement

Cross-checking the Project Plan §2 task list against the SRS requirement set:

**Requirements with no corresponding plan task:**

| Requirement | Note |
|---|---|
| FR-105 (installer verifies OS build ≥ minimum) | Priority `S`, no task in any week |
| FR-205 (health/heartbeat endpoint over IPC) | Week 1 task mentions "heartbeat" for the service only; the agent's is unlisted |
| FR-307 (PIN/password reset) | Priority `S`, no task; `auth.reset` exists in the API catalog |
| FR-405 (de-duplicate discovered entries) | Priority `S`, no task, though Week 2 lists both installed and running listing |
| FR-406 (cache discovery results) | Priority `C`, no task — acceptable for a `Could` |
| FR-708 (auto-apply stricter profile on battery) | Priority `C`, no task — acceptable |
| FR-802 (settings page) | Priority `M` — **no task in any week**. This is a Must with no owner. |
| FR-806 (global pause-protection toggle) | Priority `S`, no task |
| FR-807 (export/import rules) | Priority `C`, no task — acceptable |
| NFR-P1..P5 | Only P4/P5 are tested (TC-15); no task measures cold start, CPU, or memory |

**Plan tasks with no requirement trace:** "Fix high-priority bugs" and the README/docs task cites
`FR-DoD`, which is not a requirement ID that exists anywhere in the SRS.

FR-802 is the significant one — a `Must` requirement with no scheduled work is exactly the kind of
gap the traceability matrix exists to catch, and the RTM §15 row "FR-800..807 → TC-12" does not
actually test the settings page at all.

**Recommendation:** add Week 4 tasks for FR-802 and FR-806, add FR-105 to the Week 1 installer task,
add FR-307 and FR-405 to Week 2, and add an NFR measurement task to Week 4 covering P1–P3. Replace
`FR-DoD` with a reference to SRS §18. Also add a test case for the settings page to §14.

**Provisional code choice:** the settings page and pause toggle are scaffolded, so the code does not
inherit the gap even though the plan has it.

---

## SC-14 — Effort estimates exceed the calendar

Summing the Project Plan §2 estimates: Week 1 ≈ 6.5 d, Week 2 ≈ 8.25 d, Week 3 ≈ 7.5 d,
Week 4 ≈ 7.5 d — about **29.75 ideal engineering days** against a 30-calendar-day timeline that
§1.3 further reduces by reserving days 29–30 as a hardening buffer with no new features. That leaves
roughly 28 working days for ~29.75 days of estimated work, at 100% utilisation, with zero allowance
for the Week 3 virtual-desktop spike that §6 explicitly anticipates might fail and force a
fallback implementation.

The plan is honest about this — Risk 5 in the SRS rates "one-month timeline is tight" as
High/High — but the arithmetic makes Week 2 (8.25 d in a 5–7 day window) the first likely slip, and
§6's trigger for descoping is "M2 gate missed by >1 day", which the estimates predict.

**Recommendation:** either move `S`-priority Week 2/3 items (FR-702 priority reduction, FR-703
EcoQoS, FR-503/507 overlay polish) out of the MVP now rather than under pressure later, or extend to
five weeks. Deciding this before Week 2 is much cheaper than discovering it during Week 2. Note that
this is a scope/schedule decision for Hafiz alone — §4.3 of the plan explicitly forbids the
implementer from silently adjusting scope.

**Provisional code choice:** none. This is a planning decision, not a code one. The scaffold
implements `S` items where they are cheap and marks them clearly so they can be cut.

---

## SC-15 — Smaller items

| # | Item | Note | Recommendation |
|---|---|---|---|
| a | Service name | SRS FR-101 says `AppGuardian.Service`; no document states the *display* name or description shown in services.msc | Set display name "AppGuardian Protection Service" |
| b | Hidden workspace name | SRS FR-600 says `GuardianHidden` "(or equivalent)"; API Design §5 hardcodes `"workspace": "GuardianHidden"` per-rule | Make it a single global constant, not per-rule — a per-rule workspace name implies multiple hidden desktops, which nothing else supports |
| c | Max message size | API Design §2.2 says "> 256 KB default" rejected. `apps.listInstalled` returning icons as `iconBase64` (§5) will exceed this on a machine with many apps | Either paginate `apps.listInstalled` or exclude icons from list responses and fetch them individually |
| d | `E_CONFLICT` retryability | Listed as "Maybe" in §3 with no rule for deciding | Define as non-retryable without a fresh read; the client must re-fetch and merge |
| e | Audit log rotation | SRS §9.3 says 5 MB × 3 files; no requirement ID owns this | Assign an FR or fold into FR-803's acceptance |
| f | `PipeOptions.None` | API Design §2.2 specifies it to prevent remote access, but `None` also disables async I/O; a server handling concurrent clients wants `PipeOptions.Asynchronous` | Use `Asynchronous`; remote access is prevented by the pipe being created locally and by ACL, not by this flag. **This is a factual error in the spec.** |
| g | Windows 10 22H2 + EcoQoS | FR-703 applies EcoQoS "where supported"; `PROCESS_POWER_THROTTLING_EXECUTION_SPEED` needs Windows 10 2004+, so 22H2 is fine — but the same is not true of all virtual-desktop interfaces, which changed shape across 10 → 11 → 11 23H2 | Confirm the VD adapter needs at least three per-build implementations, and budget for it in Risk 1 |
| h | `.v1` vs `apiVersion` | Both encode version; §7 explains the split, but nothing states what happens if a client connects to `...v1` and requests `apiVersion: "2.0"` | Reject with `E_UNSUPPORTED_VERSION`; document that transport and contract majors move together |

---

## SC-16 — An elevated installer cannot satisfy FR-102 for more than one user

**SRS FR-100 (M):** "Provide a Windows installer requiring administrative privileges."

**SRS FR-102 (M):** "Register the user-session agent to auto-start after user login (**per-user Run
key** or Task Scheduler logon trigger)."

**The conflict.** These two cannot both be met as written. An installer running elevated writes
`HKCU` in the *installing administrator's* hive, so a per-user Run key registers the agent for
exactly one account — the one that happened to run setup. On a shared PC, every other user gets a
service that enforces policy with no agent to draw an overlay, ask for a PIN, or reveal a hidden
window. Since the hide feature's only recovery path is the agent, a second user who somehow got an
app hidden would have no way back.

There are three readings, and they lead to different installers:

1. **Machine-wide.** `HKLM\...\Run`, one entry, agent starts for every user. Matches the fact that
   policy is machine-wide (`%ProgramData%\policy.json`) and the service is a single LocalSystem
   instance. Costs: the agent runs for users who never asked for it, and FR-102's own words say
   "per-user".
2. **Task Scheduler with a group principal.** A logon trigger for `BUILTIN\Users` is the literal
   reading of "logon trigger" and is per-user in effect while being registered once. Costs: a
   scheduled task is harder for a user to see and disable than a Run entry, and the repair path
   (FR-104) has to reason about a task rather than a registry value.
3. **Per-user, deferred.** The installer registers nothing; the dashboard writes the current user's
   `HKCU` Run entry on first launch. Literal FR-102, and each user opts in by using the product.
   Costs: FR-102's acceptance test ("agent process present after logon") fails on a machine where
   nobody has opened the dashboard yet, and TC-01 would need rewording.

**Provisional code choice.** Reading 1. `installer/AppGuardian.iss` writes
`HKLM\Software\Microsoft\Windows\CurrentVersion\Run\AppGuardianAgent`, marked in the script header.
Chosen because it is the only reading under which the recovery path for a hidden window exists for
every account on the machine, which is a safety property rather than a preference. **A ruling for
reading 2 or 3 changes only the `[Registry]` and `[Run]` sections plus a first-run step in the
dashboard.**

**Related:** the same question decides whose `%LOCALAPPDATA%\AppGuardian\settings.json` the
uninstaller removes under FR-103. The current script removes the machine root plus the uninstalling
user's own folder, and leaves other users' `settings.json` behind — those hold only UI preferences,
never policy or credentials.

---

## Summary

| Severity | Conflicts | Effect if unresolved |
|---|---|---|
| **Blocking** | ~~SC-01, SC-02~~ — **both resolved 2026-08-25 (ADR-014)** | Components cannot interoperate at all |
| **Schema** (migration cost if wrong) | SC-04, SC-05, SC-06, SC-11, SC-12 | Persisted-data rework after release |
| **Requirement soundness** | SC-08, SC-09, SC-10, SC-16 | A requirement or test case that cannot pass as written |
| **Dependency / supply chain** | SC-07 | Needs a licensing and build-reproducibility decision |
| **Plan integrity** | SC-13, SC-14 | A `Must` requirement with no owner; schedule predicts its own slip |
| **Minor** | SC-03, SC-15a–h | Clarifications; SC-15f is a factual API error |

The two blocking items (SC-01, SC-02) were ruled on 2026-08-25 and are closed; see ADR-014. Everything
else can be resolved while the scaffold is being fleshed out, but the five schema items get more expensive
after the first release writes data to disk.
