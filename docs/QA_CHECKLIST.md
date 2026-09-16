# QA Checklist — AppGuardian for Windows v0.1.0

## Test Environment Setup

Before testing, record:
- [ ] Windows version and build number (`winver`)
- [ ] Display configuration (resolution, DPI scaling, monitor count)
- [ ] Windows Hello availability (fingerprint, face, none)
- [ ] .NET 8 Desktop Runtime installed? (version)
- [ ] Laptop or desktop (battery present?)
- [ ] Antivirus product and version

---

## Automated Test Gate

- [ ] **ATG-01**: Run `dotnet test AppGuardian.sln --configuration Release` before every release.
- [ ] **ATG-02**: Command completes with a pass/fail result; investigate any timeout, abort, or hang.
- [ ] **ATG-03**: All async unit tests have a five-second timeout budget.
- [ ] **ATG-04**: Unit tests use fakes or test-only names and never connect to production named pipes.
- [ ] **ATG-05**: Authorization tests reject missing, expired, reused, and SID-mismatched grants.

---

## 1. Installation

### 1.1 Clean Install
- [ ] **INS-01**: Installer launches and shows UAC prompt
- [ ] **INS-02**: Cancelling UAC aborts installation
- [ ] **INS-03**: Installer checks for .NET 8 Desktop Runtime and warns if missing
- [ ] **INS-04**: License (MIT) displayed and accepted
- [ ] **INS-05**: Default install path is `C:\Program Files\AppGuardian`
- [ ] **INS-06**: "Create desktop shortcut" option works when selected
- [ ] **INS-07**: "Create desktop shortcut" option skipped when unchecked
- [ ] **INS-08**: Installation completes without errors
- [ ] **INS-09**: Service registered: `sc query AppGuardian.Service` returns `RUNNING`
- [ ] **INS-10**: Agent startup entry exists in `HKLM\...\Run`
- [ ] **INS-11**: Start Menu entries created (AppGuardian + Uninstall)
- [ ] **INS-12**: `%ProgramData%\AppGuardian` directory created with correct ACLs
- [ ] **INS-13**: Dashboard launches when "Open AppGuardian" is checked
- [ ] **INS-14**: Agent process visible in Task Manager after install

### 1.2 Silent Install
- [ ] **INS-15**: `/VERYSILENT /SUPPRESSMSGBOXES` installs without UI
- [ ] **INS-16**: Service registered and started after silent install
- [ ] **INS-17**: Agent startup entry registered after silent install

### 1.3 Upgrade Install
- [ ] **INS-18**: Running installer over existing install stops the service
- [ ] **INS-19**: Running installer over existing install stops the agent
- [ ] **INS-20**: Files are replaced without errors
- [ ] **INS-21**: Service re-registered and restarted
- [ ] **INS-22**: Existing `policy.json` and `credentials.dat` preserved
- [ ] **INS-23**: User preferences preserved

### 1.4 Repair Install
- [ ] **INS-24**: Running installer after manually deleting the service re-registers it
- [ ] **INS-25**: Running installer after removing the Run key re-creates it

### 1.5 Uninstall
- [ ] **UNI-01**: Uninstaller stops the agent process
- [ ] **UNI-02**: Uninstaller stops the service
- [ ] **UNI-03**: Uninstaller deletes the service: `sc query AppGuardian.Service` returns error
- [ ] **UNI-04**: Uninstaller removes the Run key
- [ ] **UNI-05**: Program files removed from `C:\Program Files\AppGuardian`
- [ ] **UNI-06**: Start Menu entries removed
- [ ] **UNI-07**: Desktop shortcut removed (if created)
- [ ] **UNI-08**: "Remove settings and history?" prompt appears
- [ ] **UNI-09**: Choosing "Yes" removes `%ProgramData%\AppGuardian`
- [ ] **UNI-10**: Choosing "Yes" removes `%LocalAppData%\AppGuardian`
- [ ] **UNI-11**: Choosing "No" preserves both data directories
- [ ] **UNI-12**: No orphaned processes remain after uninstall

---

## 2. First-Run & Onboarding

- [ ] **OB-01**: First launch shows onboarding welcome screen
- [ ] **OB-02**: Welcome screen displays feature overview (Lock, Hide, Power)
- [ ] **OB-03**: "Get Started" button advances to auth setup
- [ ] **OB-04**: Auth setup detects Windows Hello availability
- [ ] **OB-05**: When Hello is available: toggle to enable/disable
- [ ] **OB-06**: When Hello is unavailable: info notice displayed
- [ ] **OB-07**: PIN/password field validates minimum 4 characters
- [ ] **OB-08**: PIN confirmation field must match
- [ ] **OB-09**: Error message shown for mismatched PINs
- [ ] **OB-10**: "Complete Setup" creates credentials and advances
- [ ] **OB-11**: Completion screen shows success with "Go to Dashboard" button
- [ ] **OB-12**: Onboarding cannot be skipped
- [ ] **OB-13**: After completion, subsequent launches skip onboarding

