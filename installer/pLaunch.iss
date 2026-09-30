; Inno Setup installer for pLaunch, two editions from the same script:
;   Light: framework-dependent build, needs the .NET 10 Desktop Runtime (small setup)
;   Full:  self-contained build, .NET runtime included (no prerequisites)
; Build with build.ps1, which publishes to ..\publish\<edition> and passes
; /DFlavor=Light|Full and /DAppVersion=<version from pLaunch.csproj>.

#ifndef Flavor
  #define Flavor "Light"
#endif
#if Flavor != "Light" && Flavor != "Full"
  #error Flavor must be Light or Full
#endif
#ifndef AppVersion
  #error AppVersion is required (pass /DAppVersion=x.y.z)
#endif

#define AppName "pLaunch"
#define AppExe "pLaunch.exe"
#define SourceDir AddBackslash(SourcePath) + "..\publish\" + LowerCase(Flavor)
; Same value the app writes from its "Start with Windows" menu (Services\Autostart.cs)
#define RunKey "Software\Microsoft\Windows\CurrentVersion\Run"

[Setup]
; One AppId for both editions: Light and Full replace each other
AppId={{013DB261-090F-418F-AE5B-CB36BB20A225}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion} ({#Flavor})
AppPublisher=Phate
AppPublisherURL=https://github.com/MarcoTrombetta/pLaunch
AppSupportURL=https://github.com/MarcoTrombetta/pLaunch/issues
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Per-user install by default (no admin rights needed); all users (with UAC) can be chosen
; in the first dialog.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=Output
OutputBaseFilename=pLaunch-Setup-{#AppVersion}-{#Flavor}
SetupIconFile=..\src\pLaunch\Assets\pLaunch.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName} ({#Flavor})
WizardStyle=modern dynamic
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=yes
; Created by the running app: setup and uninstall ask to close pLaunch first
AppMutex=pLaunch.Running

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
FinishedLabel=Setup has finished installing [name] on your computer.%n%nTo use it like the old Quick Launch, start pLaunch, right-click its taskbar button and choose "Pin to taskbar".

[CustomMessages]
RuntimeMissing=pLaunch requires the .NET 10 Desktop Runtime (x64), which does not appear to be installed.%n%nYes = open the download page and close setup%nNo = install anyway%nCancel = close setup%n%nAlternatively use the Full edition, which includes the runtime.
AutostartTask=Start pLaunch with Windows (recommended: dropping on the taskbar button needs it running)

[Tasks]
; Per-user installs only: an elevated "all users" setup may run as another account (over-the-shoulder
; UAC), whose HKCU is not the user's. Those users turn it on from pLaunch's own menu.
Name: "autostart"; Description: "{cm:AutostartTask}"; Check: not IsAdminInstallMode
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; Excludes: "*.pdb"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Per user, like the app's own menu entry; unticking the task on an upgrade leaves the current
; setting alone (it may have been turned on from the app). Uninstall removes it (see [Code]).
Root: HKCU; Subkey: "{#RunKey}"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExe}"" --minimized"; Tasks: autostart

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
; Automatic update (pLaunch runs setup with /SILENT /RELAUNCH=1): start again the lists that were open,
; as the user who ran it (not elevated, also for an all-users install)
Filename: "{app}\{#AppExe}"; Parameters: "--after-update"; Flags: nowait runasoriginaluser; Check: ShouldRelaunch

[Code]
{ Switching edition (Full <-> Light) or upgrading: remove the previous program files so no
  stale runtime DLLs are left behind. Both editions publish everything flat in the program
  folder, so only files are touched (never folders the user may have put there); the
  uninstaller files (unins*) are kept. The list of shortcuts lives in AppData, not here. }
procedure CleanProgramFolder;
var
  App, Name: String;
  FindRec: TFindRec;
begin
  App := ExpandConstant('{app}\');
  { Only a folder this setup installed before }
  if (WizardForm.PrevAppDir = '') or
     (CompareText(AddBackslash(WizardForm.PrevAppDir), App) <> 0) or
     not FileExists(App + '{#AppExe}') then
    Exit;
  { The program itself first: if it cannot be deleted pLaunch is still running, and nothing else
    must be removed (an interrupted setup would leave a program that cannot start). }
  if not DeleteFile(App + '{#AppExe}') then
    Exit;
  if FindFirst(App + '*', FindRec) then
  begin
    try
      repeat
        Name := FindRec.Name;
        if ((FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) = 0) and
           (CompareText(Copy(Name, 1, 5), 'unins') <> 0) then
          DeleteFile(App + Name);
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    CleanProgramFolder;
end;

{ The autostart entry may come from the task or from the app's menu: remove it in both cases, but
  only when it starts this installation (not another copy, nor another account's install).
  The list of shortcuts (AppData\Roaming\pLaunch) is kept, like an app's settings. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Names: TArrayOfString;
  Command, Name: String;
  I: Integer;
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;
  { "pLaunch" and the named lists' "pLaunch (Name)" }
  if RegGetValueNames(HKCU, '{#RunKey}', Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
    begin
      Name := Names[I];
      if ((CompareText(Name, '{#AppName}') = 0) or (CompareText(Copy(Name, 1, 9), '{#AppName} (') = 0)) and
         RegQueryStringValue(HKCU, '{#RunKey}', Name, Command) and
         (Pos(Lowercase(AddBackslash(ExpandConstant('{app}'))), Lowercase(Command)) > 0) then
        RegDeleteValue(HKCU, '{#RunKey}', Name);
    end;
end;

{ Automatic update: pLaunch asked every list to close just before starting setup }
function ShouldRelaunch: Boolean;
begin
  Result := WizardSilent and (ExpandConstant('{param:RELAUNCH|0}') = '1');
end;

{ Gives the lists time to close before the AppMutex check (which comes after InitializeSetup) }
procedure WaitForListsToClose;
var
  Waited: Integer;
begin
  if ExpandConstant('{param:RELAUNCH|0}') <> '1' then
    Exit;
  Waited := 0;
  while CheckForMutexes('pLaunch.Running') and (Waited < 20000) do
  begin
    Sleep(250);
    Waited := Waited + 250;
  end;
end;

#if Flavor == "Light"
{ Looks for a release (not preview) x64 .NET 10 Desktop Runtime. On ARM64 Windows the x64 runtime
  lives in dotnet\x64, the plain dotnet folder holds the native ARM64 one. }
function HasDesktopRuntime(Root: String): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if Root = '' then
    Exit;
  if FindFirst(AddBackslash(Root) + 'shared\Microsoft.WindowsDesktop.App\10.*', FindRec) then
  begin
    try
      repeat
        if ((FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and (Pos('-', FindRec.Name) = 0) then
        begin
          Result := True;
          Break;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

{ The places the app host itself looks in: DOTNET_ROOT, the registered install location and the
  standard folder. A per-user copy (dotnet-install in LocalAppData) is not among them: pLaunch
  would not start with it, so it does not count. }
function IsDesktopRuntimeInstalled: Boolean;
var
  Registered: String;
begin
  Result := HasDesktopRuntime(GetEnv('DOTNET_ROOT_X64')) or HasDesktopRuntime(GetEnv('DOTNET_ROOT'));
  if Result then
    Exit;
  if RegQueryStringValue(HKLM32, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64', 'InstallLocation', Registered) then
    Result := HasDesktopRuntime(Registered);
  if Result then
    Exit;
  if IsArm64 then
    Result := HasDesktopRuntime(ExpandConstant('{commonpf64}\dotnet\x64'))
  else
    Result := HasDesktopRuntime(ExpandConstant('{commonpf64}\dotnet'));
end;

function InitializeSetup: Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  if not IsDesktopRuntimeInstalled then
    case SuppressibleMsgBox(CustomMessage('RuntimeMissing'), mbConfirmation, MB_YESNOCANCEL, IDNO) of
      IDYES:
        begin
          ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/10.0', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
          Result := False;
        end;
      IDCANCEL:
        Result := False;
    end;
  if Result then
    WaitForListsToClose;
end;
#else
function InitializeSetup: Boolean;
begin
  WaitForListsToClose;
  Result := True;
end;
#endif
