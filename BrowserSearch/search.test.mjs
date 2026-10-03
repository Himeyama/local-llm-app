import test from 'node:test';
import assert from 'node:assert/strict';
import { chromium } from 'playwright';
import { browserConfig, normalizeResults, resultValue, search, validateSearchPage, withBrowser } from './search.mjs';

test('MCP errors are surfaced and only valid distinct source URLs are returned', () => {
  assert.throws(() => resultValue({ isError: true, content: [{ type: 'text', text: 'navigation failed' }] }), /navigation failed/);
  const target = 'https://example.com/日本語';
  assert.deepEqual(normalizeResults([
    { title: 'Example', url: target, snippet: 'Snippet' },
    { title: 'duplicate', url: target },
    { title: 'unsafe', url: 'javascript:alert(1)' },
    { title: 'invalid', url: 'not a URL' },
  ]), [{ title: 'Example', url: new URL(target).href, snippet: 'Snippet' }]);
});

test('changed search queries, broadened site restrictions and empty pages fail explicitly', () => {
  const query = 'site:example.com 日本語 複数の語';
  const page = { source: 'https://search.yahoo.co.jp/search?' + new URLSearchParams({ p: query }), query,
    rows: [{ title: 'Title', url: 'https://docs.example.com/page', snippet: 'Summary' }] };
  assert.equal(validateSearchPage(page, query).query, query);
  assert.throws(() => validateSearchPage({ ...page, query: '日本語' }, query), /一致しない/);
  assert.throws(() => validateSearchPage({ ...page, source: 'https://search.yahoo.co.jp/search?p=日本語' }, query), /一致しない/);
  assert.throws(() => validateSearchPage({ ...page, rows: [{ title: 'Wrong', url: 'https://example.com.evil.test' }] }, query), /site:/);
  assert.throws(() => validateSearchPage({ ...page, noResults: true }, query), /一致する検索結果はありません/);
  assert.throws(() => validateSearchPage({ ...page, rows: [] }, query), /取得できません/);
});

test('query is URL data and never part of evaluated JavaScript', async () => {
  const calls = [];
  const client = { async callTool(call) {
    calls.push(call);
    return call.name === 'browser_navigate' ? {} : { content: [{ type: 'text', text: '### Result\n' + JSON.stringify({ source: calls[0].arguments.url, query, rows: [{ title: 'Title', url: 'https://example.com', snippet: 'Summary' }] }) + '\n### Ran Playwright code\nignored' }] };
  } };
  const query = '日本語 & " ); throw new Error("injected") //';
  const result = await search(query, client);
  assert.equal(new URL(calls[0].arguments.url).searchParams.get('p'), query);
  assert.equal(calls[1].arguments.function.includes(query), false);
  assert.equal(result.results[0].snippet, 'Summary');
  await assert.rejects(() => search('', client), /検索語/);
  await assert.rejects(() => search('x'.repeat(2001), client), /検索語/);
});

test('actual MCP tools search a browser DOM with a hidden isolated browser', async () => {
  assert.equal(browserConfig.browser.launchOptions.headless, true);
  assert.equal(browserConfig.browser.isolated, true);
  const browser = await chromium.launch(browserConfig.browser.launchOptions);
  try {
    const cdp = await browser.newBrowserCDPSession();
    const version = await cdp.send('Browser.getVersion');
    assert.match(version.userAgent, /HeadlessChrome/, 'Browser must be truly headless');
    const context = await browser.newContext();
    await context.route('**/*', async route => {
      if (!route.request().url().startsWith('https://search.yahoo.co.jp/search?')) return route.abort();
      return route.fulfill({ contentType: 'text/html; charset=utf-8', body: '<html><body><input name="p" value="日本語の検索"><div class="Algo" style="display:none"><a class="sw-Card__titleInner" href="https://wrong.example"><h3>隠れた結果</h3></a></div><div class="Algo"><a class="sw-Card__titleInner" href="https://example.com/docs"><h3>検索のタイトル</h3><cite>タイトルに混ぜない表示URL</cite></a><div class="sw-Card__summary"><p>検索の要約</p></div></div><div><h3>関連検索</h3></div></body></html>' });
    });
    const result = await withBrowser(client => search('日本語の検索', client), () => Promise.resolve(context));
    assert.deepEqual(result.results, [{ title: '検索のタイトル', url: 'https://example.com/docs', snippet: '検索の要約' }]);
    assert.equal(new URL(result.source).searchParams.get('p'), '日本語の検索');
  } finally { await browser.close(); }
});
