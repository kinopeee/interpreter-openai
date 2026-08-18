#!/usr/bin/env node
// Origin の内部アプリ認証で check-run を報告する。
// 認証フロー: Ed25519秘密鍵で App JWT を署名 → installation token を取得 →
// POST /repos/{owner}/{repo}/check-runs へ upsert（同じ suite.key + run.key は上書き）。
//
// 使い方:
//   node scripts/origin-report-check.mjs \
//     --app-id app_01xxx \
//     --key build/origin-ci-app/origin-app-private.pem \
//     --repo kinopee/interpreter-openai \
//     --sha <head-sha> \
//     --suite-key local-xcodebuild --suite-name "Local CI" \
//     --run-key xcodebuild-test --run-name "xcodebuild test (macOS)" \
//     --status completed --conclusion success \
//     --title "306 tests passed" --summary "xcodebuild test: all green"
//
// 秘密鍵・トークンは出力しない。

import { createPrivateKey, sign } from "node:crypto";
import { readFileSync } from "node:fs";

const API_BASE = "https://api.cursor.com/v1/origin";

function parseArgs(argv) {
  const args = {};
  for (let i = 0; i < argv.length; i += 2) {
    const flag = argv[i];
    const value = argv[i + 1];
    if (!flag?.startsWith("--") || value === undefined) {
      throw new Error(`Invalid argument pair: ${flag} ${value ?? ""}`);
    }
    args[flag.slice(2)] = value;
  }
  return args;
}

function base64url(input) {
  return Buffer.from(input).toString("base64url");
}

function signAppJwt(appId, privateKeyPem) {
  const now = Math.floor(Date.now() / 1000);
  const header = { alg: "EdDSA", kid: appId, typ: "JWT" };
  const claims = { iss: appId, aud: "origin-apps", iat: now, exp: now + 300 };
  const signingInput = `${base64url(JSON.stringify(header))}.${base64url(JSON.stringify(claims))}`;
  const key = createPrivateKey(privateKeyPem);
  const signature = sign(null, Buffer.from(signingInput), key);
  return `${signingInput}.${signature.toString("base64url")}`;
}

async function api(path, { method = "GET", token, body } = {}) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), 30_000);
  try {
    const response = await fetch(`${API_BASE}${path}`, {
      method,
      headers: {
        Authorization: `Bearer ${token}`,
        ...(body ? { "Content-Type": "application/json" } : {}),
      },
      body: body ? JSON.stringify(body) : undefined,
      signal: controller.signal,
    });
    const text = await response.text();
    if (!response.ok) {
      throw new Error(`${method} ${path} -> HTTP ${response.status}: ${text}`);
    }
    return text ? JSON.parse(text) : {};
  } finally {
    clearTimeout(timer);
  }
}

const args = parseArgs(process.argv.slice(2));
for (const required of ["app-id", "key", "repo", "sha", "suite-key", "run-key", "status"]) {
  if (!args[required]) {
    console.error(`Missing --${required}`);
    process.exit(1);
  }
}

const repoParts = args.repo.split("/");
if (repoParts.length !== 2 || repoParts.some((part) => part.length === 0)) {
  console.error("Invalid --repo: expected owner/repo");
  process.exit(1);
}
const [owner, repoName] = repoParts;
const appJwt = signAppJwt(args["app-id"], readFileSync(args.key, "utf8"));

const { installations = [] } = await api("/app/installations", { token: appJwt });
const installation = installations.find((entry) => entry.target?.slug === owner);
if (!installation) {
  console.error(`No installation found for ${owner}. Install the app on the codebase first.`);
  process.exit(1);
}

const { token: installationToken } = await api(
  `/app/installations/${installation.id}/access_tokens`,
  { method: "POST", token: appJwt, body: {} }
);

const nowIso = new Date().toISOString();
const attemptId = args["external-id"] ?? `${args["run-key"]}-${Date.now()}`;
const body = {
  headSha: args.sha,
  checkSuite: {
    key: args["suite-key"],
    name: args["suite-name"] ?? args["suite-key"],
    externalId: `${args["suite-key"]}-${args.sha}`,
  },
  checkRun: {
    key: args["run-key"],
    name: args["run-name"] ?? args["run-key"],
    externalId: attemptId,
    status: args.status,
    ...(args.conclusion ? { conclusion: args.conclusion } : {}),
    externalUpdatedAt: nowIso,
    ...(args["details-url"] ? { detailsUrl: args["details-url"] } : {}),
    ...(args.title || args.summary
      ? { output: { title: args.title ?? "", summary: args.summary ?? "" } }
      : {}),
  },
};

const result = await api(`/repos/${owner}/${repoName}/check-runs`, {
  method: "POST",
  token: installationToken,
  body,
});
console.log(
  `Reported check '${body.checkRun.name}' (${args.status}${args.conclusion ? `/${args.conclusion}` : ""}) ` +
    `on ${args.repo}@${args.sha.slice(0, 12)} suite=${body.checkSuite.key} id=${result.checkSuite?.id ?? "?"}`
);
