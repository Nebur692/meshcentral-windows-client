// The bench's mailbox: a plain SMTP server (port 2525, no TLS, no account) and an SMS gateway of MeshCentral's "url"
// kind (port 8025, GET /sms?phone=..&msg=..). Everything received is kept in memory and read back with GET /read, so
// the tests can take the codes MeshCentral sends by email or SMS. Nothing leaves the bench's network.
// El buzón del banco: un servidor SMTP sencillo (puerto 2525, sin TLS ni cuenta) y una pasarela de SMS del tipo «url» de
// MeshCentral (puerto 8025). Lo recibido se guarda en memoria y se lee con GET /read, para que las pruebas cojan los
// códigos que manda MeshCentral por correo o por SMS. Nada sale de la red del banco.
import net from 'node:net';
import http from 'node:http';
import https from 'node:https';
import fs from 'node:fs';

const mails = [];
const sms = [];
const ntfy = [];

net.createServer((socket) => {
  let buffer = '', inData = false, to = [], data = '';
  const say = (line) => socket.write(line + '\r\n');
  say('220 sink ESMTP');
  socket.on('data', (chunk) => {
    buffer += chunk.toString('utf8');
    let i;
    while ((i = buffer.indexOf('\r\n')) >= 0) {
      const line = buffer.slice(0, i);
      buffer = buffer.slice(i + 2);
      if (inData) {
        if (line === '.') { inData = false; mails.push({ to, data, at: Date.now() }); to = []; data = ''; say('250 OK'); }
        else data += (line.startsWith('..') ? line.slice(1) : line) + '\n';
        continue;
      }
      const verb = line.slice(0, 4).toUpperCase();
      if (verb === 'EHLO') { say('250-sink'); say('250 8BITMIME'); }
      else if (verb === 'HELO') say('250 sink');
      else if (verb === 'MAIL') say('250 OK');
      else if (verb === 'RCPT') { to.push(line.replace(/^RCPT TO:\s*/i, '').replace(/[<>]/g, '')); say('250 OK'); }
      else if (verb === 'DATA') { inData = true; say('354 End with <CRLF>.<CRLF>'); }
      else if (verb === 'QUIT') { say('221 Bye'); socket.end(); }
      else if (verb === 'RSET') { to = []; data = ''; say('250 OK'); }
      else say('250 OK');
    }
  });
  socket.on('error', () => {});
}).listen(2525);

http.createServer((req, res) => {
  const url = new URL(req.url, 'http://sink');
  if (url.pathname === '/sms') {
    sms.push({ phone: url.searchParams.get('phone'), msg: url.searchParams.get('msg'), at: Date.now() });
    res.end('OK');
  } else if (url.pathname === '/read') {
    res.setHeader('content-type', 'application/json');
    res.end(JSON.stringify({ mails, sms, ntfy }));
  } else { res.statusCode = 404; res.end(); }
}).listen(8025, () => console.log('sink ready'));

// An ntfy server, when given a certificate (/certs/sink.crt and .key): MeshCentral's messaging POSTs the text to
// https://<host>/<topic> (meshmessaging.js). Kept in "ntfy" with its topic.
// Un servidor ntfy, si tiene certificado: la mensajería de MeshCentral manda el texto por POST a https://<host>/<tema>.
if (fs.existsSync('/certs/sink.crt')) {
  https.createServer({ cert: fs.readFileSync('/certs/sink.crt'), key: fs.readFileSync('/certs/sink.key') }, (req, res) => {
    let body = '';
    req.on('data', (c) => { body += c; });
    req.on('end', () => {
      if (req.method === 'POST') ntfy.push({ topic: decodeURIComponent(new URL(req.url, 'https://sink').pathname.slice(1)), msg: body, at: Date.now() });
      res.setHeader('content-type', 'application/json');
      res.end('{"id":"sink"}');
    });
  }).listen(8443, () => console.log('ntfy ready'));
}
