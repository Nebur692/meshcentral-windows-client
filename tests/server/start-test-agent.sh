#!/usr/bin/env bash
# Connects a Linux MeshAgent (in a throwaway Debian container) to the test server's "Test group".
# Conecta un MeshAgent de Linux (en un contenedor Debian desechable) al «Test group» del servidor de pruebas.
#
# Usage / Uso:  start-test-agent.sh <server-container> <network> <agent-name> <work-dir> <group-id>
set -euo pipefail
SERVER=${1:?server}; NET=${2:?network}; NAME=${3:?agent name}; WORK=${4:?work dir}; GROUP=${5:?group id}
IP=$(docker inspect "$SERVER" --format "{{(index .NetworkSettings.Networks \"$NET\").IPAddress}}")
mkdir -p "$WORK"
MID=${GROUP#mesh//}
docker exec "$SERVER" wget -q --no-check-certificate -O - "https://localhost/meshagents?id=6" > "$WORK/meshagent"
docker exec "$SERVER" wget -q --no-check-certificate -O - "https://localhost/meshsettings?id=$MID" > "$WORK/meshagent.msh"
chmod +x "$WORK/meshagent"
grep -q '^MeshServer=' "$WORK/meshagent.msh"
docker run -d --name "$NAME" --hostname test-device --network "$NET" --add-host "mesh.test:$IP" \
  --cpus 0.5 --memory 256m -v "$WORK:/agent" -w /agent debian:12-slim ./meshagent connect >/dev/null
echo "$NAME"
