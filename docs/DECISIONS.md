# Architecture Decision Records

Format: context, decision, consequences. Newest last. ADRs are append-only; a reversal is a new ADR
that supersedes an old one, not an edit.

Decisions marked **PROVISIONAL** were made so the scaffold could compile and are awaiting a ruling
from Hafiz. Each references the relevant entry in `docs/SPEC_CONFLICTS.md`.

---

## ADR-001 — Four projects, not one

**Context.** AppGuardian needs LocalSystem privilege for Job Object enforcement and service
control, but also needs to draw overlay windows and call virtual-desktop COM interfaces, which only
work inside the interactive user session. Windows Session 0 isolation (SRS CN-1) forbids a service
from showing UI at all.

**Decision.** Split into `AppGuardian.Service` (LocalSystem, no UI), `AppGuardian.Agent`
(interactive session, all UI and window manipulation), `AppGuardian.UI` (WPF dashboard, a client
only), and `AppGuardian.Shared` (contracts referenced by all three).

**Consequences.** Every cross-boundary operation costs an IPC hop, so the envelope and error model
have to be solid before feature work. Three processes means three lifetimes to supervise, hence the
watchdog. The upside is that a crash in the UI cannot take down enforcement, and the highest-
privilege process has the smallest surface — it parses JSON from an ACL-restricted pipe and touches
no UI framework at all.

---

## ADR-002 — Named pipes over the alternatives

**Context.** The three processes span two sessions and two privilege levels. Options considered:
named pipes, gRPC over localhost TCP, a shared memory-mapped file, and COM.

**Decision.** Named pipes in message mode, UTF-8 JSON payloads.

**Consequences.** No network listener exists, which makes NFR-Pr2 ("no outbound network calls")
trivially true and removes a whole class of firewall and port-conflict problems that a localhost TCP
socket would introduce. Pipe DACLs give per-principal access control for free, which is what
NFR-S2 asks for. JSON costs more CPU than a binary format, but the messages are small and infrequent
and the p95 budget is 100 ms — a generous ceiling for a local pipe. The cost is that framing must be
handled explicitly, since message mode is Windows-specific and has no cross-platform equivalent.

---

## ADR-003 — PBKDF2-HMAC-SHA256 for credential hashing, not Argon2id **PROVISIONAL**

See `SPEC_CONFLICTS.md` SC-07.

**Context.** SRS Appendix C question 1 defaults to Argon2id; API Design §5 states "Argon2id
preferred, PBKDF2 fallback". But .NET 8 has no built-in Argon2 — it requires a third-party NuGet
package, in most cases with a native dependency. Against that sit NFR-S5 (no dynamic code or
unsigned plugin loading), SRS §7.8 (MIT-compatible licensing, reproducible builds), and a one-month
timeline where a native-dependency packaging problem is an expensive surprise.

**Decision.** Ship PBKDF2-HMAC-SHA256 at 600,000 iterations using only `Rfc2898DeriveBytes` from
the BCL. Persist the algorithm name and parameters inside the credential record. Treat Argon2id as
a post-MVP addition behind the `IKeyDerivation` abstraction.

**Consequences.** 600k iterations is six times FR-303's stated floor and matches current OWASP
guidance for PBKDF2-HMAC-SHA256, so the requirement is met with margin. The MVP takes zero crypto
dependencies, which keeps the build reproducible and the AV-flagging risk (SRS Risk 4) lower — a
native crypto blob is exactly the kind of thing that draws heuristic attention. PBKDF2 is weaker
than Argon2id against GPU attack for equivalent cost, which is a real reduction in strength; it is
acceptable here because the threat model (SRS §11) already concedes that a local administrator can
bypass protection entirely, so an offline attack on the hash is not the weakest link. Because the
algorithm name is stored per-record, adding Argon2id later verifies old PBKDF2 credentials and
rehashes on next successful unlock, with no forced reset.

