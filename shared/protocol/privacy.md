# プライバシー・秘密情報の契約（言語中立）

## 出力してはいけないもの

以下をログ・status file・アラート・例外メッセージ・テレメトリへ出さない。

- API キー、`Authorization` ヘッダ、`Bearer` トークン
- 音声データ（生バッファ・base64 いずれも）
- 認識された原文テキスト
- 翻訳されたテキスト

## エラー文言の正規化

サーバから届いた生 message をそのまま UI/ログへ出さない。以下を含む場合は固定文言へ差し替える。

- `sk-`
- `api key`
- `authorization`
- `bearer `

（いずれも小文字化して部分一致で判定）。空文字の場合も固定文言にする。
固定文言の日本語: 「翻訳サーバーでエラーが発生しました」。
表示言語対応後は `shared/locales/ui.json` の `error.genericServer`（ja/en）を表示に使う。
`fixtures/v1/privacy.json` の期待値は変えず、カタログ ja との一致をテストで担保する。
検出語とアルゴリズムは言語非依存のまま。詳細は `ui-locale.md`。

## 認証失敗の判定

`code` が次のいずれかに完全一致: `invalid_api_key` / `invalid_auth` / `authentication_error` / `unauthorized` / `unauthenticated` / `401` / `403`。
または `code` が `invalid_api_key` / `authentication` / `unauthorized` / `authorization` を部分一致で含む。
または message が次のいずれかを含む: `unauthorized`, `unauthenticated`, `authorization`, `invalid_api_key`, `incorrect api key`, `invalid api key`, `authentication`, `authentication failed`, `authentication error`, `not authenticated`, `api key is invalid`。
または message に **数字で挟まれていない** `401` / `403` が現れる（正規表現 `(?<![0-9])(401|403)(?![0-9])`）。

`authority` や `4010` に誤爆しないこと。期待値は `shared/fixtures/v1/privacy.json` が正本。

## ロギング

構造化ログのみを使う。allowlist されたイベント ID と数値メタデータだけを受け付け、自由文字列を受け取らない API 形状にする。

## キーの保存

| プラットフォーム | 保存先 |
|---|---|
| macOS | Keychain |
| Windows | Credential Manager（`CredWriteW`/`CredReadW`/`CredDeleteW`、`CRED_TYPE_GENERIC`、`CRED_PERSIST_LOCAL_MACHINE`） |

`settings.json` などの設定ファイルへ書かない。初回のみ環境変数 `OPENAI_API_KEY` から取り込んでよい。

保存・読み出し・接続直前は同じ正規化を通す。アルゴリズムと期待値は `shared/fixtures/v1/api-key.json` が正本。

1. Unicode 空白・制御文字・Format（埋め込み CR/LF/TAB/ZWSP を含む）をすべて除去する。両端 trim だけでは不十分。
2. 残った文字列が空なら空キーとして拒否する。
3. 残った文字列が `A-Za-z0-9._-` 以外を含む（貼り付け時の `3:26` など）なら形式不正として拒否する。`sk-` 接頭辞は必須にしない。
4. 既存ストアの壊れたキーは読み出し時に正規化し、ヘッダ破壊や不正文字を接続へ渡さない。
