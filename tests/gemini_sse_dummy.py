"""Development-only Gemini SSE endpoint. Run: python tests/gemini_sse_dummy.py"""
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import time


class Handler(BaseHTTPRequestHandler):
    def do_POST(self):
        if self.path != '/ai-foundation/chat-ai/gemini/gemini-3.5-flash:streamGenerateContentSse':
            self.send_error(404)
            return
        try:
            request = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
            if not isinstance(request.get('contents'), list):
                raise ValueError('contents is required')
        except (KeyError, ValueError, json.JSONDecodeError):
            self.send_error(400)
            return
        self.send_response(200)
        self.send_header('Content-Type', 'text/event-stream; charset=utf-8')
        self.send_header('Cache-Control', 'no-cache')
        self.end_headers()
        for text in ('開発用ダミーからの', 'ストリーミング応答です。'):
            self.event({'candidates': [{'content': {'parts': [{'text': text}]}}]})
            time.sleep(0.3)
        self.event({'candidates': [{'finishReason': 'STOP'}], 'usageMetadata': {'totalTokenCount': 12}})

    def event(self, obj):
        self.wfile.write(('data: ' + json.dumps(obj, ensure_ascii=False) + '\n\n').encode('utf-8'))
        self.wfile.flush()


if __name__ == '__main__':
    print('Development dummy: http://localhost:8765/ai-foundation/chat-ai/gemini/gemini-3.5-flash:streamGenerateContentSse', flush=True)
    ThreadingHTTPServer(('127.0.0.1', 8765), Handler).serve_forever()
