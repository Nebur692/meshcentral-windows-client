#!/usr/bin/env bash
# Runs ALL the Core tests (unit + integration) against a throwaway MeshCentral with a real Linux agent,
# and removes everything it created when it ends, even on failure.
# Pasa TODAS las pruebas del núcleo (unitarias + integración) contra un MeshCentral desechable con un agente
# Linux de verdad, y quita todo lo que creó al acabar, aunque falle.
#
# Usage / Uso:  tests/server/run-integration.sh [work-dir]
#   MESH_IMAGE (default ghcr.io/ylianst/meshcentral:latest), SDK_IMAGE (default mcr.microsoft.com/dotnet/sdk:10.0)
#   MESHTEST_FILTER: only some tests, with their full output (e.g. "FullyQualifiedName~Desktop")
#   MESHTEST_FILTER: solo algunas pruebas, con toda su salida (p. ej. "FullyQualifiedName~Desktop")
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
HERE="$ROOT/tests/server"
MESH_IMAGE=${MESH_IMAGE:-ghcr.io/ylianst/meshcentral:latest}
SDK_IMAGE=${SDK_IMAGE:-mcr.microsoft.com/dotnet/sdk:10.0}
ID=$$
NET=mctest-net-$ID; SRV=mctest-srv-$ID; AGENT=mctest-agent-$ID; OIDC=mctest-oidc-$ID; SSHD=mctest-ssh-$ID
WORK=${1:-$(mktemp -d)}
mkdir -p "$WORK"

cleanup() {
  docker stop "$AGENT" "$SRV" "$OIDC" "$SSHD" >/dev/null 2>&1 || true
  # The agent is not --rm, so its state survives a crash: removed here by name.
  # El agente no es --rm, para que su estado sobreviva a una caída: se borra aquí por su nombre.
  docker rm -f "$AGENT" >/dev/null 2>&1 || true
  for _ in $(seq 1 20); do docker inspect "$SRV" >/dev/null 2>&1 || docker inspect "$AGENT" >/dev/null 2>&1 || docker inspect "$OIDC" >/dev/null 2>&1 || docker inspect "$SSHD" >/dev/null 2>&1 || break; sleep 1; done
  docker network rm "$NET" >/dev/null 2>&1 || true
  # Files written by root inside the containers: removed from a container too.
  # Ficheros que escribió root dentro de los contenedores: se borran también desde un contenedor.
  docker run --rm -v "$WORK:/w" --entrypoint sh "$SDK_IMAGE" -c 'rm -rf /w/meshdata /w/agent /w/results /w/oidc /w/agentcache /w/ssh' >/dev/null 2>&1 || true
  rmdir "$WORK" 2>/dev/null || true
}
trap cleanup EXIT

docker network create "$NET" >/dev/null

# Throwaway OpenID Connect provider (tests/oidc), started before MeshCentral because it discovers it at start.
# Proveedor OpenID Connect desechable (tests/oidc), antes que MeshCentral porque este lo descubre al arrancar.
mkdir -p "$WORK/oidc" && cp "$ROOT/tests/oidc/package.json" "$ROOT/tests/oidc/package-lock.json" "$ROOT/tests/oidc/provider.mjs" "$WORK/oidc/"
docker run -d --rm --name "$OIDC" --network "$NET" --cpus 0.5 --memory 256m -e ISSUER="http://$OIDC:3000" \
  -v "$WORK/oidc:/o" -w /o node:22-alpine sh -c 'npm ci --no-audit --no-fund --loglevel=error && exec node provider.mjs' >/dev/null
for _ in $(seq 1 60); do docker logs "$OIDC" 2>&1 | grep -q "oidc ready" && break; sleep 2; done
docker logs "$OIDC" 2>&1 | grep -q "oidc ready" || { echo "the OIDC provider did not start / el proveedor OIDC no arrancó" >&2; docker logs "$OIDC" >&2; exit 1; }

