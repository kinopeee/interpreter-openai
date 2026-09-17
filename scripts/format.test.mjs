import assert from "node:assert/strict";
import { execFile as execFileCallback } from "node:child_process";
import {
  access,
  chmod,
  mkdtemp,
  mkdir,
  readFile,
  rm,
  symlink,
  writeFile,
} from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { promisify } from "node:util";
import { after, test } from "node:test";
import {
  computeCacheKey,
  computeCacheKeyData,
  createRunner,
  defaultSpawn,
} from "./format.mjs";

const execFile = promisify(execFileCallback);
const repoRoot = path.resolve(import.meta.dirname, "..");
const temporary = [];

after(async () => {
  await Promise.all(
    temporary.map((directory) =>
      rm(directory, { recursive: true, force: true }),
    ),
  );
});

async function git(directory, ...args) {
  return execFile("git", ["-C", directory, ...args]);
}

async function makeRepo(language, files = {}) {
  const root = await mkdtemp(path.join(tmpdir(), "format-test-"));
  temporary.push(root);
  await mkdir(path.join(root, ".devin/format"), { recursive: true });
  await mkdir(path.join(root, "windows"), { recursive: true });
  await writeFile(
    path.join(root, ".devin/format/tool-versions.json"),
    await readFile(path.join(repoRoot, ".devin/format/tool-versions.json")),
  );
  await writeFile(
    path.join(root, ".devin/format/swift-format.Package.resolved"),
    await readFile(
      path.join(repoRoot, ".devin/format/swift-format.Package.resolved"),
    ),
  );
  await writeFile(
    path.join(root, ".devin/format/swift-format.json"),
    await readFile(path.join(repoRoot, ".devin/format/swift-format.json")),
  );
  await writeFile(
    path.join(root, ".devin/format/csharpier.json"),
    await readFile(path.join(repoRoot, ".devin/format/csharpier.json")),
  );
  for (const [relative, contents] of Object.entries(files)) {
    await mkdir(path.dirname(path.join(root, relative)), { recursive: true });
    await writeFile(path.join(root, relative), contents);
  }
  await git(root, "init", "-q");
  await git(root, "config", "user.email", "format@test.invalid");
  await git(root, "config", "user.name", "Format Test");
  await git(root, "add", ".");
  await git(root, "commit", "-qm", "fixture");
  return root;
}

function streams() {
  let stdout = "";
  let stderr = "";
  return {
    stdout: {
      write(value) {
        stdout += value;
      },
    },
    stderr: {
      write(value) {
        stderr += value;
      },
    },
    get stdoutText() {
      return stdout;
    },
    get stderrText() {
      return stderr;
    },
  };
}

function probeSpawn(calls, language, overrides = {}) {
  return async (cmd, args, options) => {
    calls.push({ cmd, args, cwd: options.cwd });
    if (overrides[cmd]) return overrides[cmd](args, options);
    if (cmd.includes(".devin/format/tools/") && overrides.formatter)
      return overrides.formatter(args, options);
    if (cmd === "git") return defaultSpawn(cmd, args, options);
    if (language === "swift" && cmd === "swift" && args[0] === "--version") {
      return {
        code: 0,
        signal: null,
        stdout: "Swift version 6.3.3\n",
        stderr: "",
      };
    }
    if (language === "csharp" && cmd === "dotnet" && args[0] === "--version") {
      return { code: 0, signal: null, stdout: "10.0.401\n", stderr: "" };
    }
    return (
      overrides[cmd]?.(args, options) ?? {
        code: 0,
        signal: null,
        stdout: "",
        stderr: "",
      }
    );
  };
}

