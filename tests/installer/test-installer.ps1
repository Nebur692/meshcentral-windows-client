# The installer as a person meets it, on a clean Windows (the CI runner): install without questions, the app where
# Windows looks for it, the version it reports kept right by the app itself, install again over it, and uninstall
# leaving nothing behind but the person's own settings. Then the same for all users (the CI runner is an administrator):
# Program Files, the entry in HKLM, the update the app starts reopening it, and a clean uninstall.
# El instalador tal como lo encuentra una persona, en un Windows limpio (la máquina de la CI): instalar sin preguntas,
# la app donde Windows la busca, la versión que declara corregida por la propia app, instalar otra vez encima, y
# desinstalar sin dejar nada salvo los ajustes de la persona. Luego lo mismo para todos los usuarios (la máquina de la CI
# es administradora): Archivos de programa, la entrada en HKLM, la actualización que lanza la app reabriéndola, y
# desinstalar sin restos.
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
  Write-Host "$((Get-Date).ToString('HH:mm:ss')) ok: $what"
}

function Until([scriptblock] $condition, [int] $seconds, [string] $what) {
  $end = (Get-Date).AddSeconds($seconds)
  while ((Get-Date) -lt $end) { if (& $condition) { return } ; Start-Sleep -Milliseconds 300 }
  throw "INSTALLER: $what (after $seconds s)"
}

# What runs from the app's folder. / Lo que corre desde la carpeta de la app.
function Running { @(Get-Process | Where-Object { $_.Path -and $_.Path.StartsWith($dir, [StringComparison]::OrdinalIgnoreCase) }) }

# Never -Wait without a limit (seen: the step hung 20 min and GitHub cancelled it, with no log): on timeout, what it
# was doing is shown. / Nunca -Wait sin límite (visto: el paso se colgó 20 min y GitHub lo canceló, sin log): si se
# pasa, se enseña qué estaba haciendo.
function Run([string] $file, [string] $name, [int] $seconds, [string[]] $extra = @()) {
  Write-Host "$((Get-Date).ToString('HH:mm:ss')) run: $name $extra"
  $p = Start-Process $file -ArgumentList (@('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$log\$name.log") + $extra) -PassThru
  if (-not $p.WaitForExit($seconds * 1000)) {
    Write-Host "--- $name still running after $seconds s; its log:"
    Get-Content "$log\$name.log" -Tail 40 -ErrorAction SilentlyContinue
    Write-Host '--- processes from the app folder:'; Running | Format-Table Id, ProcessName, Path -AutoSize | Out-String | Write-Host
    try { $p.Kill() } catch { }
    throw "INSTALLER: $name did not finish in $seconds s"
  }
  if ($p.ExitCode -ne 0) { Get-Content "$log\$name.log" -Tail 40; throw "INSTALLER: $name exited with $($p.ExitCode)" }
}

function Install([string] $name) { Run $Setup $name 180 }

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
finally {
  if (-not $app.HasExited) { $app.Kill($true); $app.WaitForExit(10000) | Out-Null }
  foreach ($p in Running) { try { $p.Kill($true) } catch { } }
}
Check $true 'the app keeps Installed apps on its own version'
# Installing over a running app makes the installer ask to close it; here nothing of it may be left running.
# Instalar encima de la app abierta hace que el instalador pida cerrarla; aquí no puede quedar nada suyo corriendo.
Until { (Running).Count -eq 0 } 30 'something from the app folder still running'

# 3. Again over itself, as an upgrade does. / Otra vez encima, como hace una actualización.
Install 'reinstall'
Check (Test-Path $exe) 'the app still there after installing again'

# 4. Uninstall: Inno's uninstaller copies itself to %TEMP% and returns at once, so the result is waited for.
#    Desinstalar: el desinstalador de Inno se copia a %TEMP% y vuelve enseguida, así que se espera al resultado.
$uninstaller = ((Get-ItemProperty $key).UninstallString).Trim('"')
Run $uninstaller 'uninstall' 120
Until { -not (Test-Path $key) -and -not (Test-Path $dir) } 120 'the uninstall left the app, its folder or its entry'
Check (-not (Test-Path $link)) 'the Start menu entry gone'

# 5. For all users: what /ALLUSERS chooses is what "Install for all users" chooses on the first page.
#    Para todos los usuarios: lo que elige /ALLUSERS es lo que elige «Instalar para todos los usuarios» en la primera página.
$userKey = $key
$key = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{C2F50A12-AB55-4901-A024-D09CB431EBE4}_is1'
$dir = Join-Path $env:ProgramFiles 'MeshCentral Windows Client'
$exe = Join-Path $dir 'MeshCentralClient.exe'
$link = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\MeshCentral Windows Client.lnk'
Check (-not (Test-Path $key) -and -not (Test-Path $exe)) 'nothing installed for all users before'
Run $Setup 'install-all' 180 @('/ALLUSERS')
Check (Test-Path $exe) "the app at $exe"
Check (Test-Path $link) 'the Start menu entry for all users'
Check (-not (Test-Path $userKey)) 'no entry for the account only'
Check ((Get-ItemProperty $key).DisplayVersion -eq $Version) "Installed apps (all users) says $Version"
Check ((Get-ItemProperty $key).InstallLocation.TrimEnd('\') -eq $dir) 'the entry points to Program Files (the app reads it to update itself with the installer)'

# 6. The update the app starts (MainWindow.UpdateNow_Click): silent, over the running app, and the app opens again.
#    La actualización que lanza la app: silenciosa, encima de la app abierta, y la app vuelve a abrirse.
$env:MESHCLIENT_UPDATE_FEED = 'off'
$app = Start-Process $exe -PassThru
Until { (Running).Count -gt 0 } 30 'the installed app did not start'
Run $Setup 'update-all' 180 @('/CLOSEAPPLICATIONS', '/ALLUSERS', '/RELAUNCH=1')
Check ($app.HasExited) 'the installer closed the running app'
Until { (Running).Count -gt 0 } 60 'the app did not open again after the update'
Check $true 'the app opened again after the update'
foreach ($p in Running) { try { $p.Kill($true) } catch { } }
Until { (Running).Count -eq 0 } 30 'something from the app folder still running'

$uninstaller = ((Get-ItemProperty $key).UninstallString).Trim('"')
Run $uninstaller 'uninstall-all' 120
Until { -not (Test-Path $key) -and -not (Test-Path $dir) } 120 'the uninstall for all users left the app, its folder or its entry'
Check (-not (Test-Path $link)) 'the Start menu entry for all users gone'
Write-Host "INSTALLER OK: $Version installs for the account and for all users, keeps its version, updates reopening the app and uninstalls cleanly"
