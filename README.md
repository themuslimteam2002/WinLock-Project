# AppGuardian for Windows

<p align="center">
  <strong>Lock, hide, and throttle Windows applications.</strong><br/>
  Local-first. No cloud. No telemetry. No license keys.
</p>

<p align="center">
  <a href="#features">Features</a> · <a href="#screenshots">Screenshots</a> · <a href="#installation">Installation</a> · <a href="#usage">Usage</a> · <a href="#build-from-source">Build from Source</a> · <a href="#contributing">Contributing</a> · <a href="#license">License</a>
</p>

---

AppGuardian is a free and open-source Windows desktop utility that gives you control over your applications:

- **Lock** selected apps behind Windows Hello or a PIN/password
- **Hide** selected apps on a dedicated hidden virtual desktop
- **Restrict** selected apps from excessive battery and CPU drain

Everything works offline. No accounts, no network calls, no analytics, no subscriptions.

> **Project status: Build succeeded (0 errors).** The solution builds cleanly in Release configuration (`dotnet build -c Release`). All 4 projects (UI, Agent, Service, Shared), IPC communication pipelines, MVVM layer, design system reconciliation, and accessibility features are complete and verified. Remaining tasks are runtime QA validation and Inno Setup installer compilation.

## Features

### 🔒 App Lock
- Lock any desktop application behind authentication
- Unlock with Windows Hello (fingerprint, face, Windows PIN) or a custom PIN/password
- Full-screen lock overlay appears when a locked app gains focus
- Multi-monitor support — overlay covers the correct display
- Temporary unlock with configurable timeout (1–60 minutes)
- Rate-limited failed attempts with escalating backoff

### 👁️ Hidden Apps
- Move selected apps to a hidden virtual desktop/workspace
- Hidden apps are invisible on the main desktop
- Reveal with a single click after authentication
- Persistent across reboots — hidden state survives restart

### ⚡ Battery & CPU Restriction
- Apply power profiles to background apps (Balanced, Low Impact, Strict Savings), or leave an app with no rule at all
- CPU throttling via Windows Job Objects
- Process priority reduction
- EcoQoS power hints on supported hardware
- Profiles apply to running processes without restart

### 🛡️ Security & Privacy
- All data stored locally — nothing leaves your PC
- Credentials stored as salted PBKDF2-HMAC-SHA256 hashes (600k iterations)
- No plain-text passwords, no reversible encryption
- Atomic file writes prevent data corruption on power loss
- Audit log records all lock/unlock/hide actions

### 📊 Dashboard
- Real-time status of all protected, locked, hidden, and throttled apps
- System health indicators for service, agent, and subsystems
- Activity log with searchable history
- Light and dark themes, plus "follow Windows", switchable at runtime from Settings → Appearance (see ADR-015). The lock overlay stays dark in both.

## Screenshots

| Screenshot | Description |
|---|---|
| ![Dashboard](docs/screenshots/dashboard.png) | **Dashboard** — Live protection status dot, quick actions, and sidebar navigation |
| ![App Lock & Rules](docs/screenshots/app-lock-rules.png) | **App Lock** — Application picker and inline lock/hide/power rule configuration |
| ![Lock Overlay](docs/screenshots/lock-overlay.png) | **Lock Overlay** — Fullscreen protective cover with Windows Hello biometric unlock |
| ![Hidden Apps](docs/screenshots/hidden-apps.png) | **Hidden Apps** — Isolate applications on a hidden virtual desktop |
| ![Battery & CPU](docs/screenshots/power-profiles.png) | **Battery & CPU** — Process scheduler throttling and efficiency mode |
| ![Activity Log](docs/screenshots/activity-log.png) | **Activity Log** — Local, append-only security and unlock audit trail |
| ![Settings](docs/screenshots/settings.png) | **Settings** — Theme selection, PIN updates, and limitation disclosures |

## System Requirements

| Requirement | Detail |
|---|---|
| **OS** | Windows 10 22H2 (build 19045) or later; Windows 11 23H2+ recommended |
| **Architecture** | x64 only (ARM64 out of scope for MVP) |
| **Runtime** | .NET 8 Desktop Runtime (x64) — the installer checks for it |
| **Disk Space** | ~25 MB installed |
| **Privileges** | Administrator (for installation only) |

### Optional
- **Windows Hello** — fingerprint reader, IR camera, or Windows PIN for biometric unlock
- **Virtual Desktop support** — Windows 10 20H1+ for full hidden apps functionality