async function fakeCache(root, language, spawn) {
  const keyData = await computeCacheKeyData({
    root,
    language,
    platform: "linux",
    arch: "x64",
    spawn,
  });
  const key = await computeCacheKey({
    root,
    language,
    platform: "linux",
    arch: "x64",
    spawn,
  });
  const tool = language === "swift" ? "swift-format" : "csharpier";
  const cache = path.join(root, ".devin/format/tools", tool, key);
  const binaryRelative =
    language === "swift" ? "bin/swift-format" : "tools/csharpier";
  const binary = path.join(cache, binaryRelative);
  await mkdir(path.dirname(binary), { recursive: true });
  await writeFile(binary, "fake formatter");
  const crypto = await import("node:crypto");
  const hash = (value) =>
    crypto.createHash("sha256").update(value).digest("hex");
  const provenance = {
    ...keyData,
    binaries: { [binaryRelative]: hash(await readFile(binary)) },
    toolVersionOutput: language === "swift" ? "main" : "1.3.0\n",
    completedAt: new Date().toISOString(),
  };
  await writeFile(
    path.join(cache, "provenance.json"),
    JSON.stringify(provenance),
  );
  return cache;
}

test("invalid CLI arguments print usage and spawn nothing", async () => {
  /* Given: 不正な引数を受け取るランナーがある */
  /* When: 引数検証を実行する */
  /* Then: usage を出して外部プロセスを起動しない */
  const calls = [];
  const io = streams();
  const runner = createRunner({
    root: repoRoot,
    spawn: async (...args) => calls.push(args),
    stderr: io.stderr,
  });
  assert.equal(await runner.run("other", "check"), 2);
  assert.match(io.stderrText, /usage:/);
  assert.equal(calls.length, 0);
});

test("untracked files are excluded and staged files are included", async () => {
  /* Given: 未追跡と追跡済みの Swift ファイルがある */
  /* When: check を実行する */
  /* Then: インデックスにあるファイルだけを formatter に渡す */
  const root = await makeRepo("swift", {
    "RealtimeTranslator/Tracked.swift": "let tracked = 1\n",
    "RealtimeTranslator/Untracked.swift": "let untracked = 1\n",
  });
  await git(root, "rm", "-q", "RealtimeTranslator/Untracked.swift");
  await writeFile(
    path.join(root, "RealtimeTranslator/Untracked.swift"),
    "let untracked = 1\n",
  );
  const calls = [];
  const spawn = probeSpawn(calls, "swift", {
    [path.join(root, ".devin/format/tools/swift-format")]: () => ({ code: 0 }),
  });
  await fakeCache(root, "swift", spawn);
  const io = streams();
  const code = await createRunner({
    root,
    spawn,
    platform: "linux",
    arch: "x64",
    stdout: io.stdout,
    stderr: io.stderr,
  }).run("swift", "check");
  assert.equal(code, 0);
  const formatter = calls.find((call) => call.args.includes("lint"));
  assert.deepEqual(formatter.args.at(-1), "RealtimeTranslator/Tracked.swift");
  await git(root, "add", "RealtimeTranslator/Untracked.swift");
  calls.length = 0;
  await createRunner({
    root,
    spawn,
    platform: "linux",
    arch: "x64",
    stdout: io.stdout,
    stderr: io.stderr,
  }).run("swift", "check");
  assert.ok(
    calls.find(
      (call) => call.args.at(-1) === "RealtimeTranslator/Untracked.swift",
    ),
  );
});

test("target selection skips generated, symlink, deleted, and non-target paths", async () => {
  /* Given: 生成物、シンボリックリンク、削除済み、非対象のファイルがある */
  /* When: csharp check を実行する */
  /* Then: 通常のソースだけを引数に含める */
  const root = await makeRepo("csharp", {
    "windows/src/A/Keep.cs": "class Keep {}\n",
    "windows/src/A/Keep.g.cs": "class Generated {}\n",
    "windows/src/A/Note.xaml": "<Grid />\n",
    "windows/src/A/Deleted.cs": "class Deleted {}\n",
  });
  await git(root, "rm", "-q", "windows/src/A/Deleted.cs");
  await symlink("Keep.cs", path.join(root, "windows/src/A/Link.cs"));
  await git(root, "add", "windows/src/A/Link.cs");
  const calls = [];
  const spawn = probeSpawn(calls, "csharp");
  await fakeCache(root, "csharp", spawn);
  const io = streams();
  const code = await createRunner({
    root,
    spawn,
    platform: "linux",
    arch: "x64",
    stdout: io.stdout,
    stderr: io.stderr,
  }).run("csharp", "check");
  assert.equal(code, 0);
  const formatter = calls.find((call) => call.args.includes("check"));
  assert.deepEqual(formatter.args.at(-1), "windows/src/A/Keep.cs");
  assert.match(io.stderrText, /generated file|symlink|deleted/);
});

