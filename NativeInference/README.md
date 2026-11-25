# Rust / ROCm inference

現在の Qwen GGUF を直接読み込み、AMD GPU でテキストを生成する独立した推論サーバーです。
実行時に llama.cpp、ggml、PyTorch、Python を呼びません。Rust、ROCm クレート [`rocmrc`](https://github.com/Reis-McMillan/rocmrc)、AMD rocBLAS、小さな HIP C++ カーネルで構成しています。
**現時点ではテキスト用の実装です。既存サーバーの全機能を置き換えたものではありません。**

## 対応範囲

| 機能 | 状態 |
| --- | --- |
| 現在の `Qwen3.8-27B-Uncensored-Q4_K_M.gguf` | R9700 で短文生成を確認。内部アーキテクチャは `qwen35` |
| GGUF v3 / byte BPE | 独立実装。GGUF の語彙・マージ表を利用 |
| F32 / F16 / BF16 / Q8_0 / Q4_K / Q5_K / Q6_K | CPU 参照計算と HIP 実機テストあり |
| Gated DeltaNet / Attention / RoPE / SwiGLU | 独立した HIP カーネル。GGUF の tiled head 順序に対応 |
| KV・履歴キャッシュ | FP16 KV と再帰状態のチェックポイント。同じトークン列の履歴を再利用し、分岐・変更時は復元またはリセット |
| 入力 prefill | rocBLAS では最大256トークン。量子化重みの行列積は FP16 入力・FP32 累積。長い会話では Attention スコア用領域の目標 64 MiB に合わせて縮小（1トークンの場合を除く）。BLAS 未配置時は最大8トークン |
| GPU 一時メモリ | 最大128 MiBのプール。演算ごとの確保・解放と不要なゼロ初期化を省略 |
| `/health`, `/props`, `/v1/models` | 対応 |
| `/v1/chat/completions` | 通常 JSON と SSE、usage、reasoning_content、function tools |
| 生成方法 | greedy (`temperature=0`)。temperature 省略時も greedy |
| 推論レベル | `none` は思考無効、low/medium/xhigh は有効。レベル別の予算調整は未実装 |
| 画像・mmproj / 音声 | 未実装。画像は HTTP 400 で明示的に拒否 |
| Claude Messages / Codex Responses API | 未実装。HTTP 501 を返す |
| MTP / 投機的生成 | 未実装。MTP 重みは主ネットワークから除外 |
| 他アーキテクチャ / 他の量子化形式 | 未対応の形式は読み込み時に拒否 |

131,072 のコンテキスト容量を指定できます。入力は最大256トークンずつ処理し、生成は1トークンずつ行います。
成熟した推論エンジンと同等の長文性能・全 logits 一致は未検証です。
ツール用 ChatML アダプターはこのモデル向けの限定実装で、任意の Jinja テンプレートを実行する機能はありません。

## ビルドとランタイム配置

Windows x64、Rust 1.99 以降、対応 GPU ドライバーと ROCm 7 の SDK / HIPRTC が必要です。
この環境では `stable-x86_64-pc-windows-gnu` でビルドしています。新しい Rust や依存パッケージを
自動インストールする処理はありません。

リポジトリ直下で実行します。

```powershell
cargo build --locked --release --manifest-path NativeInference/Cargo.toml
python NativeInference/prepare_runtime.py --sdk-root 'C:\path\to\rocm-sdk'
python NativeInference/prepare_blas.py --source-bin 'C:\path\to\matching-rocm-libraries\bin'
.\Start-RocmServer.ps1
```

`--sdk-root` は `bin/amdhip64_7.dll` と HIPRTC を含む AMD SDK のルートです。
既存の ROCm Python 配布物に含まれる `_rocm_sdk_core` も利用できますが、Python や PyTorch 自体には依存しません。
配置スクリプトは AMD SDK を `runtime/rocm/`、実行ファイルを `runtime/native/local-rocm.exe` に配置します。
同じボリュームではハードリンクを使い、SDK のライセンスなどを含む `share/` も保持します。
`runtime/` とモデルは Git 管理外です。既存の異なるランタイムファイルは上書きしません。

配置せず既存 SDK を指定して実行することもできます。

```powershell
.\Start-RocmServer.ps1 -RocmBin 'C:\path\to\rocm-sdk\bin' -Build
```

通常は `127.0.0.1:9931` で待ち受けます。LAN 公開は明示的な `-LanAccess` 指定が必要です。
同じポートで既存サーバーが動いている場合は起動に失敗します。既存プロセスを自動終了しません。

GUI ではサーバー設定の実行ファイルに `runtime/native/local-rocm.exe` の絶対パスを指定できます。
`Start-LlamaServer.ps1` はこのファイル名を認識し、native 用の引数で起動します。
SDK の既定位置は `runtime/rocm/bin` です。`LOCAL_ROCM_BIN` で変更できます。
mmproj は使いません。MTP 設定は無効です。GUI の画像添付や Claude / Codex CLI はまだ対象外です。
GUI と `Start-LlamaServer.ps1` の既定実行ファイルは `runtime/native/local-rocm.exe` です。
保存済みの明示的な実行ファイル設定は保持します。画像や専用 API が必要な場合は対応サーバーを別途指定してください。

HIP / HIPRTC / rocBLAS は `rocmrc` 0.5.1 経由で呼び出します。Windows の DLL 名・配置を扱う小さな変更だけを
同梱版に適用しています。生成済みバインディングと上流ライセンスは保持しています。
変更内容は [WINDOWS-PATCH.md](vendor/rocmrc/WINDOWS-PATCH.md) に記載しています。

rocBLAS と依存する hipBLASLt は **同じ ROCm リリース** の DLL・GPU カーネル資産一式が必要です。
`prepare_blas.py` は既存の資産を `runtime/rocm/blas/bin` に配置します。追加ダウンロードは行いません。
SDK の `bin/rocblas.dll`、上記の配置先、または `LOCAL_ROCM_BLAS_BIN` の指定先を利用します。
量子化重みは常駐時には圧縮形式のまま保ち、バッチ行列積の直前に一つの FP16 作業領域へ展開します。
入力も FP16 に丸め、rocBLAS の GEMM_EX で FP32 の累積・出力を使います。F32 / BF16 重みは FP32 経路を保ちます。
`LOCAL_ROCM_PREFILL_F32=1` を設定して起動すると、従来の FP32 展開・SGEMM 経路を使えます。
FP16 経路は丸め方が変わるため、全入力で従来の logits や生成結果が一致する保証はありません。
単一トークンの量子化行列積は HIP カーネルで直接計算します。BLAS 未配置時も HIP 経路で動作します。

## 検証コマンド

```powershell
cargo test --locked --manifest-path NativeInference/Cargo.toml
$env:LOCAL_ROCM_BIN = (Resolve-Path runtime/rocm/bin).Path
cargo test --locked --manifest-path NativeInference/Cargo.toml --test gpu -- --ignored --test-threads=1
.\runtime\native\local-rocm.exe gpu-check $env:LOCAL_ROCM_BIN
python -X utf8 NativeInference/tests/api_smoke.py --rocm-bin $env:LOCAL_ROCM_BIN
```

通常の Rust テストはモデルや GPU を読み込みません。GPU テストは小さい既知の配列を
f64 の CPU 計算と比較し、tiled head の対応、再帰状態、畳み込み履歴、FP16 KV を確認します。
`api_smoke.py` は **27B モデルを GPU にロードします**。テスト用ポート 19931 で独自サーバーだけを起動し、
計算、日本語、SSE、ツール呼び出し、usage、生成中の health、未対応入力の拒否を確認して子プロセスを停止します。
GUI と同じ .NET `JsonContent` の chunked 送信は、GPU 不要の別テストで確認できます。

```powershell
dotnet build NativeInference/tests/HttpClientProbe/HttpClientProbe.csproj
cargo test --locked --manifest-path NativeInference/Cargo.toml gui_json_content_roundtrip -- --ignored
```

HTTP は Content-Length と chunked の双方に対応し、復号後の本文を 16 MiB まで受け付けます。
手動の単発生成も可能です。

```powershell
.\runtime\native\local-rocm.exe generate `
  models/Qwen3.8-27B-Uncensored/Qwen3.8-27B-Uncensored-Q4_K_M.gguf `
  runtime/rocm/bin 'What is 2 + 2? Reply with just the number.' 8
```

## コードの読み方

| ファイル | 責務 |
| --- | --- |
| `gguf.rs` | メタデータとテンソルの範囲・型・整列の検証。重みは必要時のみ読む |
| `quant.rs` | ブロック形式と CPU 参照デコード |
| `tokenizer.rs` | バイト BPE と special token |
| `rocm.rs` | DLL ロード、rocmrc の HIP / HIPRTC 呼び出し、GPU メモリとモジュールの所有権。unsafe の境界 |
| `rocm/ops.rs` | バッファと演算形状の検証 |
| `rocm/memory.rs` | 同じ HIP stream 上で一時バッファを安全に再利用する有界プール |
| `rocm/blas.rs` | rocmrc の rocBLAS API、行列の配置とハンドル寿命 |
| `rocm/profile.rs` | 明示的に有効化した場合だけ HIP event で GPU 時間を測定 |
| `kernels.hip` | 量子化演算、正規化、Attention、再帰演算 |
| `model.rs` | 逐次生成・バッチ入力・会話状態とチェックポイント |
| `prompt_cache.rs` | トークン一致による現在位置の再利用・チェックポイント復元・リセットの選択 |
| `chat.rs` | ChatML 入力と逐次生成の reasoning / tool 解析 |
| `server.rs` | HTTP、SSE、1 本の推論ワーカーと有界キュー |

FFI バッファは `Rc` でライブラリの寿命を保持し、Send/Sync にしません。
ソケット処理と GPU の所有者を分け、生成中も health を返します。
中断された tool JSON は実行可能な呼び出しとして返しません。

形式・演算の確認先:
[GGUF specification](https://github.com/ggml-org/ggml/blob/master/docs/gguf.md)、
[Qwen3.5 model equations](https://github.com/huggingface/transformers/blob/main/src/transformers/models/qwen3_5/modeling_qwen3_5.py)、
[GGUF Qwen head layout](https://github.com/ggml-org/llama.cpp/blob/master/conversion/qwen.py)。
これらは仕様確認用の参照で、実行時・ビルド時の依存ではありません。

## 今回の検証記録（2026-11-20）

- 通常の Rust テスト 11 件と明示実行の GPU テスト 3 件が成功。
- `gpu-check` の 7 形式で CPU デコード・内積と GPU 結果が一致。別途 hipBLAS の F32 経路も確認。
- リポジトリ内へ配置した ROCm ランタイムで実モデルの API 結合テストが成功。計算、日本語、SSE、tool call、usage、状態リセット、生成中の health、画像・コンテキストエラーを確認。
- `dotnet build LocalLlm.Gui.csproj --no-restore` が警告 0・エラー 0 で成功。
- 起動スクリプトの構文を Windows PowerShell 5.1 と PowerShell 7.6 で確認。
- Clippy はこの Rust ツールチェーンに未インストールのため実行できず。
- 既存の `tests/SmokeTests.csproj` は、今回の変更外の未追跡 `ChatContextTests.cs` が同時にコンパイルされ、CS8802 / CS0246 でビルドできず。この既存テストプロジェクトとユーザーの作業中ファイルは変更していません。

長い会話全体の品質、画像対応、既存 CLI の接続、性能の同等性は、この検証の対象外です。

### GUI 接続の修正

GUI の .NET `JsonContent` は chunked で本文を送信します。初版はこれを拒否しており、
Python の Content-Length 送信だけでは検出できませんでした。GUI と同じクライアントで
`An error occurred while sending the request` を再現し、chunked 対応後に SSE まで通過することを確認しました。
UTF-8 の途中で分割されたチャンク、拡張・trailer、上限超過、不正な framing もテストしています。
この修正時の通常テストは 14 件、明示実行の .NET 通信テストは 1 件が成功しています。
起動中の状態確認や生成中断による接続キャンセルは、通常動作として扱います。

### 応答待ち時間の改善

初版には毎回の履歴再処理、1トークンずつの入力処理、演算ごとのメモリ確保・解放がありました。
これを、履歴チェックポイント・最大8トークンの prefill・一時メモリプールに変更しています。
再帰状態は assistant の思考用プレフィックス直前に保存し、次の要求が同じ履歴を含む場合に復元します。
Attention の KV はその位置まで有効なまま保ち、別の会話・編集・要約で一致しなければリセットします。
27B モデルではチェックポイントに約150 MiBの追加 VRAM を使います。

GPU テストでは、3層の小さな合成モデルを使い、1トークンずつの計算とバッチ計算、
キャッシュ復元後と全履歴の再処理が一致することを確認しています。
さらに7形式の量子化行列積を CPU 計算と比較し、Attention の未来位置のマスクも確認します。
この検証では稼働中の GUI とは別に27Bモデルをロードしません。
この改善時には通常テスト18件、明示実行のGPUテスト8件、.NET通信テスト1件が成功しています。

R9700 上の演算単位の測定では、行列・正規化・加算120反復の中央値が22.216msから5.697msに短縮しました。
実モデルと同じ FFN 形状の Q4_K 行列では、32トークン分の射影の中央値が34.320msから10.467msになりました。
これらは演算単位の比較であり、アプリ全体の応答時間の倍率ではありません。

ログの `prefill` に入力総数・再利用数・未処理数、`timings` に入力処理時間・生成評価速度・待ち時間の内訳を出します。
新しい会話の最初の入力はキャッシュがなく、長い入力や長い思考は引き続き時間を要します。
演算だけを測る GPU ベンチマークは次のコマンドで実行できます。モデルをロードしません。

```powershell
.\runtime\native\local-rocm.exe gpu-bench runtime/rocm/bin
.\runtime\native\local-rocm.exe prefill-bench runtime/rocm/bin
```

### ROCm クレート移行後の実モデル比較（2026-11-20）

`rocmrc` 0.5.1 の HIP / HIPRTC / rocBLAS API に移行しました。量子化行列のバッチ入力は
AMD rocBLAS の SGEMM を使い、FP32 展開用の一時領域を再利用します。
メモリプールは成長する Attention 作業領域をサイズクラスにまとめ、64 MiB の範囲で古い領域を入れ替えます。

R9700、同じ27B GGUF、コンテキスト容量131,072、入力1,356トークン、greedy 生成50評価の比較です。
各版を別プロセスで1回ずつ測定し、モデルロード時間は除外、履歴チェックポイントの保存は含めています。

| 測定 | 修正前 | クレート移行・最適化後 |
| --- | ---: | ---: |
| 入力処理 | 78.070秒 | 31.054秒 |
| 生成50評価 | 11.262秒 | 8.105秒 |
| 合計 | 89.332秒 | 39.159秒 |
| 新規GPU確保（常駐重みを含む） | 43,459回 | 1,129回 |

この固定入力の生成50トークンは全ID一致しました。通常の会話品質全体や他の入力での同一性を保証する比較ではありません。
測定データは [benchmarks/r9700-2026-11-20.json](benchmarks/r9700-2026-11-20.json) に保存しています。
同じワークロードを再測定する場合は、GUI のサーバーを停止してから次を実行します（27B をロードします）。

```powershell
.\runtime\native\local-rocm.exe model-bench `
  models/Qwen3.8-27B-Uncensored/Qwen3.8-27B-Uncensored-Q4_K_M.gguf runtime/rocm/bin
```

移行後は通常テスト18件、GPUテスト9件、.NET通信テスト1件が成功しました。
GPUテストは32トークンの行列積と逐次計算・再帰状態・履歴復元の一致を含みます。
実モデルのAPIテストでも計算、日本語、SSE、tool call、usage、生成中のhealth、入力エラーを確認しました。
当時の起動ログは `ROCm bindings: rocmrc / prefill batch 32 / rocBLAS` です。

### 入力処理の追加高速化（2026-11-20）

rocBLAS の最大バッチを32から128トークンへ増やし、同じ重みの展開回数を削減しました。
演算は従来と同じ FP32 です。長い会話では Attention のスコア用領域が32 MiB以下になるよう
バッチを半分ずつ縮小します（1トークンの場合を除く）。27B の131,072コンテキスト付近では2トークンになります。
一時メモリプール全体の上限は64 MiBを維持し、最大32 MiBの領域をキャッシュできます。
必要量の2倍以内で最小の空き領域を再利用し、少し違う形状ごとの再確保を抑えます。

同じ27Bモデル、R9700、131,072コンテキスト容量、入力1,356トークン＋生成50評価を
修正前後それぞれ別プロセスで再測定しました。ロード時間は除外しています。

| 測定 | 32トークン版 | 128トークン版 |
| --- | ---: | ---: |
| 入力処理 | 31.863秒 | 13.797秒 |
| 生成50評価 | 8.193秒 | 8.127秒 |
| 合計 | 40.055秒 | 21.924秒 |

生成50トークンは全ID一致しました。これは固定入力1回ずつの比較です。
詳細は [benchmarks/r9700-prefill128-2026-11-20.json](benchmarks/r9700-prefill128-2026-11-20.json) に保存しています。
起動時は `ROCm bindings: rocmrc / prefill batch 128 / rocBLAS`、要求ごとは `prefill` 行末の `batch` で
現在のサイズを確認できます。usage の `prompt_tokens_details.cached_tokens` は再利用した入力トークン数です。

通常テスト19件、GPUテスト9件、.NET通信テスト1件が成功しました。
GPUテストは7形式の128トークン行列積、および131トークンの逐次計算・バッチ計算・履歴復元の一致を含みます。
実モデルAPIテストでは長い入力の初回処理とキャッシュ復元を追加し、応答一致と再利用数も確認しました。

### llama.cpp との比較と演算の修正（2026-11-20）

同梱 llama.cpp（`0.2.0-dev`, commit `7584430`）と、同じ27B GGUF・R9700・入力1,356トークンの
同一ID列で比較しました。コンテキスト容量131,072、KV は双方 FP16、全層 GPU、greedy、MTP 無効です。
各エンジンを順番に別プロセスで実行し、モデルロード時間を除外しています。

| 測定 | 自前実装・修正前 | 自前実装・修正後 | llama.cpp |
| --- | ---: | ---: | ---: |
| 入力処理 | 14.499秒 | 3.994秒 | 1.397秒 |
| 生成50評価 | 8.119秒 | 2.433秒 | 1.731秒 |
| 合計 | 22.618秒 | 6.428秒 | 約3.13秒 |
| 生成評価/秒 | 6.16 | 20.55 | 28.89 |

llama.cpp は最初のトークンを入力 logits から出すため、51トークン出力で50回の生成評価になります。
自前ベンチは50トークン出力し、50個すべてをモデルに戻して評価します。
llama.cpp の合計は HTTP 要求全体、自前版はモデル内部の時間で、後者には再帰状態のチェックポイント保存も含みます。
バッチは llama.cpp が2048 / microbatch 512、自前版が最大256です。
固定入力の先頭50出力トークンは、修正前・修正後・llama.cpp の全てで一致しました。
これは各1回の固定入力比較で、一般の会話品質や全 logits の同一性を保証する検証ではありません。

修正前の HIP event 計測では、生成の量子化行列積が約147ms/評価を占めていました。
K量子化の行列積を32レーンでブロック単位に読み、レーンごとに8要素を処理するカーネルへ変更しました。
メタデータの読み直しとブロック全体の同期を減らし、波単位の縮約を使います。
入力側は FP32 SGEMM から FP16 入力・FP32 累積の GEMM_EX へ移行し、FP16 展開も量子化ブロック単位にまとめました。
バッチ上限は256、Attention スコアの目標上限は32 MiBを維持しています。
拡大した FFN 一時領域が同時に再利用できるよう、プール総量の上限を128 MiBにしました。
量子化重みの常駐形式・コンテキスト容量・生成の FP32 演算は維持しています。

詳細は [benchmarks/r9700-llama-comparison-2026-11-20.json](benchmarks/r9700-llama-comparison-2026-11-20.json) に保存しています。
llama.cpp の比較は次のスクリプトで再測定できます。GUI を停止してから実行してください。

```powershell
python -X utf8 NativeInference/tests/compare_llama.py
```

GPU 内の時間を演算ごとに調べる場合は、`LOCAL_ROCM_PROFILE=1` を設定して `model-bench` を実行します。
最初の入力バッチ後の1バッチと、最初の生成評価だけを計測します。
イベントを追加する分の測定負荷があるため、通常の性能比較ではこの設定を外してください。
通常のサーバーでは計測イベントを作成しません。

GPUテスト9件で、7形式の行列積を CPU の参照計算と比較しています。
FP16 経路は重みと入力を FP16 に丸めた参照値と比較し、FP32 の累積精度を確認します。
量子化行列ベクトル積は5ブロック幅で、不均等な波の割り当ても確認します。
259トークンの合成モデルでは、逐次計算・バッチ処理・チェックポイント復元を比較しています。

### 継続会話で毎回 `reused 0` になる不具合の修正（2026-11-20）

GUI は思考内容を表示用メタデータとして保存し、次の要求からは除外します。
回答の先頭に残った空行は assistant ヘッダー末尾の改行と BPE で結合されるため、
以前のチェックポイントは文字列では一致してもトークン列では一致せず、毎回リセットされていました。
保存位置を assistant ヘッダーの手前へ移し、そのヘッダーも必要な入力として再計算するようにしました。

従来の同一要求の再送テストと文字列の先頭一致テストでは、この条件を見落としていました。
実 GGUF の BPE を使う GPU 不要の回帰テストで修正前のリセットを再現し、修正後の再利用を確認しています。
空行を含む日本語回答、コードブロック、空行なしの回答を扱います。

```powershell
cargo test --locked --manifest-path NativeInference/Cargo.toml --test cache_tokens -- --ignored
```

実モデル API テストにも、思考ありで生成した回答から `reasoning_content` を取り除いて次の発言を送る
継続会話を追加しました。入力1,035トークンのうち1,012トークンを再利用し、残り23トークンを処理しました。
入力処理は約0.406秒で、同じ履歴を全て再計算した約2.133秒の場合と回答が一致しました。
編集・要約・別の会話などで保存済みの先頭トークン列が変わった場合は、従来どおりリセットします。

### 生成時の Attention の並列化（2026-11-20）

1トークン生成時の Value 集計を128トークンの履歴タイルに分け、部分和を最後にまとめる方式へ変更しました。
従来は query head ごとの少数のブロックが履歴全体を逐次集計していました。
新方式では softmax、タイルごとの集計、部分和の縮約を分け、同じ KV head を参照するブロックを近くに配置します。
履歴長256未満とバッチ入力は従来の経路を使います。生成演算は FP32、KV は FP16 のままです。

R9700、同じ27Bモデル、コンテキスト容量131,072、同じ1,356入力トークンからgreedyで1,000回生成評価しました。
別プロセスで順番に1回ずつ測定し、ロード時間を除外しています。
修正前の逐次集計は `LOCAL_ROCM_TILED_ATTENTION=0` で同じ実行ファイルから選択しました。

| 生成のみ | 逐次集計 | タイル集計 |
| --- | ---: | ---: |
| 1,000評価の時間 | 50.668秒 | 45.949秒 |
| 評価/秒 | 19.74 | 21.76 |

この入力では生成時間が約9.3%短縮し、生成1,000トークンの全IDが一致しました。
FP32 の加算順序は変わるため、全入力の logits や出力の同一性を保証する比較ではありません。
詳細は [benchmarks/r9700-decode-attention-2026-11-20.json](benchmarks/r9700-decode-attention-2026-11-20.json) に保存しています。
起動時の `decode attention: tiled FP32 (128-token tiles)` が有効化の目印です。

GPUテスト10件を確認しました。追加テストでは257・1,400・2,057トークン、非一様なスコア、
4 query heads / 2 KV heads、端数タイルの結果を f64 の CPU 参照計算と比較します。
通常の単体テスト、.NET通信テスト、BPEキャッシュ回帰テストも通過しています。
同じ長さで再測定する場合は GUI のサーバーを停止して次を実行します。

```powershell
.\runtime\native\local-rocm.exe model-bench `
  models/Qwen3.8-27B-Uncensored/Qwen3.8-27B-Uncensored-Q4_K_M.gguf runtime/rocm/bin 1000
```

`model-bench` の最後の引数は生成評価回数（1〜4,096、省略時50）です。
投機生成、出力上限や思考設定の変更による短縮は含みません。

### 生成時の Attention スコア計算（2026-11-23）

1トークンの Attention スコアを32スレッドで分担し、隣接する FP16 キーを連続して読むようにしました。
バッチ入力のスコア計算は従来のままです。浮動小数点の加算順序は変わります。

R9700、27B Q4_K_M、入力1,356トークン、生成50評価の各1回の測定では、HIP event による
最初の生成評価のスコア計算16層合計は5.290 msから3.116 msになりました。一方、イベントなしの
生成全体は6.817秒から6.991秒で、この測定では全体の高速化は確認できませんでした。
計測イベントあり・なしは別プロセスです。固定入力の出力50トークンは一致しましたが、
他の入力での一致や速度向上を保証するものではありません。測定時の全体速度は過去の記録とも異なり、
単発測定のためばらつきの影響を含みます。
条件と結果は [benchmarks/r9700-decode-scores-2026-11-23.json](benchmarks/r9700-decode-scores-2026-11-23.json) に記録しています。