**Deviates from the stated Appendix C default. Needs an explicit ruling.**

---

## ADR-004 — `appId` derives from identity, never from file content **PROVISIONAL**

See `SPEC_CONFLICTS.md` SC-06.

**Context.** API Design §5 shows `appId` as a "stable derived key" and separately carries
`fileHash`, but never says what `appId` is computed from. Hashing file content would mean every
application update produces a new identity, silently orphaning the user's rules — while Project Plan
§5.5 explicitly requires "app updated and path changed" to keep working.

**Decision.** `appId = "sha256:" + hex(SHA256(normalisedIdentity))` where `normalisedIdentity` is
the lowercased `packageFamilyName` when the app is packaged, and otherwise the lowercased, fully
resolved executable path. `fileHash` is recorded independently and never participates in identity —
it exists only as a tamper/update signal.

**Consequences.** Rules survive application updates, which is the common case and the one users
would be angriest about. Rules do *not* survive the app being moved or reinstalled to a different
path, which is rarer and recoverable by re-adding the rule; a future enhancement could offer to
re-point an orphaned rule when a same-named executable appears elsewhere. Packaged apps get the
better identity of the two because `packageFamilyName` is genuinely stable across updates and
install locations. The derivation lives in exactly one method so a different ruling is a one-line
change.

---

## ADR-005 — All audit writes go through the Service **PROVISIONAL**

See `SPEC_CONFLICTS.md` SC-12.

**Context.** SRS §9.1 places `audit.log` under `%ProgramData%` with admin-only write, but FR-508
has the agent logging lock/unlock events and §9.2's `actor` enum includes `agent`. A
user-privilege process cannot append to an admin-only-write file. Loosening the ACL to let the agent
write directly would let any user-level process forge or truncate audit entries, defeating the
repudiation mitigation in §11.

**Decision.** The Service is the only process that holds a handle to `audit.log`. The Agent and UI
emit `system.audit` messages over IPC; the Service validates and appends. The ACL stays admin-only.

**Consequences.** Audit integrity is preserved against a non-elevated attacker, and the append-only
guarantee has exactly one enforcement point. The cost is that audit writes become best-effort when
the service is down — the Agent buffers in memory and flushes on reconnect, and entries written
during an outage carry the buffered timestamp rather than the write time, which the log format makes
explicit. A lost buffer on agent crash is an accepted gap; the alternative (a user-writable log) is
worse.

---

## ADR-006 — Policy mutations gated by unlock session, not by token elevation **PROVISIONAL**

See `SPEC_CONFLICTS.md` SC-09.

**Context.** API Design §2.2 requires that policy-mutating calls come from an Administrator or
SYSTEM caller. But SRS §5 grants the standard interactive user the ability to lock, hide, and apply
power profiles, and FR-801 lets that user create and delete rules from the dashboard — which is a
policy mutation from a non-elevated process. Enforced literally, the primary use case is impossible.

**Decision.** Within the pipe's ACL boundary (SYSTEM, Administrators, interactive user), policy
mutations require an **active unlock session**, which FR-308 already mandates. Genuine
Administrator-or-SYSTEM enforcement is reserved for operations that bypass authentication itself:
`auth.reset` (FR-307) and installer-driven repair.

**Consequences.** The dashboard works as designed for a normal user, and the security property that
actually matters — you cannot change protection without authenticating — is preserved and is
strictly stronger than an elevation check, since elevation persists for a process lifetime while an
unlock session expires. What is given up is defence against a *non-elevated attacker who has already
authenticated*, which is indistinguishable from the legitimate user by construction.

---

## ADR-007 — Virtual desktop access behind a feature-detected adapter

**Context.** Windows exposes no supported API for moving an arbitrary window to a specific virtual
desktop. `IVirtualDesktopManager` is documented but only tells you whether a window is on the
current desktop and can move windows you own. The interfaces that do more are undocumented, and
their IIDs and vtable layouts have changed across Windows 10, Windows 11, and Windows 11 23H2.
This is SRS Risk 1, rated High impact / High likelihood.

