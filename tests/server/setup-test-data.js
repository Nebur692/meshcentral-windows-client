// Runs INSIDE the test MeshCentral container (it uses its ws and otplib modules).
// Se ejecuta DENTRO del contenedor MeshCentral de pruebas (usa sus módulos ws y otplib).
// Enables TOTP on the "otp" account and creates the "Test group". Prints: <otp secret> <group id>
// Activa TOTP en la cuenta «otp» y crea el «Test group». Escribe: <secreto otp> <id del grupo>
const M = '/opt/meshcentral/meshcentral/node_modules/';
const WebSocket = require(M + 'ws');
const otplib = require(M + 'otplib');
const auth = (u, p) => Buffer.from(u).toString('base64') + ',' + Buffer.from(p).toString('base64');
function session(u, p, onMsg, onOpen) {
  const ws = new WebSocket('wss://localhost/control.ashx', { rejectUnauthorized: false, headers: { 'x-meshauth': auth(u, p) } });
  ws.on('message', m => onMsg(ws, JSON.parse(m.toString())));
  ws.on('open', () => setTimeout(() => onOpen(ws), 300));
  ws.on('error', e => { console.error(e.message); process.exit(1); });
  return ws;
}
setTimeout(() => { console.error('timeout'); process.exit(1); }, 15000);
let secret;
session('otp', 'Otp-Pass-1234', (ws, d) => {
  if (d.action === 'otpauth-request') { secret = d.secret; ws.send(JSON.stringify({ action: 'otpauth-setup', secret, token: otplib.generateSync({ secret }) })); }
  if (d.action === 'otpauth-setup') {
    if (!d.success) { console.error('otp setup failed'); process.exit(1); }
    ws.close();
    session('admin', 'Admin-Pass-1234', (ws2, e) => {
      if (e.action === 'createmesh') { if (e.result !== 'ok') { console.error('createmesh failed'); process.exit(1); } console.log(secret + ' ' + e.meshid); process.exit(0); }
    }, ws2 => ws2.send(JSON.stringify({ action: 'createmesh', meshname: 'Test group', meshtype: 2, desc: 'Integration tests', responseid: 'cm' })));
  }
}, ws => ws.send('{"action":"otpauth-request"}'));
