Realtime Translator の配布物です。OpenAI API キーはご自身のもの（BYOK）を使います。キーは各OSの安全な保管領域（Windows 資格情報マネージャー / macOS キーチェーン）にのみ保存されます。

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
- Developer ID 署名・公証を行っていない ad-hoc 署名ビルドです。初回は Gatekeeper にブロックされるため、ZIP を検証してから `.app` を `/Applications` へ移し、隔離属性を外してください。

SHA-256 は zip 内ではなく、Release アセット `RealtimeTranslator-<tag>-macos-arm64.zip.sha256` です。

```bash
# zip と .sha256 を置いた作業ディレクトリで実行する（/Applications 直下は書き込み保護されており
# `ditto: .: Operation not permitted` になる）
cd ~/Downloads

# 起動前に検証
shasum -a 256 -c RealtimeTranslator-<tag>-macos-arm64.zip.sha256

# 検証成功後に展開し、/Applications へ配置して起動
ditto -x -k RealtimeTranslator-<tag>-macos-arm64.zip .
ditto RealtimeTranslator.app /Applications/RealtimeTranslator.app
xattr -dr com.apple.quarantine /Applications/RealtimeTranslator.app
open /Applications/RealtimeTranslator.app
```

- 起動するとメニューバーに常駐します。初回起動時にマイク使用許可を求められます。

## 共通

- 日本語音声は英語へ、英語音声は日本語へ自動翻訳し、字幕として重ねて表示します。
