#define MyAppName "Soft Trace"
#define MyAppVersion "0.2.0"
#define MyAppPublisher "Jing Wang"
#define MyAppURL "https://github.com/yuzhounh/soft-trace"
#define MyAppExeName "SoftTrace.exe"
#define PublishDirectory "..\dist\SoftTrace-v" + MyAppVersion + "-win-x64"

[Setup]
AppId={{4A162029-BB84-4C67-866F-284C276559B7}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={localappdata}\Programs\SoftTrace
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
OutputDir=..\dist
OutputBaseFilename=SoftTrace-v{#MyAppVersion}-win-x64-Setup
SetupIconFile=..\src\SoftTrace.App\Assets\SoftTrace.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
CloseApplicationsFilter={#MyAppExeName}
RestartApplications=no
VersionInfoVersion=0.2.0.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} installer
VersionInfoProductName={#MyAppName}

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式："; Flags: unchecked

[Files]
Source: "{#PublishDirectory}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
var
  DeleteUserData: Boolean;

function InitializeUninstall(): Boolean;
begin
  if UninstallSilent then
    DeleteUserData := False
  else
    DeleteUserData :=
      MsgBox(
        '是否同时删除 Soft Trace 的本地数据和登录信息？' + #13#10 + #13#10 +
        '选择“否”将保留使用记录，便于以后重新安装。',
        mbConfirmation,
        MB_YESNO) = IDYES;
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'SoftTrace');

  if (CurUninstallStep = usPostUninstall) and DeleteUserData then
    DelTree(ExpandConstant('{localappdata}\SoftTrace'), True, True, True);
end;
