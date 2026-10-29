/* global Terminal, FitAddon */
(() => {
  const host = window.chrome.webview;
  const send = message => host.postMessage(message);
  const tabs = new Map();
  const surface = document.getElementById('terminal');
  const fontKey = 'terminal.fontSize';
  let activeTab;
  const clampFont = value => Number.isFinite(value) ? Math.max(8, Math.min(40, value)) : 14;
  const readFontSize = () => {
    try { return clampFont(Number(localStorage.getItem(fontKey) || 14)); }
    catch { return 14; }
  };
  let fontSize = readFontSize();

  const resize = () => {
    if (activeTab && surface.clientWidth > 30 && surface.clientHeight > 30) activeTab.fit.fit();
  };
  function activate(tab) {
    activeTab = tab;
    for (const item of tabs.values()) {
      const selected = item === tab;
      item.panel.classList.toggle('active', selected);
    }
    resize();
    tab.terminal.focus();
    requestAnimationFrame(resize);
  }
  function setFontSize(value, persist) {
    fontSize = clampFont(value);
    for (const tab of tabs.values()) tab.terminal.options.fontSize = fontSize;
    if (persist) {
      try { localStorage.setItem(fontKey, String(fontSize)); } catch { /* Storage may be unavailable. */ }
    }
    resize();
  }
  function createTab(id) {
    if (tabs.has(id)) return;
    const panel = document.createElement('div');
    panel.id = `terminal-${id}`; panel.className = 'terminal-screen active'; panel.setAttribute('role', 'tabpanel');
    surface.append(panel);
    const terminal = new Terminal({
      fontFamily: '"Cascadia Code", monospace', fontSize,
      cursorBlink: true, scrollback: 5000, allowProposedApi: false,
      theme: { background: '#111318', foreground: '#e7e9ef', cursor: '#e7e9ef', selectionBackground: '#51668c80' }
    });
    const fit = new FitAddon.FitAddon();
    const tab = { id, panel, terminal, fit };
    tabs.set(id, tab);
    terminal.loadAddon(fit); terminal.open(panel);
    terminal.onData(data => send({ type: 'input', tabId: id, data }));
    terminal.onResize(({ cols, rows }) => send({ type: 'resize', tabId: id, cols, rows }));
    terminal.attachCustomKeyEventHandler(event => {
      if (event.ctrlKey && !event.altKey && (event.key === ';' || event.key === '-')) {
        event.preventDefault();
        if (event.type === 'keydown') setFontSize(fontSize + (event.key === ';' ? 1 : -1), true);
        return false;
      }
      if (event.ctrlKey && !event.altKey && event.shiftKey && ['KeyC', 'KeyV'].includes(event.code)) {
        event.preventDefault();
        if (event.type === 'keydown') {
          if (event.code === 'KeyC') send({ type: 'copy', data: terminal.getSelection() });
          else send({ type: 'paste', tabId: id });
        }
        return false;
      }
      return true;
    });
    // WinUI sends a separate activation after creating/selecting its native tab.
    fit.fit(); panel.classList.remove('active');
    send({ type: 'open', tabId: id, cols: terminal.cols, rows: terminal.rows });
  }
  function closeTab(tab) {
    tabs.delete(tab.id);
    tab.terminal.dispose(); tab.panel.remove();
    if (activeTab === tab) activeTab = null;
  }
  document.addEventListener('keydown', event => {
    if (!event.ctrlKey || !event.shiftKey || event.altKey || !['KeyT', 'KeyW'].includes(event.code)) return;
    event.preventDefault(); event.stopPropagation();
    if (event.repeat) return;
    send({ type: event.code === 'KeyT' ? 'new-tab' : 'close-tab' });
  }, true);
  surface.addEventListener('contextmenu', event => {
    event.preventDefault();
    if (activeTab) send({ type: 'paste', tabId: activeTab.id });
  });
  window.addEventListener('storage', event => {
    if (event.key === fontKey) setFontSize(readFontSize(), false);
  });
  host.addEventListener('message', ({ data: message }) => {
    const tab = tabs.get(message.tabId);
    switch (message.type) {
      case 'create': createTab(message.tabId); break;
      case 'close': if (tab) closeTab(tab); break;
      case 'output':
        if (tab) tab.terminal.write(Uint8Array.from(atob(message.data), character => character.charCodeAt(0)),
          () => send({ type: 'ack', id: message.id }));
        else send({ type: 'ack', id: message.id });
        break;
      case 'status': if (tab) tab.terminal.write(message.text); break;
      case 'activate': if (tab) activate(tab); break;
      case 'paste': if (tab) tab.terminal.paste(message.data); break;
    }
  });
  new ResizeObserver(resize).observe(surface);
  send({ type: 'ready' });
})();
