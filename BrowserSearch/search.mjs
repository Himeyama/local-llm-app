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
      const url = new URL(row.url);
      if (!['https:', 'http:'].includes(url.protocol) || url.username || url.password || !row.title?.trim() || seen.has(url.href)) return [];
      seen.add(url.href);
      return [{ title: row.title.trim().slice(0, 300), url: url.href, snippet: (row.snippet ?? '').trim().slice(0, 1000) }];
    } catch { return []; }
  }).slice(0, 8);
}

export function validateSearchPage(page, query) {
  const normalize = text => typeof text === 'string' ? text.trim().replace(/\s+/gu, ' ') : '';
  let url;
  try { url = new URL(page.source); } catch { throw new Error('検索ページの URL を確認できません。'); }
  if (url.origin !== 'https://search.yahoo.co.jp' || url.pathname !== '/search' || normalize(url.searchParams.get('p')) !== normalize(query) || normalize(page.query) !== normalize(query))
    throw new Error('要求した検索語と実際の検索ページが一致しないため、結果を返しません。');
  if (!Array.isArray(page.rows)) throw new Error('検索結果の形式が不正です。');
  if (page.noResults) throw new Error('指定された検索語に一致する検索結果はありません。検索語を勝手に変更せず、別の語句を指定してください。');
  const results = normalizeResults(page.rows);
  if (!results.length) throw new Error('表示中の通常のウェブ検索結果を取得できませんでした。結果カードがない、または検索サービスの制限・確認画面の可能性があります。');
  // Enforce an unambiguous, positive site: restriction even if the provider
  // silently broadens the query. Other search operators stay with the provider.
  const sites = [...query.matchAll(/(?:^|\s)site:([\w.-]+)(?=\s|$)/gi)].map(m => m[1].toLowerCase());
  if (sites.length === 1 && results.some(row => {
    const host = new URL(row.url).hostname.toLowerCase();
    return host !== sites[0] && !host.endsWith('.' + sites[0]);
  })) throw new Error('検索結果が site: の条件に一致しないため、結果を返しません。');
  return { query: query.trim(), source: url.href, retrievedAt: new Date().toISOString(), results };
}

export async function search(query, client) {
  if (typeof query !== 'string' || !query.trim() || query.length > 2000) throw new Error('検索語を 1～2,000 文字で指定してください。');
  const source = 'https://search.yahoo.co.jp/search?' + new URLSearchParams({ p: query.trim(), ei: 'UTF-8' });
  const navigation = await client.callTool({ name: 'browser_navigate', arguments: { url: source } });
  if (navigation.isError) throw new Error(navigation.content?.map(c => c.text ?? '').join('\n') || '検索ページを開けません。');
  // Fixed DOM code only: neither model output nor the query is evaluated as JS.
  const result = await client.callTool({ name: 'browser_evaluate', arguments: { function: `async () => {
    const visible = element => element && element.getClientRects().length > 0 &&
      element.checkVisibility({ checkOpacity: true, checkVisibilityCSS: true }) && !element.closest('[aria-hidden="true"]');
    const clean = text => (text ?? '').replace(/\\s+/gu, ' ').trim();
    const snapshot = () => ({
      source: location.href,
      query: document.querySelector('input[name="p"]')?.value ?? '',
      noResults: /一致する情報は見つかりませんでした/.test(document.body.innerText),
      rows: Array.from(document.querySelectorAll('.Algo')).filter(visible).flatMap(item => {
        const link = item.querySelector('a.sw-Card__titleInner[href]');
        const title = link?.querySelector('h3');
        if (!visible(link) || !visible(title)) return [];
        const snippet = item.querySelector('.sw-Card__summary');
        return [{ title: clean(title.innerText), url: link.href, snippet: visible(snippet) ? clean(snippet.innerText) : '' }];
      }).slice(0, 12)
    });
    const deadline = Date.now() + 8000;
    let page, previous = '', stableSince = Date.now();
    do {
      page = snapshot();
      const fingerprint = JSON.stringify(page);
      if (fingerprint !== previous) { previous = fingerprint; stableSince = Date.now(); }
      if ((page.rows.length || page.noResults) && Date.now() - stableSince >= 400) return page;
      await new Promise(resolve => setTimeout(resolve, 200));
    } while (Date.now() < deadline);
    return snapshot();
  }` } });
  return validateSearchPage(resultValue(result), query);
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
    process.stdin.setEncoding('utf8');
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
