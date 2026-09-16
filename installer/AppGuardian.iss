; ============================================================================
;  AppGuardian.iss - Inno Setup script. FR-100..FR-106.
;
;  Expects a published layout, not a bin folder. Build it with:
;
;      dotnet publish src\AppGuardian.Service\AppGuardian.Service.csproj -c Release -r win-x64 --self-contained false -o dist\
;      dotnet publish src\AppGuardian.Agent\AppGuardian.Agent.csproj     -c Release -r win-x64 --self-contained false -o dist\
;      dotnet publish src\AppGuardian.UI\AppGuardian.UI.csproj           -c Release -r win-x64 --self-contained false -o dist\
;      iscc installer\AppGuardian.iss
;
;  To publish the standalone bootstrapper (served from the marketing website):
;      dotnet publish src\AppGuardian.Bootstrapper\AppGuardian.Bootstrapper.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist\bootstrapper\
;
;  The bootstrapper is framework-dependent for the same reason as everything else here, and
;  deliberately not trimmed - the SDK refuses PublishTrimmed for WPF (NETSDK1168). See the
;  comment block in AppGuardian.Bootstrapper.csproj for the three sizes that were weighed.
;
;  All three publish into one folder deliberately. They share AppGuardian.Shared.dll and the
;  same runtime assemblies, and three separate folders would ship three copies of each and
;  leave three chances for a partial upgrade to pair a new service with an old agent.
;
;  Framework-dependent, not self-contained: ADR-011 records the reasoning - a self-contained
;  build is ~150 MB across three executables against a ~2 MB payload, and the .NET Desktop
;  Runtime is a prerequisite this installer checks for and names rather than silently bundling.
;
;  SPEC CONFLICT SC-16: FR-100 requires an administrative installer and FR-102 requires a
;  per-user logon entry for the agent, but an elevated install writes HKCU for the *installing*
;  administrator only. This script registers the agent under HKLM\...\Run so it starts for every
;  user of the machine, which is the only reading of FR-102 that survives a second user logging
;  in. Flagged rather than decided: if the intent was one user per install, this becomes an
;  HKCU entry plus a first-run step in the dashboard.
; ============================================================================

#define AppName "AppGuardian"
#define AppVersion "0.1.0"
#define AppPublisher "Hafiz"
#define AppUrl "https://github.com/hafiz/appguardian"
#define ServiceName "AppGuardian.Service"
#define ServiceDisplayName "AppGuardian Protection Service"
#define ServiceExe "AppGuardian.Service.exe"
#define AgentExe "AppGuardian.Agent.exe"
#define DashboardExe "AppGuardian.exe"
#define SourceDir "..\dist\payload"

