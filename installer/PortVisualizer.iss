; Inno Setup script for Port Visualizer.
; Build:  ISCC.exe installer\PortVisualizer.iss   (expects artifacts\publish\PortVisualizer.exe)

#define AppName "Port Visualizer"
#define AppVersion "1.0.0"
#define AppPublisher "Port Visualizer contributors"
#define AppURL "https://github.com/unupunct/Port-Visualizer"
#define AppExe "PortVisualizer.exe"

[Setup]
AppId={{6B1F3A52-8D0E-4C57-9E5B-3C2A7D41F9A0}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}/issues
AppUpdatesURL={#AppURL}/releases
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} Setup
VersionInfoProductName={#AppName}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes
LicenseFile=..\LICENSE
SetupIconFile=..\src\PortVisualizer.App\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
OutputDir=..\release
OutputBaseFilename=PortVisualizer_Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Per-user install by default (no admin needed); the dialog lets users choose an all-users install.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
; Same AppId => running the new setup upgrades in place. User data lives in %LOCALAPPDATA%\PortVisualizer and is never touched here.
AppMutex=Local\PortVisualizer.SingleInstance
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\artifacts\publish\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\release\README.txt"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

; Deliberately no [Registry] Run entry: Port Visualizer never starts with Windows on its own.

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\PortVisualizer');
    if DirExists(DataDir) and not UninstallSilent then
    begin
      if MsgBox('Also delete your Port Visualizer settings, connection history database and logs?' + #13#10 + #13#10 +
                DataDir + #13#10 + #13#10 + 'Choose No to keep them for a future reinstall.',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
    end;
  end;
end;