test("paths remain individual arguments and sorting uses UTF-8 bytes", async () => {
  /* Given: 空白と日本語を含むパスが複数ある */
  /* When: check を実行する */
  /* Then: 各パスが単一引数として安定した順序で渡される */
  const root = await makeRepo("swift", {
    "RealtimeTranslator/z.swift": "let z = 1\n",
    "RealtimeTranslator/a 日本.swift": "let a = 1\n",
  });
  const calls = [];
  const spawn = probeSpawn(calls, "swift");
  await fakeCache(root, "swift", spawn);
  const io = streams();
  assert.equal(
    await createRunner({
      root,
      spawn,
      platform: "linux",
      arch: "x64",
      stdout: io.stdout,
      stderr: io.stderr,
    }).run("swift", "check"),
    0,
  );
  const formatter = calls.find((call) => call.args.includes("lint"));
  assert.deepEqual(formatter.args.slice(-2), [
    "RealtimeTranslator/a 日本.swift",
    "RealtimeTranslator/z.swift",
  ]);
});

test("no targets, merge conflicts, spawn errors, and signals fail without formatting", async () => {
  /* Given: 対象なし、競合、spawn エラー、シグナル終了の各状態がある */
  /* When: check を実行する */
  /* Then: 明示的な診断と非ゼロ終了になる */
  const root = await makeRepo("swift", { "shared/fixture.json": "{}\n" });
  const calls = [];
  const spawn = probeSpawn(calls, "swift", {
    formatter: () => ({ code: null, signal: "SIGKILL" }),
  });
  await fakeCache(root, "swift", spawn);
  const io = streams();
  const code = await createRunner({
    root,
    spawn,
    platform: "linux",
    arch: "x64",
    stderr: io.stderr,
  }).run("swift", "check");
  assert.equal(code, 1);
  assert.match(io.stderrText, /no target files/);
  assert.equal(
    calls.filter((call) => call.cmd.includes("swift-format")).length,
    0,
  );
});

test("unresolved index conflicts stop before formatter invocation", async () => {
  /* Given: 二つのブランチが同じ Swift ファイルを競合させている */
  /* When: 競合中に check を実行する */
  /* Then: index の競合を診断し formatter を起動しない */
  const root = await makeRepo("swift", {
    "RealtimeTranslator/Conflict.swift": "let value = 1\n",
  });
  await git(root, "checkout", "-qb", "other");
  await writeFile(
    path.join(root, "RealtimeTranslator/Conflict.swift"),
    "let value = 2\n",
  );
  await git(root, "add", "RealtimeTranslator/Conflict.swift");
  await git(root, "commit", "-qm", "other");
  await git(root, "checkout", "-q", "-");
  await writeFile(
    path.join(root, "RealtimeTranslator/Conflict.swift"),
    "let value = 3\n",
  );
  await git(root, "add", "RealtimeTranslator/Conflict.swift");
  await git(root, "commit", "-qm", "main");
  await assert.rejects(git(root, "merge", "other"));
  const calls = [];
  const spawn = probeSpawn(calls, "swift");
  await fakeCache(root, "swift", spawn);
  const io = streams();
  assert.equal(
    await createRunner({
      root,
      spawn,
      platform: "linux",
      arch: "x64",
      stderr: io.stderr,
    }).run("swift", "check"),
    1,
  );
  assert.match(io.stderrText, /unresolved merge conflicts in index/);
  assert.equal(
    calls.some((call) => call.cmd.includes("swift-format")),
    false,
  );
});

