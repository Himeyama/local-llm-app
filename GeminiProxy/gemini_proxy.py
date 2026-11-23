"""Local Gemini SSE proxy. Reads GEMINI_API_KEY from the environment."""
import http.client
import json
import os
import re
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

PATH = '/ai-foundation/chat-ai/gemini/gemini-3.5-flash:streamGenerateContentSse'
UPSTREAM = '/v1beta/models/gemini-3.1-flash-lite:streamGenerateContent?alt=sse'
MAX_BODY = 20_000_000


class RequestTooLarge(Exception):
    pass


def read_body(handler):
    length = handler.headers.get('Content-Length')
    transfer = handler.headers.get('Transfer-Encoding', '').lower()
    if transfer:
        if transfer != 'chunked' or length is not None:
            raise ValueError('Invalid transfer encoding')
        chunks = []
        total = 0
        while True:
            line = handler.rfile.readline(1025)
            if not line.endswith(b'\r\n') or len(line) > 1024:
                raise ValueError('Invalid chunk header')
            size_text = line[:-2].split(b';', 1)[0]
            if not re.fullmatch(rb'[0-9a-fA-F]+', size_text):
                raise ValueError('Invalid chunk size')
            size = int(size_text, 16)
            total += size
            if total > MAX_BODY:
                raise RequestTooLarge()
            if size == 0:
                # Trailers are not needed by this client; bound them too.
                for _ in range(100):
                    trailer = handler.rfile.readline(8193)
                    if trailer == b'\r\n':
                        return b''.join(chunks)
                    if not trailer.endswith(b'\r\n') or len(trailer) > 8192:
                        raise ValueError('Invalid trailer')
                raise ValueError('Too many trailers')
            data = handler.rfile.read(size)
            if len(data) != size or handler.rfile.read(2) != b'\r\n':
                raise ValueError('Incomplete chunk')
            chunks.append(data)
    if length is None or not re.fullmatch(r'[0-9]+', length):
        raise ValueError('Content-Length or chunked encoding required')
    if int(length) > MAX_BODY:
        raise RequestTooLarge()
    body = handler.rfile.read(int(length))
    if len(body) != int(length):
        raise ValueError('Incomplete request')
    return body


class Handler(BaseHTTPRequestHandler):
    protocol_version = 'HTTP/1.1'

    def do_POST(self):
        if self.headers.get('Host', '').lower() not in ('localhost:8765', '127.0.0.1:8765'):
            self.send_error(403)
            return
        if self.path != PATH:
            self.send_error(404)
            return
        if self.headers.get('Origin') or self.headers.get('Sec-Fetch-Site'):
            self.send_error(403)
            return
        try:
            body = read_body(self)
        except RequestTooLarge:
            self.close_connection = True
            self.send_error(413, 'Request exceeds 20 MB; reduce attachments or start a new chat')
            return
        except ValueError:
            self.close_connection = True
            self.send_error(400, 'Invalid request framing')
            return
        try:
            if not isinstance(json.loads(body).get('contents'), list):
                raise ValueError()
        except (ValueError, UnicodeDecodeError, AttributeError):
            self.send_error(400)
            return
        connection = http.client.HTTPSConnection('generativelanguage.googleapis.com', timeout=20)
        headers_sent = False
        try:
            connection.request('POST', UPSTREAM, body, {'Content-Type': 'application/json', 'Accept': 'text/event-stream', 'x-goog-api-key': os.environ['GEMINI_API_KEY']})
            upstream = connection.getresponse()
            self.send_response(upstream.status)
            self.send_header('Content-Type', upstream.getheader('Content-Type', 'application/json'))
            self.send_header('Cache-Control', 'no-cache')
            # Close-delimited response preserves incremental SSE delivery without buffering.
            self.send_header('Connection', 'close')
            self.end_headers()
            headers_sent = True
            self.close_connection = True
            while chunk := upstream.read1(8192):
                self.wfile.write(chunk)
                self.wfile.flush()
        except (OSError, http.client.HTTPException) as exc:
            if not headers_sent:
                self.send_error(502, 'Gemini upstream connection failed')
            self.close_connection = True
            self.log_error('Upstream connection failed: %s', type(exc).__name__)
        finally:
            connection.close()


if __name__ == '__main__':
    if '--user-env' in sys.argv:
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, 'Environment') as environment:
            os.environ['GEMINI_API_KEY'] = winreg.QueryValueEx(environment, 'GEMINI_API_KEY')[0]
    if not os.environ.get('GEMINI_API_KEY'):
        raise SystemExit('GEMINI_API_KEY is not set')
    print('Gemini proxy listening on http://127.0.0.1:8765' + PATH, flush=True)
    ThreadingHTTPServer(('127.0.0.1', 8765), Handler).serve_forever()
