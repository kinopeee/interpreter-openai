# RealtimeTranslator.AgcBench

AGC バリアント (off / v1 ピーク追跡 / v2 現行 RMS) を同じ WAV コーパスへ適用し、
処理後 PCM・ゲイン trace・transcription 結果を比較する評価専用 console ツール。
App からは参照しない。Linux 上で `compose` / `process` / `report` までは決定的に動作し、
`transcribe` だけが実 API (`OPENAI_API_KEY`) を使う。

## コマンド

```bash
agc-bench compose    --scenarios <scenarios.json> --out <corpusDir>
agc-bench process    --corpus <corpusDir> --out <processedDir> [--variants off,v1,v2]
agc-bench transcribe --processed <processedDir> --out <resultsDir> [--runs 3] [--variants off,v1,v2] [--clips id1,id2]
agc-bench report     --processed <processedDir> --results <resultsDir> --out <report.md>
```

exit code: 成功 0 / 引数誤り 2 / 実行失敗 1。

## compose

`scenarios.json` からコーパス WAV (24 kHz PCM16 mono) と `corpus.json` を生成する。
`seed` 固定で完全に決定的。

```json
{ "sampleRate": 24000,
  "scenarios": [ { "id": "small-voice-ja", "base": "base/ja-1.wav", "language": "ja", "pair": "ja-en",
                   "reference": "期待される原文",
                   "gainDb": -20, "leadingSilenceMs": 500, "trailingSilenceMs": 1000,
                   "noise": { "kind": "white", "rms": 0.004, "dipEveryMs": 2000, "dipMs": 100, "dipRms": 0.001 },
                   "clicks": [ { "atMs": 1200, "peak": 0.95, "durationMs": 2 } ],
                   "seed": 1 } ] }
```

- 出力 = 先頭無音 + `base × 10^(gainDb/20)` + 末尾無音、その上にノイズとクリックを加算。
- `noise.rms` は生成ノイズの実 RMS に一致するよう正規化する。`dipEveryMs` ごとの `dipMs` 窓は `dipRms`。
- `base` / `noise` / `clicks` は省略可。`base` 省略時はノイズのみクリップになり `expectSilence: true` が書かれる。
- `speechOnsetMs` = `leadingSilenceMs`。base パスは scenarios.json からの相対。

`corpus.json` は手で編集してよい:

```json
{ "sampleRate": 24000,
  "clips": [ { "id": "small-voice-ja", "file": "small-voice-ja.wav", "language": "ja", "pair": "ja-en",
               "reference": "期待される原文", "speechOnsetMs": 500, "expectSilence": false } ] }
```

`language`: `ja` | `en` | `es`、`pair`: `ja-en` | `ja-es` | `en-es`。

## process

各クリップ × バリアントで 100 ms (2400 サンプル) フレーム処理し、以下を出力する。

- `<id>.<variant>.wav`: 処理後 PCM16 24 kHz mono (末尾フレームは zero-padding 込み)。
- `<id>.<variant>.trace.csv`: `frame,inRms,inPeak,gain,appliedGain,outPeak,clippedSamples`。
- `process-summary.json`: `{ clip, variant, frames, maxGain, minGain, framesAtMaxGain, clippedSamples, speechOutRms }`。
  `speechOutRms` は `speechOnsetMs` 以降のフレームのみ対象。
- `corpus.json` を processedDir へコピーする。

`process --out` ディレクトリは process が占有し、実行時に既存の `*.wav` / `*.trace.csv` をすべて削除してから書き直す。

バリアント:

- `off`: 素通し (gain 1)。
- `v1`: #161 以前のピーク追跡 `AdaptiveMicrophoneGain` の複写 (`Variants/LegacyPeakGain.cs`)。
  実機ではプラットフォーム依存チャンク長だったが、bench では 100 ms フレームで再生する。
  適用順序は実機と同じ「同一バッファで Observe した gain をそのバッファへ即適用」。
- `v2`: 現行 `AdaptiveMicrophoneGain` (RMS + ランプ + リミッタ)。

## transcribe

実 API へ送るため実行料金がかかる。利用者自身のキーを `OPENAI_API_KEY` 環境変数で渡す
(未設定なら usage error)。run は逐次実行、送信は実時間 pacing (100 ms/frame)。

送信対象は processedDir 内 `process-summary.json` に実績のある (clip, variant) ペア。
`--variants` を明示したとき summary に無いペアは usage error で全失敗、既定では
`skipped <clip>.<variant>` と表示して進む。`--clips` に corpus 外の id を渡しても
usage error。各 run の JSON には handshake 時間 `connectMs` も記録する。

結果は `<id>.<variant>.run<N>.json` に `{ clip, variant, run, transcript, firstDeltaMs,
firstDeltaFromOnsetMs, sentMs, error }` として保存する。**評価用音声の transcript を
結果 JSON に書くのは意図された動作**であり、代わりに stdout へは transcript 本文を出さない。

## report

`process-summary.json` と結果 JSON からクリップ別比較表 (markdown) を出す。
誤り率は ja=CER / en・es=WER、無音クリップは `falseSubtitleChars`、初回字幕までの
遅延は `firstDeltaMs − speechOnsetMs` の統計。エラー終了した run は統計から除外し
`runs with error` (N/M) だけに数える。結果が無い組は `–` で埋まる。

## ベースクリップの作り方

実マイク録音でも TTS でもよい。24 kHz mono WAV へ変換して `scenarios.json` の `base` に置く。

```bash
# macOS (TTS)
say -o base.aiff "文"
afconvert -f WAVE -d LEI16@24000 -c 1 base.aiff base.wav

# 任意の OS (ffmpeg)
ffmpeg -i in.wav -ac 1 -ar 24000 -sample_fmt s16 base.wav
```

## CI 範囲

`ci-windows-core.sh` は compose/process/report/metrics と CLI 引数のテストのみを実行する。
`transcribe` (実 API) は CI では実行しない。
