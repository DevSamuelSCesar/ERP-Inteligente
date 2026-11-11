; Inno Setup 6 script of the Nexus Windows installer (feature 9). Built by installer/build.ps1:
;   ISCC /DAppVersion=1.0.0 /DPublishDir=<dotnet publish output> /O<output dir> installer\nexus.iss
; Per-user install (no administrator rights). The database and its keys live in the user's profile and key store
; (%LOCALAPPDATA%\Nexus and DPAPI): the uninstaller never touches them, so reinstalling keeps the data.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #error PublishDir must point to the dotnet publish output
#endif

[Setup]
AppId={{6C8E2B1F-5A4D-4C2E-9B7A-3F1D0E2C4B5A}
AppName=Nexus ERP
AppVersion={#AppVersion}
AppPublisher=ERP Inteligente
DefaultDirName={localappdata}\Programs\Nexus
DefaultGroupName=Nexus
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputBaseFilename=Nexus-{#AppVersion}-setup
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName=Nexus ERP
WizardStyle=modern

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Nexus ERP"; Filename: "{app}\erp_inteligente.exe"
Name: "{autodesktop}\Nexus ERP"; Filename: "{app}\erp_inteligente.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\erp_inteligente.exe"; Description: "{cm:LaunchProgram,Nexus ERP}"; Flags: nowait postinstall skipifsilent
