const { chromium } = require('../BrowserSearch/node_modules/playwright');
const http = require('node:http');
const fs = require('node:fs/promises');
const path = require('node:path');
const assert = require('node:assert/strict');

(async () => {
  const root = path.resolve(__dirname, '../Chat3D');
  const server = http.createServer(async (req, res) => {
    try {
      const file = path.resolve(root, '.' + (req.url === '/' ? '/index.html' : req.url));
      if (!file.startsWith(root + path.sep)) throw Error('invalid path');
      res.setHeader('Content-Type', file.endsWith('.js') ? 'text/javascript' : 'text/html');
      res.end(await fs.readFile(file));
    } catch { res.statusCode = 404; res.end(); }
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  let browser;
  try {
    browser = await chromium.launch({ executablePath: process.env.EDGE_PATH || 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', headless: true });
    const page = await browser.newPage({ viewport: { width: 1000, height: 640 } });
    await page.addInitScript(() => { window.chrome.webview = { addEventListener: (_, fn) => { window.receiveChat = fn; }, postMessage: () => {} }; });
    await page.goto(`http://127.0.0.1:${server.address().port}/`);
    await page.waitForFunction(() => window.receiveChat);
    await page.evaluate(() => window.receiveChat({ data: { type: 'conversation', sessionId: 'one', theme: 'light', title: '調査', entries: [
      { role: 'user', text: 'ファイルを確認' }, { role: 'assistant', text: '確認します' },
      { role: 'call', name: 'read_file', toolId: 'abc123', toolFullId: 'call_full_abc123', text: '{"path":"a.txt"}' }, { role: 'tool', toolId: 'abc123', toolFullId: 'call_full_abc123', text: 'ツール結果' },
      { role: 'user', direction: 'user-pc', text: 'direct' }, { role: 'tool', direction: 'pc-user', text: 'direct result' }
    ] } }));
    await page.waitForSelector('.event');
    assert.equal(await page.locator('.actor').allInnerTexts().then(v => v.join(',')), 'ユーザー,PC,生成 AI');
    assert.equal(await page.locator('.event').count(), 6);
    assert.equal(await page.locator('html').getAttribute('data-theme'), 'light');
    assert.equal(await page.locator('.event.user-llm').count(), 1);
    assert.equal(await page.locator('.event.llm-user').count(), 1);
    assert.equal(await page.locator('.event.llm-pc').count(), 1);
    assert.equal(await page.locator('.event.pc-llm').count(), 1);
    assert.match(await page.locator('.event.llm-pc .meta').innerText(), /#abc123/);
    assert.match(await page.locator('.event.pc-llm .meta').innerText(), /#abc123/);
    assert.equal((await page.locator('.event .meta').allInnerTexts()).some(text => text.includes('→')), false);
    assert.equal(await page.locator('.event .message').first().evaluate(e => getComputedStyle(e).borderLeftWidth), '3px');
    assert.equal(await page.locator('.event .arrow').first().evaluate(e => getComputedStyle(e).height), '2px');
    const arrows = await page.locator('.event .arrow').evaluateAll(elements => elements.map(e => ({ height: getComputedStyle(e).height, tip: getComputedStyle(e, '::after').clipPath })));
    assert(arrows.every(a => a.height === '2px'), 'Arrow lines must use the same thickness');
    assert.equal(arrows[0].tip, arrows[3].tip, 'User to AI and PC to AI must point right');
    assert.equal(arrows[1].tip, arrows[2].tip, 'AI to user and AI to PC must point left');
    assert.notEqual(arrows[0].tip, arrows[1].tip);
    assert.deepEqual(await page.locator('.event .arrow').evaluateAll(elements => elements.map(e => getComputedStyle(e).backgroundColor)), ['rgb(31, 119, 180)', 'rgb(255, 127, 14)', 'rgb(44, 160, 44)', 'rgb(214, 39, 40)', 'rgb(148, 103, 189)', 'rgb(140, 86, 75)']);
    assert.equal(await page.locator('.event.llm-pc .arrow').evaluate(e => getComputedStyle(e).outlineStyle), 'none');
    assert.equal(await page.locator('.event.llm-pc .arrow').evaluate(e => getComputedStyle(e).filter), 'none');
    assert.equal(await page.locator('.event').nth(2).locator('.preview').evaluate(e => e.classList.contains('json')), true);
    assert.equal(await page.locator('.event').nth(2).locator('.preview').innerText(), 'a.txt');
    assert.equal(await page.locator('.event').nth(3).locator('.preview').innerText(), 'a.txt');
    await page.locator('.event button').nth(1).focus();
    await page.keyboard.press('Enter');
    assert.equal(await page.locator('#detailBody').innerText(), '確認します');
    await page.locator('.event button').nth(2).click();
    assert.match(await page.locator('#detailBody').innerText(), /call_full_abc123/);
    assert.match(await page.locator('#detailBody').innerText(), /パス\s+a.txt/);
    assert.equal(await page.locator('.json-toggle').getAttribute('open'), null);
    await page.locator('.json-toggle summary').click();
    assert.match(await page.locator('.json-block').innerText(), /"path": "a.txt"/);
    await page.locator('.event button').nth(3).click();
    assert.match(await page.locator('#detailBody').innerText(), /call_full_abc123[\s\S]*ツール結果/);
    const positions = await page.evaluate(() => [...document.querySelectorAll('.event .message')].map(e => e.getBoundingClientRect().x));
    assert(positions[0] < positions[2], 'User and PC messages must occupy different lanes');
    const separator = page.locator('#divider');
    assert.equal(await separator.evaluate(e => getComputedStyle(e).cursor), 'ew-resize');
    const beforeDrag = Number(await separator.getAttribute('aria-valuenow'));
    const bounds = await separator.boundingBox();
    await page.mouse.move(bounds.x + bounds.width / 2, bounds.y + 40);
    await page.mouse.down(); await page.mouse.move(bounds.x + 75, bounds.y + 40); await page.mouse.up();
    assert(Number(await separator.getAttribute('aria-valuenow')) > beforeDrag, 'Dragging the divider must resize the panes');
    await separator.focus(); await page.keyboard.press('ArrowLeft');
    assert(Number(await separator.getAttribute('aria-valuenow')) < beforeDrag + 8, 'Keyboard resize must be available');
    await page.evaluate(() => window.receiveChat({ data: { type: 'conversation', sessionId: 'one', theme: 'dark', title: '調査', entries: [
      { role: 'user', text: 'ファイルを確認' }, { role: 'assistant', text: '確認します' },
      { role: 'call', name: 'read_file', text: '{}' }, { role: 'tool', text: '更新された結果' }
    ] } }));
    assert.equal(await page.locator('html').getAttribute('data-theme'), 'dark');
    assert.equal(await page.locator('#detailBody .detail-text').innerText(), '更新された結果');
    await page.evaluate(() => window.receiveChat({ data: { type: 'conversation', sessionId: 'commands', theme: 'light', entries: [
      { role: 'call', name: 'execute_command', toolId: 'abc123', toolFullId: 'call_command_abc123', text: '{"command":"git status --short","working_directory":"src"}' },
      { role: 'tool', name: 'execute_command', toolFullId: 'call_command_abc123', text: '{"stdout":"ready","stderr":"warning"}' },
      { role: 'call', name: 'execute_command', toolFullId: 'call_command_error', text: '{"command":"bad"}' },
      { role: 'tool', name: 'execute_command', toolFullId: 'call_command_error', text: '{"stdout":"","stderr":"failure"}' },
      { role: 'call', name: 'write_file', toolId: 'def456', toolFullId: 'call_write_def456', text: '{"path":"src/main.cs","content":"hello"}' },
      { role: 'tool', name: 'write_file', toolFullId: 'call_write_def456', text: '保存しました' },
      { role: 'call', name: 'read_file', toolFullId: 'call_read', text: '{"path":"C:\\\\project\\\\notes.txt"}' },
      { role: 'tool', name: 'read_file', toolFullId: 'call_read', text: 'file contents' }
    ] } }));
    assert.equal(await page.locator('.preview').nth(0).innerText(), 'git status --short');
    assert.equal(await page.locator('.preview').nth(1).innerText(), 'ready');
    assert.equal(await page.locator('.preview').nth(3).innerText(), 'failure');
    assert.equal(await page.locator('.preview').nth(4).innerText(), 'main.cs');
    assert.equal(await page.locator('.preview').nth(5).innerText(), 'main.cs');
    assert.equal(await page.locator('.preview').nth(6).innerText(), 'notes.txt');
    assert.equal(await page.locator('.preview').nth(7).innerText(), 'notes.txt');
    await page.locator('.event button').first().click();
    assert.match(await page.locator('#detailBody').innerText(), /コマンド\s+git status --short/);
    assert.match(await page.locator('#detailBody').innerText(), /call_command_abc123/);
    if (process.env.CHAT3D_SCREENSHOT) await page.screenshot({ path: process.env.CHAT3D_SCREENSHOT });
    console.log('PASS: sequence directions, themes, JSON and mouse/keyboard selection');
  } finally {
    await browser?.close();
    await new Promise(resolve => server.close(resolve));
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
