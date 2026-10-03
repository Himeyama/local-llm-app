# Local LLM GUI

WinUI 3 / C# / .NET 8 のローカル LLM を扱う Windows アプリです。このリポジトリ内のスクリプト、モデル、llama.cpp、Open WebUI を使います。

<img width="600" src="https://github.com/user-attachments/assets/872b5c0f-1a7b-49b1-9fd7-63f2db3fbfbf" />

## 起動

```powershell
.\Start-Gui.ps1
```

Windows 10 version 2004 以降 / Windows 11、.NET 8 SDK、WebView2 Evergreen Runtime が必要です。初回の NuGet 復元にはネットワーク接続が必要です。Windows App SDK はビルド出力に同梱します。NU1900 だけを抑制し、取得できた脆弱性情報の警告は表示します。

チャットのウェブ検索には Node.js 18 以降（PATH 上の `node`）と Microsoft Edge が必要です。初回に検索用の依存パッケージをセットアップします。バージョンは `BrowserSearch/package-lock.json` で固定します。

```powershell
Push-Location .\BrowserSearch
npm ci
Pop-Location
```

## ローカル実行環境

現在のマシンではモデル、ROCm 実行環境、Python、Open WebUI の環境とデータをこのリポジトリ内に配置済みです。モデルと読み取り専用パッケージにはハードリンクを利用し、元のフォルダーを参照するシンボリックリンクやジャンクションは使いません。元のファイルを削除してもこちらのファイルは残ります。会話データは独立したコピーです。

新しく clone した場合は、以下の配置で準備してください。大容量のモデル・実行環境・会話データは Git に含みません。

- `models/Qwen3.8-27B-Uncensored/Qwen3.8-27B-Uncensored-Q4_K_M.gguf`
- `models/Qwen3.8-27B-Uncensored/mmproj-Qwen3.8-27B-Uncensored-F16.gguf`
- `runtime/llama.cpp/llama-server.exe` と同じ ROCm 配布物の DLL、`rocblas/`、`hipblaslt/`。配布物全体を配置します。
- `runtime/python/` に Python 3.11 の実行環境（またはインストール済み Python 3.11 をセットアップ時に指定）。
- `openwebui/.venv/` は以下で作成します。

```powershell
.\Setup-OpenWebUI.ps1 -PythonExe 'python'
# Python のパスを指定する場合:
.\Setup-OpenWebUI.ps1 -PythonExe 'C:\Python311\python.exe'
```

Open WebUI 0.11.3 をインストールします。初回インストールにはネットワーク接続が必要です。Claude Code / Codex CLI と Node.js は各 CLI の実行に必要です。起動スクリプトとプロキシは `Cli/` に同梱しています。通信キャプチャーを使う場合だけ mitmweb が必要です。

## 操作

