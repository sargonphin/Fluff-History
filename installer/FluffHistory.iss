#ifndef Payload
 #error Payload is required
#endif
#ifndef ProductVersion
 #error ProductVersion is required
#endif
#ifndef ProductMinimumWindows
 #error ProductMinimumWindows is required
#endif
#ifndef ReleaseVersion
 #define ReleaseVersion "development"
#endif
#ifndef SigningMode
 #define SigningMode "Development"
#endif
[Setup]
SetupArchitecture=x64
AppId={{7753C55A-17C1-4BC2-B36C-2A8B95E93221}
AppName=Fluff-History
AppVersion={#ProductVersion}
VersionInfoVersion={#ProductVersion}
VersionInfoProductVersion={#ProductVersion}
VersionInfoTextVersion={#ProductVersion}
VersionInfoProductTextVersion={#ProductVersion}
VersionInfoCompany=Fish & Dog Software
VersionInfoDescription=Fluff-History Setup [{#ReleaseVersion}]
VersionInfoProductName=Fluff-History
VersionInfoOriginalFileName=Fluff-History-Setup.exe
AppPublisher=Fish & Dog Software
DefaultDirName={localappdata}\Programs\Fluff-History
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion={#ProductMinimumWindows}
OutputBaseFilename=Fluff-History-Setup
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
LanguageDetectionMethod=uilanguage
#if SigningMode == "UnsignedPublic"
InfoBeforeFile=UnsignedPublic.txt
#else
InfoBeforeFile=DevelopmentPreview.txt
#endif
LicenseFile=DistributionTerms.txt
CloseApplications=no
RestartApplications=no
UninstallDisplayIcon={app}\Versions\{#ProductVersion}\FluffHistory.Desktop.exe
UninstallDisplayName=Fluff-History
SetupMutex=FluffHistory.Setup.CurrentUser
AppMutex=
Uninstallable=yes

#include "Generated\Localization.iss"

[Tasks]
Name: desktopicon; Description: "{cm:Installer_DesktopShortcut}"; GroupDescription: "{cm:Installer_ShortcutsGroup}"
Name: startmenuicon; Description: "{cm:Installer_StartMenuShortcut}"; GroupDescription: "{cm:Installer_ShortcutsGroup}"

[Icons]
Name: "{userdesktop}\Fluff-History"; Filename: "{app}\Versions\{#ProductVersion}\FluffHistory.Desktop.exe"; WorkingDir: "{app}\Versions\{#ProductVersion}"; AppUserModelID: "FluffHistory.Desktop"; Tasks: desktopicon
Name: "{userprograms}\Fluff-History"; Filename: "{app}\Versions\{#ProductVersion}\FluffHistory.Desktop.exe"; WorkingDir: "{app}\Versions\{#ProductVersion}"; AppUserModelID: "FluffHistory.Desktop"; Tasks: startmenuicon

[InstallDelete]
Type: files; Name: "{userdesktop}\Fluff-History.lnk"; Tasks: not desktopicon
Type: files; Name: "{userprograms}\Fluff-History.lnk"; Tasks: not startmenuicon

[Files]
Source: "{#Payload}\*"; DestDir: "{app}\Versions\{#ProductVersion}"; Flags: recursesubdirs createallsubdirs onlyifdoesntexist
Source: "{#Payload}\Installer\Lifecycle.ps1"; DestDir: "{tmp}"; Flags: dontcopy
Source: "{#Payload}\installed-files.json"; DestDir: "{tmp}"; Flags: dontcopy
Source: "{#Payload}\FluffHistory.AgentLauncher.exe"; DestDir: "{app}"; DestName: "FluffHistory.Agent.exe"; Flags: ignoreversion
Source: "{#Payload}\FluffHistory.ShellLauncher.exe"; DestDir: "{app}"; Flags: ignoreversion

[Code]
var
  LifecycleLock: THandle;
  RegistrationFailed: Boolean;

#include "DistributionTerms.iss"

function CreateFileW(Name: String; Access, Share: Cardinal; Security: NativeUInt;
  Disposition, Flags: Cardinal; Template: THandle): THandle;
  external 'CreateFileW@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';
function GetFileAttributesW(Name: String): Cardinal;
  external 'GetFileAttributesW@kernel32.dll stdcall';
function OpenEventW(Access: Cardinal; Inherit: Boolean; Name: String): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(Handle: THandle): Boolean;
  external 'SetEvent@kernel32.dll stdcall';

procedure NotifyUpdateHandoff();
var Token: String; Index: Integer; Handle: THandle;
begin
  Token := ExpandConstant('{param:UPDATEHANDOFF|}');
  if Length(Token) <> 32 then Exit;
  for Index := 1 to Length(Token) do
    if Pos(Token[Index], '0123456789abcdef') = 0 then Exit;
  Handle := OpenEventW(2, False, 'Local\FluffHistory.UpdateSetup.' + Token);
  if Handle <> 0 then begin SetEvent(Handle); CloseHandle(Handle); end;
end;

function CheckParents(Path: String): Boolean;
var Attr: Cardinal; Parent: String;
begin
  Result := True;
  while Length(Path) > 3 do begin
    Attr := GetFileAttributesW(Path);
    if (Attr <> $FFFFFFFF) and ((Attr and $400) <> 0) then begin Result := False; Exit; end;
    Parent := ExtractFileDir(Path); if Parent = Path then Exit; Path := Parent;
  end;
end;

function AcquireLifecycleLock(): Boolean;
var Dir: String;
begin
  Dir := ExpandConstant('{localappdata}\Fluff-History\Runtime');
  Result := CheckParents(Dir); if not Result then Exit;
  Result := ForceDirectories(Dir) and CheckParents(Dir); if not Result then Exit;
  LifecycleLock := CreateFileW(Dir + '\installation.lock', $C0000000, 0, 0, 4, $00200080, 0);
  Result := LifecycleLock <> THandle(-1);
end;

procedure ReleaseLifecycleLock();
begin
  if (LifecycleLock <> 0) and (LifecycleLock <> THandle(-1)) then CloseHandle(LifecycleLock);
  LifecycleLock := 0;
end;

function RunLifecycle(Action, Script: String): Boolean;
var Code: Integer; Parameters, Fault: String;
begin
  Fault := ExpandConstant('{param:FAULT|}');
  Parameters := '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + Script +
    '" -Action ' + Action + ' -Version "{#ProductVersion}"';
  if Action = 'Prepare' then Parameters := Parameters + ' -ExpectedManifest "' + ExpandConstant('{tmp}\installed-files.json') + '"';
  if Fault <> '' then begin
    if (Fault <> 'ClassicVerb') and (Fault <> 'Properties') and (Fault <> 'Startup') and
       (Fault <> 'Agent') and (Fault <> 'MigrationCopy') and (Fault <> 'Shutdown') then begin Result := False; Exit; end;
    Parameters := Parameters + ' -Fault ' + Fault;
  end;
  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Parameters,
    '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := RequireDistributionTerms();
  if Result <> '' then Exit;
  if CompareText(ExpandConstant('{app}'), ExpandConstant('{localappdata}\Programs\Fluff-History')) <> 0 then begin
    Result := CustomMessage('Installer_FixedFolder'); Exit;
  end;
  if not AcquireLifecycleLock() then begin Result := CustomMessage('Installer_Busy'); Exit; end;
  NotifyUpdateHandoff();
  ExtractTemporaryFile('Lifecycle.ps1');
  ExtractTemporaryFile('installed-files.json');
  if not RunLifecycle('Prepare', ExpandConstant('{tmp}\Lifecycle.ps1')) then begin
    ReleaseLifecycleLock(); Result := CustomMessage('Installer_PrepareFailed');
  end;
end;

procedure CurStepChanged(Step: TSetupStep);
var Success: Boolean;
begin
  if Step = ssPostInstall then begin
    Success := RunLifecycle('Install', ExpandConstant('{app}\Versions\{#ProductVersion}\Installer\Lifecycle.ps1'));
    ReleaseLifecycleLock();
    if Success then Success := RunLifecycle('Activate', ExpandConstant('{app}\Versions\{#ProductVersion}\Installer\Lifecycle.ps1'));
    if not Success then begin
      RegistrationFailed := True;
      if AcquireLifecycleLock() then begin
        RunLifecycle('Rollback', ExpandConstant('{app}\Versions\{#ProductVersion}\Installer\Lifecycle.ps1'));
        ReleaseLifecycleLock();
      end;
      RaiseException(CustomMessage('Installer_InstallFailed'));
    end;
  end;
end;

function GetCustomSetupExitCode(): Integer;
begin
  if RegistrationFailed then Result := 1 else Result := 0;
end;

procedure CurPageChanged(PageID: Integer);
begin
  if (PageID = wpFinished) and RegistrationFailed then begin
    WizardForm.FinishedHeadingLabel.Caption := CustomMessage('Installer_AttentionTitle');
    WizardForm.FinishedLabel.Caption := CustomMessage('Installer_AttentionBody');
  end;
end;

function InitializeUninstall(): Boolean;
begin
  Result := AcquireLifecycleLock();
  if Result then Result := RunLifecycle('Uninstall', ExpandConstant('{app}\Versions\{#ProductVersion}\Installer\Lifecycle.ps1'));
  if not Result then begin
    ReleaseLifecycleLock();
    if not UninstallSilent then MsgBox(CustomMessage('Installer_UninstallBusy'), mbError, MB_OK);
  end;
end;

procedure DeinitializeSetup();
begin ReleaseLifecycleLock(); end;
procedure DeinitializeUninstall();
begin ReleaseLifecycleLock(); end;
