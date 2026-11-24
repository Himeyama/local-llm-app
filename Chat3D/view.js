'use strict';
const events = document.querySelector('#events');
const scroll = document.querySelector('.scroll');
let sessionId = null, selected = -1, entries = [];

function isJson(text) {
  if (typeof text !== 'string' || !/^\s*[\[{]/.test(text)) return false;
  try { const value = JSON.parse(text); return value !== null && typeof value === 'object'; } catch { return false; }
}
function direction(entry) {
  if (['user-pc', 'pc-user', 'user-llm', 'llm-user', 'llm-pc', 'pc-llm'].includes(entry.direction)) return entry.direction;
  return entry.role === 'user' ? 'user-llm' : entry.role === 'assistant' ? 'llm-user' :
    entry.role === 'call' ? 'llm-pc' : entry.role === 'tool' ? 'pc-llm' : 'summary';
}
function description(entry) {
  const type = entry.role === 'user' ? 'メッセージ' : entry.role === 'assistant' ? '応答' :
    entry.role === 'call' ? 'ツール呼び出し' : entry.role === 'tool' ? 'ツール結果' : '会話の要約';
  return type + (entry.name ? ' / ' + entry.name : '') + (entry.toolId ? '  #' + entry.toolId : '');
}
function parsedJson(text) {
  if (!isJson(text)) return null;
  try { return JSON.parse(text); } catch { return null; }
}
function filename(path) {
  const parts = path.replace(/\\/g, '/').split('/').filter(Boolean);
  return parts.length ? parts[parts.length - 1] : path;
}
function previewText(entry, callsById) {
  const value = parsedJson(entry.text);
  if (entry.role === 'tool') {
    const call = entry.toolFullId ? callsById.get(entry.toolFullId) : null;
    const name = entry.name || call?.name || '';
    const args = call ? parsedJson(call.text) : null;
    if (name === 'execute_command' && value && !Array.isArray(value)) {
      if (typeof value.stdout === 'string' && value.stdout.trim()) return value.stdout;
      if (typeof value.stderr === 'string' && value.stderr.trim()) return value.stderr;
      return '（出力なし）';
    }
    if (/^(read_file|write_file)$/.test(name) && typeof args?.path === 'string')
      return filename(args.path) + (entry.text?.startsWith('ツール失敗:') ? '（失敗）' : '');
  }
  if (entry.role === 'call' && value && !Array.isArray(value)) {
    if (/^(execute|execute_command)$/.test(entry.name || '') && typeof value.command === 'string') return value.command;
    if (/^(read_file|write_file)$/.test(entry.name || '') && typeof value.path === 'string') return filename(value.path);
  }
  return entry.text || '（内容なし）';
}
function addField(list, label, value, code) {
  const field = document.createElement('div'); field.className = 'detail-field';
  const title = document.createElement('dt'); title.textContent = label;
  const content = document.createElement('dd'); content.textContent = value;
  if (code) content.className = 'code';
  field.append(title, content); list.appendChild(field);
}
function select(index) {
  selected = index;
  [...events.children].forEach((node, i) => {
    node.querySelector('button').setAttribute('aria-pressed', String(i === selected));
  });
  const entry = entries[index];
  if (!entry) return;
  document.querySelector('#detailHeading').textContent = description({ ...entry, toolId: '' });
  const detail = document.querySelector('#detailBody');
  detail.replaceChildren(); detail.classList.remove('json');
  const data = parsedJson(entry.text);
  if (entry.role === 'call' || entry.role === 'tool') {
    const fields = document.createElement('dl'); fields.className = 'detail-fields';
    if (entry.name) addField(fields, 'ツール名', entry.name, true);
    if (entry.toolFullId || entry.toolId) addField(fields, 'ツール ID', entry.toolFullId || entry.toolId, true);
    if (data && !Array.isArray(data)) {
      for (const [key, value] of Object.entries(data)) {
        const display = typeof value === 'string' ? value : JSON.stringify(value, null, 2);
        addField(fields, key === 'command' ? 'コマンド' : key === 'path' ? 'パス' : key, display, true);
      }
    }
    detail.appendChild(fields);
  }
  if (data) {
    const disclosure = document.createElement('details'); disclosure.className = 'json-toggle';
    const summary = document.createElement('summary'); summary.textContent = 'JSON を開く';
    const raw = document.createElement('pre'); raw.className = 'json-block'; raw.textContent = JSON.stringify(data, null, 2);
    disclosure.append(summary, raw); detail.appendChild(disclosure);
  } else {
    const text = document.createElement('div'); text.className = 'detail-text'; text.textContent = entry.text || '（内容なし）'; detail.appendChild(text);
  }
  detail.scrollTop = 0;
}
function setup(data) {
  const oldSelection = sessionId === data.sessionId ? selected : -1;
  const oldScroll = sessionId === data.sessionId ? scroll.scrollTop : 0;
  sessionId = data.sessionId;
  document.documentElement.dataset.theme = data.theme === 'light' ? 'light' : 'dark';
  entries = Array.isArray(data.entries) ? data.entries : [];
  const callsById = new Map();
  for (const entry of entries) if (entry.role === 'call' && entry.toolFullId) callsById.set(entry.toolFullId, entry);
  selected = -1; events.replaceChildren();
  document.querySelector('#empty').hidden = entries.length !== 0;
  entries.forEach((entry, index) => {
    const item = document.createElement('div'); item.className = 'event ' + direction(entry);
    item.style.setProperty('--accent', entry.role === 'user' ? 'var(--user)' : entry.role === 'tool' ? 'var(--pc)' : entry.role === 'summary' ? 'var(--muted)' : 'var(--llm)');
    const button = document.createElement('button'); button.type = 'button'; button.className = 'event-button'; button.setAttribute('aria-pressed', 'false');
    const message = document.createElement('span'); message.className = 'message';
    const meta = document.createElement('span'); meta.className = 'meta';
    const metaLabel = document.createElement('span'); metaLabel.className = 'meta-label'; metaLabel.textContent = description({ ...entry, toolId: '' }); meta.appendChild(metaLabel);
    if (entry.toolId) { const id = document.createElement('span'); id.className = 'tool-id'; id.textContent = '#' + entry.toolId; meta.appendChild(id); }
    const preview = document.createElement('span'); preview.className = 'preview'; preview.textContent = previewText(entry, callsById); preview.classList.toggle('json', isJson(entry.text));
    const arrow = document.createElement('span'); arrow.className = 'arrow'; arrow.setAttribute('aria-hidden', 'true');
    message.append(meta, preview); button.append(message, arrow); button.addEventListener('click', () => select(index)); item.append(button); events.append(item);
  });
  if (oldSelection >= 0 && oldSelection < entries.length) select(oldSelection);
  else { document.querySelector('#detailHeading').textContent = 'メッセージを選択'; document.querySelector('#detailBody').textContent = 'メッセージを選ぶと詳細を表示します。'; document.querySelector('#detailBody').classList.remove('json'); }
  scroll.scrollTop = oldScroll;
}
window.chrome.webview.addEventListener('message', e => { if (e.data?.type === 'conversation') setup(e.data); });
window.chrome.webview.postMessage('ready');
const divider = document.querySelector('#divider');
const workspace = document.querySelector('.workspace');
let diagramPercent = 60;
function resizeDiagram(percent) {
  diagramPercent = Math.max(15, Math.min(85, percent));
  workspace.style.setProperty('--diagram-width', `${diagramPercent}%`);
  divider.setAttribute('aria-valuenow', String(Math.round(diagramPercent)));
}
divider.addEventListener('pointerdown', e => { if (e.button !== 0) return; divider.setPointerCapture(e.pointerId); e.preventDefault(); });
divider.addEventListener('pointermove', e => { if (!divider.hasPointerCapture(e.pointerId)) return; const bounds = workspace.getBoundingClientRect(); if (bounds.width) resizeDiagram((e.clientX - bounds.left) / bounds.width * 100); });
divider.addEventListener('pointerup', e => { if (divider.hasPointerCapture(e.pointerId)) divider.releasePointerCapture(e.pointerId); });
divider.addEventListener('keydown', e => { if (e.key === 'ArrowLeft' || e.key === 'ArrowRight') { e.preventDefault(); resizeDiagram(diagramPercent + (e.key === 'ArrowLeft' ? -2 : 2)); } });
