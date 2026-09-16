# AppGuardian for Windows — UI/UX Upgrade Audit

> **Scope:** Deliverables 1–5 of the premium frontend audit. All XAML patches are production-ready, secure, and preserve every existing C# ViewModel binding. **Nothing here has been compiled** — `dotnet build -c Release` must be run on the Windows machine (no .NET SDK or network is available in the Linux workspace).

---

## Two premise corrections

These must be stated outright, not worked around silently.

### D1 Step 2: Windows has no "Accessibility permission" gate

Windows does not have a per-app permission called "Accessibility" and no "Overlay permission" toggle — those are Android/macOS concepts. AppGuardian needs neither: a `WinEvent` hook does not require an Accessibility grant, the unlock prompt is a topmost WPF window launched from an interactive-session agent, and EcoQoS is applied via `SetProcessInformation`. The genuinely checkable Windows-side prerequisites on this PC are:

- **Background service reachable** — `SystemStatus.ServiceRunning`. A null reply from the service means "not responding."
- **Desktop agent running** — `SystemStatus.AgentRunning`, link to `ms-settings:startupapps`.
- **Windows Hello enrolled** — `AuthStatus.WindowsHelloAvailable`, link to `ms-settings:signinoptions`.
- **Notifications** — informational only; Windows exposes no reliable per-app answer, so this reads "Optional" as a constant. Link to `ms-settings:notifications`.

AppGuardian does not *request* permissions — it reports state and links out. That is why the buttons read "Open Settings" and "Check again," not "Grant."

### D5: VisualStateManager is inert on WPF's ButtonBase/ToggleButton/TextBoxBase

`ButtonBase`, `ToggleButton` and `TextBoxBase` never invoke `VisualStateManager.GoToState`. The animation mechanism that works in this codebase is `Trigger.EnterActions`/`ExitActions` with `BeginStoryboard`/`StopStoryboard` — already used by the `Row` style and the wizard step host. All interactive polish in this audit uses that pattern.

---

## Deliverable 1 — Premium Onboarding Wizard

**Status: complete.** `OnboardingView.xaml` has been fully rewritten into a 4-step wizard. `OnboardingViewModel.cs` (841 lines) and `OnboardingView.xaml.cs` are unchanged and intact.

### Step overview

| Step | ViewModel property | Header (`Title`) | What it shows |
|------|--------------------|-------------------|---------------|
| 1 | `IsWelcomeStep` | `Title` → "Everything stays on this PC" | Three honest pillars (local-first, one service, limits of the lock) under `Icon.ShieldCheck`. |
| 2 | `IsPermissionsStep` | `Title` → "What AppGuardian needs from Windows" | Four rows: service (Check again), agent (Open startup settings), Hello (Open sign-in options), Notifications (Open notification settings). Each row has a `Badge.Status` fed by `ServiceStatus` / `AgentStatus` / `HelloStatus` / `NotificationsStatus`. |
| 3 | `IsSecurityStep` | `Title` → "Choose how you unlock" | Hello `CheckBox` (`PreferHello` / `HelloAvailable`) + the `HelloExplanation` paragraph; `PasswordBox x:Name="SecretBox"` + the 4-segment `Strength.Segment` meter bound to `StrengthScore` / `StrengthLevel`; `PasswordBox x:Name="ConfirmBox"`. |
| 4 | `IsReadyStep` | `Title` → "You're set up" | Success check badge + three quick-feature previews (Lock/Hide/Throttle). |

### Layout & spacing (settled)

- Root: `ScrollViewer` with `Padding="{StaticResource Pad.Page.Scrolling}"` → centred `StackPanel MaxWidth="520"`.
- **Progress rail** (top): a 5-column Grid, four `*` segments plus an `Auto` column reading "Step {StepIndex} of 4". Each segment is two Borders (`Rail.Segment` + `Rail.Segment.Fill`), the fill's `Visibility` driven by `IntAtLeast` with `ConverterParameter=1..4`. Segments 2–4 carry `Margin="{StaticResource Space.Inline.S}"` on both layers; segment 1 has none.
- **Card**: `Card.Elevated` — the one shadowed card, documented in `Theme.xaml` as existing for this purpose.
- **Step host**: a `Grid` holding all four panels in one cell via `BoolToVisible`; each panel's entrance runs from the host's `Grid.Style` with four `DataTrigger`s (one per `StepIndex` value), each with a named `BeginStoryboard` calling `WizardStepEnter` plus a paired `StopStoryboard`. A `TranslateTransform` on the host gives `WizardStepEnter` its slide-up target.
- **Banner:** the single line `<ContentControl Style="{StaticResource PageBanner}" />` — visibility not pinned at the use site.
- **Footer:** a 3-column Grid — `Back` (`Button.Quiet`) on the left, one primary button on the right whose `Content`/`Command` swap via `DataTrigger`: "Continue"+`NextCommand` (default) → "Turn on protection"+`FinishCommand` (Security) → "Open the dashboard"+`EnterDashboardCommand` (Ready). `IsDefault="True"` for Enter-to-submit.