## Installation

### Download and Install

1. Download `AppGuardian-0.1.0-x64-setup.exe` from the [Releases](../../releases) page
2. Run the installer — it will request administrator privileges via UAC
3. The installer will:
   - Check for the .NET 8 Desktop Runtime (and tell you where to get it if missing)
   - Install application files to `C:\Program Files\AppGuardian`
   - Register and start the AppGuardian Protection Service
   - Register the user-session agent to start at logon
   - Create a Start Menu entry
   - Optionally create a desktop shortcut

> **SmartScreen warning:** The installer is not code-signed in the MVP release. Windows SmartScreen may show a warning on first run. Click "More info" → "Run anyway" to proceed. This is expected and documented.

### Silent Install

```powershell
AppGuardian-0.1.0-x64-setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

### Verify Installation

After installation, confirm the service is running:

```powershell
sc query AppGuardian.Service
# Expected: STATE = RUNNING
```

## First-Run Setup

On first launch, AppGuardian requires you to set up authentication:

1. **Welcome screen** — overview of features
2. **Authentication setup**:
   - If Windows Hello is available, you can enable it as your primary unlock method
   - **A backup PIN or password is always required** (minimum 4 characters)
   - This PIN/password is used when Windows Hello is unavailable
3. **Setup complete** — you're taken to the dashboard

> Your PIN/password is hashed with PBKDF2-HMAC-SHA256 (600,000 iterations) and a per-credential random salt. The original value is never stored.

## Usage

### Locking an App

1. Go to **App Lock** in the sidebar
2. Click **Lock an App**
3. Select an application from the picker (installed apps or browse for an EXE)
4. The app is now locked — when anyone tries to open it, a lock overlay appears
5. Authenticate with Windows Hello or your PIN to unlock

### Hiding an App

1. Go to **Hidden Apps** in the sidebar
2. Click **Hide an App**
3. Select the application to hide
4. The app is moved to a hidden virtual desktop
5. To reveal: click **Reveal** and authenticate

> **Important:** Hidden apps remain visible in Task Manager and to process-enumeration tools. AppGuardian does not use kernel-level hiding. This is a privacy convenience feature, not a security boundary.

### Restricting Battery/CPU Usage

1. Go to **Power** in the sidebar
2. Click **Add Restriction**
3. Select the app and choose a power profile:
   - **No rule** — nothing is recorded for this app. Windows schedules it normally. This is what "Remove restriction" leaves behind.
   - **Balanced** — a rule exists but imposes no CPU cap and normal priority. Distinct from *No rule*: it records an explicit decision to leave the app alone, which survives in the policy file.
   - **Low Impact** — capped at about 25% of total CPU, below-normal priority, EcoQoS requested
   - **Strict Savings** — capped at about 10% of total CPU, idle priority, EcoQoS requested
4. Restrictions apply to running processes immediately

### Temporary Unlock

When unlocking a locked app, you can choose **"Unlock for X minutes"** for a temporary session. The app re-locks automatically after the timeout expires.

## Limitations

These matter and are stated plainly:

- **Hiding is best-effort.** Hidden apps are moved to a separate virtual desktop. They remain visible in Task Manager and to other process-enumeration tools. AppGuardian does not use kernel-level or stealth techniques to conceal processes.
- **Throttling is not a battery guarantee.** Actual savings depend on workload, hardware, and Windows scheduling. AppGuardian caps CPU share and applies power hints; it cannot make an app free.
- **This is not an anti-tamper security boundary.** A local administrator can always stop the service, edit ACLs, or uninstall. AppGuardian is a privacy and convenience tool for your own machine, not a defence against someone who already has admin rights on it.
- **Some apps cannot be controlled.** Exclusive-fullscreen games, anti-cheat-protected titles, elevated processes, and system processes may resist locking or hiding. Where this happens, AppGuardian discloses it rather than failing silently.
- **Virtual Desktop support varies.** The virtual desktop API is partially undocumented and may behave differently across Windows builds. AppGuardian uses a versioned adapter layer to handle this.
- **Single user per install.** The MVP targets the interactive user who installs it. Multi-user scenarios are out of scope.

## Troubleshooting

See [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md) for detailed troubleshooting guidance covering:

- Service not running
- Agent not starting
- Lock overlay not appearing
- Windows Hello unavailable
- PIN reset
- Virtual desktop issues
- Antivirus false positives

## Architecture

Four projects, split by Windows session isolation requirements:

```
AppGuardian.UI        WPF dashboard. Configuration UI. Holds no authoritative state.
AppGuardian.Agent     Interactive session. Lock overlays, Windows Hello, window hooks, virtual desktops.
AppGuardian.Service   LocalSystem. Policy store, process monitor, CPU/power enforcement, watchdog.
AppGuardian.Shared    Models, IPC contracts, DTOs, OS-integration interfaces. Referenced by all.
```

Communication: local Named Pipes with JSON envelopes. No network listeners. Pipe ACLs restrict access to SYSTEM, Administrators, and the interactive user.

### Local Data

| Path | Contents |
|---|---|
| `%ProgramData%\AppGuardian\policy.json` | Machine-wide app rules. Admin/SYSTEM write, Users read. |
| `%ProgramData%\AppGuardian\credentials.dat` | Salted PBKDF2 hashes only. Never a reversible secret. |
| `%ProgramData%\AppGuardian\audit.log` | Append-only event log, rotated at 5 MB × 3 files. |
| `%LocalAppData%\AppGuardian\settings.json` | Per-user UI preferences. |

All JSON writes are atomic (write-temp-then-replace) so a power loss cannot corrupt policy.

## Build from Source

### Prerequisites

| Tool | Version |
|---|---|
| .NET SDK | 8.0 or later |
| Inno Setup | 6.0 or later (for installer only) |
| OS | Windows 10/11 x64 (for UI, Agent, Service projects) |

### Build

```powershell
# Clone the repository
git clone https://github.com/hafiz/appguardian.git
cd appguardian

