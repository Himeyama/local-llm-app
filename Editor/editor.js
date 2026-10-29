import * as monaco from 'monaco-editor';

globalThis.MonacoEnvironment = {
  getWorkerUrl(_moduleId, label) {
    const worker = label === 'json' ? 'json' : ['css', 'scss', 'less'].includes(label) ? 'css' :
      ['html', 'handlebars', 'razor'].includes(label) ? 'html' : ['typescript', 'javascript'].includes(label) ? 'typescript' : 'editor';
    return `${worker}.worker.js`;
  }
};
const bridge = window.chrome.webview;
const models = new Map();
let activeId = null, updating = false, readOnly = false, hostDark = null;
const dark = window.matchMedia('(prefers-color-scheme: dark)');
const contrast = window.matchMedia('(forced-colors: active)');
const editor = monaco.editor.create(document.getElementById('editor'), {
  model: null, automaticLayout: true, minimap: { enabled: false }, fontSize: 14,
  fontFamily: 'Cascadia Code, Noto Sans JP, monospace', scrollBeyondLastLine: false,
  ariaLabel: 'ファイル編集', tabSize: 4, insertSpaces: false, renderWhitespace: 'selection', wordWrap: 'on'
});
function applyTheme() {
  const isDark = hostDark ?? dark.matches;
  monaco.editor.setTheme(contrast.matches ? (isDark ? 'hc-black' : 'hc-light') : (isDark ? 'vs-dark' : 'vs'));
  document.documentElement.style.colorScheme = isDark ? 'dark' : 'light';
}
dark.addEventListener('change', () => { hostDark = null; applyTheme(); });
contrast.addEventListener('change', applyTheme);
applyTheme();
function language(path) {
  const name = path.replaceAll('\\', '/').split('/').pop().toLowerCase();
  return monaco.languages.getLanguages().find(item => item.filenames?.some(file => file.toLowerCase() === name) ||
    item.extensions?.some(extension => name.endsWith(extension)))?.id ?? 'plaintext';
}
function activate(id, focus = false) {
  if (activeId != null && models.has(activeId)) models.get(activeId).view = editor.saveViewState();
  activeId = models.has(id) ? id : null;
  const entry = models.get(activeId);
  editor.setModel(entry?.model ?? null);
  editor.updateOptions({ readOnly });
  if (entry?.view) editor.restoreViewState(entry.view);
  if (focus && entry) editor.focus();
}
editor.onDidChangeModelContent(() => {
  if (updating || activeId == null) return;
  const entry = models.get(activeId);
  bridge.postMessage({ type: 'change', id: activeId, revision: entry.revision, text: entry.model.getValue(monaco.editor.EndOfLinePreference.LF) });
});
editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => bridge.postMessage({ type: 'save', id: activeId }));
editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyW, () => bridge.postMessage({ type: 'close', id: activeId }));
editor.addAction({ id: 'local-llm.reload', label: 'ディスクから読み直す', contextMenuGroupId: 'navigation',
  run: () => bridge.postMessage({ type: 'reload', id: activeId }) });
bridge.addEventListener('message', ({ data }) => {
  updating = true;
  try {
    if (data.type === 'open') {
      if (models.has(data.id)) return;
      const model = monaco.editor.createModel(data.text, language(data.path), monaco.Uri.parse(`inmemory://workspace/${data.id}/${encodeURIComponent(data.path.replaceAll('\\', '/').split('/').pop())}`));
      model.setEOL(monaco.editor.EndOfLineSequence.LF);
      models.set(data.id, { model, revision: data.revision, view: null });
    } else if (data.type === 'activate') activate(data.id, data.focus);
    else if (data.type === 'replace') {
      const entry = models.get(data.id);
      if (entry) { entry.revision = data.revision; entry.model.setValue(data.text); entry.model.setEOL(monaco.editor.EndOfLineSequence.LF); }
    } else if (data.type === 'rename') {
      const entry = models.get(data.id);
      if (entry) monaco.editor.setModelLanguage(entry.model, language(data.path));
    } else if (data.type === 'close') {
      const entry = models.get(data.id);
      if (activeId === data.id) activate(null);
      entry?.model.dispose(); models.delete(data.id);
    } else if (data.type === 'theme') { hostDark = data.dark; applyTheme(); }
    else if (data.type === 'readOnly') { readOnly = data.value; editor.updateOptions({ readOnly }); }
    else if (data.type === 'options') editor.updateOptions({ wordWrap: data.wordWrap ? 'on' : 'off' });
  } finally { updating = false; }
});
bridge.postMessage({ type: 'ready' });
