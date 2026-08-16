# UI 表示言語の契約と実装プラン

翻訳ペア（`languagePair`）とは独立に、アプリ枠の表示言語を切り替える。
初回対象は **ja / en**。切替は **再起動後に反映**。スペイン語 UI と実行時切替は対象外。

この文書は契約（両実装が守る不変条件）と、実装時の作業手順・注意・制限を兼ねる。
実装は **この文書と `shared/locales/ui.json` を先に置き、その後に各実装を合わせる**。

## 1. 決定事項（ロック済み）

| 項目 | 値 |
| --- | --- |
| 対象ロケール | `ja` / `en` のみ |
| 既定の選択 | `system`（OS の UI 言語に追従） |
| `system` の解決 | OS が日本語なら `ja`、それ以外はすべて `en`（スペイン語 OS も英語 UI） |
| 上書き | 設定の表示言語 Picker（`system` / `ja` / `en`） |
| 反映タイミング | **プロセス再起動後**。起動中にメニュー・バナー・設定ウィンドウを差し替えない |
| 自動再起動 | しない（多重起動防止と session 排水を複雑にするため） |
| 同意バージョン | 同意の意味が変わらなければ `CurrentConsentVersion` は上げない |

保存値 `uiLanguage` は `languagePair` と混ぜない。

- `languagePair`: 音声の翻訳方向（`ja-en` / `ja-es` / `en-es`）。次回録音開始で反映（現行どおり）
- `uiLanguage`: アプリ枠の文言。次回プロセス起動で反映

## 2. 目的と非目的

### 目的

設定・メニュー / トレイ・ダイアログ・オーバーレイ枠（バナーと録音ボタン）・ユーザー向けエラーを ja/en で出す。
両実装の文言キーと意味を一致させる。

### 非目的

- 字幕の原文・訳文を訳すこと
- 認識ヒント（prompt / keywords）を UI 言語に合わせること
- 字幕記録ファイル（`session.txt`）のラベルをロケールすること
- 開発者ログ・テストコメント・製品名の翻訳
- スペイン語 UI
- 起動中の即時切替
- OS 標準ダイアログ（マイク許可、保存パネルの Cancel 等）をアプリ設定に従わせること

## 3. 不変条件

1. 外部通信先・音声経路・字幕レーン選択は変えない。
2. API キー、Authorization、音声、原文、訳文をログ・status・アラート・通知へ出さない。ローカライズ後も同じ。
3. サーバー生 message の正規化条件（`sk-` / `api key` / `authorization` / `bearer` + 半角スペース）は言語非依存のまま。差し替え先の固定文言だけがロケールされる。
4. 字幕記録のプレーンテキスト形式は現行どおり固定する。

```text
=== 録音開始 {timestamp}

--- {timestamp}
原文: {source}
訳文: {translated}
```

既存ファイルと `shared/fixtures/v1/transcript.json` の互換を壊さない。UI が英語でもこのラベルは日本語のまま。

5. ホットキー表記そのものは訳さない。macOS は `Control + Option + Space`、Windows は `Ctrl + Alt + Space`。バナーへ埋め込む。
6. 製品名は `Realtime Translator` のまま。
7. `InvariantGlobalization=false`（WPF）は維持する。true に戻すと起動時に落ちる。
8. Swift の production はリポジトリ上の `shared/` を実行時パスとして読まない。カタログはビルド時にアプリバンドルへコピーする。

## 4. 制限事項（できないこと・やらないこと）

### OS が描画する文言は `uiLanguage` に従わない

| 画面 | 実際の言語 |
| --- | --- |
| macOS マイク許可（TCC / `NSMicrophoneUsageDescription`） | **OS の言語**。`InfoPlist.strings` は ja/en を用意するが、アプリ内上書きは効かない |
| Windows のマイク許可 | OS 設定アプリ。当アプリの文字列ではない |
| `NSSavePanel` / WinForms `SaveFileDialog` の標準ボタン | OS 言語 |
| 多重起動ダイアログより前の失敗 | 設定を読む前なら OS 言語、読めたら `uiLanguage` |

実装者は「設定で英語にしてもマイク許可は日本語 OS なら日本語」を仕様として扱う。バグではない。

### 再起動必須の帰結

- 設定で表示言語を変えても、開いている設定ウィンドウ・トレイ・バナーは旧言語のまま。
- 変更後は「アプリを再起動すると表示言語が変わります」と出すだけ。再起動ボタンは初回に入れない。
- 録音中に変えても、今のセッション文言は変わらない。

