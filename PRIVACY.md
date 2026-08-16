# Privacy Policy / プライバシーポリシー

Effective date: August 17, 2026

## English

### 1. Scope

This policy describes the data handling of Realtime Translator for macOS and
Windows. The open-source project is maintained by
[@kinopeee](https://github.com/kinopeee).

Realtime Translator has no maintainer-operated backend, account system,
analytics service, advertising service, or telemetry endpoint. The maintainer
does not receive users' API keys, microphone audio, transcripts, translations,
prompts, or keywords through the app.

### 2. Data sent to OpenAI

The app does not send microphone audio until the user:

1. accepts the in-app OpenAI data-transfer notice, and
2. starts recording.

While recording, the app connects directly from the user's device to:

- `wss://api.openai.com/v1/realtime?intent=transcription`
- `wss://api.openai.com/v1/realtime/translations`

The following data may be sent to OpenAI:

- microphone audio;
- the user's OpenAI API key in the authorization header;
- transcription prompts and keywords configured by the user;
- session settings required for transcription and translation; and
- a SHA-256 hash of a randomly generated installation identifier in the
  `OpenAI-Safety-Identifier` header.

The unhashed installation identifier is not sent. OpenAI returns source
transcription and translated text to the app for display.

These requests use the user's own OpenAI API key and OpenAI account. OpenAI's
handling and retention of API data are governed by its own terms and policies:

- [OpenAI API data usage policies](https://developers.openai.com/api/docs/guides/your-data)
- [OpenAI Privacy Policy](https://openai.com/policies/privacy-policy/)

### 3. Data stored on the device

The app stores the following data locally:

- **API key**
  - macOS: Keychain
  - Windows: Credential Manager, generic credential
    `RealtimeTranslator:openai-api-key`
- **Settings**, including consent state, subtitle position, display language,
  transcription prompt, keywords, delay, and noise-reduction preference
  - macOS: system `UserDefaults` for `com.realtimetranslator.app`
  - Windows: `%LOCALAPPDATA%\RealtimeTranslator\settings.json`
- **Installation identifier**
  - generated randomly on first use;
  - stored in macOS `UserDefaults` or the Windows
    `HKCU\Software\RealtimeTranslator` registry key; and
  - sent to OpenAI only after SHA-256 hashing.
- **Subtitle transcript**, only when the user explicitly enables local subtitle
  recording
  - macOS:
    `~/Library/Application Support/com.realtimetranslator.app/transcripts/session.txt`
    (the operating-system temporary directory is used as a fallback if
    Application Support cannot be resolved)
  - Windows:
    `%LOCALAPPDATA%\RealtimeTranslator\transcripts\session.txt`

The app does not save microphone audio. Logs and debug status output are
designed not to contain API keys, authorization headers, audio, source
transcripts, or translations.

### 4. Retention and deletion

Local data remains on the user's device until the user deletes it or removes
the corresponding operating-system storage.

Users can:

- delete or replace the API key from the app's Settings;
- clear locally recorded subtitles from the app;
- disable local subtitle recording at any time;
- delete the Windows `%LOCALAPPDATA%\RealtimeTranslator` directory;
- delete the Windows `HKCU\Software\RealtimeTranslator` registry key containing
  the local installation identifier;
- delete the macOS Application Support directory shown above; and
- reset macOS preferences for `com.realtimetranslator.app`.

Removing the application alone may not remove all operating-system credentials,
preferences, transcripts, or exported subtitle copies.

OpenAI controls retention of data processed by its API. Consult the OpenAI
policies linked above and the settings of the user's OpenAI account.

### 5. Sharing

The app does not sell personal data. It sends the data described in section 2
only to OpenAI when the user has consented and started recording. Data is not
sent to the project maintainer.

Exported subtitle files are created only at a location selected by the user.
The app does not upload them.

### 6. Security

API keys are stored using the secure credential facility provided by each
operating system. Release checksums and code-signing practices are documented
in the project repository. No security measure can guarantee absolute
protection.

### 7. Changes and contact

Material changes to this policy will be published in this repository with an
updated effective date.

Questions may be filed in
[GitHub Issues](https://github.com/kinopeee/interpreter-openai/issues).
Security-sensitive reports should be sent to the project owner through
[@kinopeee](https://github.com/kinopeee).

---

## 日本語

### 1. 適用範囲

本ポリシーは、macOS版およびWindows版Realtime Translatorのデータ取扱いを説明します。
本オープンソースプロジェクトは
[@kinopeee](https://github.com/kinopeee) が管理しています。

Realtime Translatorには、管理者が運営するバックエンド、アカウントシステム、
アクセス解析、広告、テレメトリ送信先はありません。アプリを通じて、管理者が
利用者のAPIキー、マイク音声、原文、訳文、プロンプト、キーワードを受け取る
ことはありません。

### 2. OpenAIへ送信するデータ

アプリは、利用者が次の両方を行うまでマイク音声を送信しません。

1. アプリ内でOpenAIへのデータ送信に同意する
2. 録音を開始する

録音中、利用者の端末から以下へ直接接続します。

- `wss://api.openai.com/v1/realtime?intent=transcription`
- `wss://api.openai.com/v1/realtime/translations`

OpenAIへ送信される可能性があるデータは次のとおりです。

- マイク音声
- 認証ヘッダー内の利用者自身のOpenAI APIキー
- 利用者が設定した文字起こしプロンプトとキーワード
- 文字起こし・翻訳に必要なセッション設定
- ランダム生成したインストール識別子をSHA-256でハッシュした値
  （`OpenAI-Safety-Identifier`ヘッダー）

ハッシュ前のインストール識別子は送信しません。OpenAIから受信した原文と
訳文は、アプリが字幕として表示します。

通信には利用者自身のOpenAI APIキーとOpenAIアカウントを使用します。
OpenAI側でのAPIデータの取扱いと保持には、OpenAIの規約・ポリシーが適用されます。

- [OpenAI APIのデータ利用ポリシー](https://developers.openai.com/api/docs/guides/your-data)
- [OpenAIプライバシーポリシー](https://openai.com/policies/privacy-policy/)

### 3. 端末内に保存するデータ

アプリは次のデータを端末内に保存します。

- **APIキー**
  - macOS: キーチェーン
  - Windows: Windows資格情報マネージャーの汎用資格情報
    `RealtimeTranslator:openai-api-key`
- **設定**
  - 同意状態、字幕位置、表示言語、文字起こしプロンプト、キーワード、
    遅延、ノイズ低減設定など
  - macOS: `com.realtimetranslator.app` のシステム`UserDefaults`
  - Windows: `%LOCALAPPDATA%\RealtimeTranslator\settings.json`
- **インストール識別子**
  - 初回利用時にランダム生成して端末内へ保存
  - 保存先はmacOSの`UserDefaults`、またはWindowsレジストリキー
    `HKCU\Software\RealtimeTranslator`
  - OpenAIへはSHA-256ハッシュ後の値だけを送信
- **字幕記録**
  - 利用者がローカル字幕記録を明示的に有効化した場合のみ
  - macOS:
    `~/Library/Application Support/com.realtimetranslator.app/transcripts/session.txt`
    （Application Supportを解決できない場合はOSの一時ディレクトリを使用）
  - Windows:
    `%LOCALAPPDATA%\RealtimeTranslator\transcripts\session.txt`

マイク音声は端末内へ保存しません。ログやデバッグ用status出力には、APIキー、
Authorizationヘッダー、音声、原文、訳文を含めない設計です。

### 4. 保持期間と削除

ローカルデータは、利用者が削除するか、対応するOSの保存領域を削除するまで
端末内に残ります。

利用者は次の操作を行えます。

- アプリの設定からAPIキーを削除または上書きする
- アプリからローカル字幕記録を消去する
- ローカル字幕記録をいつでも無効化する
- Windowsの`%LOCALAPPDATA%\RealtimeTranslator`ディレクトリを削除する
- ローカル識別子を含むWindowsレジストリキー
  `HKCU\Software\RealtimeTranslator`を削除する
- 上記macOS Application Supportディレクトリを削除する
- macOSの`com.realtimetranslator.app`設定をリセットする

アプリ本体を削除しただけでは、OSの資格情報、設定、字幕記録、書き出した字幕の
コピーがすべて削除されるとは限りません。

OpenAI APIで処理されたデータの保持はOpenAIが管理します。上記のOpenAIポリシーと
利用者自身のOpenAIアカウント設定を確認してください。

### 5. 第三者提供

アプリは個人データを販売しません。第2節のデータは、利用者が同意して録音を
開始した場合に限りOpenAIへ送信されます。プロジェクト管理者へは送信されません。

字幕の書き出しファイルは、利用者が選択した場所にのみ作成されます。アプリが
書き出しファイルをアップロードすることはありません。

### 6. セキュリティ

APIキーは各OSが提供する安全な資格情報保管機能へ保存します。リリースの
チェックサムとコード署名方針は本リポジトリで公開します。ただし、いかなる
セキュリティ対策も絶対的な保護を保証するものではありません。

### 7. 変更と問い合わせ

本ポリシーに重要な変更がある場合は、発効日を更新して本リポジトリで公開します。

一般的な問い合わせは
[GitHub Issues](https://github.com/kinopeee/interpreter-openai/issues)へ投稿してください。
非公開で扱う必要があるセキュリティ報告は
[@kinopeee](https://github.com/kinopeee)を通じてプロジェクト管理者へ連絡してください。