[Setup]
AppId={{7C2F1E64-9B3A-4D18-8E5C-3A9F6D204B11}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
LicenseFile=..\LICENSE
OutputDir=..\dist\installer
OutputBaseFilename=AppGuardian-{#AppVersion}-x64-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

; FR-100: elevation is required, not requested. The service registration and the HKLM run entry
; both need it, and an install that silently skipped them would leave a dashboard with nothing
; behind it.
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=

; SRS Section 2.2 and Section 1.4: x64 only, Windows 10 22H2 (build 19045) or later.
; "x64compatible" requires Inno Setup 6.3 or later. On 6.0-6.2 replace both values with "x64".
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19045

UninstallDisplayIcon={app}\{#DashboardExe}
UninstallDisplayName={#AppName} {#AppVersion}
CloseApplications=yes
RestartApplications=no

; Use the VS Code-style wizard look: flat, minimal, no application icon on every page.
; WizardNoCurPageText (removed - not a valid Inno Setup directive)
; WizardNoGroupHeader (removed - not valid)
DisableWelcomePage=no

; FR-106 (priority C): no code-signing certificate is assumed (ADR-008). When one exists,
; uncomment these and pass /Ssigntool=... to iscc. Until then SmartScreen will warn on first
; run, which the README states plainly rather than leaving users to discover it.
; SignTool=signtool
; SignedUninstaller=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "launchdashboard"; Description: "Open AppGuardian after installing"; \
    GroupDescription: "After installation:"; Flags: unchecked

Name: "launchagent"; Description: "Start the desktop agent at user logon"; \
    GroupDescription: "After installation:"

Name: "desktopicon"; Description: "Create a desktop shortcut"; \
    GroupDescription: "Shortcuts:"; Flags: unchecked

Name: "contextmenu"; Description: "Add to Windows Explorer context menu"; \
    GroupDescription: "After installation:"

[Files]
; The whole published layout. Recursesubdirs picks up the runtime satellite folders; the
; exclusions keep debug symbols and the development appsettings out of a release install.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; \
    Excludes: "*.pdb,*.xml,appsettings.Development.json"

Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Dirs]
; %ProgramData%\AppGuardian, matching StoragePaths.MachineRoot. Created here rather than left to
; first run so the ACL below is in place before the service ever writes policy.json.
;
; SRS Section 9.1: Administrators and LocalSystem write, Users read. The audit log is deliberately
; user-readable - FR-803 lets a user inspect what was enforced on their own machine - and the
; read-only grant is what stops a user-privilege process forging or truncating it.
Name: "{commonappdata}\AppGuardian"; Permissions: users-readexec admins-full
Name: "{commonappdata}\AppGuardian\logs"; Permissions: users-readexec admins-full

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#DashboardExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#DashboardExe}"; Tasks: desktopicon

[Registry]
; FR-102: the agent starts at logon. HKLM rather than HKCU - see SC-16 in the header.
; No quotes needed around a path with spaces here because Inno writes the value verbatim and
; the Run key tolerates it, but they are included anyway: a bare C:\Program Files\... value is
; the classic unquoted-path ambiguity, and this one runs on every logon.
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "AppGuardianAgent"; \
    ValueData: """{app}\{#AgentExe}"""; Flags: uninsdeletevalue; Tasks: launchagent

; Scoped declaratively so the task controls installation and uninstall removes every key.
Root: HKLM; Subkey: "Software\Classes\Applications\{#DashboardExe}\shell\open"; \
    ValueType: string; ValueData: "Open with AppGuardian"; Flags: uninsdeletekey; Tasks: contextmenu
   Root: HKLM; Subkey: "Software\Classes\Applications\{#DashboardExe}\shell\open\command"; \
       ValueType: string; ValueData: """{app}\{#DashboardExe}"" ""%1"""; Flags: uninsdeletekey; Tasks: contextmenu

[Run]
; Registration, then start. Sequenced rather than combined so a failure to register is
; distinguishable in the log from a service that registered and refused to start.
;
; FR-101: start=auto, and delayed-auto is deliberately not used - a delayed start would leave a
; window after logon in which a locked app opens unprotected, which is the one thing this service
; exists to prevent.
Filename: "{sys}\sc.exe"; \
    Parameters: "create ""{#ServiceName}"" binPath= ""\""{app}\{#ServiceExe}\"""" start= auto DisplayName= ""{#ServiceDisplayName}"""; \
    Flags: runhidden waituntilterminated; StatusMsg: "Registering the protection service..."

Filename: "{sys}\sc.exe"; \
    Parameters: "description ""{#ServiceName}"" ""Enforces AppGuardian application lock, hide, and power-restriction policies. Local only; makes no network connections."""; \
    Flags: runhidden waituntilterminated

; Restart the service after a crash rather than leaving the machine unprotected until the next
; reboot. The service has its own watchdog for the agent (WatchdogRestartBudget); this is the
; equivalent for the service itself, which nothing else supervises.
Filename: "{sys}\sc.exe"; \
    Parameters: "failure ""{#ServiceName}"" reset= 86400 actions= restart/5000/restart/10000/restart/30000"; \
    Flags: runhidden waituntilterminated

Filename: "{sys}\sc.exe"; Parameters: "start ""{#ServiceName}"""; \
    Flags: runhidden waituntilterminated; StatusMsg: "Starting the protection service..."

; The agent is launched now so the machine is protected without waiting for a logoff. Run as the
; invoking user, not elevated: it draws the overlay on the interactive desktop and an elevated
; agent could not be reached by the unelevated dashboard.
Filename: "{app}\{#AgentExe}"; Description: "Start the desktop component"; \
    Flags: nowait postinstall skipifsilent runasoriginaluser shellexec; Tasks: launchagent

Filename: "{app}\{#DashboardExe}"; Description: "Open {#AppName}"; \
    Flags: nowait postinstall skipifsilent runasoriginaluser shellexec; \
    Check: ShouldLaunchDashboard

[UninstallRun]
; FR-103: stop, then delete. Order matters - deleting a running service marks it for deletion and
; leaves it running until reboot, so the agent would keep being watched by a service the user
; believes is gone.
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM ""{#AgentExe}"" /T"; \
    Flags: runhidden waituntilterminated; RunOnceId: "StopAgent"

Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM ""{#DashboardExe}"" /T"; \
    Flags: runhidden waituntilterminated; RunOnceId: "StopDashboard"

Filename: "{sys}\sc.exe"; Parameters: "stop ""{#ServiceName}"""; \
    Flags: runhidden waituntilterminated; RunOnceId: "StopService"

Filename: "{sys}\sc.exe"; Parameters: "delete ""{#ServiceName}"""; \
    Flags: runhidden waituntilterminated; RunOnceId: "DeleteService"

[Code]

{ ------------------------------------------------------------------------
  FR-105 is handled declaratively by MinVersion above, which aborts with Inno's own message
  before any file is copied. What is left for code is the .NET prerequisite and the FR-103 data
  prompt, neither of which can be expressed in a section.

  Premium wizard: custom preferences page + modern finish page.
  ------------------------------------------------------------------------ }

var
  { Set once the uninstaller has asked, so a repeated step cannot ask twice. }
  DataPromptAnswered: Boolean;
  RemoveDataChosen: Boolean;

{ The published output is framework-dependent (ADR-011), so the Desktop Runtime has to be present.
  Detected by the shared framework folder rather than by running `dotnet --list-runtimes`, because
  the CLI is part of the SDK and a machine can have the runtime without it. }
function DesktopRuntimeIsPresent: Boolean;
var
  Root: String;
  FindRec: TFindRec;
begin
  Result := False;
  Root := ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App');

  if not DirExists(Root) then
    Exit;

  { Any 8.x folder will do. A stricter patch-level check would fail on a machine that is more
    up to date than this installer knows about. }
  if FindFirst(Root + '\8.*', FindRec) then
  begin
    try
      Result := True;
    finally
      FindClose(FindRec);
    end;
  end;
end;

function InitializeSetup: Boolean;
begin
  Result := True;

  if not DesktopRuntimeIsPresent then
  begin
    { Named, not silently downloaded. NFR-S5 forbids fetching and running code at install time, and
      a link the user follows themselves is also a link they can verify. }
    MsgBox(
      'AppGuardian needs the .NET 8 Desktop Runtime (x64), which is not installed on this PC.' + #13#10#13#10 +
      'Download it from https://dotnet.microsoft.com/download/dotnet/8.0 - choose the ' +
      '"Desktop Runtime" for x64 - then run this installer again.',
      mbError, MB_OK);
    Result := False;
    Exit;
  end;
end;

{ ------------------------------------------------------------------------
  Custom Preferences Page - VS Code-style install options.
  Shown between Select Destination and Ready to Install.
  ------------------------------------------------------------------------ }

{ ------------------------------------------------------------------------
  Stops a running install before overwriting its files.
  ------------------------------------------------------------------------ }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  NeedsRestart := False;

  Exec(ExpandConstant('{sys}\sc.exe'), 'stop "{#ServiceName}"', '', SW_HIDE,
       ewWaitUntilTerminated, ResultCode);

  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM "{#AgentExe}" /T', '', SW_HIDE,
       ewWaitUntilTerminated, ResultCode);

  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM "{#DashboardExe}" /T', '', SW_HIDE,
       ewWaitUntilTerminated, ResultCode);

  { A short pause: sc returns as soon as the stop is accepted, not when the process has exited. }
  Sleep(1500);
end;

{ ------------------------------------------------------------------------
  Called by the [Run] section's Check parameter to decide whether to
  launch the dashboard on the finish page.
  ------------------------------------------------------------------------ }
   function ShouldLaunchDashboard: Boolean;
   begin
     Result := WizardIsTaskSelected('launchdashboard');
   end;
{ ------------------------------------------------------------------------
  FR-103. Asked, never assumed, and defaulting to keep.
  ------------------------------------------------------------------------ }
function AskAboutData: Boolean;
begin
  if not DataPromptAnswered then
  begin
    RemoveDataChosen := MsgBox(
      'Remove your AppGuardian settings and history as well?' + #13#10#13#10 +
      'This deletes your app rules, your PIN, and the activity log from this PC. ' +
      'Choose No to keep them, so reinstalling AppGuardian restores your setup.',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;

    DataPromptAnswered := True;
  end;

  Result := RemoveDataChosen;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  MachineRoot: String;
  UserRoot: String;
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;

  if not AskAboutData then
    Exit;

  { Both roots, matching StoragePaths. The per-user file only covers the uninstalling user's
    profile - other users' settings.json files are left behind, which is a known limitation of an
    elevated uninstall and is recorded in DECISIONS.md rather than papered over with a registry
    walk of every profile on the machine. }
  MachineRoot := ExpandConstant('{commonappdata}\AppGuardian');
  UserRoot := ExpandConstant('{localappdata}\AppGuardian');

  DelTree(MachineRoot, True, True, True);
  DelTree(UserRoot, True, True, True);
end;
