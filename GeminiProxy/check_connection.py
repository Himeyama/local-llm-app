"""Send a short diagnostic request without printing the API key or reply."""
import json
import os
import sys
import urllib.error
import urllib.request
import winreg

local = '--local' in sys.argv
chunked = '--chunked' in sys.argv
key = os.environ.get('GEMINI_API_KEY', '')
if '--user-env' in sys.argv:
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, 'Environment') as environment:
        key = winreg.QueryValueEx(environment, 'GEMINI_API_KEY')[0]
body = json.dumps({'contents': [{'parts': [{'text': 'Reply OK'}]}]}).encode()
request = urllib.request.Request(
    'http://127.0.0.1:8765/ai-foundation/chat-ai/gemini/gemini-3.5-flash:streamGenerateContentSse' if local else 'https://generativelanguage.googleapis.com/v1beta/models/gemini-3.1-flash-lite:streamGenerateContent?alt=sse',
    iter([body[:10], body[10:]]) if chunked else body,
    {'Content-Type': 'application/json', **({} if local else {'x-goog-api-key': key})},
)
try:
    with urllib.request.urlopen(request, timeout=20) as response:
        data = response.read()
        print('HTTP', response.status, 'SSE:', b'data:' in data, 'complete:', b'finishReason' in data)
except urllib.error.HTTPError as error:
    print('HTTP', error.code, error.read().decode()[:700])
except OSError as error:
    print(type(error).__name__, str(error))
