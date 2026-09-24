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

## 100 ms float フレーム化

変換後の float サンプル列は PCM16 化する前に 2,400 サンプル単位へ分割する。
`Float32FrameAccumulator`（macOS: `Float32FrameAccumulator` / Windows: 同名クラス）の不変条件:

- `Append(samples)` は 2,400 サンプルちょうどのフレーム列を返し、端数を内部に保持する。
- `FlushWithSilencePadding()` は端数を 0.0 で 2,400 サンプルへ padding して 1 フレーム返す。端数が無ければ `nil` / `null`。
- `Reset()` は端数を破棄する。

feeder の順序は 変換 → float フレーム化 → 各フレームで AGC → PCM16 → packetizer へ渡す。
停止時は端数を無音 padding した 1 フレームを同じ経路で処理してから終了する。

期待値は `shared/fixtures/v2/audio.json` の `float32Framing` ケースが正本。

## Float32 → PCM16 変換

`sample * gain` を `[-1.0, 1.0]` へクリップし、`Int16.MaxValue` (32767) 倍して**四捨五入**（round-half-away-from-zero）する。NaN は 0、±Infinity はクリップ規則により ±32767 になる。

## 適応マイクゲイン（v2: RMS ベース）

| 定数 | 値 |
|---|---|
| フレームサンプル数 | 2,400 |
| 最小ゲイン | 1.0 |
| 最大ゲイン | 8.0 |
| 初期ゲイン | 4.0 |
| 目標 RMS | 0.1 |
| 発話判定比 | 3.16 |
| 発話絶対フロア | 0.003 |
| ノイズ上限 | 0.01 |
| ノイズフロア下限 | 0.0001 |
| ノイズフロア上昇率 | 1.01 |
| ゲイン上昇率 | 1.12 |
| ゲイン下降率 | 0.8 |
| クリップ天井 | 0.9 |
| ランプサンプル数 | 120 |

状態: `gain`（持続）、`appliedGain`（直前の適用ゲイン）、`noiseFloor`（未観測は空）、`isEnabled`。
初期化: `gain = clamp(initialGain)`（非有限なら最小ゲイン。Windows 版は従来どおり `ArgumentOutOfRangeException`）、`appliedGain = isEnabled ? gain : 1.0`。
`clamp(x) = min(maximumGain, max(minimumGain, x))`。

### フレーム統計

`frameStatistics(samples)` は非有限サンプルを除外し、有限サンプルが 0 個なら `(NaN, NaN)` を返す。
`rms = sqrt(Σx² / count)`、`peak = max(|x|)`。累積は両実装とも `Double` 相当で行う。

### observe(rms, peak)

1. `isEnabled == false` → `1.0` を返し、状態を変えない（ゲインは 1.0 素通し）。
2. `rms` または `peak` が非有限 → 現在の `appliedGain` を返し、状態を変えない。
3. `rms = max(0, rms)`、`peak = max(0, peak)`。
4. `floored = max(rms, 0.0001)`。`noiseFloor` 未観測または `floored < noiseFloor` → `noiseFloor = floored`。それ以外 → `noiseFloor = min(noiseFloor * 1.01, floored)`。
   `noiseFloor` は下限 0.0001 を下回らないため、デジタル無音で 0 に固定されることはない。
5. `noiseCap = clamp(0.01 / noiseFloor)`。
6. `isSpeech = rms >= 0.003 && rms >= noiseFloor * 3.16`。
7. 発話時: `desired = min(clamp(0.1 / rms), noiseCap)`。
   - `desired > gain` → `gain = min(desired, gain * 1.12)`（フレームあたり最大 12% の上昇）。
   - `desired < gain` → `gain = max(desired, gain * 0.8)`。
   非発話時: `gain > noiseCap` なら `gain = max(noiseCap, gain * 0.8)`。それ以外は不変。
8. `applied = peak > 0 ? min(gain, 0.9 / peak) : gain`。`appliedGain = clamp(applied)` を返す。
   クリップ limiter は当該フレームの適用ゲインだけを下げ、持続する `gain` は変えない。

### ランプ付き PCM16 化

`encodePCM16(previousAppliedGain, appliedGain, samples, peak)` はフレーム先頭 `rampSamples`（120）サンプルで
`previous` から `current` へ線形にゲインを遷移させ、ゲイン不連続のクリック音を防ぐ。

- `i < 120`: `g = previous + (current - previous) * ((i + 1) / 120)`。`i >= 120`: `g = current`。
- `peak > 0` のときフレーム共通の上限 `limit = max(minimumGain, 0.9 / peak)` を計算し、`g = min(g, limit)`。
  `limit` は `maximumGain` では clamp せず、`peak > 0.9` なら `limit = 1.0`（下限 `minimumGain`）になる。
  これにより大音量フレームではランプ先頭の高いゲインでもクリップしない。
- 各サンプルは上記の Float32 → PCM16 変換規則で `g` を掛けてから符号化する。

便宜 API `process(samples)`: `frameStatistics` → `observe` → ランプ付き PCM16 化を一括して行う（`peak` は `frameStatistics` の値をそのまま渡す）。
disabled の場合は previous / applied とも 1.0 で平坦（ゲイン 1.0 素通し）になる。

期待値は `shared/fixtures/v2/audio.json` の `gain`（定数・cases・ramp）と `frameStatistics` が正本。

## 実装側の並行性要求

- キャプチャコールバック（macOS: AVAudioEngine tap / Windows: WASAPI）はバッファをキューへコピーするだけにする。
- downmix / リサンプル / gain / PCM16 変換 / 100ms パケット化は**単一の feeder タスク**から直列に呼ぶ。
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