- 人間側のメッセージはアクセントカラー・白文字の右寄せの吹き出しに原文を表示し、Markdown を解釈しません。アシスタント側は吹き出しを使わず、[Markdig](https://www.nuget.org/packages/Markdig) で見出し・強調・リスト・引用・コード・表・リンクを解析してネイティブ表示します。入力欄内に画像追加・作業フォルダー設定・ツール設定のアイコンを左から順に配置します。作業フォルダーはダイアログで入力またはフォルダー選択し、ツール設定はダイアログ内のスイッチ型トグルで変更します。送信ボタンの左の推論アイコンから推論レベル（オフ・低・中・最高）を設定でき、次の送信から適用します。ウェブ検索・ファイル変更・推論レベルの設定はアプリ再起動後も保持します。チャット画面ではタイトルを表示しません。接続状態は全画面共通の下部ステータスバーに左寄せで表示し、llama-server と Open WebUI を「｜」で区切ります。「接続済み」の文字だけを緑、「未接続」の文字だけを赤にし、サービス名・モデル名・起動状況は通常の文字色で表示します。通常時は入力欄の上に案内文を表示しません。送信／停止も入力欄内のアイコンです。送信ボタンはアクセントカラーの円形です。生成中も入力・送信・画像添付ができ、送信ボタンと停止ボタンを並べて表示します。追加のメッセージは入力欄の上に「送信待ち」として表示し、前の回答が完了すると送信順に処理します。停止・通信失敗時は待機中のメッセージを保持し、空欄のまま送信すると再試行後に続けて処理します。送信待ちはチャットごとに保持しますが、アプリを終了すると消去されます。入力欄の枠は外側の角丸枠に統一し、フォーカス時も下端に別の線を描画しません。左側のメニューアイコンは通常時は透明、ホバー時に背景色を表示し、選択中はアイコン色を変えずに薄い影色の背景を表示します。
- 起動時は独自チャットを表示します。チャットを選択しているときだけ左側に新しいチャットとセッションごとの履歴をタイトルだけで表示します（日付は表示しません）。最左列にチャット・llama-server・Open WebUI・Claude / Codex・ログのアイコンがあります。アイコンの説明はマウスを置くと表示されます。タイトルバーのボタンで履歴を折りたためます。
- **独自チャット**: Open WebUI を起動せず、`127.0.0.1:9931` の llama-server に直接接続します。モデル ID は `/props` から取得します。回答を逐次表示し、処理状況の左に点字スピナーを表示します。Enter または送信ボタンで送信、Shift+Enter で改行、停止ボタンで生成を中断できます。通信失敗・停止後は空欄のまま送信すると再試行します。セッションは自動保存し、アプリ再起動後も左側から開けます。履歴を右クリックすると削除メニューが表示され、確認ダイアログで削除した場合だけ会話と添付画像を削除します。API の形式は [llama.cpp のサーバー仕様](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md) に従います。
- **画像**: PNG / JPEG / WebP を 1 メッセージに最大 4 枚、1 枚 10 MiB まで添付できます。画像と会話を履歴に保存し、OpenAI 互換の `image_url` としてサーバーへ送ります。対応する画像モデルと mmproj が必要です。
- **ウェブ参照**: `web_search` は [Playwright MCP](https://github.com/microsoft/playwright-mcp) の `browser_navigate` / `browser_evaluate` を使い、Bing の検索結果からタイトル・URL・要約を最大 8 件取得します。検索専用のヘッドレス Edge をバックグラウンドで起動し、ブラウザーやコンソールをデスクトップに表示しません。普段のブラウザーやログイン状態は使いません。検索完了後にブラウザーを閉じ、停止操作または 60 秒のタイムアウト時には検索プロセスと子プロセスを終了します。ツール設定のウェブ検索が無効なら実行しません。検索サービス側の制限・CAPTCHA が表示された場合はエラーを返します。`web_read` は公開 HTTP/HTTPS のテキスト・HTML 本文を直接取得し、ページは最大 2 MiB、本文は最大 20,000 文字です。参照 URL は会話内からブラウザーで開けます。
- **ファイル操作**: セッションごとに作業フォルダーを選択できます。未指定時は GUI のフォルダーです。モデルはファイル名・本文検索と UTF-8 テキストの読み込みを利用し、ツール設定で「ファイル変更」を有効にすると作成・上書き・一意な文字列の置換もできます。読み書きは 1 MiB まで、検索は最大 5,000 エントリー・80 件です。作業フォルダー外、シンボリックリンク・ジャンクション、`.git`・モデル・依存パッケージなどのフォルダーは対象外です。上書き前のファイルは `%LOCALAPPDATA%/LocalLlmGui/chat-backups/` に保存し、そのパスをツール結果に表示します。ツール結果は会話内で展開できます。PDF・Word などのバイナリ文書や、ログインが必要なウェブページの本文抽出は未対応です。

- **llama-server**: サーバー設定と起動の画面。モデル・mmproj・実行ファイル欄には上記の既定ファイルの絶対パスが入ります。別の場所のファイルも絶対パスで指定できます。コンテキスト長は既定 131,072、最小 65,536（Claude は 100,000 以上）。起動中は点字スピナーと読み込み状況を表示し、接続後に起動を完了します。上部のコマンドバーから起動と設定の保存を操作します。下部ステータスバーの llama-server 表示からも起動・停止でき、チャット画面では保存済みの設定で起動します。起動ボタンには再生、保存には保存アイコンを表示し、稼働中は起動ボタンが終了（停止アイコン）に変わります。保存した設定は次回起動に反映されます。モデル・mmproj・実行ファイルのパスも保存します。終了できるのは GUI が起動したサーバーだけです。
- **Open WebUI**: サーバーの接続確認後に起動ボタンが有効になります。停止中・準備中は埋め込みブラウザーを非表示にし、接続成功後に表示します。停止すると再び非表示になります。点字スピナーと初期化状況を表示し、接続成功時に WebView2 を自動で再読み込みします。接続先は `http://127.0.0.1:9931/v1`、Web UI は既定 `http://127.0.0.1:3000` です。
- **Claude / Codex**: 作業ディレクトリを直接入力またはフォルダー選択で指定できます。選択時と CLI 起動時に保存し、次回も使用します。未指定時は GUI リポジトリ内で開きます。サーバーの接続確認後にボタンが有効になり、同梱スクリプトを Windows Terminal の新しいウィンドウ内の PowerShell で開きます。Windows Terminal が必要です。CLI 起動時はユーザー・システムの PATH と標準インストール先を取り込み、GUI 起動後にインストールされた CLI も検出します。モデル ID はサーバーの `/props` から取得します。
- **ログ**: バックグラウンド処理の出力と終了コードを直近 600 行表示します。フォントは Cascadia Code を優先し、日本語は Noto Sans JP にフォールバックします（両フォントをインストールしてください）。

GUI を開くだけではモデルを読み込みません。終了時には GUI が起動したバックグラウンド処理とその子プロセスを停止します。独立した CLI ウィンドウは継続します。

独自チャットの履歴は `%LOCALAPPDATA%/LocalLlmGui/chats/` に保存します。設定と WebView2 プロファイルは `%LOCALAPPDATA%/LocalLlmGui/`、Open WebUI の会話と設定はこのリポジトリの `openwebui/data/` に保存します。過去の設定に親リポジトリのパスが残っていても使用しません。

## 検証と配布

```powershell
.\Start-Gui.ps1 -BuildOnly
dotnet run --project .\tests\SmokeTests.csproj
dotnet run --project .\tests\ChatTests.csproj

Push-Location .\BrowserSearch
npm test
Pop-Location
.\tests\ParseScripts.ps1
python -m unittest discover -s tests -p test_configure_openwebui.py
dotnet publish .\LocalLlm.Gui.csproj -c Release -r win-x64 --self-contained true -o .\publish
```

テストはウィンドウサイズ計算、起動状況、PowerShell の日本語と引数、独立したパス解決、模擬サーバー起動、終了コードと子プロセス停止を確認します。ChatTests は画像の API ペイロード、履歴保存、ストリーミングとツール呼び出し、ファイル操作・バックアップ・パス制限、停止・中断を確認します。`dotnet run --project .\tests\ChatTests.csproj -- --web` で実際の公開ページ取得と検索も確認できます。モデルは読み込みません。

配布前に `BrowserSearch` で `npm ci` を実行してください。検索スクリプトと `node_modules` はビルド・publish 時に出力先へコピーします。配布先にも Node.js と Edge が必要です。配布時は `publish` 全体に `models/`、`runtime/` を配置し、配布先で `Setup-OpenWebUI.ps1` を実行してください。仮想環境にはインストール場所が記録されるため、新しい場所で作成します。開発ビルドは GUI プロジェクトのフォルダーを、配布ビルドは実行ファイルのフォルダーを基準にします。
