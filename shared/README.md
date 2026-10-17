# shared/ — 実装非依存の契約とフィクスチャ

`shared/fixtures/v<N>/` contains versioned canonical fixtures. The v2 directory
currently contains the subtitle source-boundary contract and the audio pipeline
contract; existing v1 fixtures remain unchanged. CI validates every version
directory.

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
├── fixtures/v2/       # v2 契約（subtitle / audio。v1 は削除せず保持）
│   ├── subtitle.json  ├── audio.json
│   └── schema/
└── locales/           # UI 文言の正本（ui-locale.md 参照）。fixtures と違い文言の推敲は通常変更。v1 不変ルールの対象外
    ├── ui.json
    └── ui.schema.json
```

## ルール

- fixture-backed な挙動を変える変更は、対象の `shared/fixtures/v<N>/` を更新し、その後に各実装を合わせる。subtitle と audio の契約は v2、その他の契約は v1 が正本（v1 fixture は削除せず保持する）。
- UI 文言のみの変更（`shared/locales/`、例: `banner.connecting` の標準化）は fixture 更新の対象外。正本は `protocol/ui-locale.md` と `locales/ui.json`。
- fixture の破壊的変更は新しい `v<N>/` ディレクトリとして追加し、既存バージョンのケースの意味を変えない。
- fixture を足したら対応する schema も更新する。CI (`shared-contracts`、正本は `scripts/ci-shared-contracts.sh`) が 1:1 対応を検査する。GitHub は `.github/workflows/shared-contracts.yml`、Origin は `.depot/workflows/shared-contracts.yml`。

## parity-map の保守ルール

- `shared/parity-map.json` は両実装の契約ドメイン型の対応表。`components` が両側に実在する型（言語慣習差は `symbol` で実名を記録）、`platformOnly` が片側専用の型（`reason` 必須）。
- 型の追加・改名・削除・移動は map へ反映する。`scan.dirs` 配下の全 top-level 型は `components` か `platformOnly` のどちらかに登録が必須で、未登録は `scripts/ci-parity.mjs` が hard fail する（CI の `shared-contracts` parity ステップ）。
- `fixtures` は `v<N>/name` 形式で、対応 fixture を両側テストが消費していることが検査される。消費判定は呼出の grep 近似（Swift `SharedFixtures.<fn>("name", ..., version: N)` / C# `SharedFixtures.<Fn>("name", ..., N)`）なので、消費形態を変えたら検査側も合わせる。

## ローカル検証

```bash
./scripts/ci-shared-contracts.sh
```

プレースホルダ名の ja/en 一致とキー一意は `scripts/ci-validate-locales.mjs`、および両実装の `UserCopyTests` でも見る。