---

## 3. Authentication

### 3.1 Windows Hello
- [ ] **AUTH-01**: Hello prompt appears when attempting to unlock a locked app
- [ ] **AUTH-02**: Successful Hello verification unlocks the app
- [ ] **AUTH-03**: Cancelled Hello prompt falls back to PIN entry
- [ ] **AUTH-04**: Hello unavailable after hardware change shows PIN-only UI

### 3.2 PIN/Password
- [ ] **AUTH-05**: PIN field accepts input and masks characters
- [ ] **AUTH-06**: Correct PIN unlocks the app
- [ ] **AUTH-07**: Incorrect PIN shows "Incorrect" error with remaining attempts
- [ ] **AUTH-08**: After 5 failed attempts: 30-second cooldown enforced
- [ ] **AUTH-09**: Cooldown timer counts down visually
- [ ] **AUTH-10**: After cooldown: attempts reset
- [ ] **AUTH-11**: Enter key submits the PIN

### 3.3 Temporary Unlock
- [ ] **AUTH-12**: "Unlock for X minutes" option visible on lock overlay
- [ ] **AUTH-13**: Temporary unlock requires authentication
- [ ] **AUTH-14**: App re-locks automatically after timeout expires
- [ ] **AUTH-15**: Closing the app during temp unlock session re-locks on next open

---

## 4. App Lock

### 4.1 Rule Management
- [ ] **LOCK-01**: "Lock an App" button opens app picker
- [ ] **LOCK-02**: App picker shows installed applications
- [ ] **LOCK-03**: Search/filter works in app picker
- [ ] **LOCK-04**: Manual browse ("Add by file path") works
- [ ] **LOCK-05**: Created rule appears in the lock rules list
- [ ] **LOCK-06**: Rule card shows app name, path, status, toggle
- [ ] **LOCK-07**: Toggle enables/disables rule without deleting
- [ ] **LOCK-08**: Edit button opens edit dialog
- [ ] **LOCK-09**: Delete button shows confirmation dialog
- [ ] **LOCK-10**: Confirming delete removes the rule
- [ ] **LOCK-11**: Empty state shown when no rules exist

### 4.2 Lock Enforcement
- [ ] **LOCK-12**: Launching a locked app triggers lock overlay
- [ ] **LOCK-13**: Overlay appears within 1 second of app gaining focus
- [ ] **LOCK-14**: Overlay covers the entire monitor (not just the app window)
- [ ] **LOCK-15**: Overlay is topmost — cannot be moved behind
- [ ] **LOCK-16**: Alt+Tab away from locked app keeps it locked
- [ ] **LOCK-17**: Clicking the overlay backdrop does not dismiss it
- [ ] **LOCK-18**: Escape key keeps the app locked (overlay remains)
- [ ] **LOCK-19**: Successful auth closes overlay and shows the app

### 4.3 Multi-Monitor
- [ ] **LOCK-20**: Overlay appears on the monitor where the locked app window is
- [ ] **LOCK-21**: Moving the app to a different monitor updates overlay position
- [ ] **LOCK-22**: DPI-scaled monitors show correctly sized overlay

### 4.4 Persistence
- [ ] **LOCK-23**: Lock rules survive system reboot
- [ ] **LOCK-24**: Lock rules survive service restart
- [ ] **LOCK-25**: Lock rules survive agent restart

---

## 5. Hidden Apps

### 5.1 Rule Management
- [ ] **HIDE-01**: "Hide an App" button works
- [ ] **HIDE-02**: App appears in hidden apps list after hiding
- [ ] **HIDE-03**: "Reveal" button requires authentication
- [ ] **HIDE-04**: Successful auth reveals the app on the main desktop
- [ ] **HIDE-05**: Delete rule removes the hide rule
- [ ] **HIDE-06**: Empty state shown when no apps are hidden

### 5.2 Virtual Desktop
- [ ] **HIDE-07**: App window moves to the hidden virtual desktop
- [ ] **HIDE-08**: App is not visible on the main desktop
- [ ] **HIDE-09**: App is not visible in Alt+Tab on the main desktop
- [ ] **HIDE-10**: Revealing moves the app back to the main desktop
- [ ] **HIDE-11**: Hidden state persists across reboots

### 5.3 Fallback
- [ ] **HIDE-12**: Warning banner shown if Virtual Desktop API is unavailable
- [ ] **HIDE-13**: Feature degrades gracefully (no crash) on unsupported Windows builds

---

## 6. Battery/CPU Restriction

### 6.1 Rule Management
- [ ] **PWR-01**: "Add Restriction" button works
- [ ] **PWR-02**: Power profile selection (Balanced, Low Impact, Strict Savings) works
- [ ] **PWR-03**: Rule appears in power rules list
- [ ] **PWR-04**: Remove restriction button works
- [ ] **PWR-05**: Empty state shown when no restrictions exist

