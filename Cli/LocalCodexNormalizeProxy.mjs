import http from 'node:http';
import { appendFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { normalizeTools, restoreSseEvent, restoreToolCalls } from './CodexResponsesTools.mjs';

const listenHost = '127.0.0.1';
const listenPort = 8084;
const upstream = 'http://127.0.0.1:9931';
const proxyCapabilities = { status: 'ok', visionPassthrough: true, toolCompatibility: 1, upstream };
const debugLogPath = path.join(
  path.dirname(fileURLToPath(import.meta.url)),
  'LocalCodexNormalizeProxy.debug.log',
);

function logFailedRequest(request, status, responseText) {
  try {
    const entry = {
      ts: new Date().toISOString(),
      status,
      response: responseText,
      request,
    };
    appendFileSync(debugLogPath, `${JSON.stringify(entry)}\n`);
  } catch { }
}

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

const supportedItemTypes = new Set(['message', 'function_call', 'function_call_output']);

function normalizeResponses(body) {
  // Discard Codex's agent prompt and supply only the local environment facts.
  body.instructions = localSystemPrompt();
  if (Array.isArray(body.input)) {
    // llama-server rejects items without a content array (reasoning,
    // item_reference, ...); keep only the types it converts to chat messages.
    body.input = body.input.filter(
      (item) =>
        supportedItemTypes.has(item?.type) &&
        !(item?.type === 'message' && (item.role === 'system' || item.role === 'developer')),
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
      const isResponsesRequest = req.method === 'POST' && requestPath === '/v1/responses';
      let responsesRequest;
      let renamedTools = new Map();
      if (isResponsesRequest && body.length > 0) {
        responsesRequest = normalizeResponses(JSON.parse(body.toString('utf8')));
        renamedTools = normalizeTools(responsesRequest);
        body = Buffer.from(JSON.stringify(responsesRequest));
      }

      const headers = { ...req.headers };
      delete headers.host;
      headers['content-length'] = String(body.length);

      const upstreamResponse = await fetch(`${upstream}${req.url}`, {
        method: req.method,
        headers,
        body: body.length > 0 ? body : undefined,
      });

      // llama-server adds a non-standard "models" array that Codex cannot
      // decode; serve the standard OpenAI "data" list only.
      if (req.method === 'GET' && requestPath === '/v1/models' && upstreamResponse.ok) {
        const modelsBody = await upstreamResponse.json();
        delete modelsBody.models;
        const payload = JSON.stringify(modelsBody);
        res.writeHead(upstreamResponse.status, {
          'content-type': 'application/json',
          'content-length': String(Buffer.byteLength(payload)),
        });
        res.end(payload);
        return;
      }

      // Keep failed Responses requests on disk to diagnose upstream rejections.
      if (isResponsesRequest && !upstreamResponse.ok) {
        const responseText = await upstreamResponse.text();
        logFailedRequest(responsesRequest, upstreamResponse.status, responseText);
        res.writeHead(upstreamResponse.status, Object.fromEntries(upstreamResponse.headers));
        res.end(responseText);
        return;
      }

      if (isResponsesRequest && upstreamResponse.ok && renamedTools.size > 0) {
        const responseHeaders = Object.fromEntries(upstreamResponse.headers);
        delete responseHeaders['content-length'];
        delete responseHeaders['content-encoding'];
        const contentType = upstreamResponse.headers.get('content-type') ?? '';
        if (contentType.includes('text/event-stream')) {
          res.writeHead(upstreamResponse.status, responseHeaders);
          const decoder = new TextDecoder();
          let pending = '';
          for await (const chunk of upstreamResponse.body) {
            pending += decoder.decode(chunk, { stream: true });
            let separator;
            while ((separator = /\r?\n\r?\n/.exec(pending))) {
              const event = pending.slice(0, separator.index);
              pending = pending.slice(separator.index + separator[0].length);
              res.write(`${restoreSseEvent(event, renamedTools)}\n\n`);
            }
          }
          pending += decoder.decode();
          if (pending) res.write(restoreSseEvent(pending, renamedTools));
          res.end();
        } else {
          const responseBody = restoreToolCalls(await upstreamResponse.json(), renamedTools);
          res.writeHead(upstreamResponse.status, responseHeaders);
          res.end(JSON.stringify(responseBody));
        }
        return;
      }

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
  console.log(`Local Codex normalization proxy: http://${listenHost}:${listenPort} -> ${upstream}`);
});
