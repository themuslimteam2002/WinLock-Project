# Screenshot & Media Plan — AppGuardian for Windows

**Document Version:** 2.0  
**Status:** ✅ Ready to Capture (UI Verified & Reconciled)  
**Target Directory:** `docs/screenshots/`

---

## Overview

This document defines the exact visual assets, compositions, captions, and accessibility alt text required for the README, release packaging, and documentation. All UI views have been verified and reconciled against the design system.

---

## Directory Structure

All media assets are stored in `docs/screenshots/`:

```
docs/
└── screenshots/
    ├── dashboard.png
    ├── dashboard-light.png
    ├── onboarding-setup.png
    ├── apps-picker.png
    ├── app-lock-rules.png
    ├── lock-overlay.png
    ├── lock-overlay-hello.png
    ├── hidden-apps.png
    ├── hidden-apps-empty.png
    ├── power-profiles.png
    ├── activity-log.png
    ├── settings.png
    ├── settings-light.png
    ├── settings-reset.png
    └── installer-step.png
```

---

## Technical Specifications

| Property | Specification | Rationale |
|---|---|---|
| **Format** | PNG (lossless 24-bit with alpha) | Clean UI text rendering and crisp borders |
| **Window Resolution** | 1020 × 700 px | Default window size as defined in `MainWindow.xaml` |
| **Overlay Resolution** | 1920 × 1080 px | Fullscreen lock overlay representation |
| **Display Scaling** | 100% DPI (96 DPI) | Prevents scaling artifacts and blurry sub-pixels |
| **Primary Theme** | Dark Theme (`Palette.Dark.xaml`) | Default brand aesthetic |
| **Secondary Theme** | Light Theme (`Palette.Light.xaml`) | Provided for Hero Dashboard & Settings views |
| **Window State** | Windowed (Centered, unmaximized) | Shows subtle window corner radii and borders |
| **Data Context** | Realistic local app samples (Chrome, VS Code, Slack, Terminal) | Professional demonstration without leaking PII |

---

## Required Visual Assets & Captions

### 1. Hero Dashboard (`dashboard.png` / `dashboard-light.png`)
- **View:** `MainWindow.xaml` with `AppsView` or `LockedAppsView` active.
- **State:** Status strip showing green dot (`Protecting — all background systems active`), quick actions (`Pause`, `Lock now`), and sidebar navigation.
- **Caption:** "AppGuardian dashboard showing active protection status, quick controls, and sidebar navigation."
- **Alt Text:** "Screenshot of AppGuardian for Windows dashboard displaying active protection status dot and app navigation tabs in dark theme."

### 2. First-Run Setup (`onboarding-setup.png`)
- **View:** `OnboardingView.xaml`
- **State:** Centered card with Windows Hello toggle enabled and PIN input fields.
- **Caption:** "First-run setup: configure Windows Hello biometrics and fallback PIN."
- **Alt Text:** "AppGuardian first-run onboarding screen showing PIN configuration form and biometric authentication settings."

### 3. Application Selector & Rule Editor (`apps-picker.png`)
- **View:** `AppsView.xaml` with an application selected.
- **State:** Left pane showing installed application list with search filter; right pane showing inline Rule Editor cards (Lock, Hide, Battery).
- **Caption:** "App picker and inline rule configuration: configure lock, workspace hiding, and CPU limits in one view."
- **Alt Text:** "AppGuardian Apps view with application list on the left and rule configuration cards on the right."

### 4. Locked Applications Monitor (`app-lock-rules.png`)
- **View:** `LockedAppsView.xaml`
- **State:** List of protected apps with temporary unlock countdown timer ("Unlocked for 14:22") and active "Lock now" action button.
- **Caption:** "Locked applications monitor showing active unlock countdown timers and immediate re-lock controls."
- **Alt Text:** "Locked apps screen showing active apps, temporary unlock remaining times, and Lock Now buttons."

### 5. Fullscreen Lock Overlay (`lock-overlay.png` / `lock-overlay-hello.png`)
- **View:** `LockOverlayWindow.cs`
- **State:** Semi-transparent 97% opaque backdrop over a protected window with centered auth prompt and biometric button.
- **Caption:** "Secure lock overlay preventing unauthorized access until PIN or Windows Hello is verified."
- **Alt Text:** "Full screen semi-transparent lock overlay covering a desktop app with a Windows Hello prompt in the center."

### 6. Hidden Apps Workspace (`hidden-apps.png`)
- **View:** `HiddenAppsView.xaml`
- **State:** Displaying apps assigned to the isolated virtual desktop with "Bring back" and "Stop hiding" controls.
- **Caption:** "Hidden apps manager: isolate sensitive application windows on a separate desktop workspace."
- **Alt Text:** "Hidden apps management screen with reveal and unhide action buttons."

### 7. Battery & CPU Restrictions (`power-profiles.png`)
- **View:** `PowerView.xaml`
- **State:** List of power-managed processes showing power profiles (Balanced, Low Impact, Strict Savings) and live EcoQoS throttling status.
- **Caption:** "Battery & CPU throttling: enforce process scheduling caps and Windows efficiency mode."
- **Alt Text:** "Power restriction view showing CPU rate limit percentage and efficiency mode indicators per application."

### 8. Local Audit Log (`activity-log.png`)
- **View:** `ActivityView.xaml`
- **State:** Chronological log entries of locks, unlocks, failed authentication attempts, and rule modifications.
- **Caption:** "Local activity log: tamper-resistant audit trail stored strictly on this PC."
- **Alt Text:** "Activity log interface displaying timestamps, action types, actors, and outcomes of app security events."

### 9. Settings & Disclosures (`settings.png` / `settings-light.png`)
- **View:** `SettingsView.xaml`
- **State:** Theme selection radio buttons, PIN update card, diagnostic health indicators, and honest limitation disclosures.
- **Caption:** "Settings: theme preferences, credential management, and honest system boundary disclosures."
- **Alt Text:** "Settings screen with light/dark theme selection, PIN modification form, and diagnostic service versions."

### 10. Inno Setup Direct Installer (`installer-step.png`)
- **View:** `installer/AppGuardian.iss` runtime dialog
- **State:** Modern Inno Setup wizard showing administrative prerequisite verification and service installation.
- **Caption:** "Automated direct-download installer configuring Windows Service and user session components."
- **Alt Text:** "AppGuardian Inno Setup installation wizard dialog window on Windows."

---

## README Embed Map

The project `README.md` embeds these screenshots in priority sequence:

```markdown
<!-- Hero -->
![AppGuardian Dashboard](docs/screenshots/dashboard.png)

<!-- Feature Highlights -->
![App Lock and Rules](docs/screenshots/app-lock-rules.png)
![Lock Screen Overlay](docs/screenshots/lock-overlay.png)
![Hidden Apps](docs/screenshots/hidden-apps.png)
![Battery and CPU Management](docs/screenshots/power-profiles.png)
```