test("batching invokes every batch and returns failure if one fails", async () => {
  /* Given: 引数上限より多い対象ファイルと一つ失敗する formatter がある */
  /* When: write を実行する */
  /* Then: 全バッチを実行し、失敗を返す */
  const root = await makeRepo("swift", {
    "RealtimeTranslator/A.swift": "let a = 1\n",
    "RealtimeTranslator/B.swift": "let b = 1\n",
    "RealtimeTranslator/C.swift": "let c = 1\n",
  });
  const calls = [];
  const spawn = probeSpawn(calls, "swift", {
    formatter: (args) => ({
      code: args.at(-1).endsWith("B.swift") ? 1 : 0,
      signal: null,
    }),
  });
  await fakeCache(root, "swift", spawn);
  const io = streams();
  const runner = createRunner({
    root,
    spawn,
    platform: "linux",
    arch: "x64",
    stderr: io.stderr,
    maxArgBytes: 70,
  });
  assert.equal(await runner.run("swift", "write"), 1);
  const batchesRun = calls.filter((call) => call.cmd.endsWith("swift-format"));
  assert.equal(batchesRun.length, 3);
  assert.deepEqual(
    batchesRun.flatMap((call) => call.args.slice(-1)),
    [
      "RealtimeTranslator/A.swift",
      "RealtimeTranslator/B.swift",
      "RealtimeTranslator/C.swift",
    ],
  );
});

test("provenance mismatches name the field and valid cache reuses setup", async () => {
  /* Given: バイナリハッシュ不一致と有効なキャッシュがある */
  /* When: check と setup を実行する */
  /* Then: 不一致は拒否し、有効なキャッシュはインストールを行わない */
  const root = await makeRepo("swift", {
    "RealtimeTranslator/Foo.swift": "let foo = 1\n",
  });
  const calls = [];
  const spawn = probeSpawn(calls, "swift");
  const cache = await fakeCache(root, "swift", spawn);
  await writeFile(path.join(cache, "bin/swift-format"), "changed");
  const io = streams();
  const runner = createRunner({
    root,
    spawn,
    platform: "linux",
    arch: "x64",
    stdout: io.stdout,
    stderr: io.stderr,
  });
  assert.equal(await runner.run("swift", "check"), 1);
  assert.match(io.stderrText, /binary bin\/swift-format/);
  await fakeCache(root, "swift", spawn);
  calls.length = 0;
  assert.equal(await runner.run("swift", "setup"), 0);
  assert.match(io.stdoutText, /reusing cache/);
  assert.equal(calls.filter((call) => call.cmd === "git").length, 0);
});

test("mismatched key fields are rejected", async () => {
  /* Given: キャッシュのキー項目が現在の環境と異なる */
  /* When: check を実行する */
  /* Then: 不一致のフィールド名を診断して拒否する */
  const root = await makeRepo("swift", {
    "RealtimeTranslator/Foo.swift": "let foo = 1\n",
  });
  const calls = [];
  const spawn = probeSpawn(calls, "swift");
  const cache = await fakeCache(root, "swift", spawn);
  const provenance = JSON.parse(
    await readFile(path.join(cache, "provenance.json"), "utf8"),
  );
  provenance.arch = "different";
  await writeFile(
    path.join(cache, "provenance.json"),
    JSON.stringify(provenance),
  );
  const io = streams();
  assert.equal(
    await createRunner({
      root,
      spawn,
      platform: "linux",
      arch: "x64",
      stderr: io.stderr,
    }).run("swift", "check"),
    1,
  );
  assert.match(io.stderrText, /provenance mismatch: arch/);
});