### サーバー由来の英語

`SanitizeServerMessage` を通過した OpenAI の英語は、秘密情報を含まない限りそのまま出す。ja UI でも英語が混ざる。汎用メッセージ（`error.genericServer`）だけ翻訳する。

### レイアウト

英語は日本語より長い。設定ウィンドウの固定サイズは折り返しで収め、収まらない項目だけ幅/高さを最小限広げる（macOS 560×720、Windows 620×560）。トレイの `NotifyIcon.Text` は **63 文字上限**。

### テスト並列

プロセス広域の `UserCopy.Current` をテスト中に `en` へ差し替えると xUnit / XCTest の並列で壊れる。テストの既定は **常に ja カタログ**。en の検証はローカルにテーブルを載せて行い、Current を切り替えない。

## 5. アーキテクチャ

```text
shared/locales/ui.json              ← キーと ja/en の正本
shared/locales/ui.schema.json
        │
        ├─ テスト: 両実装のテストが読む（SharedFixtures と同じ repo-root 探索）
        ├─ macOS: XcodeGen でアプリバンドルへ resource コピー
        └─ Windows Core: EmbeddedResource（ビルド時パス参照）
```

実行時はキー引きだけ。`.xcstrings` と `.resx` を別管理しない（すぐずれる）。

**`fixtures/v1` には置かない。** fixtures は「v1 の既存ケースは意味を変えない。破壊的変更は `v2/`」が
ルールであり、UI 文言は推敲で変わり得るため、この不変ルールの対象にしない。
`shared/locales/` は「キーの追加・文言の改善は通常変更、キーの削除・意味変更は両実装同時」の運用とする。
CI（`shared-contracts`）は現状 `fixtures/v1` しか検査しないため、`locales/` の schema 検査ステップを追加する。

### 解決器

名前は `UserCopy`（ユーザー向け文言。ログや字幕本文ではない）。

- 起動の最初に 1 回ロードし、プロセス内で不変。
- 欠けたキーは `en` へフォールバックし、DEBUG ではログにキー名だけ出す（文言本文は出さない）。
- プレースホルダは `{name}` の単純置換。`string.Format` / `String(format:)` の `%@` は使わない（両言語でずれやすい）。

Windows Core は AOT 対象のため、カタログ読みは既存の `AppSettingsCodec` と同様に `JsonNode` だけを使う（反射シリアライズしない）。

### Core に日本語を残さない方向

ユーザー向け文字列の正本は `ui.json`。Core の例外・バナーはキーまたは `kind` 経由で `UserCopy` を引く。

ただし移行中は次で既存テストを守る。

1. 起動前 / テスト既定で `UserCopy` に ja を載せる。
2. `RealtimeTranslationException.Message` や `LocalizedError.errorDescription` は、コンストラクタ時点の `UserCopy` で解決した文字列を返す（現行テストが日本語リテラルで断言しているため）。
3. 新しいテストは `kind` またはキーで断言する。日本語リテラル断言は増やさない。

`DualRealtimeTranslationClient` の `ArgumentException`（英語、接続未設定）は開発者向け。訳さない。

`InstallIdentifierStore` の例外も UI に出さない。訳さない。

## 6. カタログ形式

`shared/locales/ui.json`:

```json
{
  "$schema": "./ui.schema.json",
  "version": 1,
  "description": "アプリ枠のユーザー向け文言。字幕本文と記録ファイル形式は含めない。",
  "locales": ["ja", "en"],
  "fallback": "en",
  "strings": [
    {
      "key": "menu.startTranslation",
      "ja": "翻訳を開始",
      "en": "Start translation"
    }
  ]
}
```

`ui.schema.json`（および `shared-contracts` の schema 検査）で保証すること:

- `strings[].key` は一意
- 各要素に `ja` と `en` が必須で、空文字禁止

プレースホルダ名が ja/en で一致すること（`{hotkey}` が片方だけ、は不可）は **JSON Schema だけでは表現しない**。両実装のテスト（または schema 検査に続く必須のカスタム検証）で、各 `strings[].key` について ja/en の `{name}` 集合が等しいことを検査する。schema 検査だけを足してプレースホルダ検査を省略してはいけない。

キー命名: `領域.用途`。例: `settings.tab.general`、`menu.startTranslation`、`banner.idle`、`error.authenticationFailed`。

CI（`shared-contracts`）へ `locales/ui.json` の schema 検査ステップを追加する（fixtures の 1:1 ループとは別ステップ）。プレースホルダ一致は上記どおりテスト側（または同ジョブ内の必須カスタム検証）に置く。