OIDC_ISSUER="http://$OIDC:3000" "$HERE/start-test-server.sh" "$SRV" "$WORK/meshdata" "$NET" "$MESH_IMAGE" >/dev/null
SRV_IP=$(docker inspect "$SRV" --format "{{(index .NetworkSettings.Networks \"$NET\").IPAddress}}")
read -r OTP GROUP < <(docker exec -i "$SRV" node - < "$HERE/setup-test-data.js")
[ -n "${OTP:-}" ] && [ -n "${GROUP:-}" ]
"$HERE/start-test-agent.sh" "$SRV" "$NET" "$AGENT" "$WORK/agent" "$GROUP" >/dev/null
# A throwaway SSH server on the same network, reached by the agent by its name: the target of the SSH tests. Its client
# keys (one with a passphrase) are written to the work folder for the tests to read.
# Un servidor SSH desechable en la misma red, al que el agente llega por su nombre: el destino de las pruebas de SSH. Sus
# claves de cliente (una con frase) se dejan en la carpeta de trabajo para que las lean las pruebas.
mkdir -p "$WORK/ssh"
docker run -d --rm --name "$SSHD" --network "$NET" --cpus 0.5 --memory 128m -v "$WORK/ssh:/k" --entrypoint sh node:22-alpine -c '
  apk add --no-cache openssh >/dev/null && ssh-keygen -A >/dev/null &&
  adduser -D -s /bin/sh sshuser && echo "sshuser:Ssh-Pass-1234" | chpasswd &&
  ssh-keygen -q -t ed25519 -N "" -C mctest -f /k/id_plain && ssh-keygen -q -t ed25519 -N "Frase-1234" -C mctest -f /k/id_phrase &&
  mkdir -p /home/sshuser/.ssh && cat /k/id_plain.pub /k/id_phrase.pub > /home/sshuser/.ssh/authorized_keys &&
  chown -R sshuser /home/sshuser/.ssh && chmod 700 /home/sshuser/.ssh && chmod 600 /home/sshuser/.ssh/authorized_keys &&
  chmod 644 /k/id_plain /k/id_phrase && for h in /etc/ssh/ssh_host_*_key.pub; do ssh-keygen -lf $h; done > /k/hostkeys &&
  echo "sshd ready" && exec /usr/sbin/sshd -D -e' >/dev/null
for _ in $(seq 1 60); do docker logs "$SSHD" 2>&1 | grep -q "sshd ready" && break; sleep 1; done
docker logs "$SSHD" 2>&1 | grep -q "sshd ready" || { echo "the SSH server did not start / el servidor SSH no arrancó" >&2; docker logs "$SSHD" >&2; exit 1; }

CERT=$(docker exec "$SRV" node -e "const c=require('crypto'),f=require('fs');console.log(new c.X509Certificate(f.readFileSync('/opt/meshcentral/meshcentral-data/webserver-cert-public.crt')).fingerprint256.replace(/:/g,''))")
echo "server $SRV · agent $AGENT · cert ${CERT:0:16}…"

# The tests call the server by its certificate name, as a browser would (the sign-on comes back to https://mesh.test/).
# Las pruebas llaman al servidor por el nombre de su certificado, como un navegador (el inicio único vuelve a https://mesh.test/).
docker run --rm --network "$NET" --add-host "mesh.test:$SRV_IP" --cpus 2 --memory 3g --user "$(id -u):$(id -g)" \
  -e HOME=/tmp -e DOTNET_CLI_HOME=/tmp -e NUGET_PACKAGES=/tmp/nuget -e DOTNET_NOLOGO=1 -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
  -e MESHTEST_FILTER="${MESHTEST_FILTER:-}" -e MESHTEST_URL="https://mesh.test/" -e MESHTEST_CERT_SHA256="$CERT" -e MESHTEST_OTP_SECRET="$OTP" -e MESHTEST_GROUP_ID="$GROUP" \
  -e MESHTEST_SSH_HOST="$SSHD" -e MESHTEST_SSH_DIR=/work/ssh \
  -v "$ROOT:/src" -v "$WORK:/work" -w /src "$SDK_IMAGE" \
  sh -c 't() { dotnet test tests/MeshCentralClient.Core.Tests --nologo -v q "$@" --results-directory /work/results --artifacts-path /tmp/artifacts; }
         if [ -n "$MESHTEST_FILTER" ]; then t --filter "$MESHTEST_FILTER" --logger "console;verbosity=detailed"; exit; fi
         # Asking the Linux agent for a desktop when it has no screen crashes the agent itself (MeshAgent, measured on
         # 1.3.1 with exactly what the website sends), and after two crashes it stops. So the desktop test runs last.
         # Pedir el escritorio al agente Linux sin pantalla cuelga al propio agente (MeshAgent, medido en 1.3.1 con
         # exactamente lo que manda la web), y a los dos cuelgues se para. Por eso la prueba del escritorio va la última.
         # Moving the agent to another group goes alone too: meanwhile the others would not find their device.
         # Mover el agente a otro grupo también va solo: mientras, las demás no encontrarían su equipo.
         t --filter "FullyQualifiedName!~DesktopIntegrationTests&FullyQualifiedName!~GroupsMoveIntegrationTests" --logger "console;verbosity=normal" &&
         t --no-build --filter "FullyQualifiedName~GroupsMoveIntegrationTests" --logger "console;verbosity=normal" &&
         t --no-build --filter "FullyQualifiedName~DesktopIntegrationTests" --logger "console;verbosity=normal"' || {
  rc=$?
  # On failure, what the agent and the server were doing: without it a dropped agent can only be guessed at.
  # Si falla, qué hacían el agente y el servidor: sin esto, un agente caído solo se puede adivinar.
  echo "== agent $AGENT: $(docker inspect "$AGENT" --format 'status={{.State.Status}} exit={{.State.ExitCode}} oom={{.State.OOMKilled}}' 2>&1)"
  docker logs --tail 60 "$AGENT" 2>&1 | sed 's/^/  agent| /'
  docker logs --tail 80 "$SRV" 2>&1 | sed 's/^/  server| /'
  exit "$rc"
}
