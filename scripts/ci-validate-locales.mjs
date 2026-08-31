#!/usr/bin/env node
// shared/locales/ui.json のキー一意と ja/en プレースホルダ一致を検査する。
// スキーマ検証（ajv）は scripts/ci-shared-contracts.sh が先に行う。
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const catalogPath = join(root, "shared/locales/ui.json");
const catalog = JSON.parse(readFileSync(catalogPath, "utf8"));
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

const names = (text) =>
  new Set([...text.matchAll(/\{([A-Za-z_][A-Za-z0-9_]*)\}/g)].map((match) => match[1]));

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
