# 音声パイプライン契約（言語中立）

## 送信フォーマット

| 項目 | 値 |
|---|---|
| サンプリングレート | 24,000 Hz |
| ビット深度 | 16bit signed PCM |
| チャンネル | mono |
| バイトオーダー | little-endian |
| フレーム長 | 100 ms = 2,400 samples = **4,800 bytes** |
| 転送表現 | base64 |

録音中は無音フレームも送り続ける。VAD で無音を捨てない。

## パケット化

`Pcm16FramePacketizer` の不変条件:

- `Append(pcm)` は 4,800 バイト境界で切り出したフレーム列を返し、端数を内部に保持する。
- `FlushWithSilencePadding()` は端数を無音（0x00）で 4,800 バイトへ padding して 1 フレーム返す。端数が無ければ `null`。
- 4,800 バイトを超える端数が残ることはないが、超えた場合は先頭 4,800 バイトへ切り詰める。
- `Reset()` は端数を破棄する。

期待値は `shared/fixtures/v2/audio.json` の `packetizer` ケースが正本。

## Float32 → PCM16 変換

`sample * gain` を `[-1.0, 1.0]` へクリップし、`Int16.MaxValue` (32767) 倍して**四捨五入**（round-half-away-from-zero）する。

## 適応マイクゲイン

v2 から、ゲインはリサンプル後の Float32 を **100 ms（2,400 samples）単位**に区切ってから決める。入力バッファの長さに依存せず、両実装で時間定数が一致する。停止時の端数は無音（0.0）で 2,400 samples へ padding してから処理する。

| 定数 | 値 | 意味 |
|---|---|---|
| frameSamples | 2400 | ゲインを決める単位（100 ms） |
| minimumGain / maximumGain | 1.0 / 8.0 | ゲインの範囲 |
| defaultInitialGain | 4.0 | 録音開始時の持続ゲイン |
| targetRms | 0.1 | 発話フレームの目標 RMS（約 -20 dBFS） |
| speechRatio | 3.16 | 雑音フロアより約 +10 dB 以上なら発話 |
| speechAbsoluteFloor | 0.003 | これ未満の RMS は発話にしない（約 -50 dBFS） |
| digitalSilenceRms | 0.00001 | これ未満の RMS は雑音窓へ入れない（ミュート等のデジタル無音） |
| noiseWindowFrames | 30 | 雑音フロアを求める窓（直近 3 秒） |
| gainRiseFactor / gainFallFactor | 1.12 / 0.8 | 1 フレームあたりの上昇・下降の上限（約 +1 dB / -2 dB） |
| clipCeiling | 0.9 | フレーム内リミッタの目標ピーク |
| rampSamples | 120 | 適用ゲインを切り替える線形ランプ（5 ms） |

状態は、持続ゲイン `gain`、直前フレームの適用ゲイン `appliedGain`（初期値は `gain`）、直近の RMS を保持する雑音窓の 3 つ。`clamp` は `[minimumGain, maximumGain]` への丸めで、非有限値は `minimumGain` にする。初期ゲインは `clamp` する。

フレームの `rms` と `peak`（絶対値の最大）は有限なサンプルだけから求める。有限なサンプルが無い、または空のフレームは `rms = peak = 0`。

1 フレームの処理:

1. `rms` か `peak` が有限でなければ、状態を変えずに直前の `appliedGain` を返す。負の値は 0 として扱う。
2. `rms >= digitalSilenceRms` なら `rms` を雑音窓へ追加する。窓が `noiseWindowFrames` を超えたら最古を捨てる。
3. 雑音窓が空でなく、`rms >= speechAbsoluteFloor` かつ `rms >= min(雑音窓) * speechRatio` なら発話フレーム。現在フレームも窓に含むため、最初のフレームは発話にならない。
4. 発話フレームだけ `gain` を動かす。`desired = clamp(targetRms / rms)`。
   - `desired > gain` なら `gain = clamp(min(desired, gain * gainRiseFactor))`
   - `desired < gain` なら `gain = clamp(max(desired, gain * gainFallFactor))`
   - 非発話フレームでは `gain` を変えない（雑音だけでゲインを上げない）。
5. `appliedGain = clamp(peak > 0 ? min(gain, clipCeiling / peak) : gain)`。リミッタはそのフレームだけに効き、`gain` を変えない（クリック音で持続ゲインを下げない）。
6. サンプル `i`（0 始まり）に掛けるゲインは、`i < rampSamples` なら `previous + (applied - previous) * ((i + 1) / rampSamples)`、それ以外は `applied`。`previous` はこのフレームの処理前の `appliedGain`。その後は「Float32 → PCM16 変換」の規則で変換する。

自動ゲインを無効にした場合は、`gain` と `appliedGain` を常に 1.0 とし、状態を更新しない。設定の反映は次の録音開始から。

期待値は `shared/fixtures/v2/audio.json` の `gain`（`cases` / `level` / `ramp`）が正本。v1 の `gain`（ピーク追跡方式）は履歴として残し、実装は v2 に従う。

## 実装側の並行性要求

- キャプチャコールバック（macOS: AVAudioEngine tap / Windows: WASAPI）はバッファをキューへコピーするだけにする。
- downmix / リサンプル / 100ms の Float32 フレーム化 / gain / PCM16 変換 / パケット化は**単一の feeder タスク**から直列に呼ぶ。
- 送信キューは bounded にし、単一 writer から送る。

## 送信前の欠落検知

送信前の音声欠落は、変換前バッファと送信キューを別々に観測する。
`shared/fixtures/v2/audio.json` の `loss` が両実装の正本である。

- フレームには capture lifecycle の `generation`、世代内の 0 始まり `sequence`、
  変換前バッファの累積破棄時間 `discardedMs` を付ける。
- 連番の欠番は送信キューの drop として `欠番 * 100ms` を数える。
  `discardedMs` は capture 側が数えた変換前バッファの累積値との差分だけを数える。
  両者は別区間なので二重計上しない。
- 世代の最初のフレームでは 0 からの欠番を数え、`discardedMs` は 0 を基準に数える。
  世代変更時は連番と discard の基準をリセットする。
- 欠落を観測したフレームでは未確定字幕を破棄し、欠落後の汚染された字幕を確定・記録しない。
  欠落前に確定済みの字幕は保持する。
- 30 秒窓内の欠落合計が 6,400ms 以上になった場合だけ `ReconnectBudget` を通じて再接続する。
  3,200ms の単発 drop では再接続しない。再接続を返したら欠落イベント窓を消去する。
- 診断ログ・status は音声・原文・訳文を含めず、件数・時間・待機時間などの数値だけにする。
