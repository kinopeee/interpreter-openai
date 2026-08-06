# OpenAI Realtime版 検証記録

## macOS版

日付: 2026-08-05

### 自動検証

- `xcodegen generate`: 成功
- `xcodebuild build`: 成功（`platform=macOS` / `./build/DerivedData`）
- `xcodebuild test`: 成功（94 tests, 0 failures, coverage enabled）
- カバーした単体テスト:
  - Keychain / 環境変数取り込み
  - Realtimeイベントcodec
  - 100 ms PCM16 packet化
  - Connection handshake / auth失敗 / close drain / send timeout
  - 専用live transcription・原文送信分離・preroll・翻訳停滞時の原文継続
  - 字幕lane選択・句読点/idle確定
  - InterpretationSessionの開始ゲート・二重stop・再接続・秘密非漏洩

### 実APIスモーク（マイクなし）

同一APIキーで `gpt-live-transcribe`、`target=en`、`target=ja` を同時接続できることを確認済み。専用transcriptionはcommit前から最初の原文deltaを約0.34秒で返すことも確認した。

- 結果: 専用transcription接続・継続delta受信に成功
- 字幕本文・APIキーはログへ未出力

### 実マイク検証（手動）

```bash
cd /Users/yoo/dev/interpreter-openai
# 事前にXcode schemeまたは設定画面でAPIキーをKeychainへ保存する
./scripts/run.sh
```

1. 初回のマイク権限と、設定画面での同意・APIキー保存を完了します。
2. 字幕上の「録音開始」を押します。
3. 日本語を話し、原文と英訳が表示されることを確認します。
4. 続けて英語を話し、原文と和訳が再起動なしで切り替わることを確認します。
5. 「録音終了」を押し、停止直前の発話が完全ペアとして残ることを確認します。

追加確認:

- [ ] Cursor / Chrome / Zoom / Keynoteより前面に表示される
- [ ] フルスクリーン中も字幕が表示される
- [ ] 字幕本文上のクリックが背後アプリへ届く
- [ ] 1語、固有名詞、数字、早口、長文、日英混在で誤lane・重複表示がない
- [ ] 無効キー、ネット切断、片側socket切断、再接続上限を正しく表示する
- [ ] ログ、status file、クラッシュ情報にAPIキー・音声・字幕本文がない
- [ ] 1時間連続でqueue増大、buffer leak、再接続loopがなく、OpenAI usageが想定範囲である

## Windows版

日付: 2026-08-06 / 環境: Windows Server 2022 Standard（x64）、.NET 10 SDK

### 自動検証

```powershell
dotnet build windows/RealtimeTranslator.slnx -c Release
dotnet test  windows/RealtimeTranslator.slnx -c Release
dotnet list  windows/RealtimeTranslator.slnx package --vulnerable --include-transitive --format json --output-version 1
pwsh -File scripts/publish-windows.ps1
```

- `dotnet build -c Release`: 成功（0 warning / 0 error、`TreatWarningsAsErrors` 有効）
- `dotnet test -c Release`: 成功（Core 205 + Platform 18 = 223 tests、0 failures）
- `dotnet list package --vulnerable --include-transitive --format json --output-version 1`: 全5 projectで `vulnerabilities` 件数 0（CIのAuditも同判定）
- Snyk Code（`snyk code test windows`）: 0 件
- `scripts/publish-windows.ps1`: 自己完結（win-x64）publish成功。出力された `RealtimeTranslator.App.exe` の起動と常駐（応答あり）を確認。`win-arm64` はスクリプトで生成可能だが本検証の対象外（実験的）
- カバーした単体テスト:
  - shared fixture同値性（audio / codec / language / routing / subtitle / tuning / privacy）
  - Realtimeイベントcodec、100 ms PCM16 packet化、adaptive gain
  - 3接続handshake / auth失敗 / close drain / send timeout / 再接続
  - 専用live transcription・原文送信分離・4秒rolling preroll・言語切替時のpreroll flush・翻訳停滞時の原文継続
  - 字幕lane選択・句読点/idle確定・末尾切り詰め・旧epoch破棄・Error時のバナー抑止
  - WASAPI frame pipeline（4,800 bytes固定・`DropOldest(32)`）、mono downmix
  - 資格情報マネージャー往復、install identifierのhash化、多重起動防止、ログの秘密非出力

