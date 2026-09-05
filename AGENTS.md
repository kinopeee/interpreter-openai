Shared fixtures live under `shared/fixtures/v<N>` and are the canonical contract
for both Swift and Windows implementations. The subtitle source-boundary
contract is version 2; `scripts/ci-shared-contracts.sh` validates every version.
# RealtimeTranslator 開発ガイド

macOS版（Swift / `RealtimeTranslator/`）とWindows版（.NET / `windows/`）の2実装がある。以下はmacOS版の規約で、共通の不変条件はWindows版にも適用する。Windows固有の規約は「Windows版」を参照する。

## 目的と不変条件

- macOS 26以降向けの、OpenAI Realtime Translationによるリアルタイム日英字幕アプリである。
- 外部通信先は `api.openai.com/v1/realtime` と `api.openai.com/v1/realtime/translations` に限定する。それ以外の外部翻訳APIは追加しない。
- 利用者自身のOpenAI APIキー（BYOK）をmacOS Keychainへ保存する。事業者キーは同梱しない。
- 原文音声は専用 `gpt-live-transcribe` へ常時送信する。直近4秒のrolling prerollを常時保持し、判定前は原文のみ送信、判定後は日本語なら `target=en`、英語なら `target=ja` へ送る。言語切替時は新targetへprerollをflushする。
- 原文文字種の反転（末尾ウィンドウ判定）をセグメント境界として扱い、確定・ルーティング・prerollを切り替える。
- Translationセッション付属のtranscriptionを原文authorityにしない。専用transcriptionの低遅延deltaを使い、原文と訳文を常にペア表示する。
- APIキー、Authorization、音声、原文、訳文をログ・status file・アラートへ出さない。
- オプトイン時のみ、確定した原文・訳文ペアをローカル字幕記録ファイルへ保存する（ログ・status・アラートへは出さない）。字幕記録の形式（`原文:` / `訳文:` / `=== 録音開始`）は表示言語に依存せず固定する。
- アプリ枠の表示言語は設定の `uiLanguage`（`system` / `ja` / `en`）。翻訳ペア `languagePair` とは独立で、反映はプロセス再起動後。文言正本は `shared/locales/ui.json`。スレッドカルチャは変更しない。
- 初回MVPでは翻訳音声を再生しない。`session.output_audio.delta` は受信するがデコードしない。

## 構成

- `App/`: AppKitライフサイクルと全体調停。SwiftUI `App`へ戻さない。
- `Security/`: Keychain APIキーストアと環境変数からの初回取り込み。
- `Audio/RealtimeAudioCaptureService.swift`: AVAudioEngine、24 kHz PCM16変換、100 ms packet化。
- `Audio/SpokenLanguage.swift` / `SpokenLanguageDetector.swift`: 原文deltaの文字種判定と末尾ウィンドウによる言語切替検出。
- `OpenAI/`: Realtime Transcription / Translation WebSocket、イベントcodec、日英dual client。
- `Realtime/InterpretationSession.swift`: 接続、音声送信、字幕整列、状態遷移を統合。
- `Subtitles/`: 単一current字幕集約、透明オーバーレイ、録音コントロール、オプトイン時のローカル字幕記録。
- `Localization/`: `UserCopy`（`shared/locales/ui.json` をバンドルから読む）。
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

- 原文は `wss://api.openai.com/v1/realtime?intent=transcription` と `gpt-live-transcribe` を使う。`delay` は既定 `low`（設定で `minimal`〜`xhigh` に変更可）、noise reduction は既定 `far_field`。
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
- 字幕は単一のcurrentスロットのみ。録音中の確定ペアもその場に残し、次発話開始で上書きする（履歴ブロックなし、録音中のタイマー消去なし）。録音停止後は約5秒でcurrentを消す。原文だけを確定しない。
- 更新待ちの旧訳文は`isTranslationCurrent = false`かつ`canFinalize = false`にする。
- 発話途中の原文表示は約160ms間隔に抑え、行高を維持してちらつきを防ぐ。
- パネル高は複数行の1ブロックを収め、ベースラインをクリップしない。

