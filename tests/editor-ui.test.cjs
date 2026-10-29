const { test } = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
const fs = require('node:fs/promises');
const http = require('node:http');
const { chromium } = require('../BrowserSearch/node_modules/playwright');

test('one Monaco editor keeps per-file models, undo, read-only and live OS themes', async () => {
  const root = path.resolve(__dirname, '../Editor/dist');
  const server = http.createServer(async (request, response) => {
    const file = path.resolve(root, '.' + new URL(request.url, 'http://localhost').pathname);
    if (!file.startsWith(root + path.sep)) { response.writeHead(403).end(); return; }
    try {
      const mime = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.ttf': 'font/ttf' };
      response.setHeader('Content-Type', mime[path.extname(file)] || 'text/plain');
      response.end(await fs.readFile(file));
    } catch { response.writeHead(404).end(); }
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  let browser;
  try {
    browser = await chromium.launch({ channel: 'msedge', headless: true });
    const page = await browser.newPage({ viewport: { width: 900, height: 600 }, colorScheme: 'light' });
    const errors = [], network = [];
    page.on('pageerror', error => errors.push(error.message));
    page.on('request', request => network.push(request.url()));
    await page.addInitScript(() => {
      window.messages = [];
      window.chrome.webview = {
        postMessage: message => window.messages.push(message),
        addEventListener: (_, handler) => { window.receive = data => handler({ data }); }
      };
    });
    await page.goto(`http://127.0.0.1:${server.address().port}/index.html`);
    await page.waitForFunction(() => window.messages.some(message => message.type === 'ready'));
    const background = () => page.locator('.monaco-editor').first().evaluate(element => getComputedStyle(element).backgroundColor);
    await page.evaluate(() => {
      window.receive({ type: 'open', id: 'first', path: '日本語.js', text: 'const first = 1;', revision: 0 });
      window.receive({ type: 'open', id: 'second', path: 'second.json', text: '{ "value": 2 }', revision: 0 });
      window.receive({ type: 'activate', id: 'first', focus: true });
    });
    assert.ok(await page.locator('.monaco-editor').first().evaluate(element => element.classList.contains('vs')));
    const lightBackground = await background();
    await page.keyboard.press('Control+End'); await page.keyboard.type(' // edited');
    await page.waitForFunction(() => window.messages.some(message => message.type === 'change' && message.id === 'first' && message.text.endsWith('// edited')));
    await page.keyboard.press('Control+s');
    await page.waitForFunction(() => window.messages.some(message => message.type === 'save' && message.id === 'first'));
    await page.evaluate(() => window.receive({ type: 'activate', id: 'second', focus: true }));
    await page.keyboard.press('Control+End'); await page.keyboard.type(' ');
    await page.waitForFunction(() => window.messages.some(message => message.type === 'change' && message.id === 'second'));
    await page.evaluate(() => window.receive({ type: 'activate', id: 'first', focus: true }));
    await page.keyboard.press('Control+z');
    await page.waitForFunction(() => {
      const text = window.messages.filter(message => message.type === 'change' && message.id === 'first').at(-1)?.text;
      return text?.startsWith('const first = 1;') && text !== 'const first = 1; // edited';
    });
    await page.emulateMedia({ colorScheme: 'dark' });
    await page.waitForFunction(() => document.querySelector('.monaco-editor').classList.contains('vs-dark'));
    assert.notEqual(await background(), lightBackground);
    await page.emulateMedia({ colorScheme: 'light' });
    await page.waitForFunction(() => document.querySelector('.monaco-editor').classList.contains('vs'));
    await page.evaluate(() => window.receive({ type: 'theme', dark: true }));
    await page.waitForFunction(() => document.querySelector('.monaco-editor').classList.contains('vs-dark'));
    await page.evaluate(() => window.receive({ type: 'readOnly', value: true }));
    const before = await page.evaluate(() => window.messages.filter(message => message.type === 'change').length);
    await page.keyboard.type('must not edit');
    assert.equal(await page.evaluate(() => window.messages.filter(message => message.type === 'change').length), before);
    await page.evaluate(() => {
      window.receive({ type: 'replace', id: 'first', text: 'reloaded\n', revision: 1 });
      window.receive({ type: 'readOnly', value: false });
    });
    await page.keyboard.press('Control+End'); await page.keyboard.type('new');
    await page.waitForFunction(() => window.messages.some(message => message.type === 'change' && message.revision === 1 && message.text === 'reloaded\nnew'));
    await page.keyboard.press('Control+w');
    await page.waitForFunction(() => window.messages.some(message => message.type === 'close' && message.id === 'first'));
    await page.evaluate(() => { window.receive({ type: 'close', id: 'first' }); window.receive({ type: 'activate', id: 'second', focus: true }); });
    assert.equal(await page.locator('.monaco-editor').count(), 1);
    assert.equal(await page.locator('button').filter({ hasText: /^(保存|読み直す)$/ }).count(), 0);
    assert.deepEqual(errors, []);
    // Word wrap defaults to on and changing the setting must not modify file contents.
    await page.evaluate(() => {
      window.receive({ type: 'open', id: 'wrap', path: 'long.txt', text: 'word '.repeat(100), revision: 0 });
      window.receive({ type: 'activate', id: 'wrap', focus: true });
    });
    await page.waitForFunction(() => document.querySelectorAll('.monaco-editor .view-line').length > 1);
    const changesBeforeWrap = await page.evaluate(() => window.messages.filter(message => message.type === 'change').length);
    await page.evaluate(() => window.receive({ type: 'options', wordWrap: false }));
    await page.waitForFunction(() => document.querySelectorAll('.monaco-editor .view-line').length === 1);
    await page.evaluate(() => window.receive({ type: 'options', wordWrap: true }));
    await page.waitForFunction(() => document.querySelectorAll('.monaco-editor .view-line').length > 1);
    assert.equal(await page.evaluate(() => window.messages.filter(message => message.type === 'change').length), changesBeforeWrap);
    assert.ok(network.every(url => url.startsWith(`http://127.0.0.1:${server.address().port}/`) || url.startsWith('blob:')), 'Assets and workers load locally');
  } finally { await browser?.close(); await new Promise(resolve => server.close(resolve)); }
});