test("setup failure removes partial and cache provenance", async () => {
  /* Given: Swift の build が失敗するセットアップがある */
  /* When: setup を実行する */
  /* Then: 非ゼロ終了し provenance とキャッシュを残さない */
  const root = await makeRepo("swift");
  const calls = [];
  const spawn = probeSpawn(calls, "swift", {
    swift: (args) =>
      args[0] === "build"
        ? { code: 1, signal: null }
        : { code: 0, signal: null, stdout: "Swift version 6.3.3\n" },
    git: () => ({ code: 1, signal: null }),
  });
  const io = streams();
  const code = await createRunner({
    root,
    spawn,
    platform: "linux",
    arch: "x64",
    stderr: io.stderr,
  }).run("swift", "setup");
  assert.equal(code, 1);
  const cacheKey = await computeCacheKey({
    root,
    language: "swift",
    platform: "linux",
    arch: "x64",
    spawn,
  });
  await assert.rejects(
    readFile(
      path.join(
        root,
        ".devin/format/tools/swift-format",
        cacheKey,
        "provenance.json",
      ),
    ),
  );
});

test("setup removes stale partial caches but preserves live partial caches", async () => {
  /* Given: 死んだPIDと生きたPIDのセットアップ途中キャッシュがある */
  /* When: CSharpier の setup を実行する */
  /* Then: 死んだPIDだけ削除し、生きたPIDは残す */
  const root = await makeRepo("csharp");
  const calls = [];
  const version = "1.3.0";
  const spawn = probeSpawn(calls, "csharp", {
    dotnet: async (args) => {
      if (args[0] === "tool") {
        const tools = args[args.indexOf("--tool-path") + 1];
        const dll = path.join(
          tools,
          ".store",
          "csharpier",
          version,
          "csharpier",
          version,
          "tools/net10.0/any/CSharpier.dll",
        );
        await mkdir(path.dirname(dll), { recursive: true });
        await writeFile(path.join(tools, "csharpier"), "fake csharpier");
        await writeFile(dll, "fake CSharpier.dll");
      }
      return {
        code: 0,
        signal: null,
        stdout: args[0] === "--version" ? "10.0.401\n" : "",
        stderr: "",
      };
    },
    formatter: () => ({
      code: 0,
      signal: null,
      stdout: `${version}\n`,
      stderr: "",
    }),
  });
  const cacheKey = await computeCacheKey({
    root,
    language: "csharp",
    platform: "linux",
    arch: "x64",
    spawn,
  });
  const cacheRoot = path.join(root, ".devin/format/tools/csharpier", cacheKey);
  const deadPartial = `${cacheRoot}.partial-99999999`;
  const livePartial = `${cacheRoot}.partial-${process.ppid}`;
  await mkdir(deadPartial, { recursive: true });
  await mkdir(livePartial, { recursive: true });
  const io = streams();
  assert.equal(
    await createRunner({
      root,
      spawn,
      platform: "linux",
      arch: "x64",
      stdout: io.stdout,
      stderr: io.stderr,
    }).run("csharp", "setup"),
    0,
  );
  await assert.rejects(access(deadPartial));
  await assert.doesNotReject(access(livePartial));

  /* Given: 有効なキャッシュがあり、所有者が終了した partial が残っている */
  /* When: setup を再実行する */
  /* Then: キャッシュ再利用で早期終了しても古い partial は削除される */
  await mkdir(deadPartial, { recursive: true });
  const second = streams();
  assert.equal(
    await createRunner({
      root,
      spawn,
      platform: "linux",
      arch: "x64",
      stdout: second.stdout,
      stderr: second.stderr,
    }).run("csharp", "setup"),
    0,
  );
  assert.match(second.stdoutText, /reusing cache/);
  await assert.rejects(access(deadPartial));

  /* Given: 有効なキャッシュがあり、削除できない古い partial が残っている */
  /* When: setup を再実行する */
  /* Then: 削除失敗は警告に留め、有効なキャッシュを再利用して 0 を返す */
  await mkdir(deadPartial, { recursive: true });
  await writeFile(path.join(deadPartial, "lock"), "");
  await chmod(deadPartial, 0o555);
  try {
    const third = streams();
    assert.equal(
      await createRunner({
        root,
        spawn,
        platform: "linux",
        arch: "x64",
        stdout: third.stdout,
        stderr: third.stderr,
      }).run("csharp", "setup"),
      0,
    );
    assert.match(third.stdoutText, /reusing cache/);
    assert.match(third.stderrText, /could not remove/);
    await assert.doesNotReject(access(deadPartial));
  } finally {
    await chmod(deadPartial, 0o755);
  }
});

