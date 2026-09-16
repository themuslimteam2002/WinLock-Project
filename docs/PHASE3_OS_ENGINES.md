# Phase 3 — OS Engine Implementations

## 1. App Lock Engine (foreground hook → overlay)

### P/Invoke surface — src/AppGuardian.Agent/Native/NativeMethods.cs

```csharp
[DllImport("user32.dll", SetLastError = true)]
internal static extern nint SetWinEventHook(
    uint eventMin, uint eventMax, nint hmodWinEventProc,
    WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

[DllImport("user32.dll", SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
internal static extern bool UnhookWinEvent(nint hWinEventHook);

internal delegate void WinEventProc(
    nint hWinEventHook, uint eventType, nint hwnd,
    int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

[DllImport("user32.dll", SetLastError = true)]
internal static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);
```

Constants: `EventSystemForeground = 0x0003`, `WineventOutOfContext = 0x0000`, `WineventSkipOwnProcess = 0x0002`.

### Hook host — src/AppGuardian.Agent/Foreground/ForegroundWatcher.cs

- Installs the hook on a dedicated STA thread with its own `GetMessage` loop (lines 57-107).
- `OnWinEvent` filters to `idObject == OBJID_WINDOW` only, calls `GetWindowThreadProcessId`, skips self-PID.
- Raises `ForegroundChanged` with executable path + window title on the hook thread.
- The delegate is pinned for the hook's lifetime (the `WinEventProc` field on line 32).

### Coordination — src/AppGuardian.Agent/Coordination/LockCoordinator.cs

On foreground change:
1. Checks `_overlay.IsVisible` → if up, re-asserts topmost (NFR-P4 budget preserved).
2. Resolves appId via `AppIdentity.DeriveAppId(path, null)`.
3. Under `_gate`: checks `_paused`, `_unlocked`, `_lockedApps` membership.
4. Calls `_overlay.Show(rule.Identity, _watcher.GetMonitors())`.

### IPC registration — src/AppGuardian.Agent/Ipc/AgentDispatcher.cs

```csharp
dispatcher
    .On(MessageTypes.AuthVerifyWindowsHello, VerifyHelloAsync)
    .On(MessageTypes.LockShowOverlay, ShowOverlayAsync)
    .On(MessageTypes.HideEnsureWorkspace, EnsureWorkspaceAsync)
    .On(MessageTypes.HideMoveToHidden, MoveToHiddenAsync)
    .On(MessageTypes.HideReveal, RevealAsync)
    .On(MessageTypes.SystemPing, PingAsync);
```

`ShowOverlayAsync` forwards the incoming request to `LockCoordinator.ShowOverlayFor`, which calls the overlay directly — no UI→Agent pipe needed.

---

## 2. Hide Apps Engine (Virtual Desktop COM Interop)

### COM interfaces — src/AppGuardian.Agent/Desktops/VirtualDesktopInterop.cs

```csharp
[ComImport]
[Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IServiceProvider
{
    [PreserveSig]
    int QueryService(ref Guid service, ref Guid riid, out IntPtr ppvObject);
}

[ComImport]
[Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IVirtualDesktopManager
{
    [PreserveSig]
    int IsWindowOnCurrentVirtualDesktop(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] out bool onCurrent);

    [PreserveSig]
    int GetWindowDesktopId(IntPtr hwnd, out Guid desktopId);

    [PreserveSig]
    int MoveWindowToDesktop(IntPtr hwnd, ref Guid desktopId);
}
```

CLSID constants:
- `ImmersiveShellClsid = {C2F03A33-21F5-47FA-B4BB-156362A2F239}`
- `VirtualDesktopManagerInternalClsid = {C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B}`
- `VirtualDesktopManagerClsid = {AA509086-5CA9-4C25-8F95-589D3C07B48A}`

### Adapter — src/AppGuardian.Agent/Desktops/VirtualDesktopAdapter.cs

Feature-detected at startup via `EnsureProbed()` (line 227): attempts `GetWindowDesktopId` on the shell window; falls back cleanly to `ShowWindow(SW_HIDE)` when the COM interface layout differs.

- `MoveWindow(hwnd, target)`: calls `IVirtualDesktopManager.MoveWindowToDesktop`. Falls back to `HideWithFallback`.
- `MoveToCurrent(hwnd)`: resolves the current desktop GUID via `GetShellWindow`, then moves the window back.
- Per-PID fallback table `_fallbackHidden` preserves minimize state for correct restoration.

### Coordination — src/AppGuardian.Agent/Coordination/HideCoordinator.cs

```csharp
public DesktopId EnsureWorkspace() => _desktops.EnsureHidden(PolicyConstants.HiddenWorkspaceName);
public async Task<HideResult> HideAsync(MoveToHiddenRequest request, CancellationToken ct)
{
    var target = EnsureWorkspace();
    ...
    _desktops.MoveWindow(request.Hwnd, target);
}
```

SC-03: SC-03 is not resolved per the 2026-08-25 rulings; see docs/ for resolution status.
---

## 3. Battery/CPU Restriction Engine (Job Objects)

### P/Invoke surface — src/AppGuardian.Service/Native/NativeMethods.cs

