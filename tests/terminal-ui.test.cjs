const { test } = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { chromium } = require('../BrowserSearch/node_modules/playwright');

test('terminal delegates tab controls to WinUI and retains buffers, saved font size and edge scrollbars', async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 900, height: 280 } });
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.addInitScript(() => {
      window.messages = [];
      window.chrome.webview = {
        postMessage: message => window.messages.push(message),
        addEventListener: (_, handler) => { window.receive = data => handler({ data }); }
      };
    });
    await page.goto(pathToFileURL(path.resolve(__dirname, '../Terminal/index.html')).href);
    await page.waitForFunction(() => window.messages.some(message => message.type === 'ready'));
    assert.equal(await page.locator('button, [role="tab"], [role="tablist"]').count(), 0, 'Tab controls belong to WinUI');
    assert.equal(await page.locator('.terminal-screen').count(), 0, 'WinUI creates the first tab');
    const firstTabId = 'native-1';
    await page.evaluate(tabId => {
      window.receive({ type: 'create', tabId }); window.receive({ type: 'activate', tabId });
    }, firstTabId);
    const output = page.locator('.terminal-screen.active .xterm-rows');
    const font = await output.evaluate(element => getComputedStyle(element).fontFamily);
    assert.match(font, /Cascadia Code/);
    await page.evaluate(tabId => window.receive({ type: 'output', tabId, data: btoa('\x1b[31mANSI red\x1b[0m\r\nPS> '), id: 7 }), firstTabId);
    await page.waitForFunction(() => window.messages.some(message => message.type === 'ack' && message.id === 7));
    await page.waitForFunction(() => document.querySelector('.terminal-screen.active .xterm-rows')?.textContent.includes('ANSI red'));
    assert.match(await output.innerText(), /ANSI red/);
    await page.keyboard.type('Get-Location');
    await page.keyboard.press('Enter');
    await page.keyboard.press('ArrowUp');
    await page.keyboard.press('Control+c');
    const input = await page.evaluate(() => window.messages.filter(message => message.type === 'input').map(message => message.data).join(''));
    assert.match(input, /Get-Location\r/);
    assert.ok(input.includes('\x1b[A'));
    assert.ok(input.includes('\x03'));
    const row = page.locator('.terminal-screen.active .xterm-rows > div').first();
    const originalHeight = await row.evaluate(element => element.getBoundingClientRect().height);
    await page.keyboard.press('Control+;');
    assert.ok(await row.evaluate(element => element.getBoundingClientRect().height) > originalHeight);
    await page.keyboard.press('Control+-');
    assert.equal(await row.evaluate(element => element.getBoundingClientRect().height), originalHeight);
    assert.equal(await page.evaluate(() => localStorage.getItem('terminal.fontSize')), '14');
    await page.keyboard.press('Control+Shift+v');
    assert.ok(await page.evaluate(() => window.messages.some(message => message.type === 'paste')));
    const oldColumns = await page.evaluate(() => window.messages.find(message => message.type === 'open').cols);
    await page.setViewportSize({ width: 500, height: 280 });
    await page.waitForFunction(old => window.messages.some(message => message.type === 'resize' && message.cols < old), oldColumns);
    const scrollbar = await page.locator('.terminal-screen.active .xterm-viewport').boundingBox();
    assert.equal(scrollbar.x + scrollbar.width, 500, 'Scrollbar must reach the right edge');
    assert.equal(scrollbar.y + scrollbar.height, 280, 'Scrollbar must reach the bottom edge');

    await page.keyboard.press('Control+;');
    await page.keyboard.press('Control+Shift+t');
    assert.ok(await page.evaluate(() => window.messages.some(message => message.type === 'new-tab')));
    assert.equal(await page.locator('.terminal-screen').count(), 1, 'Shortcut requests a native tab');
    const secondTabId = 'native-2';
    await page.evaluate(tabId => {
      window.receive({ type: 'create', tabId }); window.receive({ type: 'activate', tabId });
    }, secondTabId);
    assert.equal(await page.locator('.terminal-screen').count(), 2);
    assert.ok(await row.evaluate(element => element.getBoundingClientRect().height) > originalHeight, 'New tabs inherit font size');
    await page.evaluate(tabId => window.receive({ type: 'output', tabId, data: btoa('background output'), id: 8 }), firstTabId);
    await page.waitForFunction(() => window.messages.some(message => message.type === 'ack' && message.id === 8));
    assert.ok(!(await output.innerText()).includes('background output'), 'Tabs must have separate buffers');
    await page.evaluate(tabId => window.receive({ type: 'activate', tabId }), firstTabId);
    await page.waitForFunction(() => document.querySelector('.terminal-screen.active .xterm-rows')?.textContent.includes('background output'));
    assert.match(await output.innerText(), /background output/);
    await page.keyboard.press('Control+Shift+w');
    assert.ok(await page.evaluate(() => window.messages.some(message => message.type === 'close-tab')));
    assert.equal(await page.locator('.terminal-screen').count(), 2, 'Native host confirms tab closure');
    await page.evaluate(({ closed, selected }) => {
      window.receive({ type: 'close', tabId: closed }); window.receive({ type: 'activate', tabId: selected });
    }, { closed: firstTabId, selected: secondTabId });
    assert.equal(await page.locator('.terminal-screen').count(), 1);
    await page.keyboard.press('Control+Shift+w');
    await page.evaluate(tabId => window.receive({ type: 'close', tabId }), secondTabId);
    assert.equal(await page.locator('.terminal-screen').count(), 0);
    const replacementId = 'native-3';
    await page.evaluate(tabId => {
      window.receive({ type: 'create', tabId }); window.receive({ type: 'activate', tabId });
    }, replacementId);
    assert.equal(await page.locator('.terminal-screen').count(), 1, 'Native replacement renders exactly one surface');
    await page.evaluate(() => window.receive({ type: 'output', tabId: 'closed', data: btoa('late output'), id: 9 }));
    assert.ok(await page.evaluate(() => window.messages.some(message => message.type === 'ack' && message.id === 9)), 'Late output cannot stall a closing shell');
    await page.reload();
    await page.waitForFunction(() => window.messages.some(message => message.type === 'ready'));
    await page.evaluate(tabId => {
      window.receive({ type: 'create', tabId }); window.receive({ type: 'activate', tabId });
    }, replacementId);
    assert.equal(await page.locator('.terminal-screen').count(), 1);
    assert.ok(await row.evaluate(element => element.getBoundingClientRect().height) > originalHeight, 'Reload restores saved font size');
    assert.equal(await page.evaluate(() => localStorage.getItem('terminal.fontSize')), '15');
    assert.deepEqual(errors, []);
    console.log('PASS: native tab requests, host-driven lifecycle, independent buffers, font persistence, edge scrollbars, no HTML tab controls');
  } finally { await browser.close(); }
});