## 7. キー棚卸し

実装時に `ui.json` へ落とす対象。文言は現行日本語を `ja` の初期値にする。英語は実装フェーズ 4 で入れる。

### 設定（両 OS）

| キー | 現行 ja（代表） |
| --- | --- |
| `settings.windowTitle` | Realtime Translator 設定 |
| `settings.tab.general` | 一般 |
| `settings.tab.speech` | 音声認識 |
| `settings.tab.subtitles` | 字幕・操作 |
| `settings.model` / `settings.languagePair` / `settings.subtitleDisplay` / `settings.translatedAudio` | モデル / 翻訳ペアまたは翻訳方向 / 字幕表示 / 翻訳音声 |
| `settings.languagePair.jaEn` 等 | 日本語 ↔ 英語 など |
| `settings.languagePairAppliesNextRecording` | 次回録音開始時に反映されます。 |
| `settings.subtitleDisplayValue` | 原文＋翻訳 |
| `settings.translatedAudioValue` | 字幕のみ（再生なし） |
| `settings.consentToggle` | マイク音声を OpenAI API へ送信することに同意する |
| `settings.consentHelp` | 録音中はマイク音声・原文・訳文が… |
| `settings.apiKey` / `settings.save` / `settings.delete` | API キー / 保存 / 削除 |
| `settings.apiKeySaved.mac` / `settings.apiKeySaved.windows` | Keychainに保存済み / 資格情報マネージャーに保存済み |
| `settings.apiKeyNotSaved` | 未保存 |
| `settings.apiKeySaveOk.mac` / `.windows` | 保存しました系 |
| `settings.apiKeySaveFailed` / `settings.apiKeyDeleteOk` / `settings.apiKeyDeleteFailed` / `settings.apiKeyStatusUnknown` | 失敗・削除・状態不明 |
| `settings.apiKeyStorageHelp.mac` / `.windows` | 保管先の説明（OS 差） |
| `settings.noiseReduction` / `.nearField` / `.farField` | ノイズ低減とその選択肢 |
| `settings.transcriptionDelay` / `.minimal` … `.xhigh` | 認識遅延とその選択肢 |
| `settings.delayHelp` | 値を上げると… |
| `settings.applyPreset` / `settings.restoreDefaults` | プリセットを適用 / デフォルトに戻す |
| `settings.preset.softwareDevelopment` 等 | ソフトウェア開発 / ビジネス会議 / ハッカソン |
| `settings.section.recognition` / `.hints` / `.subtitles` / `.controls` / `.apiKey` | セクション見出し（認識設定 / 認識ヒント / 字幕 / 操作 / API キー） |
| `settings.promptTitle` / `settings.keywordsTitle` | 認識プロンプト / キーワード (1行1語) |
| `settings.promptHelp` | 会議のテーマや話者、話題を文章で書くと認識精度が上がります。 |
| `settings.promptCounter` | `{count}/{limit} 文字` |
| `settings.promptOverLimit` | （超過分は切り詰められます） |
| `settings.keywordCounter` | `{count}/{limit} 語` |
| `settings.keywordOverLimit` | （超過分は送信されません） |
| `settings.keywordForbidden` | 「<」「>」は送信時に自動除去されます。 |
| `settings.tuningLiveHelp` | プロンプト・キーワード・認識遅延の変更は… |
| `settings.fontSize` | フォントサイズ: `{size}pt` |
| `settings.recordSubtitles` | 字幕をローカルに記録する |
| `settings.recordSubtitlesHelp.mac` / `.windows` | このMacにのみ… / この PC にのみ… |
| `settings.controlsHelp.mac` / `.windows` | ホットキー説明（OS 差） |
| `settings.uiLanguage` | 表示言語 |
| `settings.uiLanguage.system` / `.ja` / `.en` | システムと同じ / 日本語 / English |
| `settings.uiLanguageRestartHint` | アプリを再起動すると表示言語が変わります。 |
| `settings.appVersion` | バージョン `{version}` |

OS 差がある説明文は **キーを分ける**。1 キーで `{os}` 分岐しない（テストと翻訳が難しくなる）。

表示名の OS 差はカタログ化のときに揃える。

| 項目 | 現行 macOS | 現行 Windows | 寄せ先 |
| --- | --- | --- | --- |
| 遠距離 | 会議・遠距離 | 遠距離マイク | `遠距離マイク`（短く、両 OS 共通） |
| 遅延 | 最速（精度低め）等 | 最小 / 低 / 中… | macOS 側の説明付き（英語でも意味が通る） |

