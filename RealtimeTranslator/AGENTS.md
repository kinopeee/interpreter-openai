# macOS 開発ガイド

[ルートの AGENTS.md](../AGENTS.md) の共通規約に追加して適用する。対象は `RealtimeTranslator/`、`project.yml`、Xcode 生成物、macOS のスクリプト・workflow。macOS 26以降、Swift 6 を前提とする。

コマンドと `project.yml`、`shared/`、`scripts/`、`.github/` のパスはリポジトリルートを基準とする。「構成」のソースパスは `RealtimeTranslator/` を基準とする。

## 構成

- `App/`: AppKitライフサイクルと全体調停。SwiftUI `App`へ戻さない。
- `Security/`: Keychain APIキーストアと環境変数からの初回取り込み。
- `Audio/RealtimeAudioCaptureService.swift`: AVAudioEngine、24 kHz PCM16変換、100 ms packet化。
- `Audio/SpokenLanguage.swift` / `Audio/SpokenLanguageDetector.swift`: 翻訳ペアと、原文deltaの文字種・語の証拠による言語判定。
- `Realtime/SourceBoundaryTracker.swift`: 切替判定とは独立した原文境界候補の追跡。
- `OpenAI/`: Realtime Transcription / Translation WebSocket、イベントcodec、選択ペアのdual client。
- `Realtime/InterpretationSession.swift`: 接続、音声送信、字幕整列、状態遷移を統合。
- `Subtitles/`: 単一current字幕集約、透明オーバーレイ、録音コントロール、オプトイン時のローカル字幕記録。
- `Localization/`: `UserCopy`（`shared/locales/ui.json` をバンドルから読む）。
- `project.yml`: XcodeGen設定、Info.plist項目、権限説明の正本。

## 並行処理と起動

- UI、AppKit、セッション状態は`@MainActor`で扱う。
- `NSApplicationDelegate`メソッドは`nonisolated`を維持する。SDK既定のMainActor隔離へ戻すとCFRunLoop経由でクラッシュし得る。
- 起動は`DispatchQueue.main.async`と`MainActor.assumeIsolated { AppRuntime.start() }`を使い、delegate通知だけに依存しない。
- Core Audio、TCC、URLSession等のコールバックがMainActor上で呼ばれると仮定しない。
- リアルタイム音声tapではバッファをキューへ渡すだけにし、変換やネットワーク送信を行わない。
- `AVAudioConverter`は状態を持つため、単一feederタスクから直列に呼ぶ。
- WebSocket送信はactor境界で管理する。
- 非MainActorコールバックにMainActor継承クロージャを渡さない。明示的な`@Sendable`ヘルパーを使う。
- Swift 6 strict concurrencyを維持し、警告回避目的の`@unchecked Sendable`は境界型だけに限定する。
- importは必ずファイル先頭へ置き、関数内importを追加しない。

## 字幕パネル

- 字幕本文パネルはクリック透過、録音ボタンの別パネルだけを操作可能にする。
- `.floating`、`.canJoinAllSpaces`、`.fullScreenAuxiliary`を維持し、他アプリやフルスクリーン上の表示を壊さない。

## プロジェクト設定

- `RealtimeTranslator.xcodeproj`と`Info.plist`はXcodeGen生成物として扱う。
- 永続的なInfo.plist変更は`project.yml`の`targets.RealtimeTranslator.info.properties`へ追加する。
- `NSMicrophoneUsageDescription`を削除しない。用途説明はOpenAI送信を明示する。
- `RealtimeTranslator.entitlements`もXcodeGen生成物。`xcodegen generate`で上書きされるため、変更は`project.yml`の`targets.RealtimeTranslator.entitlements.properties`へ追加する。
- Hardened Runtime (`ENABLE_HARDENED_RUNTIME: YES`) を有効にしているため、`com.apple.security.device.audio-input`を削除しない。削除するとマイク許可済みでも`AVCaptureDevice.requestAccess(for: .audio)`が拒否される。
- App Sandboxは無効。将来Sandboxを有効化する場合だけ`network.client`を追加する。
- 同一Bundle IDの多重起動を許さない。Xcode実行と`run.sh`を同時に残さない。

## APIキーと実行時の診断

- 利用者自身のOpenAI APIキーはmacOS Keychainへ保存し、設定画面から取り込む。DEBUGビルドでは環境変数`OPENAI_API_KEY`からも自動取り込みできる。
- 実行状態はDEBUGビルドのみ`/tmp/realtimetranslator.status`へ書き出す（Releaseでは作らない）。
- クラッシュ時は最新のDiagnosticReportsと該当スレッドを確認し、推測だけで修正しない。

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
- macOS の `xcodebuild test` はローカルおよび `.github/workflows/release.yml` の package (macOS) ジョブでも検証する。
- 検証範囲と実機確認の要件はルートの「検証の選び方」に従う。手動項目は [VALIDATION.md の macOS版](../VALIDATION.md#macos版) を参照する。
- macOS Devbox の実デスクトップを操作・診断する場合は [macos-devbox-gui](../.agents/skills/macos-devbox-gui/SKILL.md) を参照する。使い捨て Devbox 専用の手順を実機へ適用しない。