**Decision.** All virtual-desktop access goes through `IVirtualDesktopAdapter` in
`AppGuardian.Shared`, with a per-build implementation selected at runtime and an `IsSupported`
feature-detect probe. When probing fails, `IsSupported` returns false, callers surface
`E_VD_UNAVAILABLE`, and the UI discloses that hiding is degraded rather than silently doing nothing.

**Consequences.** A Windows update that changes the COM layout breaks one adapter, not the feature —
the app degrades to the disclosed fallback instead of crashing or, worse, appearing to hide an app
that stays visible. The cost is at least three adapter implementations to write and test, and an
ongoing maintenance obligation on every major Windows release. The fallback path (minimise plus
off-screen positioning) is materially weaker than a real hidden desktop and must be disclosed as
such, per FR-607.

---

## ADR-008 — Job Objects as the primary throttling mechanism

**Context.** FR-701 requires CPU throttling. Options: Job Object CPU rate control, thread priority
manipulation, EcoQoS alone, or a polling suspend/resume loop.

**Decision.** Assign target processes to a Job Object with `JOBOBJECT_CPU_RATE_CONTROL_INFORMATION`
as the primary mechanism, with optional priority reduction (FR-702) and EcoQoS (FR-703) layered on
top per profile.

**Consequences.** The cap is enforced by the kernel scheduler, so it is precise, low-overhead, and
— importantly for SC-08 — survives the death of the service that set it, because the limit lives on
the kernel object rather than in a supervising loop. A suspend/resume loop would have been the
opposite: cheap to write, but it stops working the instant the supervisor dies and produces visibly
janky application behaviour. The constraint is that a process can only belong to one job in the
nesting-unaware case, so AppGuardian must handle `AssignProcessToJobObject` failing when a target is
already jobbed by something else (common with browsers and some launchers) and report
`E_UNSUPPORTED_TARGET` rather than pretending success.

---

## ADR-009 — API Design envelope and models are canonical over the SRS **PROVISIONAL**

See `SPEC_CONFLICTS.md` SC-01, SC-02, SC-04, SC-05, SC-11.

**Context.** The SRS and API Design define the same wire envelope, pipe names, and data models with
different field names, different casing, and in some cases different capability. A client written to
one fails against a server written to the other.

**Decision.** Implement the API Design v0.2.0 envelope, pipe names, and §5 models. Where the SRS
carries a richer *enumeration* (the three-way audit `actor`, the `configChange` action), keep the
SRS values inside the API Design field names.

**Consequences.** One schema, in `AppGuardian.Shared`, which API Design §1.1 already designates the
single source of truth for wire types. The API Design version is chosen because it is the more
capable of the two — structured errors, separate correlation id for write deduplication, semantic
version negotiation, and the `appId` key that the rest of the message catalog depends on. The SRS
sections that restate wire detail (§10.2, §9.2) should be reduced to pointers so this cannot drift
again. Until that edit happens, the SRS text and the code disagree on field names, which is a
documentation defect rather than a code one.

---

## ADR-010 — `AppGuardian.Shared` multi-targets `net8.0;net8.0-windows`

**Context.** `AppGuardian.Shared` originally targeted plain `net8.0` so that a Windows-only API could
not leak into the contract layer, with the CI `build-portable` job enforcing it on Linux. The named
pipe implementation then broke that rule out of necessity: the DACL factory needs `PipeSecurity`,
`PipeAccessRule`, and `NamedPipeServerStreamAcl` (Windows-only, and shipped in the
`System.IO.Pipes.AccessControl` package rather than the base framework), caller identity needs
`NamedPipeServerStream.RunAsClient`, and framing needs `PipeStream.IsMessageComplete`. All three
belong with the transport, which is shared by the service, the agent, and the dashboard — moving them
into any one of those would force the other two to reference it.

