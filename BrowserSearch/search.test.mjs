import test from 'node:test';
import assert from 'node:assert/strict';
import { chromium } from 'playwright';
import { browserConfig, normalizeResults, resultValue, search, withBrowser } from './search.mjs';

test('MCP errors are surfaced and Bing redirects become original source URLs', () => {
  assert.throws(() => resultValue({ isError: true, content: [{ type: 'text', text: 'navigation failed' }] }), /navigation failed/);
  const target = 'https://example.com/日本語';
  const wrapped = 'https://www.bing.com/ck/a?u=a1' + Buffer.from(target).toString('base64url');
  assert.deepEqual(normalizeResults([
    { title: 'Example', url: wrapped, snippet: 'Snippet' },
    { title: 'duplicate', url: target },
    { title: 'unsafe', url: 'javascript:alert(1)' },
    { title: 'invalid', url: 'not a URL' },
  ]), [{ title: 'Example', url: new URL(target).href, snippet: 'Snippet' }]);
});

test('query is URL data and never part of evaluated JavaScript', async () => {
  const calls = [];
  const client = { async callTool(call) {
    calls.push(call);
    return call.name === 'browser_navigate' ? {} : { content: [{ type: 'text', text: '### Result\n[{"title":"Title","url":"https://example.com","snippet":"Summary"}]\n### Ran Playwright code\nignored' }] };
  } };
  const query = '日本語 & " ); throw new Error("injected") //';
  const result = await search(query, client);
  assert.equal(new URL(calls[0].arguments.url).searchParams.get('q'), query);
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
      if (!route.request().url().startsWith('https://www.bing.com/search?')) return route.abort();
      return route.fulfill({ contentType: 'text/html; charset=utf-8', body: '<html><body><ol id="b_results"><li class="b_algo"><h2><a href="https://example.com/docs">検索のタイトル</a></h2><div class="b_caption"><p>検索の要約</p></div></li></ol></body></html>' });
    });
    const result = await withBrowser(client => search('日本語の検索', client), () => Promise.resolve(context));
    assert.deepEqual(result.results, [{ title: '検索のタイトル', url: 'https://example.com/docs', snippet: '検索の要約' }]);
    assert.equal(new URL(result.source).searchParams.get('q'), '日本語の検索');
  } finally { await browser.close(); }
});