## プロジェクト設定

- `RealtimeTranslator.xcodeproj`と`Info.plist`はXcodeGen生成物として扱う。
- 永続的なInfo.plist変更は`project.yml`の`targets.RealtimeTranslator.info.properties`へ追加する。
- `NSMicrophoneUsageDescription`を削除しない。用途説明はOpenAI送信を明示する。
- `RealtimeTranslator.entitlements`もXcodeGen生成物。`xcodegen generate`で上書きされるため、変更は`project.yml`の`targets.RealtimeTranslator.entitlements.properties`へ追加する。
- Hardened Runtime (`ENABLE_HARDENED_RUNTIME: YES`) を有効にしているため、`com.apple.security.device.audio-input`を削除しない。削除するとマイク許可済みでも`AVCaptureDevice.requestAccess(for: .audio)`が拒否される。
- App Sandboxは無効。将来Sandboxを有効化する場合だけ`network.client`を追加する。
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
- APIキーはKeychainへ保存する。設定画面から取り込む。DEBUGビルドでは環境変数`OPENAI_API_KEY`からも自動取り込みできる。
- 権限、実API、実マイク、オンライン動作はユニットテストだけでは検証できない。
- 手動検証項目は`VALIDATION.md`も参照する。
- 実行状態はDEBUGビルドのみ`/tmp/realtimetranslator.status`へ書き出す（Releaseでは作らない）。
- クラッシュ時は最新のDiagnosticReportsと該当スレッドを確認し、推測だけで修正しない。
- ログへ認識した発話内容、APIキー、Authorizationを出力しない。
- `shared/fixtures/v1` は両実装の契約正本。Swiftテストからも読み、Windows版と同値性を保つ。契約検査の正本は `scripts/ci-shared-contracts.sh`。
- Origin の PR CI は Depot CI（`.depot/workflows/`）が `shared-contracts` と Windows Core テストを実行する。Depot に Windows / macOS サンドボックスは無い。
- GitHub Actions（`.github/workflows/`）は GitHub 側の Windows 全体・macOS `xcodebuild`・タグ Release 用に残す。Platform / App / publish / 署名・公証はこちら。
- Depot を有効にするには Origin リポジトリの Apps から Depot を接続し、`.depot/workflows/` を default branch へマージする。
- macOS の `xcodebuild test` はローカルおよび `.github/workflows/release.yml` の package (macOS) ジョブでも検証する。

## テスト方針

- 純粋ロジックはXCTestで検証し、各テストに日本語のGiven/When/Thenコメントを付ける。
- 最低限、イベントcodec、100 ms packet化、専用原文transcription、原文送信分離とrolling preroll、言語切替セグメント分割、送信timeout、字幕lane選択、旧epoch破棄、停止時close drainを維持する。
- 非同期境界、空文字、句読点、停止時finalize、多重起動、秘密情報非漏洩の回帰を優先する。
- UIや音声経路を変更したら、ビルドと全テストに加えて実際に日英を1文ずつ話して確認する。

## Windows版

### 構成

- `windows/RealtimeTranslator.slnx`: .NET 10 solution。プロジェクト追加はここへ登録する。
- `windows/src/RealtimeTranslator.Core/`: OS非依存。codec、tuning、packetizer、gain、言語判定、字幕整列、接続、`InterpretationSession`、字幕snapshot・geometry・設定codec、`UserCopy`。Windows APIやWPF型を持ち込まない。
- `windows/src/RealtimeTranslator.Platform/`: Windows固有。WASAPI capture、資格情報マネージャー、install identifier、多重起動防止、グローバルホットキー、ログ、設定ファイル、字幕記録ファイル。
- `windows/src/RealtimeTranslator.App/`: WPFシェル（composition root、トレイ、設定ウィンドウ、字幕オーバーレイ）。ロジックは持たずCoreへ委譲する。
- `windows/tests/`: `RealtimeTranslator.Core.Tests` と `RealtimeTranslator.Platform.Tests`（xUnit）。
- `shared/`: 言語中立の契約とfixture。両実装の同値性はここを正本にする。

