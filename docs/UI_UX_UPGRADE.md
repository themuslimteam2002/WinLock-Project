# AppGuardian for Windows — UI/UX Engineering & Architecture Specification

## Executive Overview
AppGuardian for Windows has undergone a complete frontend/UX transformation and code-level validation pass. This document details the architectural decisions, design token scale, micro-interactions, copy matrix, and technical fixes applied across the codebase.

---

## 1. Bootstrapper & Distribution Pipeline Strategy
- **Packaging Model**: Framework-Dependent Single-File WPF Executable (~1.5 MB payload).
- **Rationale**: Replaced Native-AOT (which breaks WPF XAML/Dependency Properties) and Self-Contained (~65 MB). Ensures full native Windows UI capabilities with minimal download size.
- **Bootstrapper Tasks**: Prerequisites check (.NET 8 runtime detection), Service installation (sc.exe / ServiceController), Interactive desktop agent launch.

---

## 2. Onboarding Experience (4-Step Wizard)
Implemented in `OnboardingView.xaml` and `OnboardingViewModel.cs`.

### Step Sequence & Visual Structure
1. **Welcome & Privacy**: Local-first architecture emphasis. Honest pillars detailing local SQLite storage, Windows background service boundaries, and local PIN protection.
2. **System Diagnostic & Settings Deep-Links**: Live status checks for Service, Agent, Windows Hello, and Notifications. Provides direct `ms-settings:` URI deep-links:
   - `ms-settings:startupapps`
   - `ms-settings:signinoptions`
   - `ms-settings:notifications`
3. **Security Configuration & Real-Time Strength Meter**:
   - Hello preferences (`PreferHello` / `HelloAvailable`).
   - 4-segment real-time visual strength indicator evaluating length, character set complexity, and sequential patterns.
4. **Dashboard Reveal**: Visual previews of protection mechanisms (Locking, Hiding, EcoQoS Throttling) before transitioning to the primary application interface.

---

## 3. Design Tokens & Metrics Scale
Standardized in `Metrics.xaml` and applied globally across all views (`AppsView`, `LockedAppsView`, `HiddenAppsView`, `PowerView`, `ActivityView`, `SettingsView`, `MainWindow`):

### Spacing Scale
- `Space.Stack.XS` = 4px
- `Space.Stack.S` = 8px
- `Space.Stack.M` = 12px
- `Space.Stack.L` = 16px
- `Space.Stack.XL` = 24px
- `Pad.Page` = 24px padding around root view containers.
- `Pad.Header` = 16px vertical, 24px horizontal header padding.

### Component Standardizations
- **Card Containers**: Standardized on `Card.Flush` for list containers to eliminate floating gaps on row hover.
- **Page Chrome**: Replaced inline banner blocks with `<ContentControl Style="{StaticResource PageBanner}" />`.
- **Buttons & Inputs**: Standardized secondary actions to `Button.Quiet` and input fields to `Input.Base`.

---

## 4. Reusable Controls & Micro-Interactions

### Empty State Control (`Controls/EmptyState.xaml`)
Features a 56px tinted badge holding a 24px vector glyph, headline text, descriptive body text, and an action button. Integrated into `LockedAppsView`, `HiddenAppsView`, and `PowerView`.

### Animated Skeleton Loader
`SkeletonList` style with animated shimmer brushes in `Theme.xaml`. Bound to `IsLoading` property on view models for smooth feedback during application discovery and audit log loading.

### Micro-Interactions & Storyboards
- **Hover & Focus**: `Interaction.HoverBrush` color shifts, subtle glow effects on hover, and focused accent rings.
- **Selection Marker**: `Row` style in `Theme.xaml` includes a 3px vertical accent indicator that slides in smoothly on item selection.
- **Status Dot Pulse**: Global `PulseStatus` storyboard in `Animations.xaml` provides an opacity cycle for degraded/uninitialized protection states.
- **Wizard Step Transitions**: `WizardStepEnter` slide-up transitions (220ms ease-out) on step changes in the onboarding wizard.

---

## 5. XAML Schema & Code Quality Fixes

### Fix 1: Invalid Storyboard Tag in `MainWindow.xaml`
- **Issue**: `<Storyboard Storyboard="{StaticResource PulseStatus}" />` was nested inside `<BeginStoryboard>`, causing a XAML parser exception.
- **Resolution**: Refactored to `<BeginStoryboard Name="PulseStory" Storyboard="{StaticResource PulseStatus}" />`.

### Fix 2: Duplicate Style Property in `HiddenAppsView.xaml`
- **Issue**: `TextBlock` declared both `Style="{StaticResource Text.Small}"` attribute and a local `<TextBlock.Style>` element, causing build error `MC3024`.
- **Resolution**: Removed `Style="{StaticResource Text.Small}"` attribute from the element tag while maintaining `BasedOn="{StaticResource Text.Small}"` in the inner `<Style>`.

---

## 6. Windows Host Build & Verification Guide

To compile and verify the application on a Windows host machine:

```bash
# 1. Open terminal in project root
cd "D:\All Builds\Claude Build\Winlock Project"

# 2. Compile all solution projects in Release configuration
dotnet build -c Release

# 3. Execute unit test suite
dotnet test

# 4. Publish bootstrapper as single-file framework-dependent executable
dotnet publish src\AppGuardian.Bootstrapper -c Release -r win-x64 --no-self-contained

# 5. Build installer executable using Inno Setup
iscc installer\AppGuardian.iss
```

---
*Report published on 2026-09-14.*
