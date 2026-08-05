# RealtimeTranslator 開発ガイド

## 目的と不変条件

- macOS 26以降向けの、OpenAI Realtime Translationによるリアルタイム日英字幕アプリである。
- 外部通信先は `api.openai.com/v1/realtime` と `api.openai.com/v1/realtime/translations` に限定する。それ以外の外部翻訳APIは追加しない。
- 利用者自身のOpenAI APIキー（BYOK）をmacOS Keychainへ保存する。事業者キーは同梱しない。
- 原文音声は専用 `gpt-live-transcribe` へ常時送信する。直近4秒のrolling prerollを常時保持し、判定前は原文のみ送信、判定後は日本語なら `target=en`、英語なら `target=ja` へ送る。言語切替時は新targetへprerollをflushする。
- 原文文字種の反転（末尾ウィンドウ判定）をセグメント境界として扱い、確定・ルーティング・prerollを切り替える。
- Translationセッション付属のtranscriptionを原文authorityにしない。専用transcriptionの低遅延deltaを使い、原文と訳文を常にペア表示する。
- APIキー、Authorization、音声、原文、訳文をログ・status file・アラートへ出さない。
- 初回MVPでは翻訳音声を再生しない。`session.output_audio.delta` は受信するがデコードしない。

## 構成

- `App/`: AppKitライフサイクルと全体調停。SwiftUI `App`へ戻さない。
- `Security/`: Keychain APIキーストアと環境変数からの初回取り込み。
- `Audio/RealtimeAudioCaptureService.swift`: AVAudioEngine、24 kHz PCM16変換、100 ms packet化。
- `Audio/SpokenLanguage.swift` / `SpokenLanguageDetector.swift`: 原文deltaの文字種判定と末尾ウィンドウによる言語切替検出。
- `OpenAI/`: Realtime Transcription / Translation WebSocket、イベントcodec、日英dual client。
- `Realtime/InterpretationSession.swift`: 接続、音声送信、字幕整列、状態遷移を統合。
- `Subtitles/`: 単一current字幕集約、透明オーバーレイ、録音コントロール。
- `project.yml`: XcodeGen設定、Info.plist項目、権限説明の正本。

## 並行処理

- UI、AppKit、セッション状態は`@MainActor`で扱う。
- `NSApplicationDelegate`メソッドは`nonisolated`を維持する。SDK既定のMainActor隔離へ戻すとCFRunLoop経由でクラッシュし得る。
- 起動は`DispatchQueue.main.async`と`MainActor.assumeIsolated { AppRuntime.start() }`を使い、delegate通知だけに依存しない。
- Core Audio、TCC、URLSession等のコールバックがMainActor上で呼ばれると仮定しない。
- リアルタイム音声tapではバッファをキューへ渡すだけにし、変換やネットワーク送信を行わない。
- `AVAudioConverter`は状態を持つため、単一feederタスクから直列に呼ぶ。
- WebSocket送信はactor境界で管理する。原文送信は翻訳送信と分離し、翻訳側の停滞・失敗に巻き込まない。判定後は選択されたtargetだけが同じ音声frame列を受信し、切替時はrolling prerollを新targetへflushする。
- WebSocket `send` は約5秒でtimeoutし、`recoverableTransportFailure` として再接続する。
- 非MainActorコールバックにMainActor継承クロージャを渡さない。明示的な`@Sendable`ヘルパーを使う。
- Swift 6 strict concurrencyを維持し、警告回避目的の`@unchecked Sendable`は境界型だけに限定する。
- importは必ずファイル先頭へ置き、関数内importを追加しない。

## Realtime Translationの制約

