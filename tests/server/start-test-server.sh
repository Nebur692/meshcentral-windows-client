#!/usr/bin/env bash
# Starts a throwaway MeshCentral server for integration tests.
# Arranca un servidor MeshCentral desechable para las pruebas de integración.
#
# Usage / Uso:  start-test-server.sh <name> <data-dir> [network] [image]
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
cat > "$DATA/config.json" <<'JSON'
{
  "settings": { "cert": "mesh.test", "port": 443, "redirPort": 0, "mpsPort": 0, "selfUpdate": false,
                "maxInvalidLogin": false },
  "domains": { "": { "title": "Test", "newAccounts": false } }
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
for _ in $(seq 1 90); do
  if docker exec "$NAME" sh -c 'wget -q --no-check-certificate -O /dev/null https://localhost/ 2>/dev/null'; then
    echo "$NAME"; exit 0
  fi
  if ! docker inspect "$NAME" >/dev/null 2>&1; then echo "server exited / el servidor se cerró" >&2; exit 1; fi
  sleep 2
done
echo "server did not answer in 180 s / el servidor no respondió en 180 s" >&2
docker logs --tail 40 "$NAME" >&2 || true
exit 1
