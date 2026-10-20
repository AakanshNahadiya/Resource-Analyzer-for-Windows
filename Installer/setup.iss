; Script generated for Resource Analyzer for Windows
#define MyAppName "Resource Analyzer for Windows"
#define MyAppVersion "2.0.0"
#define MyAppPublisher "Accessible Tools"
#define MyAppExeName "ResourceAnalyzer.exe"

[Setup]
; Basic Application Identity
AppId={{E7A23C84-4845-4DC3-8DC2-A7210FD2A882}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\Resource Analyzer for Windows
UsePreviousAppDir=no
DirExistsWarning=no
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableReadyPage=yes
OutputDir=Output
OutputBaseFilename=ResourceAnalyzer_Setup_v2.0.0
SetupIconFile=..\Resources\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
LZMAUseSeparateProcess=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
CloseApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Check: ShouldCreateDesktopIcon

[Registry]
; Start with windows registry key and startup approved entry
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Resource Analyzer for Windows"; ValueData: """{app}\{#MyAppExeName}"" --minimized"; Check: ShouldStartWithWindows; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"; ValueType: binary; ValueName: "Resource Analyzer for Windows"; ValueData: "02 00 00 00 00 00 00 00 00 00 00 00"; Check: ShouldStartWithWindows; Flags: uninsdeletevalue

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent shellexec

[Code]
const
  EVENT_MODIFY_STATE = $0002;

function OpenEvent(dwDesiredAccess: DWORD; bInheritHandle: BOOL; lpName: string): THandle;
  external 'OpenEventW@kernel32.dll stdcall';

function SetEvent(hEvent: THandle): BOOL;
  external 'SetEvent@kernel32.dll stdcall';

function CloseHandle(hObject: THandle): BOOL;
  external 'CloseHandle@kernel32.dll stdcall';

var
  DesktopIconCheck: TNewCheckBox;
  StartWithWindowsCheck: TNewCheckBox;
  AutoUpdateCheck: TNewCheckBox;
  PrivacyPolicyBtn: TNewButton;

procedure PrivacyPolicyClick(Sender: TObject);
var
  ResultCode: Integer;
begin
  ShellExec('open', 'https://gist.github.com/AakanshNahadiya/4d4f4abe3878de9c43ff9d9b3b7c9ee3', '', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
end;

function ShouldCreateDesktopIcon(): Boolean;
begin
  Result := (DesktopIconCheck <> nil) and DesktopIconCheck.Checked;
end;

function ShouldStartWithWindows(): Boolean;
begin
  Result := (StartWithWindowsCheck <> nil) and StartWithWindowsCheck.Checked;
end;

procedure InitializeWizard();
begin
  // Embed desktop shortcut option directly on the destination directory selection page
  DesktopIconCheck := TNewCheckBox.Create(WizardForm);
  DesktopIconCheck.Parent := WizardForm.SelectDirPage;
  DesktopIconCheck.Left := WizardForm.DirEdit.Left;
  DesktopIconCheck.Top := WizardForm.DiskSpaceLabel.Top + WizardForm.DiskSpaceLabel.Height + ScaleY(12);
  DesktopIconCheck.Width := WizardForm.DirEdit.Width;
  DesktopIconCheck.Height := ScaleY(24);
  DesktopIconCheck.Caption := 'Create a &desktop shortcut';
  DesktopIconCheck.Checked := True;

  // Embed start with windows option directly below the desktop shortcut checkbox
  StartWithWindowsCheck := TNewCheckBox.Create(WizardForm);
  StartWithWindowsCheck.Parent := WizardForm.SelectDirPage;
  StartWithWindowsCheck.Left := WizardForm.DirEdit.Left;
  StartWithWindowsCheck.Top := DesktopIconCheck.Top + DesktopIconCheck.Height + ScaleY(4);
  StartWithWindowsCheck.Width := WizardForm.DirEdit.Width;
  StartWithWindowsCheck.Height := ScaleY(22);
  StartWithWindowsCheck.Caption := '&Start Resource Analyzer with Windows (Minimized to System Tray)';
  StartWithWindowsCheck.Checked := True;

  // Embed daily update check option (Default unchecked for privacy)
  AutoUpdateCheck := TNewCheckBox.Create(WizardForm);
  AutoUpdateCheck.Parent := WizardForm.SelectDirPage;
  AutoUpdateCheck.Left := WizardForm.DirEdit.Left;
  AutoUpdateCheck.Top := StartWithWindowsCheck.Top + StartWithWindowsCheck.Height + ScaleY(4);
  AutoUpdateCheck.Width := WizardForm.DirEdit.Width;
  AutoUpdateCheck.Height := ScaleY(22);
  AutoUpdateCheck.Caption := 'Check for updates automatically &once per day';
  AutoUpdateCheck.Checked := False;

  // Embed Privacy Policy button
  PrivacyPolicyBtn := TNewButton.Create(WizardForm);
  PrivacyPolicyBtn.Parent := WizardForm.SelectDirPage;
  PrivacyPolicyBtn.Left := WizardForm.DirEdit.Left;
  PrivacyPolicyBtn.Top := AutoUpdateCheck.Top + AutoUpdateCheck.Height + ScaleY(8);
  PrivacyPolicyBtn.Width := ScaleX(145);
  PrivacyPolicyBtn.Height := ScaleY(26);
  PrivacyPolicyBtn.Caption := 'View &Privacy Policy';
  PrivacyPolicyBtn.OnClick := @PrivacyPolicyClick;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  // On the single options page, present the button directly as "Install"
  if CurPageID = wpSelectDir then
  begin
    WizardForm.NextButton.Caption := SetupMessage(msgButtonInstall);
  end;
end;

function KillRunningApp(): Boolean;
var
  hExitEvent: THandle;
  ResultCode: Integer;
  i: Integer;
begin
  Result := True;

  // 1. First gracefully signal running instance to exit via cross-integrity named event
  hExitEvent := OpenEvent(EVENT_MODIFY_STATE, False, 'ResourceAnalyzer_ExitSignalEvent');
  if hExitEvent <> 0 then
  begin
    SetEvent(hExitEvent);
    CloseHandle(hExitEvent);
    for i := 1 to 10 do
    begin
      if not CheckForMutexes('ResourceAnalyzer_SingleInstance_Mutex') then
        Exit;
      Sleep(50);
    end;
  end;

  // 2. Standard taskkill
  if CheckForMutexes('ResourceAnalyzer_SingleInstance_Mutex') then
  begin
    Exec('taskkill.exe', '/F /IM ResourceAnalyzer.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    for i := 1 to 6 do
    begin
      if not CheckForMutexes('ResourceAnalyzer_SingleInstance_Mutex') then
        Exit;
      Sleep(50);
    end;
  end;

  // 3. Fallback: if taskkill was blocked because app is running elevated (Admin mode), run elevated taskkill
  if CheckForMutexes('ResourceAnalyzer_SingleInstance_Mutex') then
  begin
    ShellExec('runas', 'taskkill.exe', '/F /IM ResourceAnalyzer.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    for i := 1 to 10 do
    begin
      if not CheckForMutexes('ResourceAnalyzer_SingleInstance_Mutex') then
        Exit;
      Sleep(50);
    end;
  end;
end;

function InitializeSetup(): Boolean;
begin
  // Terminate any running instance immediately on setup startup to release file locks without popups
  KillRunningApp();
  Result := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  KillRunningApp();
  Result := '';
end;

var
  DeleteUserSettings: Boolean;

function InitializeUninstall(): Boolean;
var
  ConfirmForm: TSetupForm;
  PromptLabel: TNewStaticText;
  RemoveCheck: TNewCheckBox;
  YesBtn, NoBtn: TNewButton;
  ResultCode: Integer;
begin
  Result := False;

  // Terminate any running instance before uninstalling
  KillRunningApp();

  // If invoked with /VERYSILENT (automated/scripted), proceed directly without UI
  if Pos('/VERYSILENT', UpperCase(GetCmdTail)) > 0 then
  begin
    Result := True;
    Exit;
  end;

  // If not running with /SILENT, relaunch self with /SILENT so Inno's built-in MsgBox is bypassed!
  if not UninstallSilent() then
  begin
    Exec(ExpandConstant('{uninstallexe}'), '/SILENT', '', SW_SHOW, ewNoWait, ResultCode);
    Exit;
  end;

  // Single unified accessible confirmation dialog with checkbox
  ConfirmForm := CreateCustomForm(ScaleX(440), ScaleY(190), False, False);
  try
    ConfirmForm.Caption := 'Uninstall ' + '{#MyAppName}';
    ConfirmForm.Position := poScreenCenter;

    PromptLabel := TNewStaticText.Create(ConfirmForm);
    PromptLabel.Parent := ConfirmForm;
    PromptLabel.Left := ScaleX(20);
    PromptLabel.Top := ScaleY(20);
    PromptLabel.Width := ScaleX(400);
    PromptLabel.Height := ScaleY(45);
    PromptLabel.AutoSize := False;
    PromptLabel.WordWrap := True;
    PromptLabel.Caption := 'Are you sure you want to completely remove Resource Analyzer for Windows and all of its components from your computer?';

    RemoveCheck := TNewCheckBox.Create(ConfirmForm);
    RemoveCheck.Parent := ConfirmForm;
    RemoveCheck.Left := ScaleX(20);
    RemoveCheck.Top := ScaleY(78);
    RemoveCheck.Width := ScaleX(400);
    RemoveCheck.Height := ScaleY(25);
    RemoveCheck.Caption := '&Delete saved preferences and user settings';
    RemoveCheck.Checked := False;

    YesBtn := TNewButton.Create(ConfirmForm);
    YesBtn.Parent := ConfirmForm;
    YesBtn.Left := ScaleX(240);
    YesBtn.Top := ScaleY(135);
    YesBtn.Width := ScaleX(85);
    YesBtn.Height := ScaleY(30);
    YesBtn.Caption := '&Yes';
    YesBtn.ModalResult := mrYes;
    YesBtn.Default := True;

    NoBtn := TNewButton.Create(ConfirmForm);
    NoBtn.Parent := ConfirmForm;
    NoBtn.Left := ScaleX(335);
    NoBtn.Top := ScaleY(135);
    NoBtn.Width := ScaleX(85);
    NoBtn.Height := ScaleY(30);
    NoBtn.Caption := '&No';
    NoBtn.ModalResult := mrNo;
    NoBtn.Cancel := True;

    // Set initial focus to the checkbox so screen reader users hear it immediately
    ConfirmForm.ActiveControl := RemoveCheck;

    if ConfirmForm.ShowModal = mrYes then
    begin
      DeleteUserSettings := RemoveCheck.Checked;
      Result := True;
    end
    else
    begin
      Result := False;
    end;
  finally
    ConfirmForm.Free();
  end;
end;

procedure SaveInstallerSettings();
var
  AppDataDir, SettingsFile, CheckVal: String;
  Lines: TArrayOfString;
  i: Integer;
  Found: Boolean;
begin
  if AutoUpdateCheck = nil then Exit;
  if AutoUpdateCheck.Checked then
    CheckVal := 'true'
  else
    CheckVal := 'false';

  AppDataDir := ExpandConstant('{userappdata}\ResourceAnalyzer');
  SettingsFile := AppDataDir + '\settings.json';

  if not DirExists(AppDataDir) then
    ForceDirectories(AppDataDir);

  if FileExists(SettingsFile) then
  begin
    if LoadStringsFromFile(SettingsFile, Lines) then
    begin
      Found := False;
      for i := 0 to GetArrayLength(Lines) - 1 do
      begin
        if Pos('"AutoCheckUpdatesDaily"', Lines[i]) > 0 then
        begin
          Lines[i] := '  "AutoCheckUpdatesDaily": ' + CheckVal + ',';
          Found := True;
          Break;
        end;
      end;
      if not Found then
      begin
        SetArrayLength(Lines, GetArrayLength(Lines) + 1);
        for i := GetArrayLength(Lines) - 1 downto 2 do
        begin
          Lines[i] := Lines[i - 1];
        end;
        if GetArrayLength(Lines) > 1 then
          Lines[1] := '  "AutoCheckUpdatesDaily": ' + CheckVal + ',';
      end;
      SaveStringsToFile(SettingsFile, Lines, False);
    end;
  end
  else
  begin
    SetArrayLength(Lines, 3);
    Lines[0] := '{';
    Lines[1] := '  "AutoCheckUpdatesDaily": ' + CheckVal;
    Lines[2] := '}';
    SaveStringsToFile(SettingsFile, Lines, False);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    KillRunningApp();
  end
  else if CurStep = ssPostInstall then
  begin
    // Register uninstaller with /SILENT so Windows "Add or Remove Programs" directly uses our unified dialog
    RegWriteStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{E7A23C84-4845-4DC3-8DC2-A7210FD2A882}_is1', 'UninstallString', '"' + ExpandConstant('{uninstallexe}') + '" /SILENT');

    // Sync daily update check setting with settings.json
    SaveInstallerSettings();
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  AppDataDir: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    KillRunningApp();
  end
  else if CurUninstallStep = usPostUninstall then
  begin
    AppDataDir := ExpandConstant('{userappdata}\ResourceAnalyzer');

    // Clean up settings if user checked the checkbox on the confirmation dialog
    if DeleteUserSettings then
    begin
      if DirExists(AppDataDir) then DelTree(AppDataDir, True, True, True);
    end;

    // Announce uninstallation completion to the user
    if Pos('/VERYSILENT', UpperCase(GetCmdTail)) = 0 then
    begin
      MsgBox('Resource Analyzer for Windows was successfully removed from your computer.', mbInformation, MB_OK);
    end;
  end;
end;

procedure DeinitializeSetup();
var
  TempPath, ExePath: string;
  ResultCode: Integer;
begin
  TempPath := Uppercase(GetEnv('TEMP'));
  ExePath := Uppercase(ExpandConstant('{srcexe}'));
  if (Length(TempPath) > 0) and (Pos(TempPath, ExePath) > 0) then
  begin
    ShellExec('open', 'cmd.exe', '/c timeout /t 2 /nobreak >nul & del /f /q "' + ExpandConstant('{srcexe}') + '"', '', SW_HIDE, ewNoWait, ResultCode);
  end;
end;
