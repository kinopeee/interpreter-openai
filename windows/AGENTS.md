# Windows 開発ガイド

[ルートの AGENTS.md](../AGENTS.md) の共通規約に追加して適用する。対象は `windows/` と Windows のビルド・配布スクリプト、workflow。以下のパスとコマンドはすべてリポジトリルートを基準とする。

## 構成

- `windows/RealtimeTranslator.slnx`: .NET 10 solution。プロジェクト追加はここへ登録する。
- `windows/src/RealtimeTranslator.Core/`: OS非依存。codec、tuning、packetizer、gain、言語判定、字幕整列、接続、`InterpretationSession`、字幕snapshot・geometry・設定codec、`UserCopy`。Windows APIやWPF型を持ち込まない。
- `windows/src/RealtimeTranslator.Platform/`: Windows固有。WASAPI capture、資格情報マネージャー、install identifier、多重起動防止、グローバルホットキー、ログ、設定ファイル、字幕記録ファイル。
- `windows/src/RealtimeTranslator.App/`: WPFシェル（composition root、トレイ、設定ウィンドウ、字幕オーバーレイ）。ロジックは持たずCoreへ委譲する。
- `windows/tests/`: `RealtimeTranslator.Core.Tests` と `RealtimeTranslator.Platform.Tests`（xUnit）。

## 不変条件（Windows固有）

- APIキーはWindows資格情報マネージャー（汎用資格情報 `RealtimeTranslator:openai-api-key`）へ保存する。`settings.json` などの平文設定へ書かない。
- 設定は `%LOCALAPPDATA%\RealtimeTranslator\settings.json` へ一時ファイル + 置換で保存する。書き込み中の破損ファイルを残さない。`uiLanguage`（`system` / `ja` / `en`）もここに保存し、APIキーは書かない。
- install identifierは生成値そのものを送らず、小文字SHA-256 hexだけを `OpenAI-Safety-Identifier` に載せる。`OpenAI-Beta` は送らない。
- 音声は 24 kHz / PCM16 / mono / little-endian / 100 msフレーム（2,400 samples・4,800 bytes）。フレームchannelは容量32の`DropOldest`で、遅延を溜めずに落とす。
- 翻訳送信が3回連続で失敗したらtransport errorを1回通知して翻訳pumpを止め、再接続へ回す。
- 字幕はCJKを含む場合60文字、それ以外は120文字を上限に末尾を残し、省略した先頭に `…` を付ける。文字数と語境界の扱いは `windows/src/RealtimeTranslator.Core/Subtitles/SubtitleTailClipper.cs` を正本とする。
- オプトイン時の字幕記録ファイルは `%LOCALAPPDATA%\RealtimeTranslator\transcripts\session.txt` へ追記する。記録内容と形式はルートの共通規約に従う。
- オーバーレイは通常時 `WS_EX_TRANSPARENT` / `WS_EX_NOACTIVATE` / `WS_EX_TOOLWINDOW` でクリック透過。位置編集モードのみ透過を外してドラッグを受ける。位置は作業領域へクランプして保存する。
- 多重起動は`SingleInstanceLease`でUI生成前に判定する。2個目は案内ダイアログのみで終了する。
- ホットキーは既定 `Control + Alt + Space`（`NoRepeat`）。受け皿は常駐しているオーバーレイのHWNDにする。

## WPF固有の注意

- `UseWindowsForms=true` のためDPIはマニフェストで宣言できない（WFO0003）。`ApplicationHighDpiMode=PerMonitorV2` と `DpiBootstrap`（`ModuleInitializer` から `ApplicationConfiguration.Initialize()`）で適用する。
- WPFの`XmlLanguage`は具体カルチャを解決するため、Appプロジェクトでは`InvariantGlobalization=false`を維持する。trueに戻すと起動時に落ちる。
- `ShutdownMode=OnExplicitShutdown`で通常のメインウィンドウを持たない。トレイ常駐前提を崩さない。
- セッションからのイベントは`Dispatcher`へ渡してからUIへ反映する。UI要素をワーカースレッドから触らない。
- ComboBoxには`DisplayMemberPath`を指定する。指定漏れはrecordの`ToString()`がそのまま表示される。

## ビルドと検証

```powershell
dotnet build windows/RealtimeTranslator.slnx -c Release
dotnet test  windows/RealtimeTranslator.slnx -c Release
dotnet list  windows/RealtimeTranslator.slnx package --vulnerable --include-transitive

# 配布物（自己完結）。framework-dependentにするとランタイム要求ダイアログが出る。
pwsh -File scripts/publish-windows.ps1
```

Linux 上で Windows Core を検証するコマンド:

```bash
./scripts/ci-shared-contracts.sh
./scripts/ci-windows-core.sh
```

- 警告は`TreatWarningsAsErrors`で失敗する。抑制ではなく修正する。
- 検証範囲と実機確認の要件はルートの「検証の選び方」に従う。権限、実API、実マイク、複数モニタ、フルスクリーン前面表示は [VALIDATION.md の Windows版](../VALIDATION.md#windows版) を使う。
- Windows VM での起動・GUI検証は [windows-realtimetranslator-gui-testing](../.agents/skills/windows-tray-app-testing/SKILL.md)、マイクなしの字幕オーバーレイ視覚検証は [wpf-subtitle-overlay-visual-testing](../.agents/skills/wpf-subtitle-overlay-visual-testing/SKILL.md) を参照する。
