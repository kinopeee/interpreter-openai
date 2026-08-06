Windows 版 Realtime Translator (win-x64)。

- 自己完結ビルドのため .NET のインストールは不要です。
- zip を展開し `RealtimeTranslator.App.exe` を実行すると通知領域に常駐します（タスクバーにウィンドウは出ません）。
- 初回は通知領域アイコンから「設定…」を開き、利用同意と OpenAI API キー（BYOK）を保存してください。キーは Windows 資格情報マネージャー (`RealtimeTranslator:openai-api-key`) にのみ保存されます。
- 既定のホットキーは `Control + Alt + Space` です。日本語音声は英語へ、英語音声は日本語へ自動翻訳されます。
- 同梱の `.sha256` で zip の SHA-256 を検証できます。
