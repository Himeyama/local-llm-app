# Rust 版と llama.cpp の比較・作業記録

最終更新: 2026-11-26。次のセッションでの再開用です。

## 目的と現状

同梱の独立 Rust / ROCm 推論サーバーを、本家 llama.cpp に近い演算方式にし、同等の速度を目指す。比較対象は途中で動かないよう、公式 **llama.cpp b12751 / commit 532d67426** に固定している。GPU は AMD Radeon AI PRO R9700、モデルは `Qwen3.8-27B-Uncensored-Q4_K_M.gguf`。

**目標は未達。** 今回は K 量子化の生成時行列積と prefill バッチの実験経路を追加し、速度を改善した。GUI の通常起動では両方とも無効のまま。MTP は未実装で、本家との全層の出力一致・長文品質も未検証。

## 実測結果

同じ GGUF、入力トークン列、コンテキスト容量131,072、FP16 KV、greedy、投機生成なし。ロード時間を除く。Rust の入力処理には再帰状態チェックポイント保存を含む。llama.cpp はマイクロバッチ512、Rust の通常は最大256。生成は Rust の50評価と llama.cpp の51出力に含まれる50評価を比較した。

| 1,356入力トークン | Rust 通常 | Rust 実験経路 | llama.cpp b12751 |
| --- | ---: | ---: | ---: |
| 入力処理、3回 | 3.320 / 3.501 / 3.353 秒 | 3.129 / 3.132 / 3.124 秒 | 1.275 / 1.231 / 1.232 秒 |
| 生成、3回 | 22.38 / 22.12 / 22.16 トークン/秒 | 27.48 / 27.61 / 27.48 トークン/秒 | 31.99 / 31.87 / 31.86 トークン/秒 |

Rust 実験経路は Q8_1 と最大512トークン prefill を有効化。固定入力の先頭50生成トークンは全試行で一致した。llama.cpp の値は通常版測定時の3回で、実験版測定時も約31.7〜32.0トークン/秒。これらは特定の短い反復入力での比較であり、一般の文章品質や全 logits の一致を保証しない。

8,192入力トークン・各1回では Rust 実験版の入力処理 **33.23秒**、本家 **6.97秒**。先頭50生成トークンは一致。長文の速度差が大きく、優先課題として残っている。単発測定のため統計的な精度には限界がある。

Q8_1 追加後のトップ候補確率も固定入力で測定した。Q4_K のみ有効にした段階では、本家とのトップ候補確率の絶対差の平均は約0.00027、最大約0.00276（50位置）。Q5_K・Q6_K 追加後の確率はまだ未比較。これらは確率の差であり logits 自体の絶対差ではない。

## 変更内容と使い方

- `NativeInference/src/kernels.hip`、`src/rocm.rs`、`src/model.rs`: 本家 b12751 の Q8_1 量子化と K 量子化の整数積和に基づく Q4_K / Q5_K / Q6_K の生成時実験経路。同じ入力から複数の射影を作るときは変換結果をその入力の寿命内だけ共有する。初期値は無効。浮動小数点の丸めや量子化が変わるため、元の Rust 版と常に同じ出力になる保証はない。
- `src/rocm.rs`、`src/rocm/ops.rs`: rocBLAS を利用できる場合に限り、最大512トークンの prefill を使う実験経路。64 MiB の Attention スコア領域目標に応じてバッチを縮小する。初期値は無効。
- `NativeInference/tests/compare_b12751.py`: Rust が実際に処理したトークン ID を本家へ送り、両者を順番に測る。`--prompt-tokens`、`--runs`、`--logits`、`--experimental-q8`、`--experimental-prefill512` を指定できる。`--logits` は確率計算の負荷を含むため速度比較には使わない。

実験版を明示して起動する例。既存のサーバーは先に停止する。

```powershell
$env:LOCAL_ROCM_EXPERIMENTAL_Q8_MATVEC = '1'
$env:LOCAL_ROCM_EXPERIMENTAL_PREFILL_512 = '1'
.\Start-RocmServer.ps1
```

同じ PowerShell の後続起動では環境変数が残る。通常版へ戻す場合は `Remove-Item Env:LOCAL_ROCM_EXPERIMENTAL_Q8_MATVEC, Env:LOCAL_ROCM_EXPERIMENTAL_PREFILL_512` を実行する。GUI のプロセスから起動する場合は、そのプロセスに環境変数が渡る必要がある。

比較例。公式バイナリと ROCm 10 開発 SDK はそれぞれ `runtime/llama-b12751`、`runtime/rocm10-build-env` にあるが、`runtime/` とモデルは Git 管理外。別環境では `--llama`、`--llama-dll-dir`、`--model` などを指定する。

```powershell
python -X utf8 NativeInference/tests/compare_b12751.py --native NativeInference/target/release/local-rocm.exe --experimental-q8 --experimental-prefill512
python -X utf8 NativeInference/tests/compare_b12751.py --native NativeInference/target/release/local-rocm.exe --experimental-q8 --experimental-prefill512 --prompt-tokens 8192 --runs 1
```

既定の出力は `NativeInference/target/b12751-parity.json`。固定入力のトークン ID、両エンジンの時間、生成結果を保存する。測定時の全出力は Git 管理外の `NativeInference/target/b12751-*-parity.json` にある。

## 検証と次回の優先事項

- Q4_K / Q5_K / Q6_K × Q8_1 は、複数の行列幅・ゼロを含む入力について GPU と CPU 参照を比較した。512 prefill は合成モデルで逐次処理・チェックポイント復元と比較した。
- 最終版の両実験経路を有効にした実モデル API で、日本語、計算、継続会話、履歴再利用、SSE、ツール呼び出しを確認した。
- 長文後半の GPU プロファイルでは Attention と重みの FP16 展開・行列積が支配的。既存の Attention 並列化を位置2,048から使う案は、8,192入力で33.23秒から35.51秒に悪化したため破棄した。
- 次回は異なる日本語・コード入力と長い会話で、本家との候補確率や各層の中間値を比較する。現在の `--logits` は各生成位置の上位5候補に限る。
- 長文 prefill の行列積・Attention を本家の方式と照合し、GPU 単体テスト、実モデル比較、メモリ使用量を確認しながら改善する。
- Q8_1 経路は Q4_K / Q5_K / Q6_K に限る。Q6_K は同じ量子化方式だが本家と同じスレッド配置・加算順ではない。全形式、全 GPU、全出力の同一性は未検証。
- 通常生成と状態復元の検証後に、本家 Qwen MTP の重み、隠れ状態、KV・再帰状態の巻き戻しと投機検証を移植する。Rust 版は現在 MTP 未実装。
- ネイティブ推論サーバーを変更したら、リリースビルドをリポジトリとインストール先の両方に配置し、ハッシュ一致を確認する。詳細は [AGENTS.md](AGENTS.md)。

2026-11-26 の最終リリースビルドは、`NativeInference/target/release/local-rocm.exe`、`runtime/native/local-rocm.exe`、インストール先 `C:\Users\hikari\AppData\Local\Programs\LocalLlmGui\app\runtime\native\local-rocm.exe` の SHA-256 がいずれも `630CEBBBEF8E9E3A7AE11972B458ECFFE10E6270224AE73BF446941234824087`。利用中のサーバーを再起動すると反映される。

本家自体の RDNA4 カーネル実験と、丸め差による出力分岐の調査は [tools/llama-rocm-tuning/README.md](tools/llama-rocm-tuning/README.md) にある。
