import { spawn as childSpawn } from "node:child_process";
import { createHash } from "node:crypto";
import { promises as fs } from "node:fs";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const SCRIPT_PATH = fileURLToPath(import.meta.url);
const DEFAULT_ROOT = path.resolve(path.dirname(SCRIPT_PATH), "..");
const TOOL_CONFIG = {
  swift: {
    tool: "swift-format",
    config: ".devin/format/swift-format.json",
    prefixes: ["RealtimeTranslator/"],
    extension: ".swift",
  },
  csharp: {
    tool: "csharpier",
    config: ".devin/format/csharpier.json",
    prefixes: ["windows/src/", "windows/tests/"],
    extension: ".cs",
  },
};
const SKIP_DIRECTORIES = new Set([
  "bin",
  "obj",
  "build",
  "DerivedData",
  ".build",
  "artifacts",
]);

const text = (value) =>
  value == null
    ? ""
    : Buffer.isBuffer(value)
      ? value.toString()
      : String(value);
const sha256 = (value) => createHash("sha256").update(value).digest("hex");
const canonicalJson = (value) => JSON.stringify(value);
const byteLength = (value) => Buffer.byteLength(value);

export const defaultSpawn = (cmd, args, { cwd, stdio = "inherit", env } = {}) =>
  new Promise((resolve) => {
    let stdout = "";
    let stderr = "";
    let settled = false;
    const child = childSpawn(cmd, args, { cwd, stdio, env, shell: false });
    const finish = (result) => {
      if (!settled) {
        settled = true;
        resolve({
          code: result.code ?? null,
          signal: result.signal ?? null,
          error: result.error,
          stdout,
          stderr,
        });
      }
    };
    if (child.stdout) child.stdout.on("data", (chunk) => (stdout += chunk));
    if (child.stderr) child.stderr.on("data", (chunk) => (stderr += chunk));
    child.once("error", (error) => finish({ code: null, signal: null, error }));
    child.once("close", (code, signal) => finish({ code, signal }));
  });

const write = (stream, value) => {
  if (stream?.write) stream.write(value);
};

const commandResult = async (spawn, cmd, args, cwd, capture = false, env) =>
  spawn(cmd, args, {
    cwd,
    stdio: capture ? ["ignore", "pipe", "pipe"] : "inherit",
    env,
  });

const failedResult = (result) =>
  result?.code !== 0 || result?.signal || result?.error;
const isAlive = (pid) => {
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    return error.code === "EPERM";
  }
};

const firstLine = (value) => text(value).split(/\r?\n/, 1)[0];
const outputOf = (result) => `${text(result?.stderr)}${text(result?.stdout)}`;

async function readJson(file) {
  return JSON.parse(await fs.readFile(file, "utf8"));
}

async function fileHash(file) {
  return sha256(await fs.readFile(file));
}

function toolData(language) {
  const data = TOOL_CONFIG[language];
  if (!data) throw new Error(`unknown language: ${language}`);
  return data;
}

async function metadata({ root, language, platform, arch, spawn, env }) {
  const versions = await readJson(
    path.join(root, ".devin/format/tool-versions.json"),
  );
  const setupSha256 = await fileHash(SCRIPT_PATH);
  if (language === "swift") {
    const resolvedSha256 = await fileHash(
      path.join(root, ".devin/format/swift-format.Package.resolved"),
    );
    const result = await commandResult(
      spawn,
      "swift",
      ["--version"],
      root,
      true,
      env,
    );
    if (result.error || result.code !== 0)
      throw new Error("swift toolchain not found on PATH");
    const compiler = firstLine(outputOf(result));
    return {
      key: {
        tool: "swift-format",
        os: platform,
        arch,
        compiler,
        commit: versions.swiftFormat.commit,
        resolvedSha256,
        setupSha256,
      },
      versions,
    };
  }
  const result = await commandResult(
    spawn,
    "dotnet",
    ["--version"],
    path.join(root, "windows"),
    true,
    env,
  );
  if (result.error || result.code !== 0)
    throw new Error("dotnet toolchain not found on PATH");
  return {
    key: {
      tool: "csharpier",
      os: platform,
      arch,
      dotnet: text(result.stdout).trim(),
      version: versions.csharpier.version,
      setupSha256,
    },
    versions,
  };
}

export async function computeCacheKeyData({
  root = DEFAULT_ROOT,
  language,
  platform = process.platform,
  arch = process.arch,
  spawn = defaultSpawn,
  env = process.env,
}) {
  const { key } = await metadata({
    root,
    language,
    platform,
    arch,
    spawn,
    env,
  });
  return key;
}