### 実マイク検証（仮想オーディオケーブル）

仮想オーディオケーブルを入力デバイスとして、本番と同じWASAPIキャプチャ経路で実音声を流し、100 msフレーム30個すべてが4,800 bytes（peak=16384）で流れることを確認済み。

### GUI検証（手動・実施済み）

1. トレイ常駐で起動し、通常のメインウィンドウが出ないことを確認します。
2. トレイメニューから設定を開き、同意とAPIキー保存を行い、資格情報マネージャーに `RealtimeTranslator:openai-api-key` が作られることを確認します。
3. APIキー削除で当該資格情報が消えることを確認します。
4. フォントサイズsliderとチューニング項目を変更し、`%LOCALAPPDATA%\RealtimeTranslator\settings.json` へ反映されることを確認します。
5. 位置編集モードでオーバーレイをドラッグし、作業領域内へクランプされた位置が保存されることを確認します。
6. 通常モードで字幕上のクリックが背後アプリへ届くことを確認します。
7. 再起動して設定・位置が復元されることを確認します。
8. トレイから終了し、トレイアイコンが残らないことを確認します。
9. 2個目のプロセスが案内のみで終了することを確認します。

- 結果: 上記すべてパス（設定ウィンドウの古いsnapshot参照、閉じ際のdebounce flushの回帰も含む）
- 記録: GUIテストの録画とレポートはセッション成果物として提出済み

### ライブ検証（実OpenAI APIキー・実施済み）

APIキーはマスク済み入力欄から資格情報マネージャーへ保存し、検証後に削除。音声は仮想オーディオケーブル経由で入力しました。

1. 日本語音声を流し、日本語原文と英訳がペア表示されることを確認しました。
2. 続けて英語音声を流し、アプリを再起動せずに（同一プロセス・Listening継続）英語原文と和訳へレーンが切り替わることを確認しました。
3. 録音中にプロンプトを編集し、debounce（800 ms）が切れる前に設定ウィンドウを閉じて、`transcriptionPrompt` が `settings.json` へ反映され字幕が継続することを確認しました（設定保存と字幕継続のみ）。
4. 録音停止後、直前の発話がペアで残り、約5〜6秒で字幕が消えて待機バナーへ戻ることを確認しました。
5. `settings.json`・ログ・トレイ通知に `sk-` / `Bearer` / `Authorization` / 字幕本文 / 音声データが出ないことを確認しました。
6. ブラウザ動画の音声を入力にしたデモを2本録画しました（日本語ニュース→英訳、英語トーク→和訳）。

- 結果: 上記1〜6はパス（項目3は `settings.json` 保存と字幕継続まで）。通信エラー・レート制限は発生せず
- 環境上の注意（アプリの不具合ではない）: 仮想オーディオケーブル導入後はブラウザを再起動しないと音声が仮想ケーブルへ流れない

### 未検証

- [ ] 録音中に設定を閉じた際の debounce flush が、現在のセッションへ `session.update` として送信・反映されること（UIから直接観測できず未検証）
- [ ] 無効キー、ネット切断、片側socket切断、再接続上限の表示
- [ ] 1時間連続でqueue増大、buffer leak、再接続loopがなく、OpenAI usageが想定範囲である

### 検証対象外（合意済み）

- Swiftのローカル実行（macOS環境のため本作業では対象外）
- 複数モニタ / 高DPI環境でのGUI確認（検証環境がない）
- `win-arm64` 配布物の実機検証（publishスクリプトは対応、正式サポートは x64 のみ）
