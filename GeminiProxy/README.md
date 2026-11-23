# GeminiProxy

ローカル LLM GUI の開発用プロキシです。Gemini 形式の JSON を受け取り、Google Gemini API に送信し、ストリーミング応答を SSE で返します。Python の標準ライブラリだけで動作します。

## 接続先

- ローカル URL: `http://localhost:8765/ai-foundation/chat-ai/gemini/gemini-3.5-flash:streamGenerateContentSse`
- 実際の転送先モデル: `gemini-3.1-flash-lite`

ローカル URL のモデル名は互換性のための固定パスです。転送先のモデルは `gemini_proxy.py` の `UPSTREAM` で指定しています。

## 起動

Python 3.8 以降と Google AI Studio の API キーが必要です。プロジェクトのルートで PowerShell を開き、キーを現在のプロセスに設定して起動します。

```powershell
$env:GEMINI_API_KEY = '取得した API キー'
python GeminiProxy/gemini_proxy.py
```

Windows のユーザー環境変数に `GEMINI_API_KEY` を保存済みなら、次のコマンドでその値を直接読み込めます。既に開いている端末が古い環境変数を保持している場合にも使えます。

```powershell
python GeminiProxy/gemini_proxy.py --user-env
```

起動中は端末を開いたままにし、終了するときは Ctrl+C を押します。キーを変更した場合は、プロキシを再起動してください。

GUI では左下のモデル表示を右クリックし、「Gemini」と上記のローカル URL を指定して保存します。

チャットの推論レベルは Gemini 3.1 Flash-Lite の minimal / low / medium / high に対応します。画面の「最小」は完全な推論オフではありません。Gemini が返す推論の要約は、推論表示が有効なときに回答の前に表示し、会話に保存します。要約が返されない場合もあります。別のモデルやプロキシを指定する場合は、そのモデルがこれらの thinkingLevel 値に対応している必要があります。

## 動作確認

プロキシを起動したまま、別の端末で次を実行します。短いリクエストを Gemini に送信するため、使用状況に応じて料金や利用枠が発生します。

```powershell
python GeminiProxy/check_connection.py --local
```

チャンク形式の送信も確認する場合は `--chunked` を追加します。成功時は `HTTP 200 SSE: True complete: True` と表示されます。

## 制限とエラー

- `127.0.0.1:8765` で待ち受けます。外部からの接続と、ブラウザーからの Origin 付きリクエストは受け付けません。
- リクエスト全体は 20,000,000 バイトまでです。413 が返る場合は、添付画像を小さくするか新しいチャットで再送信してください。
- 400 の `API_KEY_INVALID` が返る場合は、キーを確認し、古い環境変数が使われていないか確認してください。ユーザー環境変数を変更した場合は `--user-env` で再起動できます。
- 502 は Gemini API に接続できなかった場合に返ります。ネットワーク接続を確認してください。
- `Address already in use` が表示された場合は、同じポートを使う別のプロキシや `tests/gemini_sse_dummy.py` を停止してください。