export async function computeCacheKey({
  root = DEFAULT_ROOT,
  language,
  platform = process.platform,
  arch = process.arch,
  spawn = defaultSpawn,
  env = process.env,
}) {
  const key = await computeCacheKeyData({
    root,
    language,
    platform,
    arch,
    spawn,
    env,
  });
  return sha256(canonicalJson(key));
}

function cacheFieldsMatch(provenance, key) {
  for (const [field, expected] of Object.entries(key)) {
    if (JSON.stringify(provenance[field]) !== JSON.stringify(expected))
      return field;
  }
  return null;
}

async function validateCache(cacheDir, key) {
  let provenance;
  try {
    provenance = await readJson(path.join(cacheDir, "provenance.json"));
  } catch {
    return { valid: false, reason: "provenance.json" };
  }
  const field = cacheFieldsMatch(provenance, key);
  if (field) return { valid: false, reason: field };
  for (const [relative, expected] of Object.entries(
    provenance.binaries ?? {},
  )) {
    try {
      if ((await fileHash(path.join(cacheDir, relative))) !== expected) {
        return { valid: false, reason: `binary ${relative}` };
      }
    } catch {
      return { valid: false, reason: `binary ${relative}` };
    }
  }
  return { valid: true, provenance };
}

async function setupSwift({
  root,
  partial,
  versions,
  spawn,
  stderr,
  platform,
  env,
}) {
  if (platform === "win32") {
    write(stderr, "swift-format setup is supported on macOS and Linux only\n");
    return false;
  }
  const source = path.join(partial, "src");
  const repository = versions.swiftFormat.repository;
  const commit = versions.swiftFormat.commit;
  let result = await commandResult(
    spawn,
    "git",
    ["clone", "--no-checkout", repository, source],
    root,
    false,
    env,
  );
  if (failedResult(result)) return false;
  result = await commandResult(
    spawn,
    "git",
    ["-C", source, "checkout", "--detach", commit],
    root,
    false,
    env,
  );
  if (failedResult(result)) return false;
  result = await commandResult(
    spawn,
    "git",
    ["-C", source, "rev-parse", "HEAD"],
    root,
    true,
    env,
  );
  if (failedResult(result) || text(result.stdout).trim() !== commit) {
    write(stderr, `swift-format checkout did not resolve to ${commit}\n`);
    return false;
  }
  await fs.copyFile(
    path.join(root, ".devin/format/swift-format.Package.resolved"),
    path.join(source, "Package.resolved"),
  );
  result = await commandResult(
    spawn,
    "swift",
    [
      "build",
      "-c",
      "release",
      "--product",
      "swift-format",
      "--force-resolved-versions",
      "--package-path",
      source,
    ],
    source,
    false,
    env,
  );
  if (failedResult(result)) return false;
  const built = path.join(source, ".build/release/swift-format");
  const binary = path.join(partial, "bin/swift-format");
  await fs.mkdir(path.dirname(binary), { recursive: true });
  await fs.copyFile(built, binary);
  result = await commandResult(spawn, binary, ["--version"], root, true, env);
  if (failedResult(result)) return false;
  return {
    binaries: { "bin/swift-format": await fileHash(binary) },
    toolVersionOutput: outputOf(result),
  };
}

async function setupCSharp({ root, partial, versions, spawn, platform, env }) {
  const tools = path.join(partial, "tools");
  let result = await commandResult(
    spawn,
    "dotnet",
    [
      "tool",
      "install",
      "csharpier",
      "--version",
      versions.csharpier.version,
      "--tool-path",
      tools,
    ],
    path.join(root, "windows"),
    false,
    env,
  );
  if (failedResult(result)) return false;
  const executable = path.join(
    tools,
    platform === "win32" ? "csharpier.exe" : "csharpier",
  );
  const dll = path.join(
    tools,
    ".store",
    "csharpier",
    versions.csharpier.version,
    "csharpier",
    versions.csharpier.version,
    "tools/net10.0/any/CSharpier.dll",
  );
  result = await commandResult(
    spawn,
    executable,
    ["--version"],
    root,
    true,
    env,
  );
  if (
    failedResult(result) ||
    text(result.stdout).trim() !== versions.csharpier.version
  ) {
    return false;
  }
  return {
    binaries: {
      [`tools/${path.basename(executable)}`]: await fileHash(executable),
      [`tools/.store/csharpier/${versions.csharpier.version}/csharpier/${versions.csharpier.version}/tools/net10.0/any/CSharpier.dll`]:
        await fileHash(dll),
    },
    toolVersionOutput: text(result.stdout),
  };
}