**Decision.** Multi-target `AppGuardian.Shared` as `net8.0;net8.0-windows`. All Windows-only IPC code
lives in `Ipc/Windows/`, which is `<Compile Remove>`d from the `net8.0` target. `build-portable` now
compiles `-f net8.0` explicitly. `AppGuardian.Tests` mirrors the arrangement with a `Windows/`
folder.

**Alternatives rejected.** A separate `AppGuardian.Ipc.Windows` project is the more orthodox shape,
but it adds a fifth project for two files and splits the transport across assembly boundaries for no
behavioural gain. `#if WINDOWS` guards inside single files were rejected because a preprocessor
branch is easy to widen accidentally, whereas a folder that the portable target cannot see is not.

**Consequences.** The original guarantee survives in the form that matters: any Windows-only API used
outside `Ipc/Windows/` still fails the Linux job. The cost is that `Ipc/Windows/` is compiled and
tested on Windows only, so the pipe server and client are exercised by CI on one platform — which is
correct, since they cannot run anywhere else. This is also why `PipeStreamAdapter` exists: framing,
the part with an intermittent failure mode, stays in the portable target and is unit tested on both.

---

## ADR-011 — The installer ships a framework-dependent build and names its prerequisite

**Context.** The three executables are published into one folder and packaged by Inno Setup
(`installer/AppGuardian.iss`). .NET offers two shapes: self-contained, which bundles the runtime, or
framework-dependent, which requires the .NET 8 Desktop Runtime on the target machine. Two of the
three executables are WPF, so the *Desktop* runtime is the dependency, not the base one.

**Decision.** Framework-dependent. The installer checks for
`%ProgramFiles%\dotnet\shared\Microsoft.WindowsDesktop.App\8.*` and, if absent, tells the user what
to install and where to get it, then lets them continue or stop.

**Alternatives rejected.** Self-contained trades roughly 150 MB of installer for about 2 MB of
actual product, and — more importantly — pins the runtime at build time, so a servicing fix for a
.NET security advisory would require AppGuardian to ship a new installer rather than the machine
picking it up from Windows Update. For a product whose whole claim is that it guards other apps,
carrying a frozen runtime is the wrong default.

Downloading and silently running the Microsoft runtime installer as a prerequisite was also
rejected: NFR-S5 forbids fetching and executing code, and the product's privacy claim is that it
makes no network connections. An installer that quietly downloaded 60 MB would contradict the
statement the dashboard shows the user on its Settings page.

**Consequences.** A clean machine can fail to launch after a successful install if the user
dismissed the prerequisite warning. That path is signposted twice — at install time and in the
README's requirements section — rather than hidden. Windows 11 ships the Desktop Runtime in enough
configurations that most installs will not see the prompt, but Windows 10 22H2 (the minimum target)
frequently will.

---

## ADR-012 — Test iteration counts are lowered to the documented floor, not below it

**Context.** `Pbkdf2KeyDerivation` defaults to 600,000 iterations (FR-303) and refuses anything under
100,000. Each `Hash` call at the default costs a noticeable fraction of a second, and the credential
tests derive roughly thirty keys.

**Decision.** The tests construct the derivation with 100,000 — the enforced floor — rather than the
default, and one test asserts that construction below the floor throws.

**Alternatives rejected.** Making the floor configurable for tests would put a hole in the one check
that stops a config value from quietly weakening the KDF. Testing at the real default would push the
suite past the point where developers run it locally, which is the failure mode that lets a broken
verify path reach a release.

**Consequences.** The suite proves the *properties* of the derivation — fresh salt per hash, the
stored iteration count winning over the instance default, corrupt records reading as "wrong secret" —
at a cost the floor makes affordable, while the production cost of 600,000 is asserted separately as
a constant. No test exercises the default's timing, so a change that made the default slow enough to
be a usability problem would not be caught here.

---

## ADR-013 — One UI layer: the earlier dashboard skeleton was deleted, not kept

