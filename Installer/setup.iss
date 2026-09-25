; Script generated for Resource Analyzer for Windows
#define MyAppName "Resource Analyzer for Windows"
#define MyAppVersion "1.0.1"
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
OutputDir=Output
OutputBaseFilename=ResourceAnalyzer_Setup_v1.0.1
SetupIconFile=..\Resources\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupMutex=ResourceAnalyzerSetup_Mutex
AppMutex=ResourceAnalyzer_SingleInstance_Mutex
CloseApplications=yes
RestartApplications=no
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startwithwindows"; Description: "Start Resource Analyzer for Windows with Windows (Minimized to System Tray)"; GroupDescription: "Startup Options:"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"; Parameters: "/SILENT"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Start with windows registry key and startup approved entry
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Resource Analyzer for Windows"; ValueData: """{app}\{#MyAppExeName}"" --minimized"; Tasks: startwithwindows; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"; ValueType: binary; ValueName: "Resource Analyzer for Windows"; ValueData: "02 00 00 00 00 00 00 00 00 00 00 00"; Tasks: startwithwindows; Flags: uninsdeletevalue

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
function KillRunningApp(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  // Use fully-qualified system path to taskkill.exe to guarantee execution
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM ResourceAnalyzer.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM AccessibleTaskManager.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(300); // Give Windows time to cleanly release file locks and clean up tray icon
end;

function InitializeSetup(): Boolean;
begin
  // Terminate any running instance before installing or updating
  KillRunningApp();

  // If the app was running elevated, lowest-privilege taskkill cannot terminate it.
  // Prompt the user to close it so file-lock errors (Code 5) never occur.
  while CheckForMutexes('ResourceAnalyzer_SingleInstance_Mutex') do
  begin
    if MsgBox('Resource Analyzer for Windows is currently running.' + #13#10 + #13#10 +
              'Please close Resource Analyzer from the system tray or Task Manager before continuing setup.',
              mbConfirmation, MB_OKCANCEL) = IDCANCEL then
    begin
      Result := False;
      Exit;
    end;
    KillRunningApp();
  end;

  Result := True;
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

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    // Register uninstaller with /SILENT so Windows "Add or Remove Programs" directly uses our unified dialog
    RegWriteStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{E7A23C84-4845-4DC3-8DC2-A7210FD2A882}_is1', 'UninstallString', '"' + ExpandConstant('{uninstallexe}') + '" /SILENT');

    // Clean up legacy autostart registry entries
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Accessible Task Manager');
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'AccessibleTaskManager');
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run', 'Accessible Task Manager');
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run', 'AccessibleTaskManager');

    // Clean up legacy shortcuts
    DeleteFile(ExpandConstant('{userprograms}\Accessible Task Manager.lnk'));
    DeleteFile(ExpandConstant('{userdesktop}\Accessible Task Manager.lnk'));

    // Clean up legacy install folder if it existed
    if DirExists(ExpandConstant('{localappdata}\Programs\Accessible Task Manager')) then
    begin
      DelTree(ExpandConstant('{localappdata}\Programs\Accessible Task Manager'), True, True, True);
    end;

    // Clean up legacy roaming settings folder
    if DirExists(ExpandConstant('{userappdata}\AccessibleTaskManager')) then
    begin
      DelTree(ExpandConstant('{userappdata}\AccessibleTaskManager'), True, True, True);
    end;

    // Clean up legacy uninstall registry key
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{D3E8B582-F02A-4D7F-8A2D-7B5D5B8D8F4E}_is1');
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  AppDataDir, LegacyAppDataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    AppDataDir := ExpandConstant('{userappdata}\ResourceAnalyzer');
    LegacyAppDataDir := ExpandConstant('{userappdata}\AccessibleTaskManager');

    // Clean up settings if user checked the checkbox on the confirmation dialog
    if DeleteUserSettings then
    begin
      if DirExists(AppDataDir) then DelTree(AppDataDir, True, True, True);
      if DirExists(LegacyAppDataDir) then DelTree(LegacyAppDataDir, True, True, True);
    end;

    // Announce uninstallation completion to the user
    if Pos('/VERYSILENT', UpperCase(GetCmdTail)) = 0 then
    begin
      MsgBox('Resource Analyzer for Windows was successfully removed from your computer.', mbInformation, MB_OK);
    end;
  end;
end;