async function setup({
  root,
  language,
  platform,
  arch,
  spawn,
  stdout,
  stderr,
  env,
  toolsRoot,
}) {
  if (language === "swift" && platform === "win32") {
    write(stderr, "swift-format setup is supported on macOS and Linux only\n");
    return 1;
  }
  let metadataResult;
  try {
    metadataResult = await metadata({
      root,
      language,
      platform,
      arch,
      spawn,
      env,
    });
  } catch (error) {
    write(stderr, `${error.message}\n`);
    return 1;
  }
  const { key, versions } = metadataResult;
  const cacheDir = path.join(
    toolsRoot,
    toolData(language).tool,
    sha256(canonicalJson(key)),
  );
  const valid = await validateCache(cacheDir, key);
  if (valid.valid) {
    write(stdout, `reusing cache ${cacheDir}\n`);
    return 0;
  }
  await fs.rm(cacheDir, { recursive: true, force: true });
  const partial = `${cacheDir}.partial-${process.pid}`;
  const parent = path.dirname(cacheDir);
  const prefix = `${path.basename(cacheDir)}.partial-`;
  try {
    for (const entry of await fs.readdir(parent)) {
      if (entry.startsWith(prefix)) {
        const pid = Number(entry.slice(prefix.length));
        if (Number.isInteger(pid) && pid !== process.pid && isAlive(pid))
          continue;
        await fs.rm(path.join(parent, entry), {
          recursive: true,
          force: true,
        });
      }
    }
  } catch (error) {
    if (error.code !== "ENOENT") throw error;
  }
  await fs.mkdir(partial, { recursive: true });
  let result;
  try {
    result =
      language === "swift"
        ? await setupSwift({
            root,
            partial,
            versions,
            spawn,
            stderr,
            platform,
            env,
          })
        : await setupCSharp({ root, partial, versions, spawn, platform, env });
    if (!result) throw new Error(`${toolData(language).tool} setup failed`);
    const provenance = {
      ...key,
      binaries: result.binaries,
      toolVersionOutput: result.toolVersionOutput,
      completedAt: new Date().toISOString(),
    };
    const temp = path.join(partial, `provenance.json.tmp-${process.pid}`);
    await fs.writeFile(temp, JSON.stringify(provenance, null, 2) + "\n");
    await fs.rename(temp, path.join(partial, "provenance.json"));
    if ((await validateCache(cacheDir, key)).valid) {
      await fs.rm(partial, { recursive: true, force: true });
      write(stdout, `reusing cache ${cacheDir}\n`);
      return 0;
    }
    await fs.rename(partial, cacheDir);
  } catch (error) {
    await fs.rm(partial, { recursive: true, force: true });
    write(stderr, `${error.message}\n`);
    return 1;
  }
  return 0;
}

async function selectTargets({ root, language, spawn, stderr, env }) {
  const config = toolData(language);
  let result = await commandResult(
    spawn,
    "git",
    ["-C", root, "ls-files", "-z", "--unmerged"],
    root,
    true,
    env,
  );
  if (failedResult(result)) {
    write(stderr, "could not inspect git index\n");
    return null;
  }
  if (text(result.stdout)) {
    write(stderr, "unresolved merge conflicts in index\n");
    return null;
  }
  result = await commandResult(
    spawn,
    "git",
    ["-C", root, "ls-files", "-z", "--stage", "--", ...config.prefixes],
    root,
    true,
    env,
  );
  if (failedResult(result)) {
    write(stderr, "could not list indexed files\n");
    return null;
  }
  const entries = new Map();
  for (const record of text(result.stdout).split("\0")) {
    if (!record) continue;
    const match = record.match(/^(\d+)\s+\S+\s+\d+\t(.+)$/s);
    if (!match) continue;
    const [, mode, relative] = match;
    if (!relative.endsWith(config.extension)) continue;
    if (!config.prefixes.some((prefix) => relative.startsWith(prefix)))
      continue;
    entries.set(relative, { mode });
  }
  const targets = [];
  const rootReal = await fs.realpath(root);
  for (const [relative, { mode }] of entries) {
    const components = relative.split("/");
    let reason = null;
    if (mode === "120000") reason = "symlink";
    else if (components.some((component) => SKIP_DIRECTORIES.has(component)))
      reason = "generated directory";
    else if (
      language === "csharp" &&
      /\.(g\.cs|g\.i\.cs|designer\.cs|generated\.cs|AssemblyInfo\.cs|GlobalUsings\.g\.cs)$/i.test(
        relative,
      )
    )
      reason = "generated file";
    const absolute = path.join(root, relative);
    if (!reason) {
      try {
        const stat = await fs.lstat(absolute);
        if (stat.isSymbolicLink()) reason = "symlink";
        else if (
          (await fs.realpath(absolute)) !== path.join(rootReal, relative)
        )
          reason = "symlink directory";
      } catch (error) {
        if (error.code === "ENOENT") reason = "deleted";
        else throw error;
      }
    }
    if (reason) {
      write(stderr, `skip (${reason}): ${relative}\n`);
    } else {
      targets.push(relative);
    }
  }
  targets.sort((a, b) => Buffer.compare(Buffer.from(a), Buffer.from(b)));
  if (!targets.length) {
    write(stderr, "no target files\n");
    return null;
  }
  return targets;
}

