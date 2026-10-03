# Local LLM GUI

WinUI 3 / C# / .NET 8 の Windows アプリです。このリポジトリ内のスクリプト、モデル、llama.cpp、Open WebUI を使います。親リポジトリのスクリプトや設定は参照しません。

## 起動

```powershell
.\Start-Gui.ps1
```

Windows 10 version 2004 以降 / Windows 11、.NET 8 SDK、WebView2 Evergreen Runtime が必要です。初回の NuGet 復元にはネットワーク接続が必要です。Windows App SDK はビルド出力に同梱します。NU1900 だけを抑制し、取得できた脆弱性情報の警告は表示します。

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

- **llama-server**: 起動時の最初の画面。モデル・mmproj・実行ファイル欄には上記の既定の相対パスが入ります。コンテキスト長は既定 131,072、最小 65,536（Claude は 100,000 以上）。起動中は点字スピナーと読み込み状況を表示し、接続後に起動を完了します。稼働中の数値設定保存は次回起動に反映されます。終了ボタンは GUI が起動したサーバーだけを停止します。
- **Open WebUI**: サーバーの接続確認後に起動ボタンが有効になります。停止中・準備中は埋め込みブラウザーを非表示にし、接続成功後に表示します。停止すると再び非表示になります。点字スピナーと初期化状況を表示し、接続成功時に WebView2 を自動で再読み込みします。接続先は `http://127.0.0.1:9931/v1`、Web UI は既定 `http://127.0.0.1:3000` です。
- **Claude / Codex**: サーバーの接続確認後にボタンが有効になり、同梱スクリプトで対話 PowerShell を開きます。モデル ID はサーバーの `/props` から取得します。
- **ログ**: バックグラウンド処理の出力と終了コードを直近 600 行表示します。フォントは Cascadia Code を優先し、日本語は Noto Sans JP にフォールバックします（両フォントをインストールしてください）。

GUI を開くだけではモデルを読み込みません。終了時には GUI が起動したバックグラウンド処理とその子プロセスを停止します。独立した CLI ウィンドウは継続します。

設定と WebView2 プロファイルは `%LOCALAPPDATA%/LocalLlmGui/`、Open WebUI の会話と設定はこのリポジトリの `openwebui/data/` に保存します。過去の設定に親リポジトリのパスが残っていても使用しません。

## 検証と配布

```powershell
.\Start-Gui.ps1 -BuildOnly
dotnet run --project .\tests\SmokeTests.csproj
.\tests\ParseScripts.ps1
python -m unittest discover -s tests -p test_configure_openwebui.py
dotnet publish .\LocalLlm.Gui.csproj -c Release -r win-x64 --self-contained true -o .\publish
```

テストはウィンドウサイズ計算、起動状況、PowerShell の日本語と引数、独立したパス解決、模擬サーバー起動、終了コードと子プロセス停止を確認します。モデルは読み込みません。

配布時は `publish` 全体に `models/`、`runtime/` を配置し、配布先で `Setup-OpenWebUI.ps1` を実行してください。仮想環境にはインストール場所が記録されるため、新しい場所で作成します。開発ビルドは GUI プロジェクトのフォルダーを、配布ビルドは実行ファイルのフォルダーを基準にします。
