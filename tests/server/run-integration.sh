#!/usr/bin/env bash
# Runs ALL the Core tests (unit + integration) against a throwaway MeshCentral with a real Linux agent,
# and removes everything it created when it ends, even on failure.
# Pasa TODAS las pruebas del núcleo (unitarias + integración) contra un MeshCentral desechable con un agente
# Linux de verdad, y quita todo lo que creó al acabar, aunque falle.
#
# Usage / Uso:  tests/server/run-integration.sh [work-dir]
#   MESH_IMAGE (default ghcr.io/ylianst/meshcentral:latest), SDK_IMAGE (default mcr.microsoft.com/dotnet/sdk:10.0)
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
HERE="$ROOT/tests/server"
MESH_IMAGE=${MESH_IMAGE:-ghcr.io/ylianst/meshcentral:latest}
SDK_IMAGE=${SDK_IMAGE:-mcr.microsoft.com/dotnet/sdk:10.0}
ID=$$
NET=mctest-net-$ID; SRV=mctest-srv-$ID; AGENT=mctest-agent-$ID
WORK=${1:-$(mktemp -d)}
mkdir -p "$WORK"

cleanup() {
  docker stop "$AGENT" "$SRV" >/dev/null 2>&1 || true
  for _ in $(seq 1 20); do docker inspect "$SRV" >/dev/null 2>&1 || docker inspect "$AGENT" >/dev/null 2>&1 || break; sleep 1; done
  docker network rm "$NET" >/dev/null 2>&1 || true
  # Files written by root inside the containers: removed from a container too.
  # Ficheros que escribió root dentro de los contenedores: se borran también desde un contenedor.
  docker run --rm -v "$WORK:/w" --entrypoint sh "$SDK_IMAGE" -c 'rm -rf /w/meshdata /w/agent /w/results' >/dev/null 2>&1 || true
  rmdir "$WORK" 2>/dev/null || true
}
trap cleanup EXIT

docker network create "$NET" >/dev/null
"$HERE/start-test-server.sh" "$SRV" "$WORK/meshdata" "$NET" "$MESH_IMAGE" >/dev/null
read -r OTP GROUP < <(docker exec -i "$SRV" node - < "$HERE/setup-test-data.js")
[ -n "${OTP:-}" ] && [ -n "${GROUP:-}" ]
"$HERE/start-test-agent.sh" "$SRV" "$NET" "$AGENT" "$WORK/agent" "$GROUP" >/dev/null
CERT=$(docker exec "$SRV" node -e "const c=require('crypto'),f=require('fs');console.log(new c.X509Certificate(f.readFileSync('/opt/meshcentral/meshcentral-data/webserver-cert-public.crt')).fingerprint256.replace(/:/g,''))")
echo "server $SRV · agent $AGENT · cert ${CERT:0:16}…"

docker run --rm --network "$NET" --cpus 2 --memory 3g --user "$(id -u):$(id -g)" \
  -e HOME=/tmp -e DOTNET_CLI_HOME=/tmp -e NUGET_PACKAGES=/tmp/nuget -e DOTNET_NOLOGO=1 -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
  -e MESHTEST_URL="https://$SRV/" -e MESHTEST_CERT_SHA256="$CERT" -e MESHTEST_OTP_SECRET="$OTP" -e MESHTEST_GROUP_ID="$GROUP" \
  -v "$ROOT:/src" -v "$WORK:/work" -w /src "$SDK_IMAGE" \
  sh -c 'dotnet test tests/MeshCentralClient.Core.Tests --nologo -v q --logger "console;verbosity=normal" --results-directory /work/results --artifacts-path /tmp/artifacts'