### Deep links (XAML → C# contract)

Step 2's buttons bind `Command="{Binding OpenSettingsCommand}"` with `CommandParameter="{x:Static vm:OnboardingViewModel.StartupAppsUri}"`, `SignInOptionsUri`, and `NotificationsUri`. These constants are also on the `AllowedSettingsUris` allow-list in the view model, so the XAML cannot drift from the C# gate.

### Shell integration (task #12, complete)

`ShellViewModel.Navigate` now subscribes to `OnboardingViewModel.Completed` when the onboarding page is first created (`PageId.Onboarding` branch only). The handler clears the `OnboardingRequired` gate first, then navigates to `PageId.Apps` and calls `ReloadCurrentAsync()`. Unsubscription happens in `Dispose`. The `Onboarding DataTemplate` in `PageTemplates.xaml` no longer wraps in `Border/PageEnter` — its step-level entrance is sufficient.

---

## Deliverable 2 — Layout, Spacing & Visual Hierarchy Audit

### 2a. The spacing scale (already established in `Metrics.xaml`)

All views have been migrated onto this scale. No literal margin/padding values remain in any view XAML.

- **Stack:** `Space.Stack.XS (4px) → S (8px) → M (12px) → L (16px) → XL (24px)`
- **Inline:** `Space.Inline.S (8px) → M (12px)`
- **Page chrome:** `Pad.Page (24,16,24,16)`, `Pad.Page.Scrolling (24,16,24,24)`, `Pad.Header (24,16,24,16)` (now used by the MainWindow status strip)
- **Radii:** `Radius.Small (4)`, `Radius.Control (6)`, `Radius.Card (8)`, `Radius.Switch (9)`

### 2b. Cards, lists, and borders (rolled out)

| Control | Change applied |
|---------|----------------|
| `AppsView`, `LockedAppsView`, `HiddenAppsView`, `ActivityView`, `PowerView` | Banner block replaced with `<ContentControl Style="{StaticResource PageBanner}" />`. The copy-pasted 15-line `<Border Style="{StaticResource Banner}">` block is gone from all five views. |
| List cards (`Card` with `Padding="0"`) | Changed to `Card.Flush` so rows reach the edge and hover reads as part of the list. |
| `PasswordBox` inputs | `Style="{StaticResource Input.Base}"` added so they match `TextBox` styling. |
| `Refresh` buttons | Changed to `Button.Quiet` (secondary action, not primary). |
| Navigation buttons in lists | Changed to `Button.Quiet` where they were bare `<Button>`. |

### 2c. MainWindow status strip (rolled out)

- `Ellipse` dot replaced with a 12px `Border` based on `Badge`, using `CornerRadius="{StaticResource Radius.Switch}"` and `BorderThickness="0"`. The implicit `Ellipse.Style` that duplicated `State.Good` on both the element and the style is removed.
- Inlined `RepeatBehavior="Forever"` storyboard replaced with `Storyboard="{StaticResource PulseStatus}"` — same animation, defined once in `Animations.xaml`.
- `Background="{DynamicResource Surface.Card}"` replaced with `Style="{StaticResource Card}" BorderThickness="0,0,0,1"`.
- Padding changed from `"20,14"` to `Pad.Header` (24,16,24,16).
- `PauseButtonText` button styled as `Button.Quiet`; nav buttons use `Text.Body` instead of inline `FontSize="13"`.

---

## Deliverable 3 — UX Writing & Micro-copy Matrix

> Strings bound to C# properties are listed with their binding source so the ViewModel can be updated safely. Strings hardcoded in XAML can be changed directly.

### Status strip & shell

| Screen/Element | Old/Default Text | New Premium Copy | Old location | Rationale |
|---|---|---|---|---|
| — | (none, `StatusHeadline` was generic) | `StatusHeadline` = "Protecting" / "Paused" / "AppGuardian is waking up…" | `ShellViewModel.StatusHeadline` | "Protecting" is the state the user cares about; "AppGuardian is waking up…" replaces raw service-error text with a calm acknowledgment that resolves on its own. |

