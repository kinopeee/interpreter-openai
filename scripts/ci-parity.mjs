#!/usr/bin/env node
// shared/parity-map.json を検査する。
//   1. map が shared/parity-map.schema.json に適合する（ajv 経由）
//   2. 各 component の file/symbol が両側に実在する
//   3. 各 component の fixture が shared/fixtures/<v>/<name>.json に存在し、
//      両側のテストで消費される
//   4. scan.dirs 配下の全 top-level 型が components か platformOnly に登録済み
//   5. platformOnly エントリの file+symbol 実在 + reason 非空
//
// 使い方:
//   node scripts/ci-parity.mjs           # 全検査。違反があれば exit 1
//   node scripts/ci-parity.mjs --dump    # scan 対象の top-level 型一覧を出力
import { execFileSync } from "node:child_process";
import { promises as fs } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const MAP_PATH = path.join(ROOT, "shared", "parity-map.json");
const SCHEMA_PATH = path.join(ROOT, "shared", "parity-map.schema.json");
const FIXTURES_ROOT = path.join(ROOT, "shared", "fixtures");
const TEST_DIRS = {
  swift: [path.join(ROOT, "RealtimeTranslator", "Tests")],
  csharp: [path.join(ROOT, "windows", "tests")],
};

// top-level 宣言だけを拾うため行頭（インデントなし）に固定する。
const DECLARATION = {
  swift:
    /^(?:@\w+(?:\([^()\n]*\))?[ \t]+)*(?:(?:public|internal|private|fileprivate|open|final|indirect|nonisolated|memberwise|static)\s+)*(?:class|struct|enum|actor|protocol)\s+([A-Za-z_]\w*)\b/gm,
  csharp:
    /^(?:(?:public|internal|private|protected|sealed|static|abstract|partial|readonly|file|unsafe|new|ref)\s+)*(?:(?:record\s+(?:class|struct))|class|struct|enum|interface|record|delegate)\s+([A-Za-z_]\w*)\b/gm,
};
const SOURCE_EXT = { swift: ".swift", csharp: ".cs" };

const listFiles = async (dir) => {
  const entries = await fs.readdir(dir, { withFileTypes: true });
  const files = [];
  for (const entry of entries) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      if (entry.name === "bin" || entry.name === "obj") continue;
      files.push(...(await listFiles(full)));
    } else {
      files.push(full);
    }
  }
  return files;
};

const declaredSymbols = (content, side) => {
  const found = new Set();
  for (const match of content.matchAll(DECLARATION[side])) {
    found.add(match[1]);
  }
  return found;
};

const scanDeclarations = async (dirs, side) => {
  const result = [];
  for (const dir of dirs) {
    const absolute = path.join(ROOT, dir);
    for (const file of await listFiles(absolute)) {
      if (!file.endsWith(SOURCE_EXT[side])) continue;
      const content = await fs.readFile(file, "utf8");
      const symbols = [...declaredSymbols(content, side)];
      if (symbols.length === 0) continue;
      result.push({ file: path.relative(ROOT, file), symbols });
    }
  }
  return result;
};

const asArray = (value) => (Array.isArray(value) ? value : [value]);

const relPath = (entry) => entry.path.replace(/\\/g, "/");

const checkSchema = (errors) => {
  try {
    execFileSync(
      "npx",
      [
        "--yes",
        "ajv-cli@5.0.0",
        "validate",
        "--spec=draft2020",
        "-s",
        SCHEMA_PATH,
        "-d",
        MAP_PATH,
      ],
      { cwd: ROOT, stdio: ["ignore", "pipe", "pipe"] },
    );
  } catch (error) {
    errors.push(
      `parity-map.json が schema に適合しません:\n${(error.stdout || "") + (error.stderr || "") || error.message}`,
    );
  }
};

// fixture 消費の grep 近似。呼出単位で name + version を検査する。
//   Swift: SharedFixtures.load|section|case|caseNames("<name>" ...) / version: N
//   C#:    SharedFixtures.Load|Section|Case|CaseNames("<name>" ...) / positional , N
// version 引数なしの呼出は v1 を消費しているとみなす（両側 API の既定値）。
const callPattern = (side, name) =>
  side === "swift"
    ? new RegExp(
        `SharedFixtures\\.(?:load|section|case|caseNames)\\(\\s*"${name}"([^)]*)\\)`,
        "g",
      )
    : new RegExp(
        `SharedFixtures\\.(?:Load|Section|Case|CaseNames)\\(\\s*"${name}"([^)]*)\\)`,
        "g",
      );
const callVersion = (side, args) => {
  // Swift の名前付き引数 version: N、または C# の末尾 positional , N
  const match =
    /version\s*:\s*(\d+)/.exec(args) ?? /,\s*(\d+)\s*$/.exec(args);
  return match ? Number(match[1]) : 1;
};