Windows の ja 利用者には遅延ラベルが変わる。仕様変更として PR に書く。認識の wire 値（`low` 等）は変えない。

### メニュー / トレイ

| キー | 現行 ja |
| --- | --- |
| `menu.startTranslation` / `menu.stopTranslation` | 翻訳を開始 / 翻訳を停止 |
| `menu.languagePair` | 翻訳方向: `{pair}` |
| `menu.subtitleDisplay` | 字幕表示: 原文＋翻訳 |
| `menu.translatedAudio` | 翻訳音声: 字幕のみ |
| `menu.exportSubtitles` | 字幕を書き出し… |
| `menu.clearSubtitles` | 字幕記録をクリア |
| `menu.editPosition` | 字幕位置を編集 |
| `menu.settings` | 設定… |
| `menu.quit` | 終了 |
| `menu.quitApp` | Realtime Translator を終了 |
| `menu.edit` / `.undo` / `.redo` / `.cut` / `.copy` / `.paste` / `.delete` / `.selectAll` | 編集メニュー |

### バナー・オーバーレイ枠

| キー | 現行 | 備考 |
| --- | --- | --- |
| `banner.idle` | 待機中 — {hotkey} で録音開始 | ホットキーは注入 |
| `banner.connecting` | 接続中… / OpenAI Realtimeへ接続中… | **両 OS で `接続中…` に統一**。macOS 利用者に見える文言変更なので実装 PR に明記する |
| `banner.reconnecting` | 再接続中… | |
| `banner.reconnectingProgress` | {detail} 再接続中… ({attempt}/{max}) | detail 省略形もあり |
| `banner.listening` | 録音中… 話してください | macOS InterpretationSession |
| `banner.closing` | 録音を終了中… | macOS |
| `overlay.recording` | 録音中… | macOS アクセシビリティ |
| `overlay.startRecording` / `overlay.stopRecording` | 録音開始 / 録音終了 | macOS ボタン。Windows にボタンなし |
| `overlay.windowTitle` / `settings.windowTitle` | Realtime Translator 字幕 / 設定 | Windows の Window.Title。タスクバー非表示だが支援技術が読む |

Windows `SubtitleSnapshotBuilder` は idle / connecting / reconnecting だけ。macOS はセッション側で listening / closing も出す。キーは共通化してよいが、使わない OS があっても消さない。

### ダイアログ・通知

| キー | 現行 ja |
| --- | --- |
| `alert.alreadyRunning` | Realtime Translator は既に起動しています。 |
| `alert.clearTranscriptTitle` / `.body` / `.confirm` / `.cancel` | クリア確認 |
| `alert.needConsent` | 録音を開始する前に、設定で OpenAI への送信に同意してください。 |
| `alert.needApiKey` | 録音を開始する前に、設定で OpenAI API キーを保存してください。 |
| `alert.hotkeyFailed` | {hotkey} を登録できませんでした。トレイメニューから操作してください。 |
| `dialog.exportFilter` | テキスト ファイル (*.txt)\|*.txt | Windows のみ |
| `transcript.sizeLimitBanner` | 字幕記録が上限に達しました。書き出してクリアしてください |
| `transcript.writeFailureBanner` | 字幕の記録に失敗しました |

`transcript.json` の `messages.*` は **ファイル形式ではなくバナー**だが、`fixtures/v1` は変更しない。
`ui.json` の ja がこの fixture 値と一致することを両実装のテストで検証する（正本の二重化を一致テストで防ぐ）。
なお const 制約を持つのは `privacy.schema.json` の `genericErrorMessage` だけで、`transcript.schema.json` の
`messages` は `type: string`。どちらの schema も変更不要。

### エラー（画面に出るもの）

`RealtimeTranslationError` / `RealtimeTranslationException`:

