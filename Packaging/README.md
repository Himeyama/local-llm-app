# インストーラー

hsis **v0.11.2**（コミット `cc5d03947606deee1a7c445e3fe24f8902b0d679`）を使います。
ツールと Node.js のバージョン・取得先・チェックサムは [toolchain.json](toolchain.json) で固定しています。
この処理はコミット・タグ作成・push を行いません。

## 配布内容

- Windows 11 x64 向けの GUI。Release、.NET と Windows App SDK を同梱。
- Rust / ROCm の `runtime/native/local-rocm.exe`。新規設定と起動スクリプトの既定サーバー。
- HIP / HIPRTC / COMGR、デバイス資産、rocBLAS / hipBLASLt。外部 clang / flang / linker のコマンドラインツールは除外。
- 公式 Node.js 24.22.3、Playwright MCP と検索依存パッケージ、Monaco エディタ、端末表示、起動スクリプト。
- 同梱コンポーネントのライセンスと日本語の利用説明。

**GGUF モデルは同梱しません。** 初回はサーバー設定で対応モデルの絶対パスを指定します。
llama.cpp、Python、開発用 Rust ツールチェーンも配布しません。
標準 Rust 基盤はテキスト専用です。画像、MTP、Claude Messages / Codex Responses は未対応です。
GUI に保存済みの明示的な実行ファイル設定やモデルパスは保持します。

配置先の既定は `%LOCALAPPDATA%\Programs\LocalLlmGui` です。管理者権限は要求せず、
スタートメニューと HKCU のアプリ一覧に登録します。会話・設定は既存と同じ `%LOCALAPPDATA%\LocalLlmGui` です。
アンインストール定義は実際の配布ファイルの一覧から生成し、再帰削除を使いません。
利用者が後から配置したモデルなどのファイル、会話・設定、外部の作業フォルダーは削除しません。

AMD ドライバーと gfx1200 / gfx1201 対応 GPU が必要です。R9700 で検証しています。
ROCm の DLL は Microsoft Visual C++ x64 ランタイムも使用します。未導入の場合は Microsoft 公式の再頒布可能パッケージを導入してください。
エディタとウェブ検索には Microsoft Edge / WebView2 が必要です。

## ビルド

ビルド側は Windows 11 x64、.NET 8 / 9 SDK、Rust、Python 3.12 以降、Git、Node.js / npm、
ROCm 7.2 SDK と対応する rocBLAS 資産が必要です。GUI の NuGet と npm の依存はロックファイルに従います。
開発時の初回セットアップはリポジトリの README と NativeInference/README を参照してください。
hsis と Node.js の初回取得、NuGet の復元にはネットワーク接続が必要です。

```powershell
.\Build-Installer.ps1
# アプリのバージョンを指定する場合
.\Build-Installer.ps1 -Version 0.1.4
```

アプリバージョンは `LocalLlm.Gui.csproj` の `Version` を使用します。明示する場合も同じ番号が必要です。hsis 自体の `0.11.2` とは別です。
hsis のタグとコミット、配布テンプレート内のバージョンを検証してから使用します。
Rust は `cargo build --locked --release` でビルドし、GUI は self-contained で publish します。
`-SkipBuild` は Rust と hsis の作成済みバイナリを再利用する指定です。GUI の publish は行います。
`-RocmRoot` と `-HsisSource` で既存の配置先を指定できます。

作業フォルダーは `publish/installer/payload-日時-ID/` です。モデル・履歴・個人設定は取り込みません。
配布ファイルごとの SHA-256 を `payload-manifest.json` に記録します。
成果物は `publish/installer/LocalLlmGui-<version>-win-x64-Setup.exe` と `.exe.sha256` です。
動作条件とモデル指定方法を記載した `LocalLlmGui-<version>-win-x64-README.txt` も同じ場所へ出力します。
hsis が作成した元の出力先も作業フォルダーの `distribution/Setup-*` に保持します。
実行ファイル名は変更せずに配布してください。

標準の Windows `tar.exe` で圧縮できます。7-Zip が見つかる場合は hsis の公式な `HSIS_7ZIP_PATH` 経路を使います。
`HSIS_7ZIP_PATH` を明示すればそちらを優先します。セットアップのビルド定義は [LocalLlmGui.nsi](LocalLlmGui.nsi) です。

## 検証

```powershell
dotnet run --project Packaging/Checks/PackagingChecks.csproj
powershell.exe -NoProfile -File Packaging/check_scripts.ps1
pwsh.exe -NoProfile -File Packaging/check_scripts.ps1
```

設定の既定値、明示パスの維持、開発版とインストール版のルート解決、PowerShell 5.1 / 7 の構文を確認します。

```powershell
python -X utf8 Packaging/check_payload.py <作業フォルダー>/app --gpu
python -X utf8 Packaging/verify_installer.py <Setup.exe> --manifest <作業フォルダー>/payload-manifest.json
```

`check_payload.py` は外部の Node / ROCm を見つけない PATH で、同梱 Node と MCP モジュールを確認します。
`--gpu` は小さな既知の配列で HIP / HIPRTC と rocBLAS を検証します。GGUF モデルやサーバーはロードしません。
`verify_installer.py` は生成済み EXE の HSISPKG3 フッターと配置情報を検査し、7-Zip または Windows 標準 tar で展開します。
配布元の全ファイルとサイズ・SHA-256 を比較し、モデル除外、アプリ一覧登録の定義、ユーザーデータを保持する削除定義を確認します。
検証用ファイルは `publish/installer/verification-*` に置き、システムのレジストリやショートカットは変更しません。

`v<バージョン>` タグの push で GitHub Actions が Windows x64 のセットアップを作成・検証し、チェックサムと説明書を GitHub Releases に公開します。タグとプロジェクトのバージョンが異なる場合は失敗します。GPU を持たない GitHub のランナーでは GPU の実行テストは行いません。成果物は署名されません。
