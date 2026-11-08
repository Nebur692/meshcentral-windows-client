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

Write-Host '== OpenID Connect test provider'
# Before MeshCentral, which discovers it when it starts. / Antes que MeshCentral, que lo descubre al arrancar.
$oidc = Join-Path $Work 'oidc'
New-Item -ItemType Directory -Force $oidc | Out-Null
Copy-Item (Join-Path $root 'tests\oidc\package.json'), (Join-Path $root 'tests\oidc\package-lock.json'), (Join-Path $root 'tests\oidc\provider.mjs') $oidc
Push-Location $oidc
npm ci --no-audit --no-fund --loglevel=error | Out-Host
$env:ISSUER = 'http://localhost:3000'
$env:REDIRECT_URI = 'https://mesh.test:4443/auth-oidc-callback'
$oidcProc = Start-Process node -ArgumentList 'provider.mjs' -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $Work 'oidc.log') -RedirectStandardError (Join-Path $Work 'oidc.err')
Pop-Location
$ok = $false
for ($i = 0; $i -lt 30 -and -not $ok; $i++) { Start-Sleep 1; try { Invoke-WebRequest 'http://localhost:3000/.well-known/openid-configuration' -TimeoutSec 3 | Out-Null; $ok = $true } catch { } }
if (-not $ok) { Get-Content (Join-Path $Work 'oidc.err') -Tail 20; throw 'the OIDC provider did not start' }

Write-Host "== MeshCentral $MeshCentralVersion from npm"
Push-Location $mc
npm install --no-audit --no-fund --loglevel=error "meshcentral@$MeshCentralVersion" | Out-Host
@'
{
  "settings": { "cert": "mesh.test", "port": 4443, "redirPort": 0, "mpsPort": 0, "selfUpdate": false, "maxInvalidLogin": false, "agentDownloads": { "enabled": false } },
  "domains": { "": { "title": "Test", "newAccounts": false,
    "authStrategies": { "oidc": { "issuer": "http://localhost:3000",
      "client": { "client_id": "meshcentral-test", "client_secret": "test-secret-not-for-production" },
      "newAccounts": true, "groups": { "claim": "groups", "required": ["mesh-users"] } } } } }
}
'@ | Set-Content -Encoding utf8 (Join-Path $data 'config.json')
$m = 'node_modules\meshcentral'
# The Windows agent the tests use, supplied before it starts instead of the server downloading 119 MiB itself: see tests/server/fetch-agents.mjs.
# El agente de Windows que usan las pruebas, puesto antes de arrancar en vez de que el servidor baje él 119 MiB: ver tests/server/fetch-agents.mjs.
$agentCache = if ($env:MESHTEST_AGENT_CACHE) { $env:MESHTEST_AGENT_CACHE } else { Join-Path $Work 'agentcache' }
node (Join-Path $root 'tests\server\fetch-agents.mjs') (Join-Path $m 'agents\agent-defaults.json') $data $agentCache MeshService64.exe
if ($LASTEXITCODE -ne 0) { throw 'the Windows agent could not be supplied to MeshCentral' }
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
# With OIDC it also installs openid-client and restarts: wait for both, then for it to answer again.
# Con OIDC instala además openid-client y se reinicia: se esperan los dos y que vuelva a contestar.
foreach ($module in 'otplib', 'openid-client') {
  for ($i = 0; $i -lt 90 -and -not (Test-Path (Join-Path $mc "node_modules\$module")); $i++) { Start-Sleep 2 }
  if (-not (Test-Path (Join-Path $mc "node_modules\$module"))) { Get-Content (Join-Path $Work 'mc.log') -Tail 40; throw "MeshCentral did not install $module" }
}
Start-Sleep 10
$ok = $false
for ($i = 0; $i -lt 60 -and -not $ok; $i++) {
  try { $page = Invoke-WebRequest -Uri 'https://mesh.test:4443/' -SkipCertificateCheck -TimeoutSec 5; $ok = $page.Content -match "authStrategies = 'oidc'" } catch { }
  if (-not $ok) { Start-Sleep 2 }
}
if (-not $ok) { Get-Content (Join-Path $Work 'mc.log') -Tail 40; throw 'MeshCentral did not come back with OIDC' }

Write-Host '== test data'
$env:MESHCENTRAL_MODULES = Join-Path $mc 'node_modules'
$env:MESHTEST_WS = 'wss://mesh.test:4443/control.ashx'
$out = (node (Join-Path $root 'tests\server\setup-test-data.js')) -split ' '
if ($out.Count -ne 2) { throw "setup-test-data.js failed: $out" }
$otp = $out[0]; $group = $out[1]
$mid = $group -replace '^mesh//', ''

Write-Host '== Windows agent'
$agent = Join-Path $Work 'agent'
# The server starts before its default agents finish downloading from GitHub and answers 404 until then: wait for it.
# El servidor arranca antes de acabar de bajar de GitHub sus agentes y hasta entonces contesta 404: se espera.
$ok = $false
for ($i = 0; $i -lt 150 -and -not $ok; $i++) {
  try { Invoke-WebRequest -Uri 'https://mesh.test:4443/meshagents?id=4' -SkipCertificateCheck -TimeoutSec 60 -OutFile (Join-Path $agent 'meshagent.exe'); $ok = $true } catch { Start-Sleep 2 }
}
if (-not $ok) { Get-Content (Join-Path $Work 'mc.log') -Tail 40; throw 'MeshCentral did not serve the Windows agent in 300 s' }
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
  $env:MESHUI_AGENT_PID = $agentProc.Id
  $env:MESHUI_SERVER = 'mesh.test:4443'
  # The server's own certificate, for tests that use the core directly. / El certificado del servidor, para las pruebas que usan el núcleo directamente.
  $env:MESHUI_CERT_SHA256 = (New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 (Join-Path $data 'webserver-cert-public.crt')).GetCertHashString('SHA256')
  $env:MESHUI_OTP_SECRET = $otp
  $env:MESHUI_SHOTS = Join-Path $Work 'shots'
  dotnet test (Join-Path $root 'tests\MeshCentralClient.UiTests') -c Release --logger 'console;verbosity=normal'
  if ($LASTEXITCODE -ne 0) { throw "UI test failed ($LASTEXITCODE)" }
}
finally {
  foreach ($p in @($agentProc, $server, $feedProc, $oidcProc)) { if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } }
  # A test may have restarted a hung agent: whatever runs from this run's agent file is stopped too.
  # Una prueba puede haber reiniciado un agente colgado: también se para lo que corra desde el agente de esta pasada.
  $agentExe = Join-Path $agent 'meshagent.exe'
  Get-Process meshagent -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $agentExe } | Stop-Process -Force -ErrorAction SilentlyContinue
}
