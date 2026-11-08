// Puts the agents the tests use where MeshCentral looks for its default agents (meshcentral-data/agentbuilds/<tag>/),
// so the test server never has to download them itself: it starts before its 31 default agents (119 MiB) finish
// downloading from GitHub, answers 404 for them until then, and gives up after 20 s on a slow link.
// Each file is checked against the size and SHA-384 the server itself expects, and kept in a cache folder between runs.
// Pone los agentes que usan las pruebas donde MeshCentral busca sus agentes por defecto (meshcentral-data/agentbuilds/<tag>/),
// para que el servidor de pruebas no tenga que bajarlos él: arranca antes de acabar de bajar de GitHub sus 31 agentes
// (119 MiB), contesta 404 por ellos hasta entonces y se rinde a los 20 s si la conexión va lenta.
// Cada fichero se comprueba con el tamaño y el SHA-384 que espera el propio servidor, y se guarda en una caché entre pasadas.
//
// Usage / Uso:  node fetch-agents.mjs <agent-defaults.json> <meshcentral-data> <cache-dir> <filename>...
import { createHash } from 'node:crypto';
import { copyFileSync, existsSync, mkdirSync, readFileSync, renameSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

const [defaultsFile, dataDir, cacheDir, ...names] = process.argv.slice(2);
if (!defaultsFile || !dataDir || !cacheDir || !names.length) {
  console.error('usage: node fetch-agents.mjs <agent-defaults.json> <meshcentral-data> <cache-dir> <filename>...');
  process.exit(2);
}
const defaults = JSON.parse(readFileSync(defaultsFile, 'utf8'));
const sha384 = bytes => createHash('sha384').update(bytes).digest('hex');
const sleep = ms => new Promise(r => setTimeout(r, ms));

for (const name of names) {
  const release = defaults.releases.find(r => r.files.some(f => f.filename === name));
  if (!release) throw new Error(`${name} is not among the server's default agents / no está entre los agentes del servidor`);
  const file = release.files.find(f => f.filename === name);
  const good = bytes => bytes.length === file.size && sha384(bytes) === file.sha384;
  const cached = join(cacheDir, release.tag, name);
  if (!(existsSync(cached) && good(readFileSync(cached)))) {
    const url = `https://github.com/${release.repository}/releases/download/${encodeURIComponent(release.tag)}/${encodeURIComponent(file.asset)}`;
    let bytes = null, last = '';
    for (let attempt = 1; attempt <= 6 && !bytes; attempt++) {
      try {
        const res = await fetch(url, { signal: AbortSignal.timeout(180_000) });
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        const got = Buffer.from(await res.arrayBuffer());
        if (!good(got)) throw new Error(`${got.length} bytes that do not match the expected SHA-384`);
        bytes = got;
      } catch (e) {
        last = e.message;
        console.error(`${name}: attempt ${attempt} failed (${last})`);
        await sleep(attempt * 5000);
      }
    }
    if (!bytes) throw new Error(`${name} could not be downloaded from ${url}: ${last} / no se pudo bajar`);
    mkdirSync(join(cacheDir, release.tag), { recursive: true });
    writeFileSync(cached + '.part', bytes);
    renameSync(cached + '.part', cached);
    console.error(`${name}: downloaded (${bytes.length} bytes)`);
  }
  const target = join(dataDir, 'agentbuilds', release.tag);
  mkdirSync(target, { recursive: true });
  copyFileSync(cached, join(target, name));
}