| キー | 現行 ja |
| --- | --- |
| `error.missingApiKey` | APIキーが設定されていません |
| `error.notConnected` | 翻訳セッションに接続していません |
| `error.invalidMessage` | 翻訳サーバーからの応答を解釈できません |
| `error.authenticationFailed` | OpenAI APIキーが無効です |
| `error.genericServer` | 翻訳サーバーでエラーが発生しました |
| `error.transportDisconnected` | 翻訳サーバーとの接続が切れました |
| `error.sourceDisconnected` | 原文字幕サーバーとの接続が切れました |
| `error.audioSendFailed` | 翻訳サーバーへの音声送信が失敗しました |
| `error.sourceSessionGeneric` | 原文字幕セッションでエラーが発生しました |
| `error.sessionUpdateTimeout` | 翻訳セッションの準備がタイムアウトしました |
| `error.closeTimeout` | 翻訳セッションの終了待ちがタイムアウトしました |
| `error.cancelled` | 翻訳セッションがキャンセルされました |
| `error.reconnectLimit` | 再接続上限に達しました |
| `error.audioInputStopped` | 音声入力が停止しました |
| `error.eventStreamStopped` | イベント受信が停止しました |
| `error.websocketNotConnected` | WebSocketに接続していません |
| `error.websocketUnsupported` | 未対応のWebSocketメッセージを受信しました |

マイク:

| キー | 現行 |
| --- | --- |
| `error.micDenied` | マイクを利用できません |
| `error.micFormatUnavailable` | マイク入力の音声形式を取得できません |
| `error.micConverterUnavailable` | 翻訳用の音声変換を開始できません |
| `error.micBufferUnavailable` | 音声バッファを準備できません |
| `error.micPipelineOverloaded` | 音声処理が遅延しています |
| `error.micDeviceChanged` | マイク入力デバイスが変更されました |
| `error.micNotFound` | マイクが見つかりません | Windows |
| `error.micStartFailed` | マイクを開始できませんでした | Windows |

API キーストア（設定画面の status に出ることがある）:

| キー | 現行 |
| --- | --- |
| `error.apiKeyEmpty` | APIキーが空です |
| `error.apiKeyMalformed` | APIキーの形式が正しくありません。コピー時に改行や余分な文字が入っていないか確認してください |
| `error.apiKeyNotFound` | APIキーが保存されていません |
| `error.apiKeyStoreUnavailable` | APIキーの保存領域へアクセスできません |
| `error.apiKeyEncodingFailed` | APIキーを処理できません |

`privacy.json` の `genericErrorMessage` は `error.genericServer` の ja と同じ値を指す。**fixture と schema（`const`）は変更しない。** 既存の fixture テストに「`ui.json` の `error.genericServer` ja が fixture 値と一致する」断言を足す。正規化アルゴリズム自体は変えない。

### Info.plist（OS 言語）

`NSMicrophoneUsageDescription` はカタログとは別に `*.lproj/InfoPlist.strings` へ置く。ja は現行文、en は同等の英訳。アプリ内 `uiLanguage` では切り替わらない（§4）。

## 8. 起動時の解決手順

両 OS とも、**最初のウィンドウ / トレイ / セッションを作る前**に行う。

1. 設定ストアから `uiLanguage` を読む。欠落・未知値は `system`。
2. `system` なら OS の UI 言語を見る。
   - macOS: `Locale.current.language.languageCode` が `ja` なら ja、それ以外は en
   - Windows: `CultureInfo.CurrentUICulture.TwoLetterISOLanguageName` が `ja` なら ja、それ以外は en
3. カタログをロードし、解決したロケールで `UserCopy` を固定する。
4. **スレッドカルチャは変更しない。** 文言は `UserCopy` が持ち、永続化は既に `InvariantCulture` 固定。
   `CurrentCulture` を変えると数値・日付書式の副作用が出る。`InvariantGlobalization=false` の維持だけで
   WPF の `XmlLanguage` 要件は満たせる。
5. その後にトレイ・オーバーレイ・設定・セッションを構築する。

多重起動検出は設定より前でもよい。その場合のメッセージは OS 言語でも許容する。設定が読めるなら `UserCopy` 後に出す方がよい。

DEBUG の環境変数取り込み（`OPENAI_API_KEY`）より前に `UserCopy` を載せる必要はないが、同意ダイアログより前には載せる。

## 9. 永続化

### 値

`uiLanguage`: `"system"` | `"ja"` | `"en"`。既定 `"system"`。

### macOS

`AppSettings` の UserDefaults キー `uiLanguage`。他フィールドと同じく変更時に即保存。Picker 変更は `onSave` を呼ぶ。セッションの言語ペア変更とは独立。

### Windows

`AppSettingsData` に `UiLanguage` を追加。`AppSettingsCodec` が `uiLanguage` を読み書き。欠落時は `system`。未知値は `system` へ倒す（起動不能にしない）。

`EncodeDecodeRoundTripsEveryField` にフィールドを足す。API キーを出さないテストは現状どおり。

## 10. 作業手順

