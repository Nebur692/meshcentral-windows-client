# The installer as a person meets it, on a clean Windows (the CI runner): install without questions, the app where
# Windows looks for it, the version it reports kept right by the app itself, install again over it, and uninstall
# leaving nothing behind but the person's own settings.
# El instalador tal como lo encuentra una persona, en un Windows limpio (la máquina de la CI): instalar sin preguntas,
# la app donde Windows la busca, la versión que declara corregida por la propia app, instalar otra vez encima, y
# desinstalar sin dejar nada salvo los ajustes de la persona.
param(
  [Parameter(Mandatory)] [string] $Setup,
  [Parameter(Mandatory)] [string] $Version
)
$ErrorActionPreference = 'Stop'

$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{C2F50A12-AB55-4901-A024-D09CB431EBE4}_is1'
$dir = Join-Path $env:LOCALAPPDATA 'Programs\MeshCentral Windows Client'
$exe = Join-Path $dir 'MeshCentralClient.exe'
$link = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\MeshCentral Windows Client.lnk'
$log = Join-Path $env:RUNNER_TEMP 'mcc-setup'
New-Item -ItemType Directory -Force $log | Out-Null

function Check([bool] $ok, [string] $what) {
  if (-not $ok) { throw "INSTALLER: $what" }
  Write-Host "ok: $what"
}

function Until([scriptblock] $condition, [int] $seconds, [string] $what) {
  $end = (Get-Date).AddSeconds($seconds)
  while ((Get-Date) -lt $end) { if (& $condition) { return } ; Start-Sleep -Milliseconds 300 }
  throw "INSTALLER: $what (after $seconds s)"
}

function Install([string] $name) {
  $p = Start-Process $Setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$log\$name.log" -PassThru -Wait
  if ($p.ExitCode -ne 0) { Get-Content "$log\$name.log" -Tail 40; throw "INSTALLER: $name exited with $($p.ExitCode)" }
}

# MENTIRA: nothing of it before, or the checks below would pass on what was already there.
# MENTIRA: nada de ella antes, o las comprobaciones de abajo pasarían por lo que ya hubiera.
Check (-not (Test-Path $key) -and -not (Test-Path $exe)) 'nothing installed before the test'

# 1. Install: no administrator, under the user's own programs. / Instalar: sin administrador, en los programas del usuario.
Install 'install'
Check (Test-Path $exe) "the app at $exe"
Check (Test-Path $link) 'the Start menu entry'
$entry = Get-ItemProperty $key
Check ($entry.DisplayVersion -eq $Version) "Installed apps says $Version (it says $($entry.DisplayVersion))"
Check ($entry.Publisher -eq 'Nebur692') 'the publisher'
Check ([bool] $entry.UninstallString) 'an uninstaller'
Check ((Get-Item $exe).VersionInfo.ProductVersion -like "$Version*") "the installed exe is $Version"

# 2. After updating itself the entry would still say the installer's version: the app puts its own back.
#    Tras actualizarse sola la entrada seguiría con la versión del instalador: la app vuelve a poner la suya.
Set-ItemProperty $key DisplayVersion '0.0.1'
$settings = Join-Path $env:RUNNER_TEMP 'mcc-setup-appdata'
$psi = New-Object System.Diagnostics.ProcessStartInfo $exe
$psi.UseShellExecute = $false
$psi.Environment['APPDATA'] = $settings
$psi.Environment['MESHCLIENT_UPDATE_FEED'] = 'off'
$app = [System.Diagnostics.Process]::Start($psi)
try { Until { (Get-ItemProperty $key).DisplayVersion -eq $Version } 60 'the app did not put its version back in Installed apps' }
finally { if (-not $app.HasExited) { $app.Kill(); $app.WaitForExit(10000) | Out-Null } }
Check $true 'the app keeps Installed apps on its own version'

# 3. Again over itself, as an upgrade does. / Otra vez encima, como hace una actualización.
Install 'reinstall'
Check (Test-Path $exe) 'the app still there after installing again'

# 4. Uninstall: Inno's uninstaller copies itself to %TEMP% and returns at once, so the result is waited for.
#    Desinstalar: el desinstalador de Inno se copia a %TEMP% y vuelve enseguida, así que se espera al resultado.
$uninstaller = ((Get-ItemProperty $key).UninstallString).Trim('"')
Start-Process $uninstaller -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$log\uninstall.log" -Wait
Until { -not (Test-Path $key) -and -not (Test-Path $dir) } 120 'the uninstall left the app, its folder or its entry'
Check (-not (Test-Path $link)) 'the Start menu entry gone'
Write-Host "INSTALLER OK: $Version installs, keeps its version, installs again and uninstalls cleanly"
