#!/usr/bin/env bash
# shared/fixtures/v1 と shared/locales/ui.json の契約検査。
# GitHub Actions と Depot CI の正本。
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

if ! command -v npx >/dev/null 2>&1; then
  echo "npx が必要です。Node.js を入れてください。" >&2
  exit 1
fi

if ! command -v node >/dev/null 2>&1; then
  echo "node が必要です。" >&2
  exit 1
fi

fixture_dir="$ROOT/shared/fixtures/v1"
locale_dir="$ROOT/shared/locales"
status=0
fixture_count=0

echo "Validating fixtures against JSON Schema..."
for schema in "$fixture_dir"/schema/*.schema.json; do
  name="$(basename "$schema" .schema.json)"
  if [ ! -f "$fixture_dir/$name.json" ]; then
    echo "::error::missing fixture $name.json for $schema"
    echo "missing fixture $name.json for $schema" >&2
    status=1
    continue
  fi
  fixture_count=$((fixture_count + 1))
  npx --yes ajv-cli@5.0.0 validate --spec=draft2020 -s "$schema" -d "$fixture_dir/$name.json" || status=1
done

for fixture in "$fixture_dir"/*.json; do
  name="$(basename "$fixture" .json)"
  if [ ! -f "$fixture_dir/schema/$name.schema.json" ]; then
    echo "::error::missing schema for $fixture"
    echo "missing schema for $fixture" >&2
    status=1
  fi
done

if [ "$status" -ne 0 ]; then
  exit "$status"
fi
echo "fixtures valid: ${fixture_count}"

echo "Validating UI locale catalog..."
npx --yes ajv-cli@5.0.0 validate --spec=draft2020 -s "$locale_dir/ui.schema.json" -d "$locale_dir/ui.json"

node -e '
  const fs = require("fs");
  const catalog = JSON.parse(fs.readFileSync(process.argv[1], "utf8"));
  const keys = catalog.strings.map((item) => item.key);
  const seen = new Set();
  const duplicates = [];
  for (const key of keys) {
    if (seen.has(key)) duplicates.push(key);
    seen.add(key);
  }
  if (duplicates.length) {
    console.error("duplicate keys:", duplicates.join(", "));
    process.exit(1);
  }
  const names = (text) => new Set(
    [...text.matchAll(/\{([A-Za-z_][A-Za-z0-9_]*)\}/g)].map((match) => match[1])
  );
  let failed = false;
  for (const item of catalog.strings) {
    const ja = names(item.ja);
    const en = names(item.en);
    if (ja.size !== en.size || [...ja].some((name) => !en.has(name))) {
      console.error("placeholder mismatch:", item.key);
      failed = true;
    }
  }
  if (failed) process.exit(1);
  console.log("ok:", keys.length, "keys");
' "$locale_dir/ui.json"