フェーズを飛ばさない。各フェーズの終わりで該当テストが緑であること。

- [x] フェーズ 1 — カタログと読み込み
- [x] フェーズ 2 — 表示層の差し替え
- [x] フェーズ 3 — Core / セッションから文言を剥がす
- [x] フェーズ 4 — 英語を入れる
- [x] フェーズ 5 — 文書と開発ガイド

### フェーズ 0 — 契約を固定する（この文書）

- [x] 対象ロケール・再起動必須・非対象を文書化する
- [x] 実装 PR ではこの文書を更新せずに挙動を変えない。変えるなら文書を先に直す

### フェーズ 1 — カタログと読み込み（見た目は変えない）

作業順:

1. `shared/locales/ui.json` と `ui.schema.json` を追加し、`shared-contracts` に検査ステップを足す。`ja` に現行文言を全部入れる。`en` は一旦 ja と同じでよい（キー集合を先に固定するため）が、フェーズ 4 で必ず英訳する。空文字は禁止。
2. `UserCopy` ローダを Core（Windows）と Swift に追加する。キー欠落・プレースホルダ不一致のテストを書く。
3. macOS: `project.yml` で `ui.json` を resource に含める。Swift production は `Bundle.main` から読む。
4. Windows: Core csproj で EmbeddedResource する。パスはリポジトリ相対。AOT でも `JsonNode` のみ。
5. テストは ja を Current に載せる（モジュール初期化 / `XCTestCase` の一括セットアップ）。既存テストはまだ日本語リテラルのままで通る。

注意:

- `shared/README.md` の「Swift production は shared/ を実行時に読まない」を守る。バンドルコピーは可。
- fixture を足したら schema も足す。CI の 1:1 が落ちる。
- キー追加漏れを防ぐため、「ソースに残っている日本語ユーザー向けリテラル」を grep する手順をフェーズ 2 の完了条件にする。

### フェーズ 2 — 表示層の差し替え

作業順（ユーザーが見る順）:

1. **設定**  
   macOS `SettingsView.swift`、Windows `SettingsWindow.xaml` + `.xaml.cs`。  
   XAML の `Text=` / `Header=` / `Content=` をコードからの代入か、起動時に埋める。XAML に日本語を残さない。  
   ComboBox は `DisplayMemberPath="DisplayName"` を維持し、`DisplayName` をカタログから入れる。
2. **表示言語 Picker** を一般タブへ追加。保存は即時。反映は再起動。ヒント文を出す。
   **リリースゲート**: `en` の実文言はフェーズ 4 で入るため、フェーズ 1〜4 を揃えてからタグを切る。
   途中でリリースする場合は Picker の `en` 選択肢を出さない（`system`/`ja` のみ）。
3. **メニュー / トレイ**  
   `MenuBarController` / `AppMainMenu` / `TrayController`。生成時に `UserCopy` を引く。状態変化（開始/停止）もキーで差し替える。
4. **ダイアログ・通知**  
   多重起動、クリア確認、同意/キー未設定、ホットキー失敗、書き出しフィルタ。
5. **macOS 編集メニュー**  
   タイトルはカタログ。テストはタイトル文字列ではなく `identifier` か `action` / `keyEquivalent` で探すように直す（`AppMainMenuTests`）。
6. **InfoPlist.strings**  
   `project.yml` に `CFBundleLocalizations: [en, ja]`。`NSMicrophoneUsageDescription` の ja は `project.yml` に残してよい（開発言語）。en は `en.lproj`。

注意:

- Windows トレイは WinForms。WPF の ResourceDictionary は使えない。
- 設定ウィンドウを開いたまま言語を変えても、そのウィンドウは再描画しない（再起動必須）。
- リンク「OpenAI Pricing」等の固有名詞は訳さない。

### フェーズ 3 — Core / セッションから文言を剥がす

作業順:

1. `RealtimeTranslationException.DescribeFor` / `RealtimeTranslationError.errorDescription` を `UserCopy` 引きにする。
2. `SubtitleSnapshotBuilder` の const バナーをやめる。コンストラクタで `UserCopy` を受け取るか、参照時に引く。idle は `{hotkey}` を OS 側で埋めた文字列を渡す。
3. `InterpretationSession` の直書きバナー（接続中、録音中、再接続中、終了中、再接続上限）をキー化。
4. マイク例外、API キーストア例外、WebSocket 例外。
5. `privacy.json` / `transcript.json` は**変更しない**。両実装の fixture テストへ「カタログ ja == fixture 値」の一致断言を足す。**`format` の `原文:` / `訳文:` も従来どおり。**

