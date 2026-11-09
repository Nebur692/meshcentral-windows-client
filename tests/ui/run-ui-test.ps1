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

# Windows' own OpenSSH server with a throwaway local account: the target of the SSH test (SshTests). If it cannot be
# turned on, the variables stay unset and that test fails saying so: it is not skipped.
# El servidor OpenSSH de Windows con una cuenta local desechable: el destino de la prueba de SSH. Si no se puede
# encender, las variables quedan sin poner y esa prueba falla diciéndolo: no se salta.
Write-Host '== OpenSSH server'
$sshUser = 'mccssh'
$sshPass = 'Ssh-' + [guid]::NewGuid().ToString('N').Substring(0, 12) + '-9a!'
$sshStarted = $false
try {
  $watch = [Diagnostics.Stopwatch]::StartNew()
  $cap = Get-WindowsCapability -Online -Name 'OpenSSH.Server*' | Select-Object -First 1
  Write-Host "OpenSSH.Server: $($cap.Name) $($cap.State)"
  if ($cap.State -ne 'Installed') { Add-WindowsCapability -Online -Name $cap.Name | Out-Null }
  New-LocalUser -Name $sshUser -Password (ConvertTo-SecureString $sshPass -AsPlainText -Force) -AccountNeverExpires -PasswordNeverExpires | Out-Null
  # As any normal account: in the local Users group (by its well-known SID, whatever the language).
  # Como cualquier cuenta normal: en el grupo local Usuarios (por su SID conocido, sea cual sea el idioma).
  Add-LocalGroupMember -SID 'S-1-5-32-545' -Member $sshUser
  # GitHub's image limits sshd with AllowGroups (seen in its log): the account joins the groups it lists.
  # La imagen de GitHub limita sshd con AllowGroups (visto en su registro): la cuenta entra en los grupos que lista.
  $sshConfig = Join-Path $env:ProgramData 'ssh\sshd_config'
  foreach ($line in (Get-Content $sshConfig | Where-Object { $_ -match '^\s*AllowGroups\s' })) {
    Write-Host "sshd_config: $line"
    foreach ($g in ($line -split '\s+' | Select-Object -Skip 1 | Where-Object { $_ })) { Add-LocalGroupMember -Group $g -Member $sshUser -ErrorAction SilentlyContinue }
  }
  # Its Windows profile is made now: at its first sign-in Windows takes a long time to make it, inside the test.
  # Su perfil de Windows se hace ahora: en su primera entrada Windows tarda mucho en hacerlo, dentro de la prueba.
  $sshCred = New-Object System.Management.Automation.PSCredential($sshUser, (ConvertTo-SecureString $sshPass -AsPlainText -Force))
  Start-Process cmd.exe -ArgumentList '/c', 'exit' -Credential $sshCred -LoadUserProfile -WindowStyle Hidden -Wait
  Start-Service sshd
  $sshStarted = $true
  Write-Host "sshd running after $([int]$watch.Elapsed.TotalSeconds) s"
  $env:MESHUI_SSH_USER = $sshUser
  $env:MESHUI_SSH_PASS = $sshPass
} catch { Write-Host "OpenSSH server not available: $_" }

# Measured 09-11: the agent sometimes comes down (FATAL EXCEPTION, always at the same address; ILibParsers.c is only what
# prints it) during the port tests, the first time they run against a fresh agent: 1 of 17 runs in a row, and never after it
# restarted. 12 real mstsc killed on their own did not do it, so what sets it off is unknown. Nothing restarts a connect-mode agent.
# As a service would, it comes back, and every time is counted and printed at the end: not hidden, and the other tests
# do not fail for it.
# Medido el 09-11: el agente a veces cae (FATAL EXCEPTION, siempre en la misma dirección; ILibParsers.c solo es quien lo
# escribe) en las pruebas de puertos, la primera vez contra un agente recién puesto: 1 de 17 pasadas seguidas, y nunca tras
# reiniciarse. 12 mstsc reales matados por sí solos no lo hicieron, así que no se sabe qué lo dispara. Como un servicio, vuelve, y cada vez se cuenta y se escribe al final: sin esconderlo, y sin que
# fallen por ello las demás pruebas.
$agentExe = Join-Path $agent 'meshagent.exe'
$agentDowns = Join-Path $Work 'agent-downs.txt'
$agentWatch = Start-Job -ArgumentList $agentExe, $agent, $agentDowns -ScriptBlock {
  param($exe, $dir, $downs)
  while ($true) {
    Start-Sleep 2
    if (-not (Get-Process meshagent -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe })) {
      Add-Content $downs (Get-Date -Format 'HH:mm:ss')
      Start-Process $exe -ArgumentList 'connect' -WorkingDirectory $dir -WindowStyle Hidden
    }
  }
}

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
  # MESHUI_FILTER: only some tests, to iterate on one thing. / Solo algunas pruebas, para iterar sobre una cosa.
  $filter = if ($env:MESHUI_FILTER) { @('--filter', $env:MESHUI_FILTER) } else { @() }
  dotnet test (Join-Path $root 'tests\MeshCentralClient.UiTests') -c Release --logger 'console;verbosity=normal' @filter
  if ($LASTEXITCODE -ne 0) { throw "UI test failed ($LASTEXITCODE)" }
}
finally {
  Stop-Job $agentWatch -ErrorAction SilentlyContinue; Remove-Job $agentWatch -Force -ErrorAction SilentlyContinue
  # How the agent ended, for when tunnels stop opening. / Cómo acabó el agente, por si dejan de abrirse los túneles.
  Write-Host "agent $($agentProc.Id): exited=$($agentProc.HasExited)"
  if (Test-Path $agentDowns) { Write-Host "AGENT CAME DOWN at: $((Get-Content $agentDowns) -join ', ')" } else { Write-Host 'agent never came down' }
  Get-ChildItem $agent | ForEach-Object { Write-Host "  agent file: $($_.Name) $($_.Length)" }
  Get-ChildItem $agent -Filter '*.log' | ForEach-Object { Write-Host "== $($_.Name)"; Get-Content $_.FullName -Tail 30 | ForEach-Object { "  agent| $_" } }
  foreach ($p in @($agentProc, $server, $feedProc, $oidcProc)) { if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } }
  if ($sshStarted) {
    # What the SSH server logged, for when the SSH test fails. / Lo que apuntó el servidor SSH, por si falla su prueba.
    Get-WinEvent -LogName 'OpenSSH/Operational' -MaxEvents 30 -ErrorAction SilentlyContinue | Sort-Object TimeCreated | ForEach-Object { "  sshd| $($_.TimeCreated.ToString('HH:mm:ss')) $($_.Message)" }
    Stop-Service sshd -ErrorAction SilentlyContinue
  }
  Remove-LocalUser -Name $sshUser -ErrorAction SilentlyContinue
  # A test may have restarted a hung agent: whatever runs from this run's agent file is stopped too.
  # Una prueba puede haber reiniciado un agente colgado: también se para lo que corra desde el agente de esta pasada.
  Get-Process meshagent -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $agentExe } | Stop-Process -Force -ErrorAction SilentlyContinue
}
