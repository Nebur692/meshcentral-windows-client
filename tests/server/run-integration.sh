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
NET=mctest-net-$ID; SRV=mctest-srv-$ID; AGENT=mctest-agent-$ID; OIDC=mctest-oidc-$ID
WORK=${1:-$(mktemp -d)}
mkdir -p "$WORK"

cleanup() {
  docker stop "$AGENT" "$SRV" "$OIDC" >/dev/null 2>&1 || true
  for _ in $(seq 1 20); do docker inspect "$SRV" >/dev/null 2>&1 || docker inspect "$AGENT" >/dev/null 2>&1 || docker inspect "$OIDC" >/dev/null 2>&1 || break; sleep 1; done
  docker network rm "$NET" >/dev/null 2>&1 || true
  # Files written by root inside the containers: removed from a container too.
  # Ficheros que escribió root dentro de los contenedores: se borran también desde un contenedor.
  docker run --rm -v "$WORK:/w" --entrypoint sh "$SDK_IMAGE" -c 'rm -rf /w/meshdata /w/agent /w/results /w/oidc' >/dev/null 2>&1 || true
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
CERT=$(docker exec "$SRV" node -e "const c=require('crypto'),f=require('fs');console.log(new c.X509Certificate(f.readFileSync('/opt/meshcentral/meshcentral-data/webserver-cert-public.crt')).fingerprint256.replace(/:/g,''))")
echo "server $SRV · agent $AGENT · cert ${CERT:0:16}…"

# The tests call the server by its certificate name, as a browser would (the sign-on comes back to https://mesh.test/).
# Las pruebas llaman al servidor por el nombre de su certificado, como un navegador (el inicio único vuelve a https://mesh.test/).
docker run --rm --network "$NET" --add-host "mesh.test:$SRV_IP" --cpus 2 --memory 3g --user "$(id -u):$(id -g)" \
  -e HOME=/tmp -e DOTNET_CLI_HOME=/tmp -e NUGET_PACKAGES=/tmp/nuget -e DOTNET_NOLOGO=1 -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
  -e MESHTEST_FILTER="${MESHTEST_FILTER:-}" -e MESHTEST_URL="https://mesh.test/" -e MESHTEST_CERT_SHA256="$CERT" -e MESHTEST_OTP_SECRET="$OTP" -e MESHTEST_GROUP_ID="$GROUP" \
  -v "$ROOT:/src" -v "$WORK:/work" -w /src "$SDK_IMAGE" \
  sh -c 'if [ -n "$MESHTEST_FILTER" ]; then set -- --filter "$MESHTEST_FILTER" --logger "console;verbosity=detailed"; else set -- --logger "console;verbosity=normal"; fi
         dotnet test tests/MeshCentralClient.Core.Tests --nologo -v q "$@" --results-directory /work/results --artifacts-path /tmp/artifacts'