注意:

- `errorDescription` を変えても `SanitizeServerMessage` の検出語は英語のまま。
- `InterpretationSessionTests` の `Assert.Equal("OpenAI APIキーが無効です")` は、フェーズ 1 で ja を載せていれば通る。kind 断言への移行はこのフェーズでやってよいが必須ではない。
- macOS の `statusBanner?.contains("マイク")` は、en では落ちる。`kind` または ja カタログの部分文字列（Current が ja のときだけ）に変える。
- 再接続バナーは回数を入れる。秘密情報を `detail` に載せない現行ロジックは維持。

### フェーズ 4 — 英語を入れる

1. `ui.json` の `en` をすべて英訳する。ja のコピー残しを禁止（キー completeness テストに「ja == en が許容されるキー」の allowlist は製品名など最小限）。
2. プレースホルダ一致テスト。
3. 設定ウィンドウの折り返し。英語でボタンが切れないこと。
4. トレイ 63 文字。`Realtime Translator ({state})` の state は enum 名のまま（英語）でよい。
5. `VALIDATION.md` に表示言語の手動項目を足す。

### フェーズ 5 — 文書と開発ガイド

1. `AGENTS.md` に `uiLanguage` と「字幕記録フォーマットはロケールしない」を短く追記。
2. `README.md` / `README.en.md` の設定表に表示言語を 1 行足す。
3. この文書のチェックリストを完了にする。

## 11. ファイル別の主な変更先

### 共有

- `shared/protocol/ui-locale.md`（本ファイル）
- `shared/protocol/privacy.md`（固定文言がロケールされる旨）
- `shared/README.md`（locales/ をツリーに追加）
- `shared/locales/ui.json` + `ui.schema.json`（新規ディレクトリ）
- `.github/workflows/shared-contracts.yml`（locales の schema 検査ステップ追加）
- `shared/fixtures/v1/` は**変更しない**（一致断言はテスト側に置く）

### macOS

- `project.yml`（resource、localizations）
- `RealtimeTranslator/Settings/AppSettings.swift` / `SettingsView.swift`
- `MenuBar/MenuBarController.swift`、`App/AppMainMenu.swift`、`App/AppCoordinator.swift`
- `Subtitles/SubtitleView.swift`、`SubtitleAggregator.swift`（バナーはセッション側が主）
- `Realtime/InterpretationSession.swift`
- `OpenAI/RealtimeTranslationEvent.swift`（errorDescription / genericServerMessage）
- `OpenAI/RealtimeWebSocketTransport.swift`
- `Audio/RealtimeAudioCaptureService.swift`
- `Security/APIKeyStore.swift`
- `en.lproj/InfoPlist.strings`（新規）
- テスト: `AppMainMenuTests`、`PrivacyFixtureTests`、`InterpretationSessionTests`、`SubtitlePresentationTests`、新規 `UserCopyTests`

### Windows

- `Core/Settings/AppSettingsData.cs`（codec / テスト）
- `Core` に `UserCopy` ローダ
- `Core/OpenAI/RealtimeTranslationException.cs`
- `Core/Subtitles/SubtitleSnapshot.cs`、`SubtitleTranscriptFormatter.cs`（バナー const のみ。format は維持）
- `Core/Realtime/InterpretationSession.cs`、接続切断メッセージ
- `Core/OpenAI/RealtimeSessionTuning.cs` の `Preset.DisplayName`（Id は維持、表示名は UI 側で引く方がよい）
- `Platform/Audio/WasapiAudioCaptureService.cs`
- `App/App.xaml.cs`、`SettingsWindow.xaml` / `.xaml.cs`、`TrayController.cs`
- テスト: `AppSettingsCodecTests`、`PrivacyFixtureTests`、`SubtitleSnapshotBuilderTests`、`InterpretationSessionTests`、新規 `UserCopyTests`

Preset の `DisplayName` は Core のレコードに日本語が残る。**Id は wire / 内部用、DisplayName は UI で上書き**する。prompt/keywords は英語のまま（API 向け）。

## 12. 注意が必要な点（実装時チェック）