**Context.** `AppGuardian.UI` had accumulated two parallel and disconnected presentation layers. The
earlier one was a first-pass skeleton (`Helpers/ViewModelBase.cs`, `Converters/Converters.cs`, ten
`Styles/*.xaml`, three `Themes/*.xaml`, `Services/{NavigationService,ServiceInterfaces,ThemeService}.cs`,
`ViewModels/{MainViewModel,DashboardViewModel,LockScreenViewModel,ViewModels}.cs`,
`Views/{DashboardView,AppLockView}.xaml`, `Views/Onboarding/*`, `Overlays/LockScreenOverlay.xaml`).
The later one is the layer `App.xaml` and `MainWindow.xaml` actually load: `Mvvm/ObservableObject.cs`,
`Views/Theme.xaml`, `Views/PageTemplates.xaml`, `Views/Converters.cs`, and the seven
`PageViewModel`-derived page view models.

The two did not merely duplicate effort — they made the project uncompilable in three separate ways.
`ViewModels/ViewModels.cs`, `MainViewModel.cs`, `DashboardViewModel.cs`, and `LockScreenViewModel.cs`
all open with `using CommunityToolkit.Mvvm.ComponentModel;` and use `[ObservableProperty]` and
`[RelayCommand]`, but the project references only DependencyInjection and Logging — there is no
CommunityToolkit package and, per the MVVM primitives written by hand in `Mvvm/ObservableObject.cs`,
deliberately so. `ViewModels.cs` also declared `HiddenAppsViewModel`, `PowerViewModel`,
`SettingsViewModel`, and `OnboardingViewModel` in namespace `AppGuardian.UI.ViewModels`, where the
live files declare the same four names with a different base class — four duplicate type definitions.
And `ThemeService.cs` indexed `Application.Current.Resources.MergedDictionaries[1]` expecting a
`Themes/` layout that `App.xaml` never merged, so the skeleton's own styles resolved to nothing.

**Decision.** Delete the skeleton layer. Nothing in the live layer referenced a single type from it,
and nothing in it was reachable from `App.xaml` or `MainWindow`, so removal changes no behaviour.
`app.manifest` — which was sitting in the project folder unreferenced — was wired up via
`<ApplicationManifest>` at the same time, since an unreferenced manifest means the process silently
loses its PerMonitorV2 DPI declaration (NFR §7.6).

**Alternatives rejected.** Keeping both and adding the CommunityToolkit package would fix the missing
attributes but not the duplicate type names, and would leave two navigation systems, two converter
sets, and two theming mechanisms in a four-week MVP — the reader of this code would have to work out
which one is live before making any change. Renaming the skeleton's types to end the collision would
preserve about 2,000 lines of dead XAML that no code path can reach.

**Consequences.** Theming is the one `Views/Theme.xaml` dictionary for structure — styles, templates and
converters — with colour split out into two interchangeable palettes. The `LockScreenOverlay` window was
also removed from the dashboard; the overlay belongs to the agent
(`src/AppGuardian.Agent/Overlay/LockOverlayWindow.cs`), which is the process that can draw on the
interactive desktop, so the copy here was in the wrong project as well as unreachable.

**Amended 2026-08-25.** This ADR originally recorded that deleting the skeleton left no runtime light/dark
switch and made `UserSettings.Theme` a dead field. Both statements are now out of date: see ADR-015, which
restores the switch in the surviving layer. The reasoning for the deletion is unchanged — the skeleton's
`ThemeService` indexed `MergedDictionaries[1]` against a `Themes/` layout `App.xaml` never merged, so it
would not have worked had it been kept.

---

## ADR-014 — The Service's pipe names and envelope are the single source of truth

Resolves `SPEC_CONFLICTS.md` SC-01 and SC-02. Supersedes the PROVISIONAL status of ADR-009 for those two
conflicts.

