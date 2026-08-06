Realtime Translator の配布物です。OpenAI API キーはご自身のもの（BYOK）を使います。キーは各OSの安全な保管領域（Windows 資格情報マネージャー / macOS キーチェーン）にのみ保存されます。

## Windows (`RealtimeTranslator-<tag>-win-x64.zip`)

- 自己完結ビルドのため .NET のインストールは不要です。Windows 10 / 11 (x64)。
- zip を展開し `RealtimeTranslator.App.exe` を実行すると通知領域に常駐します（タスクバーにウィンドウは出ません）。
- 通知領域アイコンから「設定…」を開き、利用同意と API キーを保存してください。
- 既定のホットキーは `Control + Alt + Space` です。

## macOS (`RealtimeTranslator-<tag>-macos-arm64.zip`)

- macOS 26 以降 / Apple Silicon。
- Developer ID 署名・公証を行っていない ad-hoc 署名ビルドです。初回は Gatekeeper にブロックされるため、展開した `.app` を `/Applications` へ移してから隔離属性を外してください。

```bash
# zip を展開したディレクトリで
ditto RealtimeTranslator.app /Applications/RealtimeTranslator.app
xattr -dr com.apple.quarantine /Applications/RealtimeTranslator.app
open /Applications/RealtimeTranslator.app
```

- 起動するとメニューバーに常駐します。初回起動時にマイク使用許可を求められます。

## 共通

- 日本語音声は英語へ、英語音声は日本語へ自動翻訳し、字幕として重ねて表示します。
- SHA-256 は zip 内ではなく、Release アセットとして並ぶ sidecar です。
  - `RealtimeTranslator-<tag>-win-x64.zip.sha256`
  - `RealtimeTranslator-<tag>-macos-arm64.zip.sha256`

```bash
# macOS / Linux（zip と .sha256 を同じディレクトリに置いて）
shasum -a 256 -c RealtimeTranslator-<tag>-macos-arm64.zip.sha256
```

```powershell
# Windows（zip と .sha256 を同じディレクトリに置いて）
(Get-FileHash .\RealtimeTranslator-<tag>-win-x64.zip -Algorithm SHA256).Hash.ToLowerInvariant()
Get-Content .\RealtimeTranslator-<tag>-win-x64.zip.sha256
```