- 原文は `wss://api.openai.com/v1/realtime?intent=transcription` と `gpt-live-transcribe` の `delay=low`、`far_field` noise reductionを使う。
- 専用エンドポイント `wss://api.openai.com/v1/realtime/translations?model=gpt-realtime-translate` を使う。
- `response.create`、会話turn、tool callは使わない。連続音声ストリームとして扱う。
- WebSocket入力は base64-encoded 24 kHz PCM16 mono little-endian。字幕開始を早めるため100 ms frameを使う。
- 録音中は無音frameも送り続ける。VADで無音を捨てない。
- いずれかの接続が壊れたら全体を再接続する。再接続時は言語判定をリセットする。
- 正常停止はtranscriptionをcommitし、Translation両セッションへ `session.close` を送り、完了イベントを待ってからsocketを閉じる。
- lane選択の一次信号は「セッションが設定した期待laneヒント」とし、補助に原文文字種とfirst-outputを使う。同言語echo（英語入力が `target=en` から英語で戻る等）が発生し得るため、echoだけでlaneを確定しない。
- 原文deltaの末尾ウィンドウ文字種判定を言語切替とルーティングの信号として使う。
- 古い接続epochのdeltaは画面へ反映しない。

## 字幕UIの不変条件

- 字幕本文パネルはクリック透過、録音ボタンの別パネルだけを操作可能にする。
- `.floating`、`.canJoinAllSpaces`、`.fullScreenAuxiliary`を維持し、他アプリやフルスクリーン上の表示を壊さない。
- スライドを隠す全面黒背景へ戻さない。文字周辺の薄い背景と黒いハローで可読性を確保する。
- 字幕は単一のcurrentスロットのみ。確定ペアもその場に残し、次発話開始で上書きする（履歴ブロックなし、タイマー消去なし）。原文だけを確定しない。
- 更新待ちの旧訳文は`isTranslationCurrent = false`かつ`canFinalize = false`にする。
- 発話途中の原文表示は約160ms間隔に抑え、行高を維持してちらつきを防ぐ。
- パネル高は複数行の1ブロックを収め、ベースラインをクリップしない。

## プロジェクト設定

- `RealtimeTranslator.xcodeproj`と`Info.plist`はXcodeGen生成物として扱う。
- 永続的なInfo.plist変更は`project.yml`の`targets.RealtimeTranslator.info.properties`へ追加する。
- `NSMicrophoneUsageDescription`を削除しない。用途説明はOpenAI送信を明示する。
- 現在はApp Sandboxが無効のため、entitlementsは空でよい。将来Sandboxを有効化する場合だけ`network.client`を追加する。
- 同一Bundle IDの多重起動を許さない。Xcode実行と`run.sh`を同時に残さない。

## ビルドと検証

```bash
xcodegen generate
xcodebuild -scheme RealtimeTranslator \
  -destination 'platform=macOS' \
  -derivedDataPath ./build/DerivedData build

xcodebuild test -scheme RealtimeTranslator \
  -destination 'platform=macOS' \
  -derivedDataPath ./build/DerivedData \
  -enableCodeCoverage YES
```

- 実行は`./scripts/run.sh`を使い、バイナリを直接起動しない。LaunchServices経由でTCC権限を認識させる。
- APIキーはKeychainへ保存する。初回は環境変数`OPENAI_API_KEY`または設定画面から取り込む。
- 権限、実API、実マイク、オンライン動作はユニットテストだけでは検証できない。
- 手動検証項目は`VALIDATION.md`も参照する。
- 実行状態は`/tmp/realtimetranslator.status`で確認する。
- クラッシュ時は最新のDiagnosticReportsと該当スレッドを確認し、推測だけで修正しない。
- ログへ認識した発話内容、APIキー、Authorizationを出力しない。

## テスト方針

- 純粋ロジックはXCTestで検証し、各テストに日本語のGiven/When/Thenコメントを付ける。
- 最低限、イベントcodec、100 ms packet化、専用原文transcription、原文送信分離とrolling preroll、言語切替セグメント分割、送信timeout、字幕lane選択、旧epoch破棄、停止時close drainを維持する。
- 非同期境界、空文字、句読点、停止時finalize、多重起動、秘密情報非漏洩の回帰を優先する。
- UIや音声経路を変更したら、ビルドと全テストに加えて実際に日英を1文ずつ話して確認する。
