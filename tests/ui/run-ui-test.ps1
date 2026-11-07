# End-to-end UI test on Windows: a real MeshCentral (npm), a real Windows agent, and the published app driven
# with UI Automation. Screenshots end up in <work>\shots. Used by the CI; it needs an interactive desktop and admin
# rights (it adds "mesh.test" to the hosts file).
# Prueba de interfaz de punta a punta en Windows: un MeshCentral real (npm), un agente de Windows real y la app
# publicada manejada con UI Automation. Las capturas quedan en <trabajo>\shots. La usa la CI; necesita un escritorio
# interactivo y permisos de administrador (añade «mesh.test» al fichero hosts).
param(
  [Parameter(Mandatory)] [string] $Exe,
  # The same app built as version 99.0.0, served by a local release feed to test the self-update.
  # La misma app compilada como versión 99.0.0, servida por una fuente de versiones local para probar la autoactualización.
  [Parameter(Mandatory)] [string] $Exe99,
  [Parameter(Mandatory)] [string] $Work,
  [string] $MeshCentralVersion = 'latest'
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Resolve-Path "$PSScriptRoot\..\.."
$mc = Join-Path $Work 'mc'
$data = Join-Path $mc 'meshcentral-data'
New-Item -ItemType Directory -Force $data, (Join-Path $Work 'agent'), (Join-Path $Work 'shots') | Out-Null

Write-Host "== MeshCentral $MeshCentralVersion from npm"
Push-Location $mc
npm install --no-audit --no-fund --loglevel=error "meshcentral@$MeshCentralVersion" | Out-Host
@'
{
  "settings": { "cert": "mesh.test", "port": 4443, "redirPort": 0, "mpsPort": 0, "selfUpdate": false, "maxInvalidLogin": false },
  "domains": { "": { "title": "Test", "newAccounts": false } }
}
'@ | Set-Content -Encoding utf8 (Join-Path $data 'config.json')
$m = 'node_modules\meshcentral'
node $m --createaccount admin --pass Admin-Pass-1234 --name Admin | Out-Host
node $m --adminaccount admin | Out-Host
node $m --createaccount user --pass User-Pass-1234 --name User | Out-Host
node $m --createaccount otp --pass Otp-Pass-1234 --name Otp | Out-Host
$server = Start-Process node -ArgumentList $m -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $Work 'mc.log') -RedirectStandardError (Join-Path $Work 'mc.err')
Pop-Location

Add-Content -Path "$env:windir\System32\drivers\etc\hosts" -Value "`n127.0.0.1 mesh.test"

$ok = $false
for ($i = 0; $i -lt 150 -and -not $ok; $i++) {
  Start-Sleep 2
  try { Invoke-WebRequest -Uri 'https://mesh.test:4443/' -SkipCertificateCheck -TimeoutSec 5 | Out-Null; $ok = $true } catch { }
}
if (-not $ok) { Get-Content (Join-Path $Work 'mc.log') -Tail 40; throw 'MeshCentral did not answer in 300 s' }
# otplib is installed by MeshCentral itself on first start (it may restart once). / otplib lo instala MeshCentral al arrancar (puede reiniciarse una vez).
for ($i = 0; $i -lt 60 -and -not (Test-Path (Join-Path $mc 'node_modules\otplib')); $i++) { Start-Sleep 2 }
Start-Sleep 5

Write-Host '== test data'
$env:MESHCENTRAL_MODULES = Join-Path $mc 'node_modules'
$env:MESHTEST_WS = 'wss://mesh.test:4443/control.ashx'
$out = (node (Join-Path $root 'tests\server\setup-test-data.js')) -split ' '
if ($out.Count -ne 2) { throw "setup-test-data.js failed: $out" }
$otp = $out[0]; $group = $out[1]
$mid = $group -replace '^mesh//', ''

Write-Host '== Windows agent'
$agent = Join-Path $Work 'agent'
Invoke-WebRequest -Uri 'https://mesh.test:4443/meshagents?id=4' -SkipCertificateCheck -OutFile (Join-Path $agent 'meshagent.exe')
Invoke-WebRequest -Uri "https://mesh.test:4443/meshsettings?id=$mid" -SkipCertificateCheck -OutFile (Join-Path $agent 'meshagent.msh')
$agentProc = Start-Process (Join-Path $agent 'meshagent.exe') -ArgumentList 'connect' -WorkingDirectory $agent -PassThru -WindowStyle Hidden

Write-Host '== local release feed (version 99)'
$feed = Join-Path $Work 'feed'
New-Item -ItemType Directory -Force $feed | Out-Null
Copy-Item $Exe99 (Join-Path $feed 'MeshCentralClient.exe')
$sha99 = (Get-FileHash -Algorithm SHA256 (Join-Path $feed 'MeshCentralClient.exe')).Hash.ToLowerInvariant()
"$sha99  MeshCentralClient.exe" | Set-Content -NoNewline -Encoding ascii (Join-Path $feed 'MeshCentralClient.exe.sha256')
('0' * 64) + '  MeshCentralClient.exe' | Set-Content -NoNewline -Encoding ascii (Join-Path $feed 'bad.sha256')
$port = 8099
function Release($shaFile) {
  @{ tag_name = 'v99.0.0'; draft = $false; prerelease = $false; html_url = "http://127.0.0.1:$port/";
     assets = @(@{ name = 'MeshCentralClient.exe'; browser_download_url = "http://127.0.0.1:$port/MeshCentralClient.exe" },
                @{ name = 'MeshCentralClient.exe.sha256'; browser_download_url = "http://127.0.0.1:$port/$shaFile" }) } | ConvertTo-Json -Depth 4
}
Release 'MeshCentralClient.exe.sha256' | Set-Content -Encoding utf8 (Join-Path $feed 'latest.json')
Release 'bad.sha256' | Set-Content -Encoding utf8 (Join-Path $feed 'latest-bad.json')
$feedProc = Start-Process python -ArgumentList '-m', 'http.server', $port, '--bind', '127.0.0.1', '--directory', $feed -PassThru -WindowStyle Hidden
Start-Sleep 2

try {
  Write-Host '== UI test'
  $env:MESHUI_UPDATE_FEED = "http://127.0.0.1:$port/latest.json"
  $env:MESHUI_UPDATE_FEED_BAD = "http://127.0.0.1:$port/latest-bad.json"
  $env:MESHUI_EXE99_SHA = $sha99
  $env:MESHUI_EXE = $Exe
  $env:MESHUI_SERVER = 'mesh.test:4443'
  $env:MESHUI_OTP_SECRET = $otp
  $env:MESHUI_SHOTS = Join-Path $Work 'shots'
  dotnet test (Join-Path $root 'tests\MeshCentralClient.UiTests') -c Release --logger 'console;verbosity=normal'
  if ($LASTEXITCODE -ne 0) { throw "UI test failed ($LASTEXITCODE)" }
}
finally {
  foreach ($p in @($agentProc, $server, $feedProc)) { if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } }
}
