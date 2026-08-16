# shared/ — 実装非依存の契約とフィクスチャ

macOS(Swift) 版と Windows(C#) 版が同じ挙動を保つための言語中立な正本を置く。
**Swift の production コードはこのディレクトリの追加によって変更しない。**

```
shared/
├── protocol/          # 不変条件の散文仕様
│   ├── endpoints.md   # OpenAI Realtime エンドポイント / イベント / タイムアウト
│   ├── audio.md       # 24kHz PCM16 パケット化・適応ゲイン
│   ├── routing.md     # 言語判定・dual routing・字幕整列
│   ├── privacy.md     # ログ禁止事項・エラー正規化・鍵の保管先
│   └── ui-locale.md   # アプリ枠の表示言語（ja/en、再起動後反映）
├── fixtures/v1/       # 期待値テーブル（両実装のテストが読む）
│   ├── codec.json     ├── tuning.json   ├── language.json
│   ├── subtitle.json  ├── routing.json  ├── privacy.json
│   ├── api-key.json
│   └── schema/        # 各 fixture の JSON Schema (draft 2020-12)
└── locales/           # UI 文言の正本（ui-locale.md 参照）。fixtures と違い文言の推敲は通常変更。v1 不変ルールの対象外
    ├── ui.json
    └── ui.schema.json
```

## ルール

- fixture-backed な挙動を変える変更は、まず `protocol/` と `fixtures/v1/` を更新し、その後に各実装を合わせる。`shared/fixtures/v1` は両実装の契約正本のまま。
- UI 文言のみの変更（`shared/locales/`、例: `banner.connecting` の標準化）は `fixtures/v1` 更新の対象外。正本は `protocol/ui-locale.md` と `locales/ui.json`。
- fixture の破壊的変更はディレクトリを `v2/` として増やす。`v1/` の既存ケースは意味を変えない。
- fixture を足したら対応する schema も更新する。CI (`shared-contracts`) が 1:1 対応を検査する。

## ローカル検証

```bash
# fixtures（v1 の 1:1 対応）
cd shared/fixtures/v1
for s in schema/*.schema.json; do
  n="$(basename "$s" .schema.json)"
  npx --yes ajv-cli@5.0.0 validate --spec=draft2020 -s "$s" -d "$n.json"
done
cd ../../..

# UI 文言カタログ（fixtures の 1:1 ループとは別。リポジトリルートで実行）
npx --yes ajv-cli@5.0.0 validate --spec=draft2020 \
  -s shared/locales/ui.schema.json -d shared/locales/ui.json
```

プレースホルダ名の ja/en 一致とキー一意は `shared-contracts` のカスタム検証、および両実装の `UserCopyTests` でも見る。
