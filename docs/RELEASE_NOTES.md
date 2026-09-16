# Release Notes — AppGuardian for Windows

## v0.1.0 — MVP Release

**Release Date:** TBD
**Installer:** `AppGuardian-0.1.0-x64-setup.exe`
**Runtime Dependency:** .NET 8 Desktop Runtime (x64)

---

### ✨ New Features

#### App Lock
- Lock any desktop application behind authentication
- Windows Hello (fingerprint, face, Windows PIN) unlock support
- Custom PIN/password fallback authentication
- Full-screen lock overlay on protected app focus
- Multi-monitor overlay support
- Temporary unlock with configurable timeout (1–60 min)
- Failed attempt rate limiting with escalating backoff (5 attempts → 30s cooldown)
- Lock state persists across reboots

#### Hidden Apps
- Hide selected apps on a dedicated hidden virtual desktop
- One-click reveal after authentication
- Virtual desktop adapter with feature detection for Windows build compatibility
- Hidden state persists across reboots
- Graceful fallback when virtual desktop API is unavailable

#### Battery & CPU Restriction
- Three power profiles: Balanced, Low Impact, Strict Savings
- CPU throttling via Windows Job Objects
- Process priority reduction (BelowNormal, Idle)
- EcoQoS power hints on supported hardware
- Apply/remove restrictions to running processes without restart

#### Authentication
- First-run mandatory authentication setup
- Windows Hello availability detection
- PIN/password stored as salted PBKDF2-HMAC-SHA256 hashes (600k iterations)
- Rate-limited failed attempts with escalating cooldown

#### Dashboard & UI
- Real-time status dashboard with protection counts
- System health indicators (Service, Agent, Virtual Desktop, Windows Hello)
- Quick action cards for Lock, Hide, and Power
- Activity/audit log viewer
- Settings page (theme, auth, startup, protection pause)
- Service health page with component status
- Dark and light themes following Windows system preference
- Per-monitor DPI awareness (PMv2)

#### System
- Windows Service (`AppGuardian.Service`) for policy enforcement
- User-session agent for overlays and window hooks
- Named Pipe IPC with JSON envelopes
- Atomic file writes for policy and credential storage
- Service auto-restart on crash (3 attempts with escalating delay)
- Inno Setup installer with admin elevation, service registration, and agent startup

### ⚠️ Known Limitations

- **No code signing.** The installer and binaries are unsigned. Windows SmartScreen will show a warning on first run. Click "More info" → "Run anyway".
- **Hiding is best-effort.** Hidden apps remain visible in Task Manager and process-enumeration tools. No kernel-level hiding.
- **Virtual Desktop API is partially undocumented.** Behavior may vary across Windows builds. AppGuardian uses a versioned adapter and will report when the API is unavailable.
- **Some apps cannot be controlled.** Exclusive-fullscreen games, anti-cheat-protected titles, elevated processes, and system processes may resist locking or hiding.
- **Single user per install.** Multi-user session support is out of scope for the MVP.
- **ARM64 not supported.** x64 only for this release.
- **English only.** No localization in this release.
- **No cloud sync.** All data is local to the machine.

### 🔧 Fixed Issues

> This is the initial release. No prior bugs to fix.

### 📋 Upgrade Notes

> This is the initial release. No upgrade path applies.

### 🖥️ Compatibility

| OS | Status |
|---|---|
| Windows 10 22H2 (build 19045) | Supported (minimum) |
| Windows 11 23H2 | Supported (recommended) |
| Windows 11 24H2+ | Supported |

**Requires:** .NET 8 Desktop Runtime (x64). The installer checks for this and directs you to the download page if missing.

---

### 📝 Full Changelog

See [CHANGELOG.md](../CHANGELOG.md) for the complete version history.

### 🐛 Report Issues

Found a bug or have a suggestion? [Open an issue](../../issues/new/choose).