# Restore and build
dotnet restore
dotnet build -c Release

# Run tests
dotnet test
```

`AppGuardian.Shared` targets `net8.0` and builds on any platform. The other projects target `net8.0-windows` and require Windows.

### Package the Installer

```powershell
# Publish all three executables into a single output folder
dotnet publish src\AppGuardian.Service\AppGuardian.Service.csproj -c Release -r win-x64 --self-contained false -o dist\
dotnet publish src\AppGuardian.Agent\AppGuardian.Agent.csproj     -c Release -r win-x64 --self-contained false -o dist\
dotnet publish src\AppGuardian.UI\AppGuardian.UI.csproj           -c Release -r win-x64 --self-contained false -o dist\

# Compile the installer
iscc installer\AppGuardian.iss
```

One shared output folder is deliberate: the three executables share `AppGuardian.Shared.dll`, and separate folders would allow a partial upgrade to pair a new service with an old agent.

The installer is unsigned until a code-signing certificate is obtained (FR-106). SmartScreen will warn on first run.

## Project Structure

```
├── .github/
│   └── workflows/
│       └── build.yml              CI build pipeline
├── docs/
│   ├── SRS.md                     Software Requirements Specification
│   ├── API_DESIGN.md              IPC contracts and data models
│   ├── PROJECT_PLAN.md            Milestones, tasks, and timeline
│   ├── DECISIONS.md               Architecture decision records
│   ├── SPEC_CONFLICTS.md          Specification conflicts and resolutions
│   ├── VERIFICATION.md            What was statically checked, and what was not
│   ├── TROUBLESHOOTING.md         User troubleshooting guide
│   ├── RELEASE_NOTES.md           Release notes
│   └── RELEASE_HANDOFF.md         Final release checklist
├── installer/
│   └── AppGuardian.iss            Inno Setup installer script
├── src/
│   ├── AppGuardian.Shared/        Shared models, contracts, DTOs
│   ├── AppGuardian.Service/       Windows Service (policy, process monitor)
│   ├── AppGuardian.Agent/         User-session agent (overlays, Hello, VD)
│   └── AppGuardian.UI/            WPF dashboard application
├── tests/
│   └── AppGuardian.Tests/         Unit and integration tests
├── Directory.Build.props          Shared build configuration
├── AppGuardian.sln                Solution file
├── LICENSE                        MIT license
├── README.md                      This file
├── CONTRIBUTING.md                Contributor guidelines
└── CHANGELOG.md                   Version history
```

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines on:

- Setting up your development environment
- Code style and conventions
- Pull request process
- Issue reporting

All contributions are welcome. This is a volunteer, open-source project — please be patient and respectful.

## License

MIT. See [LICENSE](LICENSE) for the full text.

Copyright © 2026 Hafiz.
