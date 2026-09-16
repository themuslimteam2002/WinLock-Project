# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

- Runtime QA test execution on Windows 10 22H2 and Windows 11 23H2 clean VMs.
- Binary packaging and installer generation via Inno Setup.

## [0.1.0] — 2026-08-26

### Solution Build & Stability
- **Build Status:** Build succeeded with 0 errors across all 4 solution projects (`AppGuardian.UI`, `AppGuardian.Agent`, `AppGuardian.Service`, `AppGuardian.Shared`).

### UI, Design System & Accessibility
- **Design System Reconciliation:** Reconciled typography scale, surface colors, fluent control templates, card borders, and spacing tokens in `Theme.xaml`.
- **Theme Switching:** High-contrast WCAG AA compliant light and dark palettes (`Palette.Dark.xaml`, `Palette.Light.xaml`) with live runtime switching and 250ms opacity cross-fade.
- **Micro-Interactions & Animations:** Added 200ms cubic ease-out entrance transitions for page views, subtle pulse animations on degraded status indicators, and hover/pressed states on buttons.
- **Toast Notification Overlay:** Implemented non-intrusive `ToastHost` overlay and `ToastService` queue with auto-dismiss timers and level-based accent styling.
- **Accessibility:** Added explicit `AutomationProperties.Name` across all interactive controls (Buttons, TextBoxes, PasswordBoxes, CheckBoxes, RadioButtons, ComboBoxes, and ListBoxes), verified keyboard focus indicators, and ensured screen-reader compatibility.
- **Calm & Honest UI Copy:** Audited all user-facing strings to maintain a calm, local-first tone with transparent disclosures of technical limitations (virtual desktop hiding, CPU rate limits, and administrative boundaries).

### Core Features
- **App Lock:** Lock selected desktop applications behind Windows Hello biometrics or custom PIN/password authentication with configurable unlock duration (1–60 minutes) and escalating rate-limit backoffs.
- **Hidden Apps:** Move selected apps to a hidden virtual desktop with persistent state and one-click reveal actions.
- **Battery & CPU Restriction:** Enforce process CPU scheduling rate limits, priority reduction, and EcoQoS hints via Windows Job Objects.
- **Authentication:** Salted PBKDF2-HMAC-SHA256 credential hashing (600,000 iterations) with DPAPI-backed local storage.
- **Local Audit Log:** Tamper-resistant, append-only security log recording locks, unlocks, and configuration changes directly to `%ProgramData%\AppGuardian`.
- **System Diagnostics:** Live background service and agent health monitoring with degraded state detection.

### Installer & Packaging
- **Inno Setup Script:** Elevated installer (`installer/AppGuardian.iss`) with automated service registration, auto-start, HKLM agent run key, .NET 8 Desktop Runtime prerequisite verification, and safe uninstaller with data retention choice.
- **Per-Monitor DPI Awareness:** PerMonitorV2 manifest configuration for crisp multi-monitor rendering.

### Known Limitations
- Installer and binaries are unsigned (SmartScreen prompt expected on first launch).
- Virtual Desktop COM API may fall back to minimization on legacy or unsupported Windows builds.
- Hidden applications remain visible in Windows Task Manager.
- Elevated processes (running as Administrator) cannot be covered by unelevated lock overlays.
- x64 architecture only (ARM64 out of scope for MVP).
- English language only (localization planned for subsequent releases).
- Single interactive user session supported per machine.

[Unreleased]: ../../compare/v0.1.0...HEAD
[0.1.0]: ../../releases/tag/v0.1.0
