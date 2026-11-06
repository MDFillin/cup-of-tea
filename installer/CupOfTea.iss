; Установщик The Cup of Tea: один exe, один запрос UAC. Собирается scripts\build-setup.ps1 из комплекта out\CupOfTea.
; Файлы копирует Inno, а службу, брандмауэр, ярлык и выбор ядер делает install.ps1 —
; тот же скрипт, что и при ручной установке. Удаление — uninstall.ps1 (служба возвращает сеть), затем Inno удаляет папку.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Bundle
  #define Bundle "..\out\CupOfTea"
#endif

[Setup]
; Не менять: по AppId Windows находит установленную программу при обновлении. Свой с 0.7.3: прежний достался от клиента,
; с которого начинался код, и установка одного затирала другой (запись прежнего снимает RemoveOldUninstallEntry).
AppId={{6271B140-7746-4A79-9A65-4A5E040BD4C7}
AppName=The Cup of Tea
AppVersion={#AppVersion}
AppVerName=The Cup of Tea {#AppVersion}
AppPublisher=The Cup of Tea
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\The Cup of Tea
UsePreviousAppDir=no
DisableProgramGroupPage=yes
DisableReadyPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\out
OutputBaseFilename=CupOfTea-Setup-{#AppVersion}
SetupIconFile=..\src\PVpn.App\Assets\pvpn.ico
UninstallDisplayIcon={app}\CupOfTea.exe
UninstallDisplayName=The Cup of Tea
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
; Интерфейс и службу останавливает PrepareToInstall: службу Restart Manager закрыть не может.
CloseApplications=no
RestartApplications=no

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[Files]
Source: "{#Bundle}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Run]
Filename: "{app}\CupOfTea.exe"; Description: "Запустить The Cup of Tea"; Flags: postinstall nowait skipifsilent runasoriginaluser; Check: Installed
; Обновление из программы (тихая установка с /RELAUNCH=1): программа закрылась перед установкой — запускаем снова.
; Именно postinstall: обычные записи [Run] выполняются раньше настройки службы (до InstallOk).
Filename: "{app}\CupOfTea.exe"; Flags: postinstall nowait runasoriginaluser; Check: Relaunch

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\uninstall.ps1"" -Target ""{app}"" -KeepFiles"; \
  Flags: runhidden waituntilterminated; RunOnceId: "CupOfTea.Uninstall"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
const
  // Запись «Приложений» с прежним AppId (до 0.7.3). С тем же AppId ставился и клиент, с которого начинался код,
  // поэтому снимаем её, только если это The Cup of Tea в этой же папке, — чужую запись не трогаем.
  OldUninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{2C59343E-7C14-469F-B429-ABD7B09B9D63}_is1';

var
  InstallOk: Boolean;

// Программа уже стоит под новым AppId в той же папке: прежняя запись и её деинсталлятор (unins000) больше не нужны.
procedure RemoveOldUninstallEntry;
var
  Name, Location, Uninstaller: String;
begin
  if not RegQueryStringValue(HKLM, OldUninstallKey, 'DisplayName', Name) or
     not RegQueryStringValue(HKLM, OldUninstallKey, 'InstallLocation', Location) then
    exit;
  if (CompareText(Name, 'The Cup of Tea') <> 0) or
     (CompareText(RemoveBackslashUnlessRoot(Location), ExpandConstant('{app}')) <> 0) then
    exit;
  if RegQueryStringValue(HKLM, OldUninstallKey, 'UninstallString', Uninstaller) then
  begin
    Uninstaller := RemoveQuotes(Uninstaller);
    if CompareText(Uninstaller, ExpandConstant('{uninstallexe}')) <> 0 then
    begin
      DeleteFile(Uninstaller);
      DeleteFile(ChangeFileExt(Uninstaller, '.dat'));
    end;
  end;
  RegDeleteKeyIncludingSubkeys(HKLM, OldUninstallKey);
end;

function PowerShell(const Params: String): Integer;
begin
  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), '-NoProfile -ExecutionPolicy Bypass ' + Params,
    '', SW_HIDE, ewWaitUntilTerminated, Result) then
    Result := -1;
end;

// Запущенные служба и интерфейс держат файлы в папке программы. Служба при остановке возвращает сеть.
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  PowerShell('-Command "Stop-Service CupOfTea -Force -ErrorAction SilentlyContinue; ' +
    'Get-Process CupOfTea, cupoftea-cli -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Milliseconds 500"');
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
  Log: String;
begin
  if CurStep <> ssPostInstall then
    exit;
  RemoveOldUninstallEntry;
  WizardForm.StatusLabel.Caption := 'Настройка службы, брандмауэра и ядер…';
  Log := ExpandConstant('{app}\install.log');
  Code := PowerShell('-File "' + ExpandConstant('{app}\install.ps1') + '" -Source "' + ExpandConstant('{app}') +
    '" -Target "' + ExpandConstant('{app}') + '" -Log "' + Log + '"');
  InstallOk := Code = 0;
  if not InstallOk then
    SuppressibleMsgBox('Файлы скопированы, но службу The Cup of Tea настроить не удалось (код ' + IntToStr(Code) + ').' + #13#10 +
      'Подробности: ' + Log + #13#10 + 'Запустите установщик ещё раз; если не поможет — пришлите этот файл.', mbError, MB_OK, IDOK);
end;

function Installed: Boolean;
begin
  Result := InstallOk;
end;

function Relaunch: Boolean;
begin
  Result := InstallOk and WizardSilent and (ExpandConstant('{param:RELAUNCH|0}') = '1');
end;

// Код выхода установщика при ошибке скрипта — для тихой установки (/VERYSILENT) и тестов.
function GetCustomSetupExitCode: Integer;
begin
  if InstallOk then
    Result := 0
  else
    Result := 1;
end;
