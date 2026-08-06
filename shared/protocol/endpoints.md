# エンドポイントとセッション契約（言語中立）

`AGENTS.md` の不変条件を macOS(Swift) / Windows(C#) 双方の実装が参照できる形へ写したもの。
両実装はこの文書と `shared/fixtures/v1/` を唯一の正本とする。

## 接続先

外部通信先は以下 2 つだけに限定する。他の翻訳 API を追加しない。

| 用途 | URL | モデル |
|---|---|---|
| 原文 transcription（原文 authority） | `wss://api.openai.com/v1/realtime?intent=transcription` | `gpt-live-transcribe` |
| 翻訳（target 別に 2 本） | `wss://api.openai.com/v1/realtime/translations?model=gpt-realtime-translate` | `gpt-realtime-translate` |

合計 3 本の WebSocket を同時に張る。翻訳側は `target=en` と `target=ja` の 2 セッション。

## 認証

- 利用者自身の OpenAI API キー（BYOK）を使う。事業者キーを同梱しない。
- macOS: Keychain / Windows: Credential Manager に保存する。設定ファイルへ平文保存しない。
- ハンドシェイクヘッダは `Authorization: Bearer <key>` と `OpenAI-Beta: realtime=v1`。

## クライアント → サーバのイベント

| 論理イベント | JSON `type` | payload |
|---|---|---|
| セッション設定 | `session.update` | `session.audio.output.language`、`session.audio.input.transcription.model`、`session.audio.input.noise_reduction.type` |
| 音声追加 | `session.input_audio_buffer.append` | `audio`: base64 の 24kHz PCM16 mono LE |
| 終了要求 | `session.close` | なし |

`response.create`、会話 turn、tool call は使わない。連続音声ストリームとして扱う。

`session.update` の正確な JSON 形状は `shared/fixtures/v1/codec.json` の `encode` ケースが正本。
特に **`noise_reduction` を無効化する場合は キー省略ではなく `null` を送る**（fixture `session_update_english_no_noise` 参照）。

## サーバ → クライアントのイベント

| JSON `type` | 論理イベント | 備考 |
|---|---|---|
| `session.created` | SessionCreated | |
| `session.updated` | SessionUpdated | handshake 完了判定に使う |
| `session.input_transcript.delta` | InputTranscriptDelta | `delta` / `event_id` / `elapsed_ms` |
| `session.output_transcript.delta` | OutputTranscriptDelta | 同上 |
| `session.output_audio.delta` | OutputAudioDelta | **payload をデコードしない**マーカー |
| `session.closed` | SessionClosed | |
| `error` | Error | `error.message` / `error.code`（`error.type` へフォールバック） |
| 上記以外 | Unknown(type) | 型名だけ保持して無視する |

不正 JSON は `InvalidMessage` エラーとして扱う（接続は再接続対象）。

## タイムアウトと再接続

| 対象 | 値 |
|---|---|
| handshake（`session.updated` 待ち） | 15s |
| `session.close` → `session.closed` 待ち | 15s |
| WebSocket `send` | 5s |
| transcription の commit → completed 待ち | 5s |
| 再接続リトライ回数 | 最大 5 回 |
| 再接続 backoff | 500ms 指数 + jitter 250ms |
| 翻訳送信の連続失敗で epoch 更新 | 3 回 |

いずれかの接続が壊れたら 3 本すべてを再接続し、言語判定をリセットする。
古い epoch の delta は画面へ反映しない。
