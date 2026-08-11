# エンドポイントとセッション契約（言語中立）

`AGENTS.md` の不変条件を macOS(Swift) / Windows(C#) 双方の実装が参照できる形へ写したもの。
両実装はこの文書と `shared/fixtures/v1/` を唯一の正本とする。

## 接続先

外部通信先は以下2つだけに限定する。他の翻訳 API を追加しない。

| 用途 | URL | モデル |
|---|---|---|
| 原文 transcription（原文 authority） | `wss://api.openai.com/v1/realtime?intent=transcription` | `gpt-live-transcribe` |
| 翻訳（pair の target ごとに1本） | `wss://api.openai.com/v1/realtime/translations?model=gpt-realtime-translate` | `gpt-realtime-translate` |

選択した言語ペアに対して、source transcription 1本と translation 2本、合計3本の
WebSocket を同時に張る。translation 側の target は pair の2言語の wire 値である。

**2系統は別プロトコルである。** transcription 接続と translations 接続はイベント名も
`session.update` の形状も異なる。以下の章はそれぞれ別に読むこと。

## 認証

- 利用者自身の OpenAI API キー（BYOK）を使う。事業者キーを同梱しない。
- macOS: Keychain / Windows: Credential Manager に保存する。設定ファイルへ平文保存しない。
- ハンドシェイクヘッダは3本とも `Authorization: Bearer <key>` と
  `OpenAI-Safety-Identifier: <安定 ID の SHA-256 hex>` の2つだけ。`OpenAI-Beta` は送らない。
- safety identifier は初回起動時に生成した UUID を永続化し、その SHA-256 を毎回送る。
  UUID 自体は送らない（非 PII の安定識別子）。

## 翻訳接続（`/v1/realtime/translations`、target ごとに1本）

### クライアント → サーバ

| 論理イベント | JSON `type` | payload |
|---|---|---|
| セッション設定 | `session.update` | `session.audio.output.language`、`session.audio.input.transcription.model`、`session.audio.input.noise_reduction.type` |
| 音声追加 | `session.input_audio_buffer.append` | `audio`: base64 の24kHz PCM16 mono LE |
| 終了要求 | `session.close` | なし |

`session.update` の正確な JSON 形状は `shared/fixtures/v1/codec.json` の `encode` ケースが正本。
`output.language` は pair の2言語のいずれかであり、`es` も有効な wire 値である。
`noise_reduction` を無効化する場合はキー省略ではなく `null` を送る。

`response.create`、会話 turn、tool call は使わない。連続音声ストリームとして扱う。

### サーバ → クライアント

| JSON `type` | 論理イベント | 備考 |
|---|---|---|
| `session.created` | SessionCreated | |
| `session.updated` | SessionUpdated | handshake 完了判定に使う |
| `session.input_transcript.delta` | InputTranscriptDelta | `delta` / `event_id` / `elapsed_ms` |
| `session.output_transcript.delta` | OutputTranscriptDelta | 同上 |
| `session.output_audio.delta` | OutputAudioDelta | payload をデコードしないマーカー |
| `session.closed` | SessionClosed | |
| `error` | Error | `error.message` / `error.code` |
| 上記以外 | Unknown(type) | 型名だけ保持して無視する |

翻訳接続の input transcript は source authority として使わない。字幕の原文は下の
source transcription 接続だけを正とする。
不正 JSON は `InvalidMessage` エラーとして扱う（接続は再接続対象）。

## 原文 transcription 接続（`/v1/realtime?intent=transcription`、1本）

### クライアント → サーバ

| 論理イベント | JSON `type` | payload |
|---|---|---|
| セッション設定 | `session.update` | 下記の transcription 形状 |
| 音声追加 | `input_audio_buffer.append` | `audio`: base64 の24kHz PCM16 mono LE |
| 終了要求 | `input_audio_buffer.commit` | なし。`session.close` は送らない |

`session.update` の `session` は翻訳側と別形状である。

```json
{
  "type": "session.update",
  "session": {
    "type": "transcription",
    "audio": {
      "input": {
        "format": { "type": "audio/pcm", "rate": 24000 },
        "transcription": {
          "model": "gpt-live-transcribe",
          "languages": ["ja", "en"],
          "delay": "low",
          "prompt": "<sanitized prompt>",
          "keywords": ["<parsed keywords>"]
        },
        "noise_reduction": { "type": "far_field" },
        "turn_detection": null
      }
    }
  }
}
```

- `languages` は選択した pair の宣言順（`ja-en`、`ja-es`、`en-es`）をそのまま使う。
- `turn_detection` は明示的 `null`。VAD による無音破棄を避ける。
- `noise_reduction` は接続時の値を維持する。録音中の live update では
  `delay` / `prompt` / `keywords` だけを差し替える。

### サーバ → クライアント

| JSON `type` | 扱い |
|---|---|
| `session.created` / `session.updated` | handshake 判定 |
| `conversation.item.input_audio_transcription.delta` | 原文 delta |
| `conversation.item.input_audio_transcription.completed` | commit 完了マーカー |
| `error` | 認証判定後、正規化した文言を流す |
| 上記以外 | 無視 |

原文イベントの lane は `source` であり、translation target を source の識別子に流用しない。
原文 delta は空なら捨て、`event_id` を重複排除に使い、`item_id` は使わない。
原文 delta には `elapsed_ms` が付かず、字幕整列側は `elapsedMs = null` として扱う。

## タイムアウトと再接続

| 対象 | 値 |
|---|---|
| handshake（`session.updated` 待ち） | 15s |
| `session.close` → `session.closed` 待ち | 15s |
| WebSocket `send` | 5s |
| transcription の commit → completed 待ち | 5s |
| 再接続リトライ回数 | 最大5回 |
| 再接続 backoff | 500ms 指数 + jitter 250ms |
| 翻訳送信の連続失敗で epoch 更新 | 3回 |

いずれかの接続が壊れたら3本すべてを再接続し、言語判定をリセットする。
古い epoch の delta は画面へ反映しない。
