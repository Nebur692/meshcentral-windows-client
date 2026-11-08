#!/usr/bin/env bash
# Starts a throwaway MeshCentral server for integration tests.
# Arranca un servidor MeshCentral desechable para las pruebas de integración.
#
# Usage / Uso:  start-test-server.sh <name> <data-dir> [network] [image]
#   OIDC_ISSUER=http://<oidc>:3000 also enables sign-in with that OpenID Connect provider (tests/oidc), group "mesh-users".
#   OIDC_ISSUER=http://<oidc>:3000 activa además la entrada con ese proveedor OpenID Connect (tests/oidc), grupo «mesh-users».
# Prints the container name when the server answers. / Escribe el nombre del contenedor cuando el servidor responde.
# Test accounts / Cuentas de prueba:
#   admin / Admin-Pass-1234  (site administrator / administrador del sitio)
#   user  / User-Pass-1234   (no rights / sin derechos)
#   otp   / Otp-Pass-1234    (TOTP enabled by setup-test-data.js / con TOTP, lo activa setup-test-data.js)
set -euo pipefail
NAME=${1:?name}
DATA=${2:?data dir}
NET=${3:-bridge}
IMAGE=${4:-ghcr.io/ylianst/meshcentral:latest}
mkdir -p "$DATA"
AUTH=""
if [ -n "${OIDC_ISSUER:-}" ]; then
  AUTH=', "authStrategies": { "oidc": { "issuer": "'"$OIDC_ISSUER"'", "client": { "client_id": "meshcentral-test", "client_secret": "test-secret-not-for-production" }, "newAccounts": true, "groups": { "claim": "groups", "required": ["mesh-users"] } } }'
fi
cat > "$DATA/config.json" <<JSON
{
  "settings": { "cert": "mesh.test", "port": 443, "redirPort": 0, "mpsPort": 0, "selfUpdate": false,
                "maxInvalidLogin": false },
  "domains": { "": { "title": "Test", "newAccounts": false, "minify": true $AUTH } }
}
JSON
M=/opt/meshcentral/meshcentral/meshcentral
CFG=/opt/meshcentral/meshcentral-data/config.json
docker run -d --rm --name "$NAME" --network "$NET" --cpus 1 --memory 768m \
  -v "$DATA:/opt/meshcentral/meshcentral-data" --entrypoint sh "$IMAGE" -c "
    cd /opt/meshcentral &&
    node $M --configfile $CFG --createaccount admin --pass Admin-Pass-1234 --name Admin &&
    node $M --configfile $CFG --adminaccount admin &&
    node $M --configfile $CFG --createaccount user --pass User-Pass-1234 --name User &&
    node $M --configfile $CFG --createaccount otp --pass Otp-Pass-1234 --name Otp &&
    exec node $M --configfile $CFG" >/dev/null
# Ready means the page answers AND the agents the tests use are served: the server starts before its default agents
# finish downloading from GitHub, and until then it answers 404 for them (Linux x64 = 6, Windows x64 = 4).
# Listo es que conteste la página Y que dé los agentes que usan las pruebas: el servidor arranca antes de acabar de bajar
# de GitHub sus agentes y hasta entonces contesta 404 por ellos (Linux x64 = 6, Windows x64 = 4).
for _ in $(seq 1 150); do
  if docker exec "$NAME" sh -c 'for u in / "/meshagents?id=6" "/meshagents?id=4"; do wget -q --no-check-certificate -O /dev/null "https://localhost$u" 2>/dev/null || exit 1; done'; then
    echo "$NAME"; exit 0
  fi
  if ! docker inspect "$NAME" >/dev/null 2>&1; then echo "server exited / el servidor se cerró" >&2; exit 1; fi
  sleep 2
done
echo "server or its agents not ready in 300 s / el servidor o sus agentes no estaban listos en 300 s" >&2
docker logs --tail 40 "$NAME" >&2 || true
exit 1
