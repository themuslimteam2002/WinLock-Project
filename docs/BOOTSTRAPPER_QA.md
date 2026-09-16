# Bootstrapper QA Checklist

## Build Verification

- [ ] **BLD-01**: `dotnet publish` for Bootstrapper produces a single .exe under 2 MB
- [ ] **BLD-02**: On a machine with the .NET 8 Desktop Runtime (x64), the EXE launches with no other files alongside it
- [ ] **BLD-03**: On a clean machine *without* the runtime, the EXE shows Windows' own "install the .NET Desktop Runtime" dialog and exits without a crash report. This is expected, not a defect — the download page must name the prerequisite next to the button
- [ ] **BLD-04**: VirusTotal scan passes for the published single-file executable
- [ ] **BLD-05**: The SHA-256 hash in `BootstrapperWindow.xaml.cs` matches the published payload zip

## Download Flow

- [ ] **DL-01**: Bootstrapper window appears centered, with correct dimensions (460x280)
- [ ] **DL-02**: Progress bar updates smoothly during download
- [ ] **DL-03**: Speed label updates (e.g., "3.2 MB/s") without flickering
- [ ] **DL-04**: Status label updates: "Preparing download..." → "Verifying integrity..." → "Installing..."
- [ ] **DL-05**: Cancel button halts the download and exits cleanly
- [ ] **DL-06**: Network timeout is handled gracefully with a user-friendly error

## Security Verification

- [ ] **SEC-01**: After download completes, SHA-256 hash verification runs
- [ ] **SEC-02**: On hash mismatch: shows "Security verification failed" dialog and aborts
- [ ] **SEC-03**: On hash match: silently launches the Inno Setup installer
- [ ] **SEC-04**: Bootstrapper process exits after handing off to the installer
- [ ] **SEC-05**: The downloaded payload is deleted from the temp directory after extraction

## Installer Integration

- [ ] **INST-01**: The Inno Setup installer accepts the temp extraction directory as its working path
- [ ] **INST-02**: The installer's custom preferences page renders correctly
- [ ] **INST-03**: "Start desktop agent at user logon" checkbox controls the launchagent task
- [ ] **INST-04**: "Add to Explorer context menu" checkbox writes the registry entry
- [ ] **INST-05**: "Create desktop shortcut" checkbox creates the shortcut
- [ ] **INST-06**: "Open AppGuardian Dashboard now" checkbox launches the UI after install
- [ ] **INST-07**: Service registration and startup occur correctly regardless of preferences

---

## Publish Commands

### Bootstrapper (single-file, framework-dependent)

PowerShell — one line. Do not use `^` continuations; that is cmd.exe syntax and PowerShell will
try to run `-c` and `-o` as cmdlets.

```powershell
dotnet publish src\AppGuardian.Bootstrapper\AppGuardian.Bootstrapper.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist\bootstrapper\
```

`PublishTrimmed` is not used: the SDK rejects it for WPF with NETSDK1168, because XAML resolves
types by name at runtime and the trimmer cannot see those references.

### Main Installer (service + agent + dashboard)

```powershell
# All three publish into the same folder deliberately — see the AppGuardian.iss header.
dotnet publish src\AppGuardian.Service\AppGuardian.Service.csproj -c Release -r win-x64 --self-contained false -o dist\
dotnet publish src\AppGuardian.Agent\AppGuardian.Agent.csproj -c Release -r win-x64 --self-contained false -o dist\
dotnet publish src\AppGuardian.UI\AppGuardian.UI.csproj -c Release -r win-x64 --self-contained false -o dist\

# Compile the Inno Setup script
iscc installer\AppGuardian.iss
```

The installer binary is at `dist\installer\AppGuardian-0.1.0-x64-setup.exe`.
The bootstrapper binary is at `dist\bootstrapper\AppGuardianBootstrapper.exe`.
