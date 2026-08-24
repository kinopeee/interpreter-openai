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
  local fixtures_dir="$ROOT/shared/fixtures/v1"
  cd "$fixtures_dir"
  local status=0
  local schema name fixture
  for schema in schema/*.schema.json; do
    name="$(basename "$schema" .schema.json)"
    if [[ ! -f "$name.json" ]]; then
      echo "::error::missing fixture $name.json for $schema"
      status=1
      continue
    fi
    "${AJV[@]}" -s "$schema" -d "$name.json" || status=1
  done
  for fixture in *.json; do
    name="$(basename "$fixture" .json)"
    if [[ ! -f "schema/$name.schema.json" ]]; then
      echo "::error::missing schema for $fixture"
      status=1
    fi
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
    sed -n '2,11p' "$0"
    exit 0
    ;;
  *)
    echo "Unknown argument: $target (expected fixtures, locales, or all)" >&2
    exit 1
    ;;
esac