const checkFixtureConsumed = async (side, name, version, errors) => {
  const calls = callPattern(side, name);
  for (const dir of TEST_DIRS[side]) {
    for (const file of await listFiles(dir)) {
      if (!file.endsWith(SOURCE_EXT[side])) continue;
      const content = await fs.readFile(file, "utf8");
      for (const match of content.matchAll(calls)) {
        if (callVersion(side, match[1]) === version) return true;
      }
    }
  }
  errors.push(
    `fixture v${version}/${name} が ${side} 側テストで消費されていません`,
  );
  return false;
};

const checkSymbolDeclared = (fileIndex, side, entry, label, errors) => {
  const rel = relPath(entry);
  const file = fileIndex.get(rel);
  if (file === undefined) {
    errors.push(`${label}: ${rel} が存在しません`);
    return;
  }
  if (!file.symbols.has(entry.symbol)) {
    errors.push(`${label}: ${rel} に ${side} 型 ${entry.symbol} がありません`);
  }
};

const main = async () => {
  const dump = process.argv.includes("--dump");
  const map = JSON.parse(await fs.readFile(MAP_PATH, "utf8"));
  const scan = map.scan ?? {};

  if (dump) {
    for (const side of ["swift", "csharp"]) {
      for (const decl of await scanDeclarations(scan[side]?.dirs ?? [], side)) {
        console.log(`[${side}] ${decl.file}`);
        for (const symbol of decl.symbols) console.log(`  ${symbol}`);
      }
    }
    return;
  }

  const errors = [];
  checkSchema(errors);

  // scan dirs をインデックス化（file -> symbols）して存在検査と網羅性に再利用する。
  const fileIndex = { swift: new Map(), csharp: new Map() };
  for (const side of ["swift", "csharp"]) {
    for (const decl of await scanDeclarations(scan[side]?.dirs ?? [], side)) {
      fileIndex[side].set(decl.file, { symbols: new Set(decl.symbols) });
    }
  }

  const registered = { swift: new Set(), csharp: new Set() };

  for (const component of map.components ?? []) {
    for (const side of ["swift", "csharp"]) {
      for (const entry of asArray(component[side])) {
        checkSymbolDeclared(
          fileIndex[side],
          side,
          entry,
          `component ${component.name}`,
          errors,
        );
        registered[side].add(`${relPath(entry)}#${entry.symbol}`);
      }
    }
    for (const fixture of component.fixtures ?? []) {
      const match = /^v(\d+)\/([a-z0-9-]+)$/.exec(fixture);
      if (!match) {
        errors.push(`component ${component.name}: fixture "${fixture}" は v<N>/name 形式で指定してください`);
        continue;
      }
      const version = Number(match[1]);
      const name = match[2];
      const fixturePath = path.join(
        FIXTURES_ROOT,
        `v${version}`,
        `${name}.json`,
      );
      try {
        await fs.access(fixturePath);
      } catch {
        errors.push(
          `component ${component.name}: shared/fixtures/v${version}/${name}.json が存在しません`,
        );
        continue;
      }
      await checkFixtureConsumed("swift", name, version, errors);
      await checkFixtureConsumed("csharp", name, version, errors);
    }
  }

  for (const side of ["swift", "csharp"]) {
    for (const entry of map.platformOnly?.[side] ?? []) {
      checkSymbolDeclared(
        fileIndex[side],
        side,
        entry,
        `platformOnly ${side} ${entry.symbol}`,
        errors,
      );
      registered[side].add(`${relPath(entry)}#${entry.symbol}`);
      if (typeof entry.reason !== "string" || entry.reason.trim() === "") {
        errors.push(`platformOnly ${side} ${entry.symbol}: reason が空です`);
      }
    }
  }

  // scan roots の網羅性: 全 top-level 型が components か platformOnly に登録済み。
  // 登録は file+symbol のペアで判定し、同名型を別ファイルへ追加する迂回を塞ぐ。
  for (const side of ["swift", "csharp"]) {
    for (const decl of await scanDeclarations(scan[side]?.dirs ?? [], side)) {
      for (const symbol of decl.symbols) {
        if (!registered[side].has(`${decl.file.replace(/\\/g, "/")}#${symbol}`)) {
          errors.push(
            `未登録の ${side} 型: ${symbol} (${decl.file}) — components か platformOnly に追加してください`,
          );
        }
      }
    }
  }

  if (errors.length > 0) {
    for (const error of errors) console.error(`::error::${error}`);
    process.exit(1);
  }
  console.log("parity-map: OK");
};

main().catch((error) => {
  console.error(`::error::${error.message}`);
  process.exit(1);
});