### Onboarding — Step 1 (Welcome)

| Screen/Element | Old/Default Text | New Premium Copy | Binding? | Rationale |
|---|---|---|---|---|
| Heading | (was the generic `Title`) | `Title` → "Everything stays on this PC" | C# `Title` property | Sets the privacy expectation with a concrete scope, not an abstract promise. |
| Lead | "Set a PIN or password first — it is what you will enter to open a locked app…" | "AppGuardian locks, hides and slows down apps on this PC. There is no account to create and nothing to sign in to." | XAML | Rewritten to lead with what the app *does*, not with the next step. The PIN comes later. |
| Pill 1 label | (none — inline) | "Everything stays local" | XAML | A label, not a sentence. |
| Pill 1 body | (none) | "No network connections, no telemetry, no cloud. Your rules live in a database on this PC." | XAML | Specifics over slogans. |
| Pill 2 label | (none) | "One small background service" | XAML | Corrects the assumption that multiple components need management. |
| Pill 2 body | (none) | "It starts with Windows and does nothing but enforce the rules you set. Close this window and your locks keep working." | XAML | Reassures that the UI is not the agent. |
| Pill 3 label | (none) | "What a lock is, and is not" | XAML | Frames the caveat as equal information, not a footnote. |
| Pill 3 body | (none) | "A lock is a prompt in front of an app, not a Windows security boundary. Someone with administrator access to this PC can get past it." | XAML | The caveat is stated up front because hiding it until after setup breeds distrust. |

### Onboarding — Step 2 (Permissions)

| Screen/Element | Old/Default Text | New Premium Copy | Binding? | Rationale |
|---|---|---|---|---|
| Heading | (was `Title`) | `Title` → "What AppGuardian needs from Windows" | C# `Title` | "Needs" is accurate; it does not ask for permissions it does not need. |
| Intro | (none) | "AppGuardian cannot change any of these for you. Each one opens the Windows setting it depends on, so you can see for yourself what is on." | XAML | Sets the expectation that these are status links, not grants. |
| Service row label | "Background service" | "Background service" | XAML | Unchanged — already accurate. |
| Service status | `ServiceStatus` ("Not responding" / "Ready") | `ServiceStatus` → "Ready" / "Not responding" | C# `ServiceStatus` | The badge colour carries the visual weight; the word confirms it. |
| Service action | "Grant Accessibility Permission" (per the original brief — this was never the real text) | "Check again" | XAML | No Settings page exists for a service. "Grant" would be dishonest. |
| Agent row label | (none) | "Desktop agent" | XAML | The agent draws the overlay prompt, not a "permission." |
| Agent status | `AgentStatus` | `AgentStatus` → "Ready" / "Not running" | C# | Same pattern as service. |
| Agent action | (none) | "Open startup settings" with `CommandParameter="{x:Static vm:OnboardingViewModel.StartupAppsUri}"` | XAML + C# URI | Deep-link to the real Settings page the user needs. |
| Hello row label | (none) | "Windows Hello" | XAML | The feature's own name. |
| Hello status | `HelloStatus` | `HelloStatus` → "Ready" / "Optional" | C# | "Optional" because locks work without it; the word says it is not a blocker. |
| Hello action | (none) | "Open sign-in options" → `SignInOptionsUri` | XAML + C# URI | Links to Windows' Hello enrollment. |
| Notifications row label | (none) | "Notifications" | XAML | Plain label. |
| Notifications status | `NotificationsStatus` | `NotificationsStatus` → "Optional" (constant) | C# | Windows exposes no reliable per-app answer; guessing would be worse than saying "Optional." |
| Notifications action | (none) | "Open notification settings" → `NotificationsUri` | XAML + C# URI | Lets the user check their own Settings. |

### Onboarding — Step 3 (Security)