### 不変条件（Windows固有）

- APIキーはWindows資格情報マネージャー（汎用資格情報 `RealtimeTranslator:openai-api-key`）へ保存する。`settings.json` などの平文設定へ書かない。
- 設定は `%LOCALAPPDATA%\RealtimeTranslator\settings.json` へ一時ファイル + 置換で保存する。書き込み中の破損ファイルを残さない。`uiLanguage`（`system` / `ja` / `en`）もここに保存し、APIキーは書かない。
- install identifierは生成値そのものを送らず、小文字SHA-256 hexだけを `OpenAI-Safety-Identifier` に載せる。`OpenAI-Beta` は送らない。
- 音声は 24 kHz / PCM16 / mono / little-endian / 100 msフレーム（2,400 samples・4,800 bytes）。フレームchannelは容量32の`DropOldest`で、遅延を溜めずに落とす。
- 原文送信は翻訳送信と分離する。翻訳送信が3回連続で失敗したらtransport errorを1回通知して翻訳pumpを止め、再接続へ回す。
- 字幕は単一currentスロット。日本語60文字・英語120文字で末尾を`…`へ切り詰める。停止後は約5秒でcurrentを消す。
- オプトイン時の字幕記録ファイルは `%LOCALAPPDATA%\RealtimeTranslator\transcripts\session.txt` へ追記する（ログ・status・アラートへ本文は出さない）。記録形式（`原文:` / `訳文:` / `=== 録音開始`）は `uiLanguage` でも変えない。
- オーバーレイは通常時 `WS_EX_TRANSPARENT` / `WS_EX_NOACTIVATE` / `WS_EX_TOOLWINDOW` でクリック透過。位置編集モードのみ透過を外してドラッグを受ける。位置は作業領域へクランプして保存する。
- 多重起動は`SingleInstanceLease`でUI生成前に判定する。2個目は案内ダイアログのみで終了する。
- ホットキーは既定 `Control + Alt + Space`（`NoRepeat`）。受け皿は常駐しているオーバーレイのHWNDにする。

### WPF固有の注意

- `UseWindowsForms=true` のためDPIはマニフェストで宣言できない（WFO0003）。`ApplicationHighDpiMode=PerMonitorV2` と `DpiBootstrap`（`ModuleInitializer` から `ApplicationConfiguration.Initialize()`）で適用する。
- WPFの`XmlLanguage`は具体カルチャを解決するため、Appプロジェクトでは`InvariantGlobalization=false`を維持する。trueに戻すと起動時に落ちる。
- `ShutdownMode=OnExplicitShutdown`で通常のメインウィンドウを持たない。トレイ常駐前提を崩さない。
- セッションからのイベントは`Dispatcher`へ渡してからUIへ反映する。UI要素をワーカースレッドから触らない。
- ComboBoxには`DisplayMemberPath`を指定する。指定漏れはrecordの`ToString()`がそのまま表示される。

### ビルドと検証

```powershell
dotnet build windows/RealtimeTranslator.slnx -c Release
dotnet test  windows/RealtimeTranslator.slnx -c Release
dotnet list  windows/RealtimeTranslator.slnx package --vulnerable --include-transitive

# 配布物（自己完結）。framework-dependentにするとランタイム要求ダイアログが出る。
pwsh -File scripts/publish-windows.ps1
```

Origin の Depot CI は Linux 上で Core のみ検証する。正本は `scripts/ci-windows-core.sh`。

```bash
./scripts/ci-shared-contracts.sh
./scripts/ci-windows-core.sh
```

- 警告は`TreatWarningsAsErrors`で失敗する。抑制ではなく修正する。
- 純粋ロジックはxUnitで検証し、各テストに日本語のGiven/When/Thenコメントを付ける。
- 権限、実API、実マイク、複数モニタ、フルスクリーン前面表示はユニットテストで検証できない。`VALIDATION.md`の「Windows版」を使う。
