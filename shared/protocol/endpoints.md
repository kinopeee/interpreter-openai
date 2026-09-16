# エンドポイントとセッション契約（言語中立）

`AGENTS.md` の不変条件を macOS(Swift) / Windows(C#) 双方の実装が参照できる形へ写したもの。
両実装はこの文書と `shared/fixtures/v1/` を唯一の正本とする。

## 接続先

外部通信先は以下 2 つだけに限定する。他の翻訳 API を追加しない。

| 用途 | URL | モデル |
|---|---|---|
| 原文 transcription（原文 authority） | `wss://api.openai.com/v1/realtime?intent=transcription` | `gpt-live-transcribe` |
| 翻訳（pair の target ごとに 1 本） | `wss://api.openai.com/v1/realtime/translations?model=gpt-realtime-translate` | `gpt-realtime-translate` |

選択した pair に対して source transcription 1 本と translation 2 本、合計 3 本の
WebSocket を同時に張る。翻訳側 target は pair の2言語である。

**2 系統は別プロトコルである。** transcription 接続と translations 接続はイベント名も
`session.update` の形状も異なる。以下の章はそれぞれ別に読むこと。

## 認証

- 利用者自身の OpenAI API キー（BYOK）を使う。事業者キーを同梱しない。
- macOS: Keychain / Windows: Credential Manager に保存する。設定ファイルへ平文保存しない。
- ハンドシェイクヘッダは 3 本とも `Authorization: Bearer <key>` と
  `OpenAI-Safety-Identifier: <安定 ID の SHA-256 hex>` の 2 つだけ。`OpenAI-Beta` は送らない。
- safety identifier は初回起動時に生成した UUID を永続化し、その SHA-256 を毎回送る。
  UUID 自体は送らない（非 PII の安定識別子）。

## 翻訳接続（`/v1/realtime/translations`、target ごとに 1 本）

### クライアント → サーバ

| 論理イベント | JSON `type` | payload |
|---|---|---|
| セッション設定 | `session.update` | `session.audio.output.language`、`session.audio.input.transcription.model`、`session.audio.input.noise_reduction.type` |
| 音声追加 | `session.input_audio_buffer.append` | `audio`: base64 の 24kHz PCM16 mono LE |
| 終了要求 | `session.close` | なし |

`response.create`、会話 turn、tool call は使わない。連続音声ストリームとして扱う。

`session.update` の正確な JSON 形状は `shared/fixtures/v1/codec.json` の `encode` ケースが正本。
特に **`noise_reduction` を無効化する場合は キー省略ではなく `null` を送る**。
`output.language` は pair の2言語のいずれかであり、`es` も有効な wire 値である。

### サーバ → クライアント

| JSON `type` | 論理イベント | 備考 |
|---|---|---|
| `session.created` | SessionCreated | `session.expires_at`（unix 秒）を保持する。下記「セッション期限」参照 |
| `session.updated` | SessionUpdated | handshake 完了判定に使う |
| `session.input_transcript.delta` | InputTranscriptDelta | `delta` / `event_id` / `elapsed_ms` |
| `session.output_transcript.delta` | OutputTranscriptDelta | 同上 |
| `session.output_audio.delta` | OutputAudioDelta | **payload をデコードしない**マーカー |
| `session.closed` | SessionClosed | |
| `error` | Error | `error.message` / `error.code` / `error.type` を**別々に**保持する（フォールバック合成はしない） |
| 上記以外 | Unknown(type) | 型名だけ保持して無視する |

不正 JSON は `InvalidMessage` エラーとして扱う（接続は再接続対象）。

### サーバー `error` の分類

翻訳接続・原文接続とも同じ分類器を使い、`error.type` と `error.code` を許可リストで照合する
（正本: `shared/fixtures/v1/server-error.json`）。サーバーが送る文字列を分類名として信用しない。

| disposition | 条件 | 挙動 |
|---|---|---|
| keepAlive | `code` が `input_audio_buffer_commit_empty` | 接続維持。termination を記録せず、下流へも流さない |
| recover | `code` が `server_error` / `rate_limit_exceeded` / `session_expired`、または `type` が `server_error` / `rate_limit_error` | `recoverableServerError` として既存の再接続へ倒す |
| halt（認証） | 既存の認証判定（`invalid_api_key` 等） | `authenticationFailed`。再接続しない |
| halt（致命） | `insufficient_quota` / `billing_hard_limit_reached`、および許可リスト外すべて | `fatalServerError`。文言は鍵・Authorization を伏せた正規化文言だけ |

終了理由の優先順位は `authenticationFailed > fatalServerError > receiveOverflow > recoverableServerError > transportFailure`。
handshake 中の `error` も同じ分類で扱い、keepAlive は読み飛ばして handshake を続ける。

翻訳接続の `session.input_transcript.delta` は原文 authority として使わない。
字幕の原文は下の transcription 接続だけを正とする。

## 原文 transcription 接続（`/v1/realtime?intent=transcription`、1 本）

### クライアント → サーバ

| 論理イベント | JSON `type` | payload |
|---|---|---|
| セッション設定 | `session.update` | 下記の transcription 形状 |
| 音声追加 | `input_audio_buffer.append` | `audio`: base64 の 24kHz PCM16 mono LE |
| 終了要求 | `input_audio_buffer.commit` | なし。`session.close` は送らない |

`session.update` の `session` は翻訳側と別形状。

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
| `session.created` / `session.updated` | handshake 判定。`session.created` に `session.expires_at` があれば同じ規則で保持する（transcription 側に公式な `expires_at` は未確認のため任意・未検証扱い） |
| `conversation.item.input_audio_transcription.delta` | 原文 delta。`delta` が空なら捨てる。`event_id` を重複排除に使い、`item_id` は使わない（同一 turn で共通のため） |
| `conversation.item.input_audio_transcription.completed` | commit 完了マーカー。close 待ちの解除に使う |
| `error` | 翻訳接続と同じ分類器で扱う。`code` / `type` は原文接続固有の値へ置き換えない |
| 上記以外 | 無視 |

原文イベントの lane は `source` であり、translation target を source の識別子に流用しない。
原文 delta には `elapsed_ms` が付かない。字幕整列側は `elapsedMs = null` として扱う。

## タイムアウトと再接続

| 対象 | 値 |
|---|---|
| handshake（`session.updated` 待ち） | 15s |
| `session.close` → `session.closed` 待ち | 15s |
| WebSocket `send` | 5s |
| transcription の commit → completed 待ち | 5s |
| 再接続リトライ回数 | 最大 5 回（`error.reconnectLimit`） |
| 再接続 backoff | 500ms × 2^(attempt-1)、上限 8s、+ jitter 0–250ms |
| 再接続の総予算 | 連続障害の開始から 120s（`error.reconnectBudgetExhausted`） |
| attempt / 予算のリセット | Listening を 30s 以上維持したあとの失敗だけ |
| 翻訳送信の連続失敗で epoch 更新 | 3 回 |

いずれかの接続が壊れたら 3 本すべてを再接続し、言語判定をリセットする。
古い epoch の delta は画面へ反映しない。

### 再接続予算（`shared/fixtures/v1/reconnect.json` が正本）

- 経過時間は単調クロック（macOS `ContinuousClock` / Windows `TimeProvider.GetTimestamp`）で測る。壁時計の変化は影響しない。
- 1 回の接続試行の上限は handshake タイムアウト（15s）で、backoff・総予算とは独立に数える。
- 失敗のたびに attempt を増やし backoff だけ待つ。attempt が 5 を超えたら `error.reconnectLimit`。
- 連続障害の開始は「最後に Listening を失った失敗」の時刻。失敗時点でそこからの経過が 120s 以上なら `error.reconnectBudgetExhausted`。総予算は attempt 上限より先に判定する。
- Listening に入っただけでは attempt / 予算をリセットしない。短時間で再度落ちる接続を「復旧」と数えないため、Listening を 30s 以上維持したあとの失敗だけが attempt=0・予算開始を作り直す。
- ユーザーの Stop は backoff 待機中でも即時に受け付け、待機後の再接続は走らない。

## セッション期限（expires_at）

`session.created` の `session.expires_at`（unix 秒）を codec が保持する。有効な値は JSON の数値かつ整数で 0 … Int64.max に限る（整数のみ。浮動小数点表現で届いた値は 2^53 以下かつ整数のものだけを受け付ける）。欠落・null・文字列・真偽値・負数・小数・非有限値はすべて「不明」として nil/null を記録し、codec エラーにはしない。

期限までの残り時間 `remaining = expires_at − 壁時計` はセッションが Listening に入った時点で一度だけ算出し、以後は単調時計で追う。壁時計の途中補正（NTP 等）は検知時刻にも残りにも影響しない。

`expiryNear`（残り 120s 以下）と `expired`（残り 0 以下）はいずれも診断のみで、再接続や lane 変更を起こさない。`session.updated` の `expires_at` は今回は使わない。

## 受信停止監視（診断のみ）

接続の健全性を観測する SessionHealthMonitor を持つ。閾値の正本は `shared/fixtures/v1/health.json` の `thresholds`、判定系列の契約は同ファイルの `scenarios`。

- 位相: `idle` → `catchingUp`（再接続直後の猶予中かつ原文進捗なし）→ `silence`（音声活動なし）→ `awaitingLanguageDetection`（lane 未選択）→ `active`。
- 検知 kind: `captureStalled`（frame が来ない、grace 対象外）、`sendStalled`（frame は来るが送信成功がない）、`receiveStalled`（直前受信以後の最初の音声活動からの経過）、`sourceStalled`（transport は生存するが原文進捗がない）、`translationStalled`（選択 lane の翻訳進捗が原文進捗に追従しない）、`expiryNear` / `expired`（上記の期限検知）。
- transport の生存判定は「decode できた受信メッセージすべて」を数える。WebSocket の ping/pong は観測しない。
- 検知は世代内 `(kind, lane)` ごとに 1 回だけ発火し、`beginGeneration`（再接続ごとの世代開始）で全状態をリセットする。検知から再接続・lane 変更・文言表示は一切起こさない（診断のみ）。
- macOS は DEBUG ビルドのみ `DBG_HEALTH` / `DBG_HEALTH_SNAPSHOT`（5 s ごと）/ `DBG_HEALTH_TERMINATION` を notice ログへ出し、status file の3行目に `health=<kind> gen=<n> lane=<lane|->` を書く。Windows は `HealthDetected` イベントと `LatestHealthSnapshot` を公開する。いずれも数値と enum 名だけで、APIキー・音声・原文・訳文・サーバー生文言は含まない。
