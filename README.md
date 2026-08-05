# Realtime Translator

macOSメニューバー常駐の、OpenAI Realtime Translationによるリアルタイム日英字幕アプリです。

マイク音声をOpenAIの `gpt-live-transcribe` と `gpt-realtime-translate` へストリーミングし、原文と翻訳字幕をペア表示します。初回MVPでは翻訳音声の再生は行いません。

## 要件

- macOS 26以降
- Apple Silicon
- Xcode 26 / XcodeGen
- インターネット接続（録音中は必須）
- OpenAI APIキー（BYOK）と利用可能な課金設定

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
