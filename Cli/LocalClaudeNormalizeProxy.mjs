import http from 'node:http';

const listenHost = '127.0.0.1';
const listenPort = 8081;
const upstream = 'http://127.0.0.1:9931';
const proxyCapabilities = { status: 'ok', visionPassthrough: true, upstream };

function localSystemPrompt() {
  const date = new Intl.DateTimeFormat('en-CA', {
    timeZone: 'Asia/Tokyo',
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
  }).format(new Date());
  return [
    `現在の日付: ${date} (Asia/Tokyo)`,
    'OSの言語: ja-JP (Japanese)',
    'アーキテクチャ: amd64',
    'シェル: PowerShell',
    '作業ディレクトリ: C:\Users\hikari',
    '画像入力が含まれる場合は、画像の内容を確認して回答してください。',
    '開発では仕様を明確化し、先にテストを書いてから実装する（仕様駆動・テスト駆動開発）。',
  ].join('\n');
}
function normalizeMessages(body) {
  // Discard Claude Code's prompt and supply only the local environment facts.
  body.system = localSystemPrompt();
  if (Array.isArray(body.messages)) {
    body.messages = body.messages.filter(
      (message) => message?.role !== 'system' && message?.role !== 'developer',
    );
  }
  return body;
}

function relay(req, res) {
  if (req.method === 'GET' && new URL(req.url, upstream).pathname === '/health') {
    res.writeHead(200, { 'content-type': 'application/json' });
    res.end(JSON.stringify(proxyCapabilities));
    return;
  }

  const chunks = [];
  req.on('data', (chunk) => chunks.push(chunk));
  req.on('end', async () => {
    try {
      let body = Buffer.concat(chunks);
      const requestPath = new URL(req.url, upstream).pathname;
      const isMessagesRequest = req.method === 'POST' && requestPath === '/v1/messages';
      if (isMessagesRequest && body.length > 0) {
        const request = JSON.parse(body.toString('utf8'));
        const normalized = normalizeMessages(request);
        body = Buffer.from(JSON.stringify(normalized));
      }

      const headers = { ...req.headers };
      delete headers.host;
      headers['content-length'] = String(body.length);

      const upstreamResponse = await fetch(`${upstream}${req.url}`, {
        method: req.method,
        headers,
        body: body.length > 0 ? body : undefined,
      });
      res.writeHead(upstreamResponse.status, Object.fromEntries(upstreamResponse.headers));
      if (upstreamResponse.body) {
        for await (const chunk of upstreamResponse.body) res.write(chunk);
      }
      res.end();
    } catch (error) {
      res.writeHead(502, { 'content-type': 'application/json' });
      res.end(JSON.stringify({ error: { message: `Local proxy error: ${error.message}`, type: 'api_error' } }));
    }
  });
}

http.createServer(relay).listen(listenPort, listenHost, () => {
  console.log(`Local Claude normalization proxy: http://${listenHost}:${listenPort} -> ${upstream}`);
});
