#!/usr/bin/env node
// .depot/workflows/ の action pin が .github/workflows/ と一致するかを検査する。
// .depot/workflows/ は Dependabot の対象外のため、ずれをここで検出する。
//   1. .depot 側の各 `uses: <action>@<ref>` について、.github 側の同じ action
//      （@ より前の文字列の完全一致）のすべての uses と SHA を比べる
//   2. .depot 側の action が .github 側に無い、ref が 40 桁 hex SHA でない、
//      uses 行を解釈できない場合は失敗にする
//   3. ローカル action（./）と docker:// は対象外。バージョンコメントは比較しない
//
// 使い方:
//   node scripts/ci-action-pins.mjs     # 違反があれば exit 1
import { promises as fs } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const DEPOT_DIR = path.join(ROOT, ".depot", "workflows");
const GITHUB_DIR = path.join(ROOT, ".github", "workflows");

const USES_LINE = /^\s*(?:-\s+)?uses\s*:(.*)$/;
const PIN = /^([^@\s]+)@([^@\s]+)$/;
const SHA = /^[0-9a-f]{40}$/;

const listWorkflows = async (dir) => {
  const entries = await fs.readdir(dir, { withFileTypes: true });
  return entries
    .filter((entry) => entry.isFile() && /\.ya?ml$/.test(entry.name))
    .map((entry) => path.join(dir, entry.name))
    .sort();
};

const unquote = (value) => {
  const quoted = /^(['"])(.*)\1$/.exec(value);
  return quoted ? quoted[2] : value;
};

const splitComment = (raw) => {
  const match = /^(.*?)(?:\s+#\s*(.*))?$/.exec(raw.trim());
  return { value: unquote(match[1].trim()), comment: match[2]?.trim() ?? "" };
};

const parseWorkflow = async (file, errors) => {
  const rel = path.relative(ROOT, file);
  const lines = (await fs.readFile(file, "utf8")).split(/\r?\n/);
  const pins = [];
  lines.forEach((line, index) => {
    const uses = USES_LINE.exec(line);
    if (!uses) return;
    const location = `${rel}:${index + 1}`;
    const { value, comment } = splitComment(uses[1]);
    if (value.startsWith("./") || value.startsWith("docker://")) return;
    const pin = PIN.exec(value);
    if (!pin) {
      errors.push(`${location}: uses を <action>@<ref> として解釈できません: ${line.trim()}`);
      return;
    }
    pins.push({ location, action: pin[1], ref: pin[2], comment });
  });
  return pins;
};

const describe = (pin) => `${pin.location} ${pin.action}@${pin.ref} (${pin.comment ? `# ${pin.comment}` : "コメントなし"})`;

const main = async () => {
  const errors = [];
  const depotPins = [];
  for (const file of await listWorkflows(DEPOT_DIR)) depotPins.push(...(await parseWorkflow(file, errors)));
  const githubPins = [];
  for (const file of await listWorkflows(GITHUB_DIR)) githubPins.push(...(await parseWorkflow(file, errors)));

  if (depotPins.length === 0 && errors.length === 0) {
    errors.push(`${path.relative(ROOT, DEPOT_DIR)} に検査対象の uses がありません`);
  }

  const reportedGithub = new Set();
  for (const depot of depotPins) {
    if (!SHA.test(depot.ref)) {
      errors.push(`${describe(depot)}: ref が 40 桁の SHA ではありません`);
    }
    const counterparts = githubPins.filter((pin) => pin.action === depot.action);
    if (counterparts.length === 0) {
      errors.push(`${describe(depot)}: .github/workflows に同じ action が無く、対応する SHA を確認できません`);
      continue;
    }
    for (const github of counterparts) {
      if (!SHA.test(github.ref)) {
        if (!reportedGithub.has(github.location)) {
          reportedGithub.add(github.location);
          errors.push(`${describe(github)}: ref が 40 桁の SHA ではありません（${depot.location} の比較対象）`);
        }
        continue;
      }
      if (github.ref !== depot.ref) {
        errors.push(`${describe(depot)} が ${describe(github)} と一致しません`);
      }
    }
  }

  if (errors.length > 0) {
    for (const error of errors) console.error(`::error::${error}`);
    process.exit(1);
  }
  console.log(`ok: ${depotPins.length} pins`);
};

main().catch((error) => {
  console.error(`::error::${error.message}`);
  process.exit(1);
});
