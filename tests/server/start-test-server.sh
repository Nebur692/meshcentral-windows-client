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
#   mfa   / Mfa-Pass-1234    (only for the email and SMS two-factor tests / solo para las pruebas de correo y SMS)
set -euo pipefail
# Test containers and networks carry a label, so the bench's cleaner finds them (PRUEBA = who runs the tests).
# Contenedores y redes de prueba llevan una etiqueta, para que el limpiador del banco los encuentre.
PRUEBA_L="--label prueba=${PRUEBA:-meshclient-bench}"
NAME=${1:?name}
DATA=${2:?data dir}
NET=${3:-bridge}
IMAGE=${4:-ghcr.io/ylianst/meshcentral:latest}
mkdir -p "$DATA"
# The agents the tests use (Linux x64, Windows x64), supplied before it starts: see fetch-agents.mjs.
# Los agentes que usan las pruebas (Linux x64, Windows x64), puestos antes de arrancar: ver fetch-agents.mjs.
CACHE=${MESHTEST_AGENT_CACHE:-$(dirname "$DATA")/agentcache}
mkdir -p "$CACHE"
docker run --rm --entrypoint cat "$IMAGE" /opt/meshcentral/meshcentral/agents/agent-defaults.json > "$DATA/agent-defaults.json"
docker run --rm --user "$(id -u):$(id -g)" -v "$(cd "$(dirname "$0")" && pwd):/s:ro" -v "$DATA:/d" -v "$CACHE:/c" node:22-alpine \
  node /s/fetch-agents.mjs /d/agent-defaults.json /d /c meshagent_x86-64 MeshService64.exe
AUTH=""
if [ -n "${OIDC_ISSUER:-}" ]; then
  AUTH=', "authStrategies": { "oidc": { "issuer": "'"$OIDC_ISSUER"'", "client": { "client_id": "meshcentral-test", "client_secret": "test-secret-not-for-production" }, "newAccounts": true, "groups": { "claim": "groups", "required": ["mesh-users"] } } }'
fi
# Session recording for the recordings tests (every protocol, the files beside the data). / Grabación de sesiones.
AUTH="$AUTH"', "sessionRecording": { "filepath": "/opt/meshcentral/meshcentral-data/recordings" }'
# SINK_HOST=<container>: email and SMS through the bench's mailbox (sink.mjs), so the server offers two-factor by email and
# SMS; every account then has its email verified (with a mail server, MeshCentral asks it to sign in).
# SINK_HOST=<contenedor>: correo y SMS por el buzón del banco, para que el servidor ofrezca doble factor por correo y SMS;
# entonces cada cuenta lleva su correo verificado (con servidor de correo, MeshCentral lo pide para entrar).
MAIL=""; EMAIL_OF() { :; }
if [ -n "${SINK_HOST:-}" ]; then
  MAIL=', "smtp": { "host": "'"$SINK_HOST"'", "port": 2525, "from": "mesh@mesh.test", "tls": false },
  "sms": { "provider": "url", "url": "http://'"$SINK_HOST"':8025/sms?phone={{phone}}&msg={{message}}" }'
  EMAIL_OF() { echo "--email $1@mesh.test"; }
fi
# SINK_CA=<dir with sink.crt>: the mailbox is also an ntfy server over HTTPS, and this server trusts its certificate only.
# SINK_CA=<carpeta con sink.crt>: el buzón es también un servidor ntfy por HTTPS, y este servidor confía solo en su certificado.
TRUST=""
if [ -n "${SINK_HOST:-}" ] && [ -n "${SINK_CA:-}" ]; then
  MAIL="$MAIL"', "messaging": { "ntfy": { "host": "'"$SINK_HOST"':8443" } }'
  TRUST="-e NODE_EXTRA_CA_CERTS=/certs/sink.crt -v $SINK_CA/sink.crt:/certs/sink.crt:ro"
fi
# The tests sign in from one address, many with a wrong code on purpose: MeshCentral would block it after 10 bad
# second factors in 10 minutes (maxInvalid2fa), as after bad passwords (maxInvalidLogin). / Sin bloqueo por 2FA malo.
cat > "$DATA/config.json" <<JSON
{
  "settings": { "cert": "mesh.test", "port": 443, "redirPort": 0, "mpsPort": 0, "selfUpdate": false,
                "maxInvalidLogin": false, "maxInvalid2fa": false, "agentDownloads": { "enabled": false } }$MAIL,
  "domains": { "": { "title": "Test", "newAccounts": false, "minify": true $AUTH } }
}
JSON
M=/opt/meshcentral/meshcentral/meshcentral
CFG=/opt/meshcentral/meshcentral-data/config.json
docker run -d --rm $PRUEBA_L --name "$NAME" --network "$NET" --cpus 1 --memory 768m $TRUST \
  -v "$DATA:/opt/meshcentral/meshcentral-data" --entrypoint sh "$IMAGE" -c "
    cd /opt/meshcentral &&
    node $M --configfile $CFG --createaccount admin --pass Admin-Pass-1234 --name Admin $(EMAIL_OF admin) &&
    node $M --configfile $CFG --adminaccount admin &&
    node $M --configfile $CFG --createaccount user --pass User-Pass-1234 --name User $(EMAIL_OF user) &&
    node $M --configfile $CFG --createaccount otp --pass Otp-Pass-1234 --name Otp $(EMAIL_OF otp) &&
    node $M --configfile $CFG --createaccount mfa --pass Mfa-Pass-1234 --name Mfa $(EMAIL_OF mfa) &&
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