**Context.** SC-01 and SC-02 were the two blocking conflicts. The SRS named the pipes three different ways
across two sections (§8.1 `\GuardianUI`/`\GuardianCtl`/`\GuardianSvc`, §10.1 `GuardianSvc`/`GuardianAgent`)
while the API Design named them `appguardian.service.v1` and `appguardian.agent.v1`; and the SRS §10.2
envelope (`v`/`type`/`op`/`id`/`ts`, bare-string errors) is wire-incompatible with the API Design envelope
(`apiVersion`/`kind`/`messageId`/`correlationId`/`type`/`timestamp`/`success`/`payload`/`error`). Either
disagreement means a client that connects successfully still cannot be understood.

**Decision.** Owner's ruling, 2026-08-25: the **Service's** names and the **Service's** envelope win, for
every peer. Concretely:

- Pipes are `\\.\pipe\appguardian.service.v1` and `\\.\pipe\appguardian.agent.v1`. The SRS names are dead
  and appear nowhere in the code.
- The wire envelope is `AppGuardian.Shared.Ipc.IpcEnvelope` exactly as the service's dispatcher validates
  it: structured `IpcError`, `messageId` distinct from `correlationId`, semantic `apiVersion` negotiated by
  `ApiVersion.IsSupported`.

**Implementation.** The alignment is structural rather than a convention anyone has to remember. Both pipe
names exist once, as constants in `PipeNames`, and all four call sites read them — the service's listener
and its `AgentBridge`, the agent's listener and its `ServiceConnection`, and the dashboard's
`GuardianClient`. The envelope has one definition, and the UI client and the agent dispatcher both reach the
wire only through `PipeClient`/`PipeServer`/`MessageDispatcher`, none of which let a caller supply its own
field names. There is therefore no second schema that *could* drift, which is why this ruling needed no
behavioural change in either peer — only the removal of the "awaiting a ruling" hedges in
`PipeNames.cs`, `IpcEnvelope.cs`, `GuardianClient.cs` and `AgentDispatcher.cs`, and the notes now recorded
there.

**Consequences.** The SRS sections that restate wire detail (§8.1, §10.1, §10.2) are now known-wrong
documentation. That is a spec defect to fix in the next revision by reducing them to pointers at
`AppGuardian.Shared`; it is not a code defect, and the code should not be changed to match them. The
`.v1` suffix stays load-bearing: a breaking framing change means new pipe names, so an old and a new build
can coexist during an upgrade rather than failing to parse each other's traffic.

---

## ADR-015 — Runtime light/dark theming, with colour split from structure

**Context.** ADR-013 deleted the UI skeleton, and with it the only code that switched themes. That left
`UserSettings.Theme` as a field nothing read and the dashboard fixed at dark. Restoring the switch is not
just a matter of adding a service: `Views/Theme.xaml` mixed fourteen `SolidColorBrush` declarations in with
every style, template and converter, and the views referenced those brushes with `StaticResource`, which
resolves once at load. Swapping a dictionary underneath a `StaticResource` reference changes nothing on
screen.

**Decision.** Split colour from structure and swap only the colour.

- `Views/Palette.Dark.xaml` and `Views/Palette.Light.xaml` declare the same fourteen keys. A key present in
  one and missing from the other is a resource-not-found at the exact moment the user switches, so the sets
  must stay identical.
- `App.xaml` merges the palette **first**, then `Theme.xaml`, then `PageTemplates.xaml`. The order is
  load-bearing in both directions: `Theme.xaml` references palette keys, `PageTemplates.xaml` references
  `Theme.xaml`'s styles, and `ThemeService` identifies the palette slot as index 0.
- Every palette reference across all thirteen XAML files is `DynamicResource`. Structural keys stay
  `StaticResource`, which is mandatory rather than stylistic: `BasedOn` cannot take a `DynamicResource`.
