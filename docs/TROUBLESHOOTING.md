# Troubleshooting Guide

## AppGuardian for Windows

This guide covers common issues and their solutions. If your problem isn't listed here, please [open an issue](../../issues/new/choose) with the details described in [Reporting a Bug](#reporting-a-bug).

---

## Table of Contents

1. [Service Issues](#service-issues)
2. [Agent Issues](#agent-issues)
3. [Lock Overlay Issues](#lock-overlay-issues)
4. [Authentication Issues](#authentication-issues)
5. [Hidden Apps Issues](#hidden-apps-issues)
6. [Battery/CPU Restriction Issues](#batterycpu-restriction-issues)
7. [Installer Issues](#installer-issues)
8. [Uninstaller Issues](#uninstaller-issues)
9. [Antivirus & SmartScreen](#antivirus--smartscreen)
10. [Log Files & Diagnostics](#log-files--diagnostics)
11. [Reporting a Bug](#reporting-a-bug)

---

## Service Issues

### Service is not running

**Symptoms:** Dashboard shows "Service: Stopped", warning banner appears, rules cannot be created or modified.

**Diagnosis:**
```powershell
sc query AppGuardian.Service
```

**Solutions:**

1. **Restart the service:**
   ```powershell
   sc start AppGuardian.Service
   ```

2. **If the service is missing (not registered):**
   ```powershell
   # Re-register the service (run as Administrator)
   sc create "AppGuardian.Service" binPath= "C:\Program Files\AppGuardian\AppGuardian.Service.exe" start= auto DisplayName= "AppGuardian Protection Service"
   sc start "AppGuardian.Service"
   ```

3. **If the service fails to start:**
   - Check the Windows Event Viewer: `Event Viewer → Windows Logs → Application`
   - Look for entries from source "AppGuardian.Service"
   - Common cause: .NET 8 Desktop Runtime not installed
   - Reinstall the runtime from https://dotnet.microsoft.com/download/dotnet/8.0

4. **Repair install:** Run the installer again — it will re-register the service.

### Service keeps stopping

**Possible causes:**
- Unhandled exception in the service process
- Insufficient permissions on `%ProgramData%\AppGuardian`
- Corrupted policy.json file

**Solutions:**
1. Check Event Viewer for crash details
2. Verify permissions:
   ```powershell
   icacls "$env:ProgramData\AppGuardian"
   # Expected: BUILTIN\Users:(OI)(CI)(RX), BUILTIN\Administrators:(OI)(CI)(F)
   ```
3. If policy.json is corrupted, rename it and restart the service — it will create a fresh empty policy:
   ```powershell
   Rename-Item "$env:ProgramData\AppGuardian\policy.json" "policy.json.bak"
   sc start AppGuardian.Service
   ```

---

## Agent Issues

### Agent is not starting at logon

**Diagnosis:**
```powershell
# Check if the registry entry exists
Get-ItemProperty "HKLM:\Software\Microsoft\Windows\CurrentVersion\Run" | Select-Object AppGuardianAgent
```

**Solutions:**

1. **Re-register the agent startup entry:**
   ```powershell
   # Run as Administrator
   reg add "HKLM\Software\Microsoft\Windows\CurrentVersion\Run" /v "AppGuardianAgent" /t REG_SZ /d "`"C:\Program Files\AppGuardian\AppGuardian.Agent.exe`"" /f
   ```

2. **Start the agent manually:**
   ```powershell
   Start-Process "C:\Program Files\AppGuardian\AppGuardian.Agent.exe"
   ```

3. **If the agent crashes immediately:** Check Event Viewer for errors. Verify .NET 8 Desktop Runtime is installed.

### Multiple agent instances running

This should not happen. If it does:
```powershell
taskkill /F /IM "AppGuardian.Agent.exe" /T
Start-Process "C:\Program Files\AppGuardian\AppGuardian.Agent.exe"
```

---

## Lock Overlay Issues

### Lock overlay does not appear

**Possible causes:**
- Agent is not running
- The app is elevated (running as Administrator) and the agent is not
- Exclusive fullscreen mode
- Anti-cheat protection blocking window hooks

**Solutions:**
1. Verify the agent is running: check Task Manager for `AppGuardian.Agent.exe`
2. If the locked app runs as Administrator, AppGuardian's user-level agent cannot intercept it. This is a [known limitation](#limitations).
3. Restart the agent
4. Check the audit log for errors:
   ```
   %ProgramData%\AppGuardian\audit.log
   ```

### Lock overlay appears on the wrong monitor

This can happen when monitors are rearranged after a rule was created. The overlay should reposition automatically. If it doesn't:
1. Restart the agent
2. The agent detects the monitor where the locked app's window is and positions the overlay accordingly

### Lock overlay flickers or disappears briefly

**Possible cause:** Another topmost window is competing for z-order.

**Solution:** This is a known edge case with some apps that use aggressive topmost windowing. AppGuardian will attempt to re-assert topmost status.

---

## Authentication Issues

### Windows Hello is not available

**Possible causes:**
- No biometric hardware (fingerprint reader, IR camera)
- Windows Hello not configured in Windows Settings
- Hardware disabled in BIOS/UEFI

**Solutions:**
1. Configure Windows Hello: `Settings → Accounts → Sign-in options`
2. If no biometric hardware is available, use the PIN/password fallback — this is always available
3. AppGuardian detects Hello availability at startup and adjusts the UI automatically

### "Incorrect PIN or password"

- Verify you're entering the PIN/password you set during AppGuardian onboarding, **not** your Windows login PIN
- AppGuardian's PIN/password is separate from your Windows account password

### Locked out after too many attempts

AppGuardian rate-limits failed authentication:
- After 5 failed attempts: 30-second cooldown
- The cooldown resets after a successful attempt or waiting the full period
- If you've forgotten your PIN entirely, see [PIN Reset](#pin-reset)

### PIN Reset

If you've forgotten your AppGuardian PIN/password:

1. **From the dashboard** (if accessible): Go to `Settings → Authentication → Reset Credentials`
2. **Manual reset** (requires Administrator):
   ```powershell
   # Stop the service
   sc stop AppGuardian.Service
   
   # Remove the credentials file (this will trigger the onboarding setup again)
   Remove-Item "$env:ProgramData\AppGuardian\credentials.dat"
   
   # Restart the service
   sc start AppGuardian.Service
   ```
   ⚠️ This removes all authentication data. You will need to complete first-run setup again.

---

## Hidden Apps Issues

### "Virtual Desktop support is not available"

**Cause:** The virtual desktop COM API is not available on your Windows version or build.

**Solutions:**
- Ensure you're running Windows 10 20H1 (build 19041) or later
- Some Windows editions may not include virtual desktop APIs
- This is a [known limitation](#limitations) — AppGuardian uses a versioned adapter and reports when the API is unavailable

### Hidden app reappears after restart

**Possible causes:**
- The app starts before the agent (race condition)
- The agent failed to start at logon

**Solution:**
1. Verify the agent starts at logon (see [Agent Issues](#agent-issues))
2. The agent should hide apps as they launch. If the app starts before the agent, there may be a brief window where it is visible.

### Cannot reveal a hidden app

1. Verify the agent is running
2. Authenticate when prompted
3. If the virtual desktop containing the hidden app was deleted externally (via Task View), the app may need to be relaunched

---

## Battery/CPU Restriction Issues

### Power restriction not being applied

**Possible causes:**
- The target app is not currently running
- The target app is elevated (Administrator) and the service cannot apply Job Objects to it
- EcoQoS not supported on your hardware

**Solutions:**
1. Restrictions apply to **running** processes. If the app is not running, the restriction will apply when it starts.
2. Verify the service is running
3. Check if your CPU supports EcoQoS: this requires Windows 11 and compatible hardware

### App performance drops too much with Strict Savings

Change the power profile to **Low Impact** or **Balanced** for that app in the Power page.

### Restrictions don't persist after reboot

Restrictions are stored in `policy.json` and re-applied when the service starts. If they're not being re-applied:
1. Verify the service starts at boot (start type should be Automatic)
2. Check `policy.json` for the rule entry

---

## Installer Issues

### Installer fails to run

- **"This app can't run on your PC"**: You're on a 32-bit or ARM system. AppGuardian requires x64 Windows.
- **UAC prompt doesn't appear**: The installer requires elevation. Right-click → "Run as administrator".
- **".NET 8 Desktop Runtime not found"**: Download from https://dotnet.microsoft.com/download/dotnet/8.0 — choose the **Desktop Runtime** for x64.

### Installer hangs during service registration

The installer stops the existing service before upgrading. If the service is unresponsive:
1. Open Task Manager and end `AppGuardian.Service.exe`
2. Re-run the installer

### Upgrade install fails

1. Uninstall the existing version first
2. Install the new version fresh
3. Your settings and rules in `%ProgramData%\AppGuardian` are preserved unless you chose to remove them during uninstall

---

## Uninstaller Issues

### Uninstall doesn't remove the service

If the service is still registered after uninstall:
```powershell
sc stop "AppGuardian.Service"
sc delete "AppGuardian.Service"
```

### Agent still starts after uninstall

Remove the startup entry manually:
```powershell
reg delete "HKLM\Software\Microsoft\Windows\CurrentVersion\Run" /v "AppGuardianAgent" /f
```

### Files left behind after uninstall

The uninstaller asks whether to remove user data. If you chose "No", these remain:
- `%ProgramData%\AppGuardian\` — rules, credentials, audit log
- `%LocalAppData%\AppGuardian\` — UI preferences

To remove them manually:
```powershell
Remove-Item -Recurse -Force "$env:ProgramData\AppGuardian"
Remove-Item -Recurse -Force "$env:LocalAppData\AppGuardian"
```

---

## Antivirus & SmartScreen

### SmartScreen warns about the installer

**This is expected.** The MVP release is not code-signed. SmartScreen warns about any unsigned executable downloaded from the internet.

**To proceed:**
1. Click **"More info"**
2. Click **"Run anyway"**

This is documented and not a security issue. The source code is fully open for inspection.

### Antivirus flags AppGuardian

Some antivirus products may flag AppGuardian because it:
- Installs a Windows Service
- Registers a startup program
- Uses window hooks (for lock overlay)
- Modifies process priority and CPU affinity

**These are legitimate features, not malicious behavior.**

To resolve:
1. Add the AppGuardian installation folder to your antivirus exclusion list: `C:\Program Files\AppGuardian\`
2. If your AV quarantined a file, restore it and add an exclusion
3. Report the false positive to your AV vendor

---

## Log Files & Diagnostics

### Log locations

| Log | Path | Contents |
|---|---|---|
| Audit log | `%ProgramData%\AppGuardian\audit.log` | Lock/unlock/hide/reveal actions |
| Service log | Windows Event Viewer → Application | Service start/stop/crash events |
| Installer log | Run with `/LOG="install.log"` flag | Installer operations |

### Generating a diagnostic report

```powershell
# Service status
sc query AppGuardian.Service

# Agent running?
Get-Process -Name "AppGuardian.Agent" -ErrorAction SilentlyContinue

# Agent startup entry
Get-ItemProperty "HKLM:\Software\Microsoft\Windows\CurrentVersion\Run" | Select-Object AppGuardianAgent

# .NET runtime check
dotnet --list-runtimes | Select-String "WindowsDesktop"

# Policy file exists?
Test-Path "$env:ProgramData\AppGuardian\policy.json"

# Credentials file exists?
Test-Path "$env:ProgramData\AppGuardian\credentials.dat"
```

### How to restart everything

```powershell
# Restart the service
sc stop AppGuardian.Service
sc start AppGuardian.Service

# Restart the agent
taskkill /F /IM "AppGuardian.Agent.exe" 2>$null
Start-Process "C:\Program Files\AppGuardian\AppGuardian.Agent.exe"
```

---

## Reporting a Bug

When reporting an issue, please include:

1. **Windows version**: Run `winver` and note the version and build number
2. **AppGuardian version**: Check in `Settings → About` or the installer filename
3. **Steps to reproduce**: What you did, step by step
4. **Expected behavior**: What should have happened
5. **Actual behavior**: What actually happened
6. **Diagnostic output**: Run the diagnostic commands above and include the output
7. **Screenshots**: If applicable, especially for UI issues
