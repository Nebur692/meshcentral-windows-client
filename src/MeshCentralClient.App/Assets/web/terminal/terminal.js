// Bridge between xterm.js and the app (WebView2 messages). / Puente entre xterm.js y la app (mensajes de WebView2).
(function () {
  'use strict';
  const host = window.chrome && window.chrome.webview;
  const send = (m) => { if (host) host.postMessage(m); };
  const themes = {
    dark: { background: '#1e1e1e', foreground: '#d4d4d4', cursor: '#d4d4d4', selectionBackground: '#264f78' },
    light: { background: '#ffffff', foreground: '#1e1e1e', cursor: '#1e1e1e', selectionBackground: '#add6ff' },
  };
  const dark = new URLSearchParams(location.search).get('dark') === '1';
  document.documentElement.classList.toggle('dark', dark);
  const term = new Terminal({
    cursorBlink: true, scrollback: 10000, allowProposedApi: false,
    // Exposes the text to screen readers (and to UI Automation). / Expone el texto a los lectores de pantalla (y a UI Automation).
    screenReaderMode: true,
    fontFamily: 'Cascadia Mono, Consolas, monospace', fontSize: 14,
    theme: dark ? themes.dark : themes.light,
  });
  const fit = new FitAddon.FitAddon();
  term.loadAddon(fit);
  term.open(document.getElementById('term'));
  fit.fit();
  term.focus();

  const b64 = (s) => Uint8Array.from(atob(s), (c) => c.charCodeAt(0));
  term.onData((d) => send({ t: 'input', d }));
  term.onTitleChange((title) => send({ t: 'title', title }));
  let timer = null;
  term.onResize(({ cols, rows }) => { clearTimeout(timer); timer = setTimeout(() => send({ t: 'size', cols, rows }), 200); });
  window.addEventListener('resize', () => fit.fit());

  // Copy and paste like PuTTY: selecting copies (drag, double click a word, triple click a line); right click,
  // middle click and Shift+Insert paste; Ctrl+Insert copies. Ctrl+Shift+C / Ctrl+Shift+V also work. Ctrl+C and
  // Ctrl+V stay for the remote programs, as in PuTTY.
  // Copiar y pegar como PuTTY: seleccionar copia (arrastrar, doble clic en una palabra, triple clic en una línea); el
  // clic derecho, el central y Mayús+Insert pegan; Ctrl+Insert copia. También Ctrl+Mayús+C / Ctrl+Mayús+V. Ctrl+C y
  // Ctrl+V siguen siendo de los programas remotos, como en PuTTY.
  const copy = () => { if (term.hasSelection()) send({ t: 'copy', text: term.getSelection() }); };
  const paste = () => send({ t: 'paste-request' });
  term.attachCustomKeyEventHandler((e) => {
    if (e.type !== 'keydown') return true;
    if (e.code === 'Insert' && e.shiftKey && !e.ctrlKey) { paste(); return false; }
    if (e.code === 'Insert' && e.ctrlKey && !e.shiftKey) { copy(); return false; }
    if (!e.ctrlKey || !e.shiftKey) return true;
    if (e.code === 'KeyC') { copy(); return false; }
    if (e.code === 'KeyV') { paste(); return false; }
    return true;
  });
  // Copied when the mouse is released, not while dragging, so the clipboard gets the final selection once.
  // Se copia al soltar el ratón, no mientras se arrastra, para que el portapapeles reciba la selección final una vez.
  const area = document.getElementById('term');
  area.addEventListener('mouseup', (e) => { if (e.button === 0) setTimeout(copy, 0); });
  // The middle button would start the browser's autoscroll: it pastes instead. / El botón central haría el autodesplazamiento del navegador: pega.
  area.addEventListener('mousedown', (e) => { if (e.button === 1) e.preventDefault(); });
  area.addEventListener('auxclick', (e) => { if (e.button === 1) { e.preventDefault(); paste(); term.focus(); } });
  document.addEventListener('contextmenu', (e) => { e.preventDefault(); paste(); term.focus(); });

  if (host) host.addEventListener('message', (e) => {
    const m = e.data;
    if (m.t === 'data') term.write(b64(m.b64));
    else if (m.t === 'text') term.write(m.text);
    else if (m.t === 'paste') term.paste(m.text);
    else if (m.t === 'theme') { document.documentElement.classList.toggle('dark', m.dark); term.options.theme = m.dark ? themes.dark : themes.light; }
    else if (m.t === 'closed') { term.write('\r\n\x1b[31m' + m.text + '\x1b[0m\r\n'); term.options.disableStdin = true; term.options.cursorBlink = false; }
    else if (m.t === 'focus') term.focus();
  });
  send({ t: 'ready', cols: term.cols, rows: term.rows });
})();