- `Services/ThemeService.cs` owns the preference. It replaces `MergedDictionaries[0]` after checking that
  slot really holds a palette, and falls back to `Insert` with a warning rather than clobbering
  `Theme.xaml` and losing every style in the app.
- The preference lives in the per-user `settings.json`, not in the machine-wide policy document. It is a UI
  choice with no security meaning, so the dashboard saves it with no IPC round trip and no unlock session —
  which is what makes it the one `UserSettings` field the settings page can honestly offer.
- `AppTheme.System` is resolved by reading `AppsUseLightTheme` under
  `Software\Microsoft\Windows\CurrentVersion\Themes\Personalize` (0 dark, 1 light, missing dark). A registry
  read rather than WinRT `UISettings`: the value is present on Windows 10 22H2, it needs no dependency, and
  this is one read at startup.

**Alternatives rejected.** Clearing and rebuilding `MergedDictionaries` throws on anything that re-resolves
mid-flight. Keeping colour in `Theme.xaml` and swapping the whole dictionary would work only if every style
were also duplicated per theme — two copies of 250 lines to keep in step. Applying the theme after the
window is shown is simpler but flashes dark before flipping to light, so `OnStartup` chains
`ShowShell()` off `InitializeAsync` on the dispatcher instead; a blocking `.GetAwaiter().GetResult()` there
would deadlock against the same dispatcher the store's continuation wants.

**Consequences.** `UserSettings.Theme` is read and written, and SC-13's note that the settings page exposes
no preference now has exactly one exception, for the stated reason. The lock overlay is unaffected and stays
dark in both themes — it sits over whatever app it is covering, and a light overlay would read as part of
that app rather than as a barrier in front of it. A save failure logs and does **not** revert the applied
palette: the user asked for the change and can see it happened, so the honest failure mode is that it will
not survive a restart.

---

## ADR-016 — `null` and `Balanced` are different power states, and the UI must show both

**Context.** `PowerSettings.Profile` is a `PowerProfile?`. The contract uses `null` to mean "no power rule
is recorded for this app" and `PowerProfile.Balanced` to mean "a rule exists and records a decision to leave
this app alone" — `PowerViewModel.RemoveAsync` writes `null`, deliberately, rather than `Balanced`. The
dashboard erased the distinction in three places: the rule editor offered three radio buttons for four
states, leaving `Balanced` unreachable and a `Balanced` rule displaying with nothing selected;
`PowerRowViewModel.Profile` collapsed `null` into `Balanced` with `?? PowerProfile.Balanced`, which also
handed the null case Balanced's description from `PowerProfileMap`, so a row with no rule claimed to have
one; and two different states both rendered as the label "No limit".

**Decision.** Keep the contract's distinction and make the UI express it. The rule editor has four options,
one per enum member plus one for the absence of a rule. `PowerRowViewModel.Profile` is
`PowerProfile?` and its label and description branch on null first — "No rule" versus "Balanced — no cap".
The converter sentinel that stands for null in XAML is renamed `None` → `NoRule`, and
`NullableEnumEqualityConverter.ConvertBack` now returns `Binding.DoNothing` for an unrecognised parameter
instead of falling through to null.

**Note on the reported defect.** This was raised as "the XAML binds `ConverterParameter=None` but the enum's
zero member is `Balanced`". That premise was not accurate: `None` was never an attempt to name an enum
member, it was the documented sentinel `NullableEnumEqualityConverter.NoneSentinel` standing for null.
Changing it to `Balanced` would have collapsed "remove this app's limit" into "explicitly leave this app
alone" and made the two indistinguishable on the wire as well as on screen. The rename to `NoRule` addresses
the real hazard the report was pointing at — a parameter that reads like an enum member — while the four-way
editor and the nullable row model fix the actual user-visible bugs.

**Consequences.** Adding a member to `PowerProfile` now means adding one radio button and nothing else; the
sentinel cannot collide with a member name; and a mistyped `ConverterParameter` in a view leaves the value
alone rather than silently removing the user's limit.


