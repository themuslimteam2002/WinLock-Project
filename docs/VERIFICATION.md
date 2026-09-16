# Verification status

Written 2026-08-25, at the end of the scaffold pass.

## The headline: this code has never been compiled

There is no .NET SDK and no network access in the environment this solution was written in. `dotnet
restore`, `dotnet build -c Release`, `dotnet test`, and `iscc installer\AppGuardian.iss` have not been
run against it even once. Nothing below should be read as "it builds" — it is a record of what could
be checked by inspection and what could not.

The first build on a Windows machine with the .NET 8 SDK is part of the work, not a formality. Expect
errors, and expect some of them to be in the places static checking is weakest: XAML compilation,
P/Invoke signatures, and `net8.0` versus `net8.0-windows` conditional compilation.

Run, in order:

```powershell
dotnet restore
dotnet build -c Release          # first real syntax and type check, including XAML
dotnet test                      # 96 [Fact]/[Theory] cases across 13 test classes
iscc installer\AppGuardian.iss   # needs dist\ populated first — see README
```

## What static checking did cover

These sweeps were run mechanically over the whole tree, not by eye, and all pass:

- Brace, parenthesis, and bracket balance across every `.cs` file, with strings and comments stripped
  first so that string literals containing braces do not register as imbalances.
- XML well-formedness of all 11 `.xaml` files, all `.csproj` files, and `Directory.Build.props`.
- Every `x:Class` value resolves to a `partial class` of that name in the matching code-behind file
  (9 of 9).
- Every `views:` and `vm:` type reference used in XAML resolves to a declared type (23 of 23).
- Every `StaticResource` and `DynamicResource` key used in XAML is declared in a merged dictionary
  (37 keys, 0 unresolved).
- Every `using AppGuardian.*` resolves to a namespace that is actually declared somewhere in the tree.
- Every non-`System` `using` is backed by a `PackageReference` in the consuming project.
- Solution-wide duplicate type scan. The only repeats are deliberate: `CallResult`/`CallResult<T>`
  (generic and non-generic pair), a per-project `internal static class NativeMethods`, and a
  per-executable `Program`.
- No `TODO` and no `NotImplementedException` anywhere in `src/` or `tests/`.
- No reference anywhere in the repository to any file deleted in this pass.
- Every message type sent by the UI has a registered handler on the pipe it is sent to.

## What static checking cannot cover

The gaps, stated so they are not mistaken for verified ground:

- **XAML compilation.** Well-formed XML and resolvable resource keys are necessary, not sufficient.
  Binding-path typos, property types that do not match, and generated-partial mismatches surface only
  in `dotnet build`.
- **P/Invoke correctness.** Every `DllImport` signature in `Agent/Native`, `Service/Native`, and the
  virtual-desktop adapter is written from documentation, not tested against a live Win32 call.
  Wrong struct layouts and calling conventions compile cleanly and fail at runtime.
- **The undocumented virtual desktop COM interface.** Behind a versioned adapter per ADR-007, and
  unverified against any Windows build. This is the single highest-risk area in the solution.
- **Runtime IPC behaviour.** Pipe ACLs, message-mode framing at buffer boundaries, and reconnect
  behaviour when the agent restarts with the session are all untested.
- **Anything requiring a real user session** — Windows Hello, overlay placement across monitors,
  WinEvent hooks, EcoQoS, Job Object CPU capping.

## Two defects static checking caught

Recorded because they show what the sweeps are and are not good for:

1. **Illegal `--` inside XAML comments** in `Views/PageTemplates.xaml`, four occurrences of the form
   `<!-- ---- header ---- -->`. A double hyphen is not legal inside an XML comment, so the XAML parser
   would have rejected the file. A build would also have caught this; the XML sweep caught it first.
2. **`hide.reveal` had no handler.** `GuardianClient.RevealAsync` sends the message to the service
   pipe, but `ServiceDispatcher` never registered it and `AgentBridge` had no reveal method. Both the
   "Reveal" and "Bring back everything" buttons on the Hidden page would have failed at runtime with
   an unknown-message error. **A build would not have caught this** — message types are strings and the
   dispatch is by dictionary lookup. Fixed by adding `AgentBridge.RevealAsync`,
   `ServiceDispatcher.RevealAsync`, and its `.On(...)` registration. Deliberately not audited on the
   service side, because `HideCoordinator` already emits the `Reveal` audit entry and auditing on both
   sides would double-count every reveal in the log the user reads.

## Deletions in this pass

`src/AppGuardian.UI` contained two complete, mutually incompatible UI layers — 90 files where 34 were
live. The dead layer was deleted rather than reconciled. Full reasoning, the list of deleted paths, the
three specific compile breaks that forced the decision, and the alternatives rejected are in ADR-013 in
`DECISIONS.md`. The one real functional consequence at the time was the loss of the runtime light/dark
switch, which left `UserSettings.Theme` a field nothing read. That has since been addressed: the switch was
rebuilt in the surviving layer on 2026-08-25 (ADR-015), with colour split into `Views/Palette.Dark.xaml` and
`Views/Palette.Light.xaml` and swapped at runtime by `Services/ThemeService.cs`. None of the theme work has
been compiled either.

`app.manifest` was also wired into `AppGuardian.UI.csproj` via `<ApplicationManifest>` in the same
pass. It existed in the project folder but was referenced nowhere, so its PerMonitorV2 declaration was
being silently discarded — which shows up as blurry text on a secondary monitor rather than as a build
error.

## Loose ends left standing deliberately

- `ErrorCodes.Conflict`, `ErrorCodes.UnsupportedTarget`, and `ErrorCodes.VirtualDesktopUnavailable` are
  declared but never raised. They are in the API Design catalog, so they stay; the handlers that should
  raise them are the gap.
- Two `PROVISIONAL` markers remain in code, in `Desktops/VirtualDesktopAdapter.cs` (SC-11) and
  `App.xaml.cs` (SC-13). Both wait on a ruling.
- 16 specification conflicts (SC-01..SC-16) are recorded in `SPEC_CONFLICTS.md`. The two blocking ones —
  SC-01 pipe names and SC-02 envelope schema — were ruled on 2026-08-25 and closed (ADR-014); the other 14
  are open. Every contested choice in the code is cross-referenced to its conflict ID.
- The theme work of 2026-08-25 (ADR-015) and the power-profile corrections (ADR-016) post-date the sweeps
  described above. They have had the palette-key and structural-key checks re-run but not the full set, and
  like everything else here they have never been compiled.