### 6.2 Enforcement
- [ ] **PWR-06**: Applying Strict Savings reduces CPU usage of the target app
- [ ] **PWR-07**: Applying Low Impact reduces process priority
- [ ] **PWR-08**: EcoQoS hint applied on supported hardware
- [ ] **PWR-09**: Removing restriction restores original CPU/priority settings
- [ ] **PWR-10**: Restrictions apply to currently running processes
- [ ] **PWR-11**: Restrictions apply when target app starts after rule creation

### 6.3 Persistence
- [ ] **PWR-12**: Power rules survive reboot
- [ ] **PWR-13**: Power rules re-applied to processes when service starts

---

## 7. Dashboard & UI

### 7.1 Dashboard
- [ ] **UI-01**: Dashboard shows correct count for protected, locked, hidden, throttled apps
- [ ] **UI-02**: Health indicators (Service, Agent, VD, Hello) show correct status
- [ ] **UI-03**: Health dots use correct colors (green=running, yellow=degraded, red=stopped)
- [ ] **UI-04**: Quick action cards navigate to correct pages
- [ ] **UI-05**: Recent activity section shows events or empty state

### 7.2 Navigation
- [ ] **UI-06**: Sidebar navigation works for all 7 pages
- [ ] **UI-07**: Active page highlighted in sidebar
- [ ] **UI-08**: Badge counts appear on Lock, Hide, Power nav items
- [ ] **UI-09**: Health dot visible on Status nav item

### 7.3 Theme
- [ ] **UI-10**: Dark theme renders correctly (text legible, contrast adequate)
- [ ] **UI-11**: Light theme renders correctly
- [ ] **UI-12**: Theme follows Windows system preference on startup
- [ ] **UI-13**: Manual theme switch works in Settings

### 7.4 Edge Cases
- [ ] **UI-14**: Long app names are truncated with ellipsis, not clipped
- [ ] **UI-15**: Empty state panels shown when no rules/data exist
- [ ] **UI-16**: Error state shown when service is unreachable
- [ ] **UI-17**: Warning banner appears when service or agent is down

### 7.5 Accessibility
- [ ] **UI-18**: All interactive elements have AutomationProperties.Name
- [ ] **UI-19**: Tab key navigates through all controls
- [ ] **UI-20**: Enter/Space activates buttons
- [ ] **UI-21**: Focus ring visible on keyboard-focused elements
- [ ] **UI-22**: Contrast ratio meets WCAG AA (4.5:1 for text)

### 7.6 DPI & Display
- [ ] **UI-23**: UI renders correctly at 100% scaling
- [ ] **UI-24**: UI renders correctly at 125% scaling
- [ ] **UI-25**: UI renders correctly at 150% scaling
- [ ] **UI-26**: UI renders correctly at 200% scaling
- [ ] **UI-27**: Moving window between monitors with different DPI works

---

## 8. Settings

- [ ] **SET-01**: Auth method display shows current method
- [ ] **SET-02**: "Change PIN" flow works
- [ ] **SET-03**: "Start with Windows" toggle works
- [ ] **SET-04**: "Start minimized" toggle works
- [ ] **SET-05**: Theme selection (Light/Dark/System) works
- [ ] **SET-06**: Temporary unlock duration can be changed
- [ ] **SET-07**: Pause protection requires authentication

---

## 9. Service Health

- [ ] **HLT-01**: Service status displayed correctly
- [ ] **HLT-02**: Agent status displayed correctly
- [ ] **HLT-03**: Version numbers shown
- [ ] **HLT-04**: Service uptime shown
- [ ] **HLT-05**: Virtual Desktop support status shown
- [ ] **HLT-06**: Windows Hello availability shown
- [ ] **HLT-07**: IPC connection status shown
- [ ] **HLT-08**: Restart buttons work (when applicable)

---

## 10. Stress & Edge Cases

- [ ] **EDGE-01**: Killing the service while dashboard is open shows warning
- [ ] **EDGE-02**: Killing the agent while a lock overlay is shown — overlay reappears when agent restarts
- [ ] **EDGE-03**: Multiple rapid lock/unlock cycles don't crash
- [ ] **EDGE-04**: Creating 50+ rules doesn't degrade UI performance
- [ ] **EDGE-05**: Locking an app that is already closed — overlay appears when app next opens
- [ ] **EDGE-06**: Hiding an app that is closed — hides when it next opens
- [ ] **EDGE-07**: Shutting down Windows while overlay is shown — clean shutdown
- [ ] **EDGE-08**: Suspending/resuming the laptop — rules re-applied on wake

---

## Sign-Off

| Role | Name | Date | Signature |
|---|---|---|---|
| QA Tester | | | |
| Developer | | | |
| Project Owner | Hafiz | | |