1. **翻訳ペア Picker の表示名を訳しても、保存値は `ja-en` のまま。** 表示だけ変える。
2. **同意トグルの文言を訳しても version は上げない。** 意味を変えたときだけ上げる。
3. **録音中の表示言語変更は無視してよい。** 再起動まで旧言語。ペア変更は「次回録音」で、表示言語は「次回起動」。説明文で混同させない。
4. **ログに翻訳後のエラー全文を増やさない。** 現行どおりイベント ID と kind。バナーに出る文はユーザー向けで、status file には出さない。
5. **en 訳で `API key` と書くと `SanitizeServerMessage` に誤爆しないか。** サニタイズはサーバー生 message に対してだけ走る。自前カタログの `error.authenticationFailed` を再度サニタイズしない。
6. **テストの Given/When/Then は日本語のまま。** ユーザー向けリテラルだけカタログへ。
7. **`SubtitlePresentationTests` のバナー文字列はレイアウト用。** カタログの idle 文を使うか、長さだけを見るようにする。英語バナーが長いと高さ予約が足りない可能性。英語でも 2 行以内に収める。
8. **Windows ComboBox に `DisplayMemberPath` を付け忘れると record の `ToString()` が出る。** 既存注意の再発防止。
9. **単一インスタンス。** 言語変更後に手で再起動する。旧プロセスが残っていると「既に起動しています」が出る。自動再起動しない理由。
10. **XcodeGen。** `Info.plist` と entitlements は生成物。localizations は `project.yml` へ。
11. **Hardened Runtime / マイク entitlement は触らない。**
12. **Core の `IsAotCompatible`。** カタログ型を `JsonSerializer.Deserialize<Dictionary<...>>` しない。`JsonNode` で読む。

## 13. テスト方針

最低限:

- カタログ: 全キーに ja/en、キー一意、プレースホルダ一致
- `uiLanguage` の encode/decode、欠落時 `system`、未知値 `system`
- OS 解決: `ja` → ja、`en` → en、`es` → en、`fr` → en
- 例外 kind → カタログキー（ja Current で現行日本語と一致、en テーブルで英語）
- `SanitizeServerMessage("")` が `error.genericServer` の **現在のロケール** になる
- カタログ ja が `fixtures/v1` の `genericErrorMessage`・`sizeLimitBanner`・`writeFailureBanner` と一致する（fixture 不変の担保）
- 字幕記録 format は `原文:` / `訳文:` のまま（回帰）
- `AppMainMenu` は action / keyEquivalent で探す
- 秘密情報非漏洩: どのロケールでも `sk-` がバナー・例外に出ない

ユニットテストでは OS 言語をモックする（実際の macOS/Windows ロケールは CI で制御しない）。

## 14. 手動検証（`VALIDATION.md` へ後で転記）

1. OS 日本語、`uiLanguage=system` で起動 → メニュー・設定・待機バナーが日本語。ホットキー表記は OS どおり。
2. 設定で English を選び、**再起動せず** → まだ日本語。ヒントが出る。
3. 再起動後 → 英語 UI。翻訳ペアは変わらない。`ja-en` のまま日本語音声を英語字幕にできる。
4. 設定で 日本語 を選び再起動 → 日本語に戻る。
5. OS を英語（または非 ja）にし `system` → 英語 UI。
6. マイク許可ダイアログは OS 言語（アプリが English でも日本語 OS なら日本語）。仕様。
7. 字幕記録を書き出し、`原文:` / `訳文:` / `=== 録音開始` が英語 UI でも同じ。
8. 無効 API キーでバナーがロケールされ、キー本文が無い。
9. 設定の英語表示でタブ・ボタンが切れない。トレイアイコンのツールチップが 63 文字以内。

権限・実 API・実マイクはユニットテストでは検証できない。従来どおり。

## 15. 完了条件

- [x] `ui.json` にユーザー向け枠文言があり、ソースに同等の直書きが残っていない（grep で確認）
- [x] 表示言語 Picker があり、保存され、**再起動後だけ**反映される
- [x] `system` / `ja` / `en` が仕様どおり
- [x] 字幕本文・記録ファイル形式・prompt/keywords・ログ禁止が維持される
- [x] Windows `dotnet test` と macOS `xcodebuild test`、`shared-contracts` が通る
- [x] `VALIDATION.md` の表示言語項目がある

## 16. 明示的な後続（この初回に入れない）

- スペイン語 UI（`es`）。カタログにロケールを足す拡張点だけ残す（`locales` 配列）
- 起動中の即時切替、再起動ボタン
- 字幕記録フォーマットの言語中立化（破壊的変更。`v2/`）
- OS 標準ダイアログの言語追従（不可能または不安定）
- prompt/keywords の UI 言語連動（認識品質の問題であり、表示言語ではない）
