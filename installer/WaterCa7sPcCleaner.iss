; WaterCa7s PC Cleaner setup (docs/superpowers/specs/2026-10-23-installer-design.md).
; Built by tools/build-installer.ps1: ISCC /DAppVersion=x.y.z /DSourceDir=<folder publish> WaterCa7sPcCleaner.iss

#define AppName "WaterCa7s PC Cleaner"
#define ExeName "WaterCa7sPcCleaner.exe"
#define InstanceMutex "WaterCa7sPcCleaner.Instance"
; Held while the setup runs: a start of the program waits for it (src/PcCleaner/Core/SetupInProgress.cs).
#define SetupMutexName "WaterCa7sPcCleaner.Setup"
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\app"
#endif

[Setup]
AppId={{6F1B3C2E-8A4D-4F5B-9C7E-2D1A0B9E8F37}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=WaterCa7
AppPublisherURL=https://waterca7.ru
AppSupportURL=https://github.com/WaterCa7/WaterCa7-s-pc-cleaner
AppUpdatesURL=https://github.com/WaterCa7/WaterCa7-s-pc-cleaner/releases/latest
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=admin
SetupMutex={#SetupMutexName}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\publish
OutputBaseFilename=WaterCa7sPcCleaner-Setup
SetupIconFile=..\src\PcCleaner\Assets\app.ico
UninstallDisplayIcon={app}\{#ExeName}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; The running program is handled in [Code] (wait during an update, ask during a manual install); Restart Manager stays off.
CloseApplications=no
ShowLanguageDialog=no
LanguageDetectionMethod=uilanguage
UsePreviousLanguage=no
; Updates (/SILENT: only the progress window) leave a log in %TEMP%.
SetupLogging=yes

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[CustomMessages]
ru.CloseProgram=Программа WaterCa7s PC Cleaner сейчас открыта. Закройте её и нажмите «Повтор».
en.CloseProgram=WaterCa7s PC Cleaner is open. Close it and press Retry.
ru.RegisteringTask=Настраиваем запуск без вопросов Windows…
en.RegisteringTask=Setting up the start without Windows prompts…
ru.RemoveData=Удалить также настройки и журнал программы?%n%nРезервные копии реестра тоже будут удалены.
en.RemoveData=Also remove the program's settings and log?%n%nRegistry backups will be removed too.
ru.RemoveQuarantine=В карантине программы %1 файл(ов). Это ваши файлы, которые программа убрала как подозрительные.%n%nУдалить их навсегда?
en.RemoveQuarantine=The program's quarantine holds %1 file(s). These are your files the program moved aside as suspicious.%n%nDelete them for good?

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "WaterCa7sPcCleaner.installed"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#ExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#ExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#ExeName}"; Parameters: "--register-task"; StatusMsg: "{cm:RegisteringTask}"; Flags: runhidden waituntilterminated
Filename: "{app}\{#ExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent runascurrentuser
Filename: "{app}\{#ExeName}"; Parameters: "--updated-from {param:UPDATEDFROM}"; Flags: nowait runascurrentuser; Check: IsUpdate

[UninstallRun]
Filename: "{app}\{#ExeName}"; Parameters: "--unregister-tasks"; Flags: runhidden waituntilterminated; RunOnceId: "UnregisterTasks"

[UninstallDelete]
Type: filesandordirs; Name: "{app}\Updates"

[Code]
var
  // An update run that got past InitializeSetup: if it ends without installing, DeinitializeSetup starts the program again.
  UpdateRunStarted: Boolean;
  // Set at ssPostInstall: the files are in place and the [Run] entry has started the new version.
  UpdateInstalled: Boolean;

function IsUpdate: Boolean;
begin
  Result := ExpandConstant('{param:UPDATEDFROM|}') <> '';
end;

function InitializeSetup: Boolean;
var
  Waited: Integer;
begin
  Result := True;
  if IsUpdate then
  begin
    // The program started this setup and is closing: wait for it (up to 30 s), never ask.
    Waited := 0;
    while CheckForMutexes('{#InstanceMutex}') and (Waited < 60) do
    begin
      Sleep(500);
      Waited := Waited + 1;
    end;
    if CheckForMutexes('{#InstanceMutex}') then
    begin
      // The program did not close: replacing its files now would fail halfway. Exit without touching anything; the program
      // counted this start as a failed install (UpdateService), so its next start tries again and gives up after two.
      Log('The program still runs after 30 s; the update exits without installing.');
      Result := False;
      Exit;
    end;
    UpdateRunStarted := True;
    Exit;
  end;
  while CheckForMutexes('{#InstanceMutex}') do
    if SuppressibleMsgBox(CustomMessage('CloseProgram'), mbError, MB_RETRYCANCEL, IDCANCEL) <> IDRETRY then
    begin
      Result := False;
      Exit;
    end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    UpdateInstalled := True;
end;

// DeinitializeSetup also runs when InitializeSetup returned False (the docs: "called even if the user exits Setup before
// anything is installed"); UpdateRunStarted is set only past that point, so the program is never started twice next to a copy
// that did not close.
procedure DeinitializeSetup;
var
  AppDir: String;
  ResultCode: Integer;
begin
  if not IsUpdate or not UpdateRunStarted or UpdateInstalled then
    Exit;
  // The update failed after the program had closed (files in use, the antivirus, the disk) and was rolled back: start the old
  // version again, so the user is not left without the program. It counted this start as a failed install.
  try
    AppDir := ExpandConstant('{app}');
  except
    AppDir := ExpandConstant('{autopf}\{#AppName}');
  end;
  Log('The update did not complete; starting the program again: ' + AddBackslash(AppDir) + '{#ExeName}');
  if not Exec(AddBackslash(AppDir) + '{#ExeName}', '', AppDir, SW_SHOWNORMAL, ewNoWait, ResultCode) then
    Log('Could not start the program: ' + SysErrorMessage(ResultCode));
end;

function InitializeUninstall: Boolean;
begin
  Result := True;
  while CheckForMutexes('{#InstanceMutex}') do
    if SuppressibleMsgBox(CustomMessage('CloseProgram'), mbError, MB_RETRYCANCEL, IDCANCEL) <> IDRETRY then
    begin
      Result := False;
      Exit;
    end;
end;

// Quarantined files are <id>.quarantine directly in the folder; index.json and its backup are the program's own bookkeeping.
function CountQuarantinedFiles(const Dir: String): Integer;
var
  Rec: TFindRec;
begin
  Result := 0;
  if FindFirst(AddBackslash(Dir) + '*.quarantine', Rec) then
  try
    repeat
      if Rec.Attributes and FILE_ATTRIBUTE_DIRECTORY = 0 then
        Result := Result + 1;
    until not FindNext(Rec);
  finally
    FindClose(Rec);
  end;
end;

procedure DeleteDataExceptQuarantine(const DataDir: String);
var
  Rec: TFindRec;
begin
  if FindFirst(AddBackslash(DataDir) + '*', Rec) then
  try
    repeat
      if (Rec.Name <> '.') and (Rec.Name <> '..') and (CompareText(Rec.Name, 'Quarantine') <> 0) then
      begin
        if Rec.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0 then
          DelTree(AddBackslash(DataDir) + Rec.Name, True, True, True)
        else
          DeleteFile(AddBackslash(DataDir) + Rec.Name);
      end;
    until not FindNext(Rec);
  finally
    FindClose(Rec);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir, Quarantine: String;
  Count: Integer;
begin
  if (CurUninstallStep <> usPostUninstall) or UninstallSilent then
    Exit;
  DataDir := ExpandConstant('{localappdata}\WaterCa7sPcCleaner');
  if not DirExists(DataDir) then
    Exit;
  if MsgBox(CustomMessage('RemoveData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    DeleteDataExceptQuarantine(DataDir);
  Quarantine := AddBackslash(DataDir) + 'Quarantine';
  Count := CountQuarantinedFiles(Quarantine);
  if (Count > 0) and
     (MsgBox(FmtMessage(CustomMessage('RemoveQuarantine'), [IntToStr(Count)]), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
    DelTree(Quarantine, True, True, True);
  RemoveDir(DataDir); // only if nothing is left
end;
