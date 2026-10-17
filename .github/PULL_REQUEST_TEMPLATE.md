## 概要

<!-- 何をなぜ変えるか。読者が diff を見なくても意図が分かる分量で -->

## 共有契約チェックリスト

- [ ] fixture-backed な挙動を変えた → 対象の `shared/fixtures/v<N>/` を先に更新し、その後に各実装を合わせた
- [ ] 契約ドメイン型を追加/改名/削除/移動した → `shared/parity-map.json` の `components` を更新した
- [ ] 片側プラットフォーム専用の型を追加した → `platformOnly` に理由付きで登録した
- [ ] `node scripts/ci-parity.mjs`（= `./scripts/ci-shared-contracts.sh parity`）が緑

## 検証

<!-- 実行した検証と未実施の検証を区別する。実機確認が要る変更は VALIDATION.md の項目を使う -->
