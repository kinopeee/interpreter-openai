#!/usr/bin/env bash
# shared/fixtures/v1 の schema↔fixture 1:1 と、locales/ui.json を検証する。
# GitHub Actions（.github/workflows/shared-contracts.yml）と
# Origin + Depot CI（.depot/workflows/shared-contracts.yml）の正本。
#
# 使い方:
#   ./scripts/ci-shared-contracts.sh           # 両方
#   ./scripts/ci-shared-contracts.sh fixtures
#   ./scripts/ci-shared-contracts.sh locales
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

AJV=(npx --yes ajv-cli@5.0.0 validate --spec=draft2020)

validate_fixtures() {
  local status=0
  local fixtures_dir schema_dir schema name fixture
  for fixtures_dir in "$ROOT"/shared/fixtures/v*/; do
    [[ -d "$fixtures_dir" ]] || continue
    cd "$fixtures_dir"
    schema_dir="$fixtures_dir/schema"
    for schema in "$schema_dir"/*.schema.json; do
      [[ -f "$schema" ]] || continue
      name="$(basename "$schema" .schema.json)"
      if [[ ! -f "$name.json" ]]; then
        echo "::error::missing fixture $name.json for $schema"
        status=1
        continue
      fi
      "${AJV[@]}" -s "$schema" -d "$name.json" || status=1
    done
    for fixture in *.json; do
      [[ -f "$fixture" ]] || continue
      name="$(basename "$fixture" .json)"
      if [[ ! -f "schema/$name.schema.json" ]]; then
        echo "::error::missing schema for $fixture"
        status=1
      fi
    done
  done
  cd "$ROOT"
  return "$status"
}

validate_locales() {
  cd "$ROOT/shared/locales"
  "${AJV[@]}" -s ui.schema.json -d ui.json
  node "$ROOT/scripts/ci-validate-locales.mjs"
  cd "$ROOT"
}

target="${1:-all}"
case "$target" in
  all)
    validate_fixtures
    validate_locales
    ;;
  fixtures)
    validate_fixtures
    ;;
  locales)
    validate_locales
    ;;
  -h|--help)
    sed -n '2,9p' "$0"
    exit 0
    ;;
  *)
    echo "Unknown argument: $target (expected fixtures, locales, or all)" >&2
    exit 1
    ;;
esac
