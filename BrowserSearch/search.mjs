import { createConnection } from '@playwright/mcp';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { InMemoryTransport } from '@modelcontextprotocol/sdk/inMemory.js';
import { pathToFileURL } from 'node:url';
import { createRequire } from 'node:module';

// Use the exact Playwright version owned by the pinned MCP package.
const { chromium } = createRequire(import.meta.resolve('@playwright/mcp/package.json'))('playwright');

// Both the browser and its profile are private to this search. Never attach to
// the user's desktop browser, and never fall back to a headed launch.
export const browserConfig = {
  browser: { browserName: 'chromium', isolated: true, launchOptions: { channel: 'msedge', headless: true },
    contextOptions: { serviceWorkers: 'block' } },
  timeouts: { navigation: 30000 },
  imageResponses: 'omit',
};

export function resultValue(result) {
  const text = result.content?.filter(c => c.type === 'text').map(c => c.text).join('\n') ?? '';
  if (result.isError) throw new Error(text || 'Playwright MCP ツールに失敗しました。');
  const match = text.match(/### Result\r?\n([\s\S]*?)(?:\r?\n### |$)/);
  if (!match) throw new Error('Playwright MCP の検索結果を読み取れません。');
  return JSON.parse(match[1].trim());
}

export function normalizeResults(rows) {
  const seen = new Set();
  return rows.flatMap(row => {
    try {
      let url = new URL(row.url);
      if (url.hostname.endsWith('bing.com') && url.pathname === '/ck/a') {
        const encoded = url.searchParams.get('u');
        if (!encoded?.startsWith('a1')) return [];
        url = new URL(Buffer.from(encoded.slice(2), 'base64url').toString('utf8'));
      }
      if (!['https:', 'http:'].includes(url.protocol) || url.username || url.password || !row.title?.trim() || seen.has(url.href)) return [];
      seen.add(url.href);
      return [{ title: row.title.trim().slice(0, 300), url: url.href, snippet: (row.snippet ?? '').trim().slice(0, 1000) }];
    } catch { return []; }
  }).slice(0, 8);
}

export async function search(query, client) {
  if (typeof query !== 'string' || !query.trim() || query.length > 2000) throw new Error('検索語を 1～2,000 文字で指定してください。');
  const source = 'https://www.bing.com/search?q=' + encodeURIComponent(query.trim());
  const navigation = await client.callTool({ name: 'browser_navigate', arguments: { url: source } });
  if (navigation.isError) throw new Error(navigation.content?.map(c => c.text ?? '').join('\n') || '検索ページを開けません。');
  // Fixed DOM code only: neither model output nor the query is evaluated as JS.
  const result = await client.callTool({ name: 'browser_evaluate', arguments: { function: `async () => {
    const deadline = Date.now() + 8000;
    while (!document.querySelector('#b_results li.b_algo h2 a') && Date.now() < deadline)
      await new Promise(resolve => setTimeout(resolve, 200));
    return Array.from(document.querySelectorAll('#b_results li.b_algo')).slice(0, 12).map(item => {
      const link = item.querySelector('h2 a');
      return { title: link?.textContent ?? '', url: link?.href ?? '', snippet: item.querySelector('.b_caption p, p')?.textContent ?? '' };
    });
  }` } });
  const rows = resultValue(result);
  if (!Array.isArray(rows)) throw new Error('検索結果の形式が不正です。');
  const results = normalizeResults(rows);
  if (!results.length) throw new Error('検索結果を取得できませんでした。検索サービスの制限・確認画面または検索結果なしの可能性があります。');
  return { source, results };
}

export async function withBrowser(action, contextGetter) {
  let browser, client, server;
  try {
    if (!contextGetter) {
      browser = await chromium.launch(browserConfig.browser.launchOptions);
      const context = await browser.newContext(browserConfig.browser.contextOptions);
      contextGetter = () => Promise.resolve(context);
    }
    // The supplied context is already isolated. MCP must reuse that context.
    const config = { ...browserConfig, browser: { ...browserConfig.browser, isolated: false } };
    server = await createConnection(config, contextGetter);
    client = new Client({ name: 'local-llm-gui-search', version: '1.0.0' });
    const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
    await server.connect(serverTransport);
    await client.connect(clientTransport);
    return await action(client);
  } finally {
    try { await client?.callTool({ name: 'browser_close', arguments: {} }); } catch { }
    try { await client?.close(); }
    finally {
      try { await server?.close(); }
      finally { await browser?.close(); }
    }
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    let input = '';
    for await (const chunk of process.stdin) {
      input += chunk;
      if (input.length > 16000) throw new Error('検索入力が大きすぎます。');
    }
    const { query } = JSON.parse(input);
    const output = await withBrowser(client => search(query, client));
    process.stdout.write(JSON.stringify(output));
  } catch (error) {
    process.stderr.write(error.message + '\n');
    process.exitCode = 1;
  }
}
