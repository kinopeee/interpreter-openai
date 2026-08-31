#!/usr/bin/env bash
# Origin へ push する前に、コミット済み差分を CodeRabbit CLI でレビューする。
# マージゲートにはしない。妥当な指摘の修正と cursor への push はスキル側が行う。
# Origin の PR CI は Depot CI（.depot/workflows）が担う。
#
# 使い方:
#   ./scripts/origin-prepush-review.sh
#   ./scripts/origin-prepush-review.sh --agent
#   ./scripts/origin-prepush-review.sh --light --base cursor/main
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

agent=0
light=0
base=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --agent) agent=1; shift ;;
    --light) light=1; shift ;;
    --base)
      if [[ $# -lt 2 ]]; then
        echo "Missing value for --base" >&2
        exit 1
      fi
      base="$2"
      shift 2
      ;;
    -h|--help)
      sed -n '2,9p' "$0"
      exit 0
      ;;
    *)
      echo "Unknown argument: $1" >&2
      exit 1
      ;;
  esac
done

if ! command -v coderabbit >/dev/null 2>&1; then
  echo "coderabbit CLI が見つかりません。https://www.coderabbit.ai/ から入れてください。" >&2
  exit 1
fi

if ! coderabbit auth status >/dev/null 2>&1; then
  echo "CodeRabbit に未ログインです。coderabbit auth login を実行してください。" >&2
  exit 1
fi

if [[ -z "$base" ]]; then
  if git rev-parse --verify --quiet cursor/main >/dev/null; then
    base="cursor/main"
  else
    base="main"
  fi
fi

if [[ -n "$(git status --porcelain)" ]]; then
  echo "未コミットの変更があります。レビュー対象はコミット済み差分だけです。" >&2
fi

cmd=(coderabbit review --committed --base "$base")
if [[ -f .coderabbit.yaml ]]; then
  cmd+=(-c .coderabbit.yaml)
fi
if [[ "$agent" -eq 1 ]]; then
  cmd+=(--agent)
fi
if [[ "$light" -eq 1 ]]; then
  cmd+=(--light)
fi

echo "Reviewing committed changes against ${base}..." >&2
if "${cmd[@]}"; then
  :
else
  status=$?
  echo "CodeRabbit review failed with exit status ${status}. Do not push." >&2
  exit "$status"
fi

echo >&2
echo "次: 妥当な指摘を直してコミットし、git push cursor <branch>" >&2
echo "Origin の PR CI は Depot CI（.depot/workflows）が担う。macOS xcodebuild の手動報告だけ scripts/origin-report-check.mjs を使う。" >&2