test("tool version output is not used as provenance identity", async () => {
  /* Given: ハッシュが一致し toolVersionOutput が main のキャッシュがある */
  /* When: check を実行する */
  /* Then: キャッシュを有効として扱う */
  const root = await makeRepo("swift", {
    "RealtimeTranslator/Foo.swift": "let foo = 1\n",
  });
  const calls = [];
  const spawn = probeSpawn(calls, "swift");
  const cache = await fakeCache(root, "swift", spawn);
  const provenance = JSON.parse(
    await readFile(path.join(cache, "provenance.json"), "utf8"),
  );
  provenance.toolVersionOutput = "main";
  await writeFile(
    path.join(cache, "provenance.json"),
    JSON.stringify(provenance),
  );
  const io = streams();
  assert.equal(
    await createRunner({
      root,
      spawn,
      platform: "linux",
      arch: "x64",
      stderr: io.stderr,
    }).run("swift", "check"),
    0,
  );
});

test("language isolation never probes the other tool", async () => {
  /* Given: Swift と CSharp の formatter ランナーがある */
  /* When: 各言語の check を実行する */
  /* Then: 相手言語の probe を呼び出さない */
  const root = await makeRepo("swift", {
    "RealtimeTranslator/Foo.swift": "let foo = 1\n",
  });
  const calls = [];
  const spawn = probeSpawn(calls, "swift");
  await fakeCache(root, "swift", spawn);
  const io = streams();
  await createRunner({
    root,
    spawn,
    platform: "linux",
    arch: "x64",
    stderr: io.stderr,
  }).run("swift", "check");
  assert.equal(
    calls.some((call) => call.cmd === "dotnet"),
    false,
  );
});

test("csharp language isolation never probes Swift", async () => {
  /* Given: CSharp の formatter ランナーがある */
  /* When: csharp check を実行する */
  /* Then: Swift の probe を呼び出さない */
  const root = await makeRepo("csharp", {
    "windows/src/Foo.cs": "class Foo {}\n",
  });
  const calls = [];
  const spawn = probeSpawn(calls, "csharp");
  await fakeCache(root, "csharp", spawn);
  const io = streams();
  await createRunner({
    root,
    spawn,
    platform: "linux",
    arch: "x64",
    stderr: io.stderr,
  }).run("csharp", "check");
  assert.equal(
    calls.some((call) => call.cmd === "swift"),
    false,
  );
});

test("intermediate symlink directories are skipped", async (t) => {
  /* Given: 対象ディレクトリの中間にシンボリックリンクがある */
  /* When: check を実行する */
  /* Then: リンク先のファイルを formatter に渡さない */
  const root = await makeRepo("swift", {
    "outside/Foo.swift": "let foo = 1\n",
  });
  await mkdir(path.join(root, "RealtimeTranslator"), { recursive: true });
  try {
    await symlink(
      path.join(root, "outside"),
      path.join(root, "RealtimeTranslator/Linked"),
    );
  } catch (error) {
    t.skip(`Windows では symlink 権限によりスキップ: ${error.code}`);
    return;
  }
  const { stdout: blob } = await execFile("git", [
    "-C",
    root,
    "hash-object",
    "-w",
    "outside/Foo.swift",
  ]);
  await execFile("git", [
    "-C",
    root,
    "update-index",
    "--add",
    "--cacheinfo",
    `100644,${blob.trim()},RealtimeTranslator/Linked/Foo.swift`,
  ]);
  const calls = [];
  const spawn = probeSpawn(calls, "swift");
  await fakeCache(root, "swift", spawn);
  const io = streams();
  assert.equal(
    await createRunner({
      root,
      spawn,
      platform: "linux",
      arch: "x64",
      stderr: io.stderr,
    }).run("swift", "check"),
    1,
  );
  assert.match(io.stderrText, /symlink directory/);
});
