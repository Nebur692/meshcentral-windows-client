; Installer for MeshCentral Windows Client (Inno Setup 6). By default for the Windows account only, without
; administrator, under %LOCALAPPDATA%\Programs, where the app replaces itself when it updates. It also offers to install
; for all users (administrator, Program Files or any folder): that copy updates by running the new installer.
; Built by .github/workflows/build.yml:
;   ISCC /DAppVersion=<version> /DSourceExe=<path to MeshCentralClient.exe> /O<output dir> installer\MeshCentralClient.iss
; Instalador de MeshCentral Windows Client (Inno Setup 6). Por defecto solo para la cuenta de Windows, sin
; administrador, en %LOCALAPPDATA%\Programs, donde la app se reemplaza sola al actualizarse. También ofrece instalar para
; todos los usuarios (administrador, Archivos de programa o cualquier carpeta): esa copia se actualiza abriendo el
; instalador nuevo. Lo compila .github/workflows/build.yml.

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
; {autopf}: %LOCALAPPDATA%\Programs for the account only, Program Files for all users.
; {autopf}: %LOCALAPPDATA%\Programs solo para la cuenta, Archivos de programa para todos.
DefaultDirName={autopf}\MeshCentral Windows Client
DefaultGroupName=MeshCentral Windows Client
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
; The first page asks "for me only" (the default) or "for all users"; /ALLUSERS and /CURRENTUSER choose without asking.
; An update keeps the choice made the first time (UsePreviousPrivileges).
; La primera página pregunta «solo para mí» (por defecto) o «para todos los usuarios»; /ALLUSERS y /CURRENTUSER eligen
; sin preguntar. Una actualización mantiene lo elegido la primera vez (UsePreviousPrivileges).
PrivilegesRequiredOverridesAllowed=dialog commandline
UsePreviousPrivileges=yes
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
; Never Windows' Restart Manager: in CI it froze the whole machine in about one install of three (11-11, 5 of 15 runs; 0 of
; 9 without it). The app is closed by its exact path in PrepareToInstall instead.
; Nunca el gestor de reinicio de Windows: en la CI congelaba la máquina entera en una de cada tres instalaciones (11-11,
; 5 de 15 pasadas; 0 de 9 sin él). La app se cierra por su ruta exacta en PrepareToInstall.
CloseApplications=no
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
Name: "{autoprograms}\MeshCentral Windows Client"; Filename: "{app}\MeshCentralClient.exe"; AppUserModelID: "Nebur692.MeshCentralClient"
Name: "{autodesktop}\MeshCentral Windows Client"; Filename: "{app}\MeshCentralClient.exe"; Tasks: desktopicon; AppUserModelID: "Nebur692.MeshCentralClient"

[Run]
; Always as the person who opened the installer, never elevated. / Siempre como quien abrió el instalador, nunca elevada.
Filename: "{app}\MeshCentralClient.exe"; Description: "{cm:LaunchProgram,MeshCentral Windows Client}"; Flags: nowait postinstall skipifsilent runasoriginaluser
; /RELAUNCH=1: the app updating itself (MainWindow.UpdateNow_Click) opens it again when the silent install ends.
; /RELAUNCH=1: la app que se actualiza (MainWindow.UpdateNow_Click) la vuelve a abrir al acabar la instalación silenciosa.
Filename: "{app}\MeshCentralClient.exe"; Flags: nowait runasoriginaluser; Check: Relaunch

[UninstallDelete]
; What a self-update leaves next to the exe (SelfUpdater). The servers and settings in %APPDATA% stay, as with any app.
; Lo que deja una autoactualización junto al exe. Los servidores y ajustes de %APPDATA% se quedan, como en cualquier app.
Type: files; Name: "{app}\MeshCentralClient.old.exe"
Type: files; Name: "{app}\MeshCentralClient.exe.new"
Type: dirifempty; Name: "{app}"

[CustomMessages]
en.AppStillOpen=MeshCentral Windows Client is still open and could not be closed. Close it and run the installer again.
es.AppStillOpen=MeshCentral Windows Client sigue abierta y no se ha podido cerrar. Ciérrala y vuelve a abrir el instalador.
en.NoWriteAccess=You cannot write in this folder without administrator rights:%n%n%1%n%nChoose a folder of yours, or go back to the first page and choose to install for all users.
es.NoWriteAccess=Sin permisos de administrador no se puede escribir en esta carpeta:%n%n%1%n%nElige una carpeta tuya, o vuelve a la primera página y elige instalar para todos los usuarios.

[Code]
// Said on the folder page, in words, instead of Inno's "Error 5: Access denied" halfway through the install.
// Se dice en la página de la carpeta, con palabras, en vez del «Error 5: Acceso denegado» de Inno a mitad de instalar.
function CanWrite(Dir: String): Boolean;
var
  Probe: String;
begin
  Dir := RemoveBackslashUnlessRoot(Dir);
  // The folder may not exist yet: the nearest one that does decides. / Puede no existir aún: decide la más cercana que sí.
  while not DirExists(Dir) and (ExtractFileDir(Dir) <> Dir) do
    Dir := ExtractFileDir(Dir);
  Probe := AddBackslash(Dir) + '.mcc-write-test-' + IntToStr(Random(1000000000));
  Result := CreateDir(Probe);
  if Result then
    RemoveDir(Probe);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpSelectDir) and not IsAdminInstallMode and not CanWrite(WizardDirValue) then
  begin
    MsgBox(FmtMessage(CustomMessage('NoWriteAccess'), [WizardDirValue]), mbError, MB_OK);
    Result := False;
  end;
end;

// Whether the exe is running: Windows does not let a running exe be opened for writing.
// Si el exe está en marcha: Windows no deja abrir para escribir un exe que corre.
function InUse(const F: String): Boolean;
var
  S: TFileStream;
begin
  Result := False;
  if not FileExists(F) then
    Exit;
  try
    S := TFileStream.Create(F, fmOpenReadWrite or fmShareExclusive);
    S.Free;
  except
    Result := True;
  end;
end;

// Only the copy at this exact path: asked to close its window, 10 s to do it, then ended. A portable copy elsewhere is left
// alone. True when the exe is free. / Solo la copia de esta ruta exacta: se le pide cerrar su ventana, 10 s, y después se
// termina. Una copia portable en otro sitio no se toca. Verdadero si el exe queda libre.
function CloseApp(const Exe: String): Boolean;
var
  I, Code: Integer;
begin
  Result := not InUse(Exe);
  if Result then
    Exit;
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -Command "$p = @(Get-Process MeshCentralClient -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq ''' + Exe + ''' }); ' +
    '$p | ForEach-Object { [void]$_.CloseMainWindow() }; $p | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue; $p | Stop-Process -Force -ErrorAction SilentlyContinue"',
    '', SW_HIDE, ewWaitUntilTerminated, Code);
  for I := 1 to 20 do
  begin
    Result := not InUse(Exe);
    if Result then
      Exit;
    Sleep(500);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not CloseApp(ExpandConstant('{app}\MeshCentralClient.exe')) then
    Result := CustomMessage('AppStillOpen');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    CloseApp(ExpandConstant('{app}\MeshCentralClient.exe'));
end;

function Relaunch: Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;