```csharp
[LibraryImport(Kernel32, EntryPoint = "CreateJobObjectW",
    StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
internal static partial nint CreateJobObject(nint securityAttributes, string? name);

[LibraryImport(Kernel32, SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
internal static partial bool AssignProcessToJobObject(nint job, nint process);

[LibraryImport(Kernel32, SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
internal static partial bool SetInformationJobObject(
    nint job, JobObjectInfoClass infoClass, nint info, uint infoLength);

[LibraryImport(Kernel32, SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
internal static partial bool IsProcessInJob(
    nint process, nint job, [MarshalAs(UnmanagedType.Bool)] out bool result);

[LibraryImport(Kernel32, SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
internal static partial bool SetProcessInformation(
    nint process, ProcessInformationClass infoClass, nint info, uint infoLength);

[LibraryImport(Kernel32, SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
internal static partial bool SetPriorityClass(nint process, PriorityClass priorityClass);

[LibraryImport(Kernel32, SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
internal static partial bool GetSystemPowerStatus(out SystemPowerStatus status);
```

### Structs

```csharp
[StructLayout(LayoutKind.Sequential)]
internal struct JobObjectCpuRateControlInformation
{
    public JobCpuRateControlFlags ControlFlags;
    public uint CpuRate; // hundredths of a percent; 25% = 2500
}

[StructLayout(LayoutKind.Sequential)]
internal struct ProcessPowerThrottlingState
{
    public uint Version;     // CurrentVersion = 1
    public uint ControlMask; // EXECUTION_SPEED = 0x1
    public uint StateMask;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SystemPowerStatus
{
    public byte AcLineStatus;   // 0 = battery, 1 = AC, 255 = unknown
    public byte BatteryFlag;
    public byte BatteryLifePercent;
    public byte SystemStatusFlag;
    public uint BatteryLifeTime;
    public uint BatteryFullLifeTime;
    public bool IsOnBattery => AcLineStatus == 0;
}
```

### Controller — src/AppGuardian.Service/Power/JobObjectPowerController.cs

Core flow in `ApplyThrottle(int pid, PowerProfile profile, bool reducePriority, bool ecoQos)`:
1. Resolves the spec via `PowerProfileMap.Resolve(profile)`.
2. OpenProcess with `ProcessAccess.ThrottleRights` (limited: `SetQuota | SetInformation | QueryInformation | Terminate`).
3. Checks `IsProcessInJob` — if already jobbed, applies priority + EcoQoS only (FR-509 surfaces this).
4. `CreateJobObject(null, null)` → unnamed local job.
5. `SetCpuRate(job, percent)` via `SetInformationJobObject` with `CpuRateControlInformation`, `ControlFlags = Enable | HardCap`.
6. `AssignProcessToJobObject(job, process)`.
7. `SetPriorityClass` + `SetProcessInformation` for EcoQoS if `ecoQos && SupportsEcoQos`.
8. Tracks `(pid → Throttle)` in `ConcurrentDictionary`; `RemoveThrottle` restores priority, clears EcoQoS, closes handles.

EcoQoS probe (`ProbeEcoQos`, line 397): calls `SetProcessInformation` on self with `EXECUTION_SPEED` set, then clears it — detects availability without trusting OS version checks.

### Dispatcher wiring — src/AppGuardian.Service/Ipc/ServiceDispatcher.cs

```csharp
dispatcher
    .On(MessageTypes.PowerSetProfile, SetPowerProfileAsync)
    .On(MessageTypes.PowerGetStatus, GetPowerStatusAsync);
```

`SetPowerProfileAsync` (line 598):
- Parses `SetPowerProfileRequest` from the envelope payload.
- Saves the rule via `PolicyUpsert`.
- If `request.Profile is null`: calls `_powerController.RemoveThrottle(pid)` for every running instance immediately.
- Audit-trails "Power profile set to {Profile}" or "Power restriction removed."
- Returns `RuleResult` with the persisted rule.

`GuardianClient` surfaces this to the UI as `SetPowerProfileAsync(appId, settings)` in `src/AppGuardian.UI/Services/GuardianClient.cs`.

---

## Wiring summary

| Layer | Message type | Handler | OS call |
|---|---|---|---|
| Foreground | local (hook callback) | `LockCoordinator.OnForegroundChanged` | `SetWinEventHook` callback |
| Lock overlay | `lock.showOverlay` | `LockCoordinator.ShowOverlayFor` | WPF `Window.Show` (TopMost) |
| Hide window | `hide.moveToHidden` | `AgentDispatcher.HideAsync` → `HideCoordinator.HideAsync` | `IVirtualDesktopManager.MoveWindowToDesktop` |
| Reveal window | `hide.reveal` | `AgentDispatcher.RevealAsync` → `HideCoordinator.RevealAsync` | `IVirtualDesktopManager.MoveWindowToDesktop` (to current desktop GUID) |
| CPU cap | `power.setProfile` | `ServiceDispatcher.SetPowerProfileAsync` | `CreateJobObject` + `SetInformationJobObject` |
| EcoQoS | (same as above) | `JobObjectPowerController.ApplyThrottle` | `SetProcessInformation` (PROCESS_POWER_THROTTLING) |
| Priority | (same as above) | `JobObjectPowerController.ApplyPriorityAndQos` | `SetPriorityClass` |
