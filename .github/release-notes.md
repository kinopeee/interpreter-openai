Realtime Translator の配布物です。OpenAI API キーはご自身のもの（BYOK）を使います。キーは各OSの安全な保管領域（Windows 資格情報マネージャー / macOS キーチェーン）にのみ保存されます。

## プライバシーとコード署名

- [プライバシーポリシー](https://github.com/kinopeee/interpreter-openai/blob/main/PRIVACY.md)
- [Code signing policy / コード署名ポリシー](https://github.com/kinopeee/interpreter-openai/blob/main/CODE_SIGNING_POLICY.md)
- Windows向けコード署名はSignPath Foundationへ申請準備中です。承認前のWindows成果物は未署名です。

承認後、SignPath経由で署名したリリースには次のクレジットを表示します。

> Free code signing provided by SignPath.io, certificate by SignPath Foundation

## Windows (`RealtimeTranslator-<tag>-win-x64.zip`)

- 自己完結ビルドのため .NET のインストールは不要です。Windows 10 / 11 (x64)。
- zip を展開し `RealtimeTranslator.App.exe` を実行すると通知領域に常駐します（タスクバーにウィンドウは出ません）。
- 通知領域アイコンから「設定…」を開き、利用同意と API キーを保存してください。
- 既定のホットキーは `Control + Alt + Space` です。

SHA-256 は zip 内ではなく、Release アセット `RealtimeTranslator-<tag>-win-x64.zip.sha256` です。展開・起動の前に検証してください。

```powershell
# zip と .sha256 を同じディレクトリに置いて
$expected = (Get-Content .\RealtimeTranslator-<tag>-win-x64.zip.sha256 -Raw).Trim().Split()[0].ToLowerInvariant()
$actual = (Get-FileHash .\RealtimeTranslator-<tag>-win-x64.zip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $expected) { throw "SHA-256 mismatch: expected $expected, got $actual" }
```

## macOS (`RealtimeTranslator-<tag>-macos-arm64.zip`)

- macOS 26 以降 / Apple Silicon。
- Developer ID Application 署名済みで、公証（notarization）と staple 済みです。ZIP を検証してから `.app` を `/Applications` へ移してください。

SHA-256 は zip 内ではなく、Release アセット `RealtimeTranslator-<tag>-macos-arm64.zip.sha256` です。

```bash
# zip と .sha256 を置いた作業ディレクトリで実行する（/Applications 直下は書き込み保護されており
# `ditto: .: Operation not permitted` になる）
cd ~/Downloads

shasum -a 256 -c RealtimeTranslator-<tag>-macos-arm64.zip.sha256

ditto -x -k RealtimeTranslator-<tag>-macos-arm64.zip .
ditto RealtimeTranslator.app /Applications/RealtimeTranslator.app
open /Applications/RealtimeTranslator.app
```

- 起動するとメニューバーに常駐します。初回起動時にマイク使用許可を求められます。

## 共通

- 設定の翻訳方向で `日本語 ↔ 英語` / `日本語 ↔ スペイン語` / `英語 ↔ スペイン語` を選べます。変更は次回の録音開始から反映されます。
- アプリ枠の表示言語は設定の「表示言語」（システム / 日本語 / 英語）です。反映はプロセス再起動後です。
- 原文と訳文をペアで字幕表示します。
