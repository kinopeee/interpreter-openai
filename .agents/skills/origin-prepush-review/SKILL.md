---
name: origin-prepush-review
description: >-
  Reviews committed local changes with CodeRabbit CLI, applies valid findings,
  then pushes the branch to the Origin remote named `cursor`.
  Use when the user asks to review before Origin push, run CodeRabbit before push,
  origin-prepush-review,
  or 「レビューしてからOriginにpush」.
---

# Origin push 前の CodeRabbit レビュー

Origin のホスト側レビューは使えない。push 前にローカルで `coderabbit review` を回し、妥当な指摘を直してから `cursor` remote へ push する。マージゲートではない。必須 check はあとから `scripts/origin-report-check.mjs` が担う。

## 手順

1. リポジトリルートへ移動する。未コミットがあれば警告し、レビュー対象に含めるなら先にコミットする（スクリプトは `--committed` のみ見る）。
2. エージェントが直す前提なら次を実行する。人が読むだけなら `--agent` を外す。

   ```bash
   ./scripts/origin-prepush-review.sh --agent
   ```

3. 指摘を表にする。列は Severity / Location (`file:line`) / Finding。重大度の高い順。
4. 妥当な指摘だけ直す。秘密情報・無関係ファイル・インフラ変更は触らない。直したらコミットする。妥当でない指摘は表に残し、直さずに理由を一言書く。
5. 妥当な指摘を直したあと（指摘ゼロ、または妥当な指摘が無いときも含む）`git push cursor <current-branch>` する。GitHub の `origin` には送らない。レビュー自体が失敗したら push しない。
6. push 後は version が切れる。続けて CI を頼まれたときだけ `xcodebuild test` と `scripts/origin-report-check.mjs` を、**新しい head SHA** に対して実行する。

## 制約

- `coderabbit` が無い、または未ログインならインストール／`coderabbit auth login` を案内して止まる。
- `--base` はスクリプト既定（`cursor/main`、なければ `main`）に任せる。ユーザーが比較先を指定したときだけ `--base` を付ける。
- レビュー強度は `.coderabbit.yaml` の `reviews.profile: assertive`。スクリプトが `-c` で渡す。
- GitHub の CodeRabbit ボットコメントを取りに行かない。捨てた `autofix` スキルは使わない。
- レビュー失敗を merge blocker にしない。指摘ゼロでも、妥当な指摘を直したあとも、`cursor` へ push する。レビューコマンド自体が失敗したときだけ push を止める。
