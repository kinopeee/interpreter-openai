# Realtime Translator

常駐型の、OpenAI Realtime Translationによるリアルタイム日英字幕アプリです。macOS版（メニューバー常駐）とWindows版（タスクトレイ常駐）があります。

マイク音声をOpenAIの `gpt-live-transcribe` と `gpt-realtime-translate` へストリーミングし、原文と翻訳字幕をペア表示します。初回MVPでは翻訳音声の再生は行いません。

Windows版の手順は [Windows版](#windows版) を参照してください。以下はmacOS版の説明です。

## 要件

- macOS 26以降
- Apple Silicon
- Xcode 26 / XcodeGen
- インターネット接続（録音中は必須）
- OpenAI APIキー（BYOK）と利用可能な課金設定

## ダウンロード（リリース）

ビルド済みの `RealtimeTranslator-<tag>-macos-arm64.zip` を [Releases](https://github.com/kinopeee/interpreter-openai/releases) から取得できます。Developer ID署名・公証を行っていないad-hoc署名ビルドです。SHA-256 は zip 内ではなく、同名の Release アセット `RealtimeTranslator-<tag>-macos-arm64.zip.sha256` です。起動前に検証し、成功後に `/Applications` へ移して隔離属性を外してください。

```bash
# zip と .sha256 を同じディレクトリに置いて
shasum -a 256 -c RealtimeTranslator-<tag>-macos-arm64.zip.sha256
# Linux では: sha256sum -c RealtimeTranslator-<tag>-macos-arm64.zip.sha256

ditto -x -k RealtimeTranslator-<tag>-macos-arm64.zip .
ditto RealtimeTranslator.app /Applications/RealtimeTranslator.app
xattr -dr com.apple.quarantine /Applications/RealtimeTranslator.app
open /Applications/RealtimeTranslator.app
```

## セットアップ

```bash
brew install xcodegen   # 未導入の場合
cd /Users/yoo/dev/interpreter-openai
xcodegen generate
open RealtimeTranslator.xcodeproj
```

APIキーは次のいずれかで登録します。

1. Xcode schemeの環境変数に `OPENAI_API_KEY` を設定して一度起動する（Keychainへ自動取り込み）
2. アプリの設定画面から `SecureField` で入力して保存する

CLIからビルド・起動する場合:

```bash
./scripts/run.sh
```

注意: `run.sh` は `open` 経由で起動するため、シェルの環境変数はアプリへ届かないことがあります。その場合はXcodeから取り込むか、設定画面で入力してください。`open --args` でキーを渡すのは禁止です。

## 使い方

1. アプリを起動します。Dockには表示されず、メニューバーと字幕オーバーレイに表示されます。
2. 初回はマイク使用を許可し、設定でOpenAI送信への同意とAPIキー保存を完了します。
3. メニューバーの開始/停止、または `Control + Option + Space` で録音を開始して話します。
4. 日本語音声は英語へ、英語音声は日本語へ自動翻訳されます。
5. 同じ操作で録音を停止します。

## 設定

メニューバーから設定を開きます。タブは次の3つです。

### 一般

| 項目 | 説明 |
| --- | --- |
| モデル / 翻訳方向 / 字幕表示 / 翻訳音声 | 現在の動作の説明（変更不可）。翻訳音声の再生はMVPでは行いません。 |
| マイク音声のOpenAI送信同意 | 録音開始前に必須。不同意のままでは翻訳を開始できません。 |
| APIキー | Keychainへ保存・削除。初回は環境変数 `OPENAI_API_KEY` からも取り込みできます。 |

### 音声認識

| 項目 | 説明 |
| --- | --- |
| ノイズ低減 | `近距離マイク`（`near_field`）または `会議・遠距離`（`far_field`、既定）。変更は次回の録音開始から反映されます。 |
| 認識遅延 | `gpt-live-transcribe` の `delay`。値を上げると短い発話の精度が上がり、字幕は遅くなります。既定は `低遅延`（`low`）。選択肢は最速 / 低遅延 / バランス / 高精度 / 最高精度。 |
| プリセット | 認識プロンプトとキーワードを一括適用（ソフトウェア開発 / ビジネス会議 / ハッカソン）。 |
| 認識プロンプト | 会話ドメインなどの文脈ヒント（最大1,000文字）。 |
| キーワード | 固有名詞など優先認識したい語。1行1語、最大64語。`<` `>` は送信時に除去されます。 |

プロンプト・キーワード・認識遅延の変更は、録音中でも数秒でセッションへ反映されます。

### 字幕・操作

| 項目 | 説明 |
| --- | --- |
| フォントサイズ | 字幕の文字サイズ（18–48pt、既定32pt）。 |
| 操作 | 開始/停止はメニューバー、または `Control + Option + Space`。 |

## アーキテクチャ

```text
マイク
  → AVAudioEngine
  → 24 kHz PCM16 mono / 100 ms frames
  → gpt-live-transcribe（常時送信、原文delta、delay既定low・設定変更可、far-field noise reduction）
  → Realtime Translation WebSocket × 2
      - 言語判定前は原文のみ＋直近4秒preroll
      - target=en（日本語判定後にprerollから送信、英訳）
      - target=ja（英語判定後にprerollから送信、和訳）
  → lane選択と字幕整列
  → 原文＋翻訳のNSPanel字幕
```

## 料金

録音中はOpenAI APIの従量課金が発生します。原文文字起こし1系統と、判定された言語に対応する翻訳1系統へ音声を送信します。固定価格は保証しません。最新料金は [OpenAI Pricing](https://developers.openai.com/api/docs/pricing) を確認してください。

## テスト

```bash
xcodegen generate
xcodebuild test \
  -scheme RealtimeTranslator \
  -destination 'platform=macOS' \
  -derivedDataPath ./build/DerivedData \
  -enableCodeCoverage YES
```

## 注意

- マイク音声、原文、訳文はOpenAI APIへ送信されます。
- オフラインでは翻訳できません。
- APIキーはKeychainへ保存し、ログへ出力しません。
- MVPでは翻訳音声の読み上げは行いません。

## Windows版

タスクトレイ常駐のWPFアプリです。エンドポイント、モデル、音声フォーマット、ルーティング、字幕semanticsはmacOS版と同一で、共有契約（`shared/`）のfixtureで同値性を検証しています。

### 要件

- Windows 10 / Windows 11（x64）。開発時の検証はWindows Server 2022（x64）で実施しています。
- マイク
- インターネット接続（録音中は必須）
- OpenAI APIキー（BYOK）と利用可能な課金設定
- ソースからビルドする場合は .NET 10 SDK

配布用の成果物は自己完結（self-contained）でpublishするため、実行側に .NET のインストールは不要です。`scripts/publish-windows.ps1 -Runtime win-arm64` で ARM64 成果物も出せますが、正式検証対象は x64 のみです（ARM64 は実験的）。

### ビルドと配布物の作成

```powershell
dotnet build windows/RealtimeTranslator.slnx -c Release
dotnet test  windows/RealtimeTranslator.slnx -c Release

# 自己完結の配布物を artifacts/RealtimeTranslator-win-x64 へ出力する
pwsh -File scripts/publish-windows.ps1
# PowerShell 7がない場合は Windows PowerShell でも実行できます（スクリプトは UTF-8 BOM）
powershell -ExecutionPolicy Bypass -File scripts/publish-windows.ps1
```

`windows` ワークフローは同じ手順を `windows-latest` で実行し、`RealtimeTranslator-win-x64` artifactを添付します。

### ダウンロード（リリース）

ビルド済みの配布物は [Releases](https://github.com/kinopeee/interpreter-openai/releases) から取得できます。SHA-256 は zip 内ではなく、同名の Release アセット `RealtimeTranslator-<tag>-win-x64.zip.sha256` です。展開・起動の前に検証してください。

```powershell
$expected = (Get-Content .\RealtimeTranslator-<tag>-win-x64.zip.sha256 -Raw).Trim().Split()[0].ToLowerInvariant()
$actual = (Get-FileHash .\RealtimeTranslator-<tag>-win-x64.zip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $expected) { throw "SHA-256 mismatch: expected $expected, got $actual" }
```

検証後に zip を展開し、`RealtimeTranslator.App.exe` を実行してください（自己完結ビルドなので .NET のインストールは不要）。

`v*` のタグを push すると `release` ワークフローがWindows（win-x64）とmacOS（arm64）の両方をテスト→ビルド→zip化し、同じReleaseへ添付します。macOS版はDeveloper ID署名・公証を行っていないad-hoc署名ビルドのため、ZIP 検証→`/Applications` 配置→隔離属性解除の順でインストールしてください（手順は上記「ダウンロード（リリース）」と同じ）。

```powershell
git tag v0.1.0; git push origin v0.1.0
```

### 使い方

1. `RealtimeTranslator.App.exe` を起動します。ウィンドウはタスクバーに出ず、通知領域アイコンと字幕オーバーレイだけが表示されます。多重起動はできません。
2. トレイアイコンを右クリックして「設定…」を開き、OpenAI送信への同意とAPIキーの保存を済ませます。
3. トレイの「翻訳を開始」、または `Control + Alt + Space` で録音を開始します。
4. 日本語音声は英語へ、英語音声は日本語へ自動翻訳されます。
5. 同じ操作で停止します。停止後、約5秒で字幕が消えます。

字幕オーバーレイは通常クリック透過で、背後のアプリ操作を妨げません。位置を変えるときはトレイの「字幕位置を編集」をONにし、字幕をドラッグして再度OFFにします（位置は保存され、作業領域内へクランプされます）。

### 設定

タブ構成と項目はmacOS版と同じ（一般 / 音声認識 / 字幕・操作）で、次の点だけWindows固有です。

| 項目 | Windowsでの扱い |
| --- | --- |
| APIキー | Windows資格情報マネージャー（汎用資格情報 `RealtimeTranslator:openai-api-key`）へ保存・削除します。設定ファイルには書きません。 |
| 開始/停止 | トレイメニュー、または `Control + Alt + Space`。 |
| 字幕位置 | トレイの「字幕位置を編集」でドラッグ移動して保存します。 |
| 設定の保存先 | `%LOCALAPPDATA%\RealtimeTranslator\settings.json`（フォントサイズ、字幕位置、同意状態、認識プロンプト・キーワード・遅延・ノイズ低減）。 |

プロンプト・キーワード・認識遅延の変更は録音中でも数秒でセッションへ反映されます。ノイズ低減の変更は次回の録音開始から反映されます。

### アーキテクチャ（Windows）

```text
マイク
  → WASAPI (NAudio)
  → 24 kHz PCM16 mono / 100 ms frames
  → RealtimeTranslator.Core（codec / packetizer / gain / 言語判定 / 字幕整列。macOS版と共有契約で同値）
  → RealtimeTranslator.Platform（WASAPI・資格情報マネージャー・多重起動防止・グローバルホットキー・秘匿ログ）
  → RealtimeTranslator.App（WPF: トレイ・設定・クリック透過オーバーレイ）
```

### 注意（Windows）

- マイク音声、原文、訳文はOpenAI APIへ送信されます。
- APIキーは資格情報マネージャーへ保存し、ログ・設定ファイルへ出力しません。
- 実機での確認項目は `VALIDATION.md` の「Windows版」を参照してください。
