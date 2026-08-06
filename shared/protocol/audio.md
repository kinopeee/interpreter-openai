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

期待値は `shared/fixtures/v1/audio.json` の `packetizer` ケースが正本。

## Float32 → PCM16 変換

`sample * gain` を `[-1.0, 1.0]` へクリップし、`Int16.MaxValue` (32767) 倍して**四捨五入**（round-half-away-from-zero）する。

## 適応マイクゲイン

| 定数 | 値 |
|---|---|
| 最小ゲイン | 1.0 |
| 最大ゲイン | 8.0 |
| 目標ピーク | 0.5（約 -6 dBFS） |
| 無音フロア | 0.005 |
| クリップ閾値 | 0.95 |
| 初期ゲイン | 4.0 |

ピーク追跡: 新しいピークが追跡値以上なら即反映、下回るなら `tracked*0.9 + peak*0.1`。

1. `tracked * gain >= 0.95` かつ `tracked > 0` → fast attack。`gain = clamp(min(gain, 0.5/tracked))` を返して終了。
2. `tracked < 0.005` → 無音。ゲインを動かさない。
3. それ以外 → `desired = clamp(0.5/tracked)`。
   - `desired > gain` なら slow release: `gain = clamp(min(desired, gain*1.05))`
   - `desired < gain` なら `gain = clamp(max(desired, gain*0.85))`

期待値は `shared/fixtures/v1/audio.json` の `gain` ケースが正本。

## 実装側の並行性要求

- キャプチャコールバック（macOS: AVAudioEngine tap / Windows: WASAPI）はバッファをキューへコピーするだけにする。
- downmix / リサンプル / gain / PCM16 変換 / 100ms パケット化は**単一の feeder タスク**から直列に呼ぶ。
- 送信キューは bounded にし、単一 writer から送る。