| Screen/Element | Old/Default Text | New Premium Copy | Binding? | Rationale |
|---|---|---|---|---|
| Heading | (was `Title`) | `Title` → "Choose how you unlock" | C# `Title` | Frames the credential as the user's choice, not AppGuardian's demand. |
| Intro | (none) | "This is what AppGuardian asks for when you open a locked app. It is stored on this PC as a salted hash, which means it cannot be read back — not by anyone with the file, and not by AppGuardian itself." | XAML | States the security model up front. |
| Hello checkbox | "Use Windows Hello when it is available" | (unchanged) | XAML | Already user-centric. |
| Hello explanation | `HelloExplanation` | `HelloExplanation` (already premium copy in C#) | C# | "Windows Hello is ready on this PC…" or "Windows Hello is not set up…". |
| PIN label | (none) | "PIN" | XAML | Short, since the body below explains scope. |
| PIN hint | "At least six characters. Choose something you will remember — AppGuardian cannot recover it, and resetting it turns off every lock." | (unchanged) | XAML | Already correct. |
| Confirm label | "Enter it again" | (unchanged) | XAML | Already plain. |
| Strength meter | (none — new) | `StrengthLabel` reads "Weak" / "Fair" / "Good" / "Strong" | C# `StrengthLevel` | Colour is never the sole signal; the word sits beside the bar. |

### Onboarding — Step 4 (Ready)

| Screen/Element | Old/Default Text | New Premium Copy | Binding? | Rationale |
|---|---|---|---|---|
| Heading | (was `Title`) | `Title` → "You're set up" | C# `Title` | Confirms completion without a triumphalist tone. |
| Body | (none) | "Your PIN is saved. Nothing is protected yet — the dashboard is where you choose the first app and what should happen to it." | XAML | Bridges the gap to the action without a success banner. |
| Feature previews | (none) | "Lock an app…", "Hide an app…", "Throttle an app…" | XAML | Previews the three rule types so the user knows what comes next. |

### Onboarding footer

| Screen/Element | Old/Default Text | New Premium Copy | Binding? | Rationale |
|---|---|---|---|---|
| Back button | (was "Back") | "Back" | XAML | Unchanged — it is already the correct word for returning to a previous step. |
| Primary button | "Finish setup" | "Continue" / "Turn on protection" / "Open the dashboard" | XAML (via trigger) | "Turn on protection" is an action with clear stakes; "Open the dashboard" frames the transition as a handoff, not a dismissal. |

---

## Deliverable 4 — Empty States & Skeleton Loaders

### Empty states (rolled out)

All four empty states now use the `EmptyState` UserControl (`Controls/EmptyState.xaml`) — a 56px tinted `Icon.Accent` badge holding a 24px glyph, a `EmptyState.Title` headline, an `EmptyState.Text` body, and a single `Button.Primary` CTA. The badge/glyph ratio is set by `Icons.xaml`: a 24px glyph stretched to 56 keeps its 1.5px stroke and stays hairline, so weight comes from the badge, not a scaled stroke.

| View | Old text | New EmptyState (headline / body / CTA) |
|---|---|---|
| `LockedAppsView` | "No apps are locked yet. Choose one on the Apps page and turn its lock on." | Headline: "Nothing is locked yet" / Body: "Choose an app on the Apps page and turn its lock on." / CTA: "Open the Apps page" (navigates to `PageId.Apps`). |
| `HiddenAppsView` | "Nothing is set to be hidden. You can turn hiding on for an app from the Apps page." | Headline: "Nothing is set to be hidden" / Body: "Turn hiding on for an app from the Apps page, and its windows leave the taskbar and Alt+Tab until you bring them back." / CTA: "Open the Apps page". |
| `PowerView` | "No apps are limited yet. Choose an app on the Apps page and pick a battery profile for it." | Headline: "No limits are set yet" / Body: "Choose an app on the Apps page and pick a battery profile for it." / CTA: "Open the Apps page". |
| `ActivityView` | `Text="{Binding Message}"` | No EmptyState — the empty message is itself explanatory ("No activity yet" vs "Nothing matches your filter"), so a badge would be redundant. Uses `SkeletonList` while loading, then the message bound TextBlock. |

### Skeleton loaders (rolled out)

`SkeletonList` (`Theme.xaml`) renders three `Skeleton.Block` rows with varied bar widths. It is now wired into the two views where the list is genuinely empty during a load — its `Visibility` follows `IsLoading`, not `IsEmpty`, so it does not flash an empty-state headline at the wrong moment.

- `LockedAppsView`: `<ContentControl Style="{StaticResource SkeletonList}" Visibility="{Binding IsLoading, Converter={StaticResource BoolToVisible}}" />` on `Grid.Row="3"`.
- `ActivityView`: same placement and binding. The view model must set `IsBusy` around its `LoadAsync`; the skeleton shows until then.

---

## Deliverable 5 — Interactive Polish & Micro-interactions

### Button states (already in `Template.Button`, unchanged)

- **Hover:** `Interaction.HoverBrush` = `Surface.Hover` background shift + `Accent.Soft` border glow (`HoverBorderBrush`). `Motion.Hover = 0.14s`.
- **Pressed:** `Interaction.PressedBrush` = `Surface.Selected`. `Motion.Press = 0.09s`.
- **Focus:** `Interaction.FocusBrush` = `Accent`. A full perimeter `Border` with `Accent.Glow`, not the legacy `FocusVisualStyle` dotted line.

### Toggle (the Hello preference checkbox uses `CheckBox`, not `Toggle`, per the established rule)

A `CheckBox` is a value you save later; a `Toggle` switch is a state you change now. Onboarding Step 3 and Settings both use `CheckBox` because the preference is persisted by a confirm button, not applied instantly.

### List row hover (already in `Row` style, unchanged)

Opacity fade on a `Surface.Hover` layer + a 3px `Accent` marker that scales in on Y from the row's centre, so selection does not jog the row's content. `Motion.Base = 0.2s`.

### Wizard step entrance (new, in `OnboardingView.xaml`)

Each step panel fades in and slides up 12px over 0.22s (`WizardStepEnter`). The host's `Grid.Style` has four `DataTrigger`s, one per `StepIndex` value, each with a named `BeginStoryboard` and a paired `StopStoryboard` — the paired stop is required because `EnterActions` without an `ExitActions` leaves the animation's final value holding the property.

### Status dot pulse (MainWindow, new)

The degraded-service dot pulses via the shared `PulseStatus` storyboard (`Animations.xaml`), not an inline `RepeatBehavior="Forever"`. The entry sets `BeginStoryboard Name="PulseStory"`; the exit calls `StopStoryboard BeginStoryboardName="PulseStory"`.

---

## Files modified

| File | Change |
|---|---|
| `Views/OnboardingView.xaml` | Full rewrite: 4-step wizard, progress rail, step host with per-step entrance, badge status rows, strength meter, footer button swap. |
| `Views/PageTemplates.xaml` | Onboarding DataTemplate documented as intentionally lacking `PageEnter` (its entrance is per-step). |
| `ViewModels/ShellViewModel.cs` | `Navigate`: subscribes to `OnboardingViewModel.Completed` on first creation. New `OnOnboardingCompleted` handler: clears gate → navigates to Apps → refreshes. `Dispose`: unsubscribes. |
| `MainWindow.xaml` | Status strip: `Ellipse`→`Badge`-based dot; inline pulse→`PulseStatus`; `Banner`→`Card` style; padding→`Pad.Header`; nav button TextBlock→`Text.Body`; Pause button→`Button.Quiet`. |
| `Views/AppsView.xaml` | `Pad.Page` margin; `Banner`→`PageBanner`; list card→`Card.Flush`; literal spacings→tokens; filter TextBox→`Input.Base`; browse button→`Button.Quiet`. |
| `Views/LockedAppsView.xaml` | `Pad.Page`; `Banner`→`PageBanner`; list card→`Card.Flush`; empty state→`EmptyState` control; added `SkeletonList`. |
| `Views/HiddenAppsView.xaml` | `Pad.Page`; `Banner`→`PageBanner`; list cards→`Card`; empty state→`EmptyState`; buttons→`Button.Quiet`. |
| `Views/PowerView.xaml` | `Pad.Page`; `Banner`→`PageBanner`; list card→`Card.Flush`; "Running on battery"→`Text.Caption`; empty state→`EmptyState`; Refresh→`Button.Quiet`. |
| `Views/ActivityView.xaml` | `Pad.Page.Scrolling`; `Banner`→`PageBanner`; list card→`Card.Flush`; empty state→message-bound `TextBlock` + `SkeletonList`; inline spacings→tokens; Refresh→`Button.Quiet`; row labels→`Text.Label`. |
| `Views/SettingsView.xaml` | `Pad.Page.Scrolling`; `Banner`→`PageBanner`; cards→`Space.Stack.L`; inline spacings→tokens; field labels→`Text.Label`; PasswordBoxes→`Input.Base`; Refresh→`Button.Quiet`; Cancel margin→token. |

### Premise corrections surfaced (not worked around)

1. **D1 Step 2:** Windows has no "Accessibility permission" and no "Overlay permission" gate. The four rows are status reports with deep-links to Windows Settings, not permission grants — hence "Check again" and "Open Settings," never "Grant."
2. **D5:** VisualStateManager is inert on WPF's `ButtonBase`/`ToggleButton`/`TextBoxBase`. All animations use `Trigger.EnterActions`/`ExitActions` with paired `BeginStoryboard`/`StopStoryboard`.

### Compilation note

Nothing here has been compiled. `dotnet build -c Release` must be run on the Windows machine — no .NET SDK and no network are available in the Linux workspace. The highest-risk runtime item is the shared `ComboBox` template in `Theme.xaml`, which should be smoke-tested after any other change.
