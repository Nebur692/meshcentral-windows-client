; Per-user installer for MeshCentral Windows Client (Inno Setup 6). No administrator: it installs under
; %LOCALAPPDATA%\Programs, where the app can still update itself. Built by .github/workflows/build.yml:
;   ISCC /DAppVersion=<version> /DSourceExe=<path to MeshCentralClient.exe> /O<output dir> installer\MeshCentralClient.iss
; Instalador por usuario de MeshCentral Windows Client (Inno Setup 6). Sin administrador: instala en
; %LOCALAPPDATA%\Programs, donde la app puede seguir actualizándose sola. Lo compila .github/workflows/build.yml.

#ifndef AppVersion
  #error Pass /DAppVersion=<version>
#endif
#ifndef FileVersion
  ; Windows' file version is numbers only: "0.14.0-beta.1" goes as 0.14.0. / La versión de fichero de Windows es solo números.
  #define FileVersion AppVersion
#endif
#ifndef SourceExe
  #error Pass /DSourceExe=<path to MeshCentralClient.exe>
#endif

[Setup]
; Never change this id: Windows and winget know the installed app by it, and the app reads its uninstall entry by it
; (src/MeshCentralClient.App/InstalledCopy.cs). / No cambiar nunca este id: Windows y winget conocen la app instalada
; por él, y la app lee por él su entrada de desinstalación.
AppId={{C2F50A12-AB55-4901-A024-D09CB431EBE4}
AppName=MeshCentral Windows Client
AppVersion={#AppVersion}
AppVerName=MeshCentral Windows Client {#AppVersion}
AppPublisher=Nebur692
AppPublisherURL=https://github.com/Nebur692/meshcentral-windows-client
AppSupportURL=https://github.com/Nebur692/meshcentral-windows-client/issues
AppUpdatesURL=https://github.com/Nebur692/meshcentral-windows-client/releases
DefaultDirName={localappdata}\Programs\MeshCentral Windows Client
DefaultGroupName=MeshCentral Windows Client
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputBaseFilename=MeshCentralClient-Setup
SetupIconFile=..\src\MeshCentralClient.App\Assets\app.ico
UninstallDisplayIcon={app}\MeshCentralClient.exe
UninstallDisplayName=MeshCentral Windows Client
LicenseFile=..\LICENSE
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#FileVersion}
VersionInfoProductName=MeshCentral Windows Client
VersionInfoDescription=MeshCentral Windows Client setup

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\MeshCentral Windows Client"; Filename: "{app}\MeshCentralClient.exe"; AppUserModelID: "Nebur692.MeshCentralClient"
Name: "{userdesktop}\MeshCentral Windows Client"; Filename: "{app}\MeshCentralClient.exe"; Tasks: desktopicon; AppUserModelID: "Nebur692.MeshCentralClient"

[Run]
Filename: "{app}\MeshCentralClient.exe"; Description: "{cm:LaunchProgram,MeshCentral Windows Client}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; What a self-update leaves next to the exe (SelfUpdater). The servers and settings in %APPDATA% stay, as with any app.
; Lo que deja una autoactualización junto al exe. Los servidores y ajustes de %APPDATA% se quedan, como en cualquier app.
Type: files; Name: "{app}\MeshCentralClient.old.exe"
Type: files; Name: "{app}\MeshCentralClient.exe.new"
Type: dirifempty; Name: "{app}"