function batches(targets, fixedArgs, maxArgBytes) {
  const fixed = fixedArgs.reduce((sum, arg) => sum + byteLength(arg) + 1, 0);
  const result = [];
  let batch = [];
  let size = fixed;
  for (const target of targets) {
    const targetSize = byteLength(target) + 1;
    if (batch.length && size + targetSize > maxArgBytes) {
      result.push(batch);
      batch = [];
      size = fixed;
    }
    batch.push(target);
    size += targetSize;
  }
  if (batch.length) result.push(batch);
  return result;
}

async function formatTargets({
  root,
  language,
  action,
  targets,
  cacheDir,
  spawn,
  stderr,
  env,
  maxArgBytes,
  platform,
}) {
  const executable =
    language === "swift"
      ? path.join(cacheDir, "bin/swift-format")
      : path.join(
          cacheDir,
          "tools",
          platform === "win32" ? "csharpier.exe" : "csharpier",
        );
  const config = path.resolve(root, toolData(language).config);
  const fixed =
    language === "swift"
      ? action === "check"
        ? ["lint", "--strict", "--configuration", config]
        : ["format", "--in-place", "--configuration", config]
      : action === "check"
        ? ["check", "--config-path", config]
        : ["format", "--config-path", config];
  const limit = maxArgBytes ?? (platform === "win32" ? 7000 : 100000);
  let exitCode = 0;
  for (const batch of batches(targets, fixed, limit)) {
    const result = await commandResult(
      spawn,
      executable,
      [...fixed, ...batch],
      root,
      false,
      env,
    );
    if (failedResult(result)) {
      exitCode = 1;
      if (result.signal)
        write(
          stderr,
          `${toolData(language).tool} terminated by signal ${result.signal}\n`,
        );
      if (result.error)
        write(
          stderr,
          `${toolData(language).tool} spawn error: ${result.error.message}\n`,
        );
    }
  }
  return exitCode;
}

export function createRunner({
  root = DEFAULT_ROOT,
  spawn = defaultSpawn,
  platform = process.platform,
  arch = process.arch,
  stdout = process.stdout,
  stderr = process.stderr,
  env = process.env,
  maxArgBytes,
  toolsRoot = path.join(root, ".devin/format/tools"),
} = {}) {
  return {
    async run(language, action) {
      if (
        !TOOL_CONFIG[language] ||
        !["setup", "check", "write"].includes(action)
      ) {
        write(
          stderr,
          "usage: node scripts/format.mjs <swift|csharp> <setup|check|write>\n",
        );
        return 2;
      }
      if (action === "setup") {
        return setup({
          root,
          language,
          platform,
          arch,
          spawn,
          stdout,
          stderr,
          env,
          toolsRoot,
        });
      }
      let metadataResult;
      try {
        metadataResult = await metadata({
          root,
          language,
          platform,
          arch,
          spawn,
          env,
        });
      } catch (error) {
        write(stderr, `${error.message}\n`);
        return 1;
      }
      const key = metadataResult.key;
      const cacheDir = path.join(
        toolsRoot,
        toolData(language).tool,
        sha256(canonicalJson(key)),
      );
      const valid = await validateCache(cacheDir, key);
      if (!valid.valid) {
        write(
          stderr,
          `${toolData(language).tool} is not set up for this environment; run: node scripts/format.mjs ${language} setup\n`,
        );
        write(stderr, `provenance mismatch: ${valid.reason}\n`);
        return 1;
      }
      const targets = await selectTargets({
        root,
        language,
        spawn,
        stderr,
        env,
      });
      if (!targets) return 1;
      return formatTargets({
        root,
        language,
        action,
        targets,
        cacheDir,
        spawn,
        stderr,
        env,
        maxArgBytes,
        platform,
      });
    },
  };
}

export async function main(argv = process.argv.slice(2), io = {}) {
  if (argv.length !== 2) {
    write(
      io.stderr ?? process.stderr,
      "usage: node scripts/format.mjs <swift|csharp> <setup|check|write>\n",
    );
    return 2;
  }
  return createRunner({ stdout: io.stdout, stderr: io.stderr, ...io }).run(
    argv[0],
    argv[1],
  );
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  const code = await main();
  process.exitCode = code;
}
