# RealtimeTranslator 開発ガイド

macOS版（Swift / `RealtimeTranslator/`）とWindows版（.NET / `windows/`）の2実装がある。以下はmacOS版の規約で、共通の不変条件はWindows版にも適用する。Windows固有の規約は「Windows版」を参照する。

## 目的と不変条件

- OpenAI Realtime TranslationによるmacOS 26以降 / Windows向けのリアルタイム字幕アプリである。対応する翻訳ペアは `ja-en`、`ja-es`、`en-es`。
- 外部通信先は `api.openai.com/v1/realtime` と `api.openai.com/v1/realtime/translations` に限定する。それ以外の外部翻訳APIは追加しない。
- 利用者自身のOpenAI APIキー（BYOK）をmacOS Keychainへ保存する。事業者キーは同梱しない。
- 原文音声は専用 `gpt-live-transcribe` へ常時送信する。直近4秒のrolling prerollを常時保持し、判定前は原文のみ送信、判定後は選択された `languagePair` の相手言語をtargetとして送る。言語切替時は新targetへprerollをflushする。
- 言語切替を確定する時点と、原文を分割する位置を分離する。原文上の境界候補を追跡し、切替確定時に候補位置で分割する。候補がない場合の扱い、句読点・Unicode・英西の境界規則は `shared/fixtures/v2/subtitle.json` と両実装の `SourceBoundaryTracker` を参照する。切替判定時点を一律に原文境界にしない。
- Translationセッション付属のtranscriptionを原文authorityにしない。専用transcriptionの低遅延deltaを使い、原文と訳文を常にペア表示する。
- APIキー、Authorization、音声、原文、訳文をログ・status file・アラートへ出さない。
- オプトイン時のみ、確定した原文・訳文ペアをローカル字幕記録ファイルへ保存する（ログ・status・アラートへは出さない）。字幕記録の形式（`原文:` / `訳文:` / `=== 録音開始`）は表示言語に依存せず固定する。
- アプリ枠の表示言語は設定の `uiLanguage`（`system` / `ja` / `en`）。翻訳ペア `languagePair` とは独立で、反映はプロセス再起動後。文言正本は `shared/locales/ui.json`。スレッドカルチャは変更しない。
- 現在の仕様では翻訳音声を再生しない。`session.output_audio.delta` は受信するがデコードしない。

## 構成

- `App/`: AppKitライフサイクルと全体調停。SwiftUI `App`へ戻さない。
- `Security/`: Keychain APIキーストアと環境変数からの初回取り込み。
- `Audio/RealtimeAudioCaptureService.swift`: AVAudioEngine、24 kHz PCM16変換、100 ms packet化。
- `Audio/SpokenLanguage.swift` / `SpokenLanguageDetector.swift`: 翻訳ペアと、原文deltaの文字種・語の証拠による言語判定。
- `Realtime/SourceBoundaryTracker.swift`: 切替判定とは独立した原文境界候補の追跡。
- `OpenAI/`: Realtime Transcription / Translation WebSocket、イベントcodec、選択ペアのdual client。
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
- 原文deltaの末尾ウィンドウの証拠を言語切替とルーティングの信号として使う。日英・日西は文字種、英西は語などの証拠を使い、全ペアを文字種反転として扱わない。判定・ルーティングの契約は `shared/fixtures/v1/language.json` と `routing.json` を参照する。
- 受信イベントの上限超過を検知し、欠落した接続世代の未確定字幕を無効化する。欠落後のペアを確定・記録せず、既に確定した字幕は保持する。終了理由・エラー通知を通常イベントの混雑や正常終了通知で失わない。優先順位と容量の正本は `shared/fixtures/v1/receive-queue.json`。送信キューの上限とは区別する。
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
- `shared/fixtures/v<N>/` は両実装のバージョン付き契約正本。現行の subtitle 契約は v2、その他は v1 とし、Swift テストと Windows 版の同値性を保つ。既存の subtitle v1 も保持し、`scripts/ci-shared-contracts.sh` で全バージョンを検査する。
- Origin の PR CI は Depot CI（`.depot/workflows/`）が `shared-contracts` と Windows Core テストを実行する。Depot に Windows / macOS サンドボックスは無い。
- GitHub Actions（`.github/workflows/`）は GitHub 側の Windows 全体・macOS `xcodebuild`・タグ Release 用に残す。Platform / App / publish / 署名・公証はこちら。
- Depot を有効にするには Origin リポジトリの Apps から Depot を接続し、`.depot/workflows/` を default branch へマージする。
- macOS の `xcodebuild test` はローカルおよび `.github/workflows/release.yml` の package (macOS) ジョブでも検証する。

## テスト方針

- 純粋ロジックはXCTestで検証し、各テストに日本語のGiven/When/Thenコメントを付ける。
- 最低限、イベントcodec、100 ms packet化、専用原文transcription、原文送信分離とrolling preroll、言語切替セグメント分割、送信timeout、受信欠落とエラー優先順位、字幕lane選択、旧epoch破棄、停止時close drainの回帰検証を維持する。
- 非同期境界、空文字、句読点、停止時finalize、多重起動、秘密情報非漏洩の回帰を優先する。
- UIや音声経路を変更したら、対象プラットフォームのビルドと全テストに加えて、影響する言語ペアの両方向で実際に1文ずつ話して確認する。

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
