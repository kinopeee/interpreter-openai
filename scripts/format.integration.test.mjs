import assert from "node:assert/strict";
import { execFile as execFileCallback } from "node:child_process";
import { mkdtemp, mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { promisify } from "node:util";
import { after, before, test } from "node:test";
import { createRunner, defaultSpawn } from "./format.mjs";

const execFile = promisify(execFileCallback);
const repoRoot = path.resolve(import.meta.dirname, "..");
const language = process.env.FORMAT_TEST_LANGUAGE;
const temporary = [];
let root;
let io;
let runner;
let spawn;

function outputStreams() {
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

async function git(...args) {
  return execFile("git", ["-C", root, ...args]);
}

async function copyConfiguration() {
  await mkdir(path.join(root, ".devin/format"), { recursive: true });
  for (const name of [
    "tool-versions.json",
    "swift-format.json",
    "csharpier.json",
    "swift-format.Package.resolved",
  ]) {
    await writeFile(
      path.join(root, ".devin/format", name),
      await readFile(path.join(repoRoot, ".devin/format", name)),
    );
  }
}

async function addFile(relative, contents) {
  await mkdir(path.dirname(path.join(root, relative)), { recursive: true });
  await writeFile(path.join(root, relative), contents);
}

before(async () => {
  if (language !== "swift" && language !== "csharp") return;
  root = await mkdtemp(path.join(tmpdir(), `format-integration-${language}-`));
  temporary.push(root);
  await copyConfiguration();
  await mkdir(path.join(root, "windows"), { recursive: true });
  if (language === "swift") {
    await addFile(
      "RealtimeTranslator/Formatted.swift",
      "import Foundation\n\nlet formatted = 1\n",
    );
    await addFile(
      "RealtimeTranslator/Unformatted.swift",
      "import Foundation\nfunc unformatted(){\nlet value=1\nprint(value)\n}\n",
    );
    await addFile("RealtimeTranslator/Syntax.swift", "func broken( {\n");
    await addFile(
      "RealtimeTranslator/Literals.swift",
      'let literal = """\n  preserved text\n  second line\n"""\n// preserved comment\n',
    );
    await addFile("shared/fixtures.json", '{"unchanged":true}\n');
    await addFile("RealtimeTranslator/NonTarget.xaml", "<Grid />\n");
  } else {
    await addFile(
      "windows/src/Formatted.cs",
      "namespace Fixture;\n\npublic static class Formatted\n{\n    public static int Value => 1;\n}\n",
    );
    await addFile(
      "windows/src/Unformatted.cs",
      "namespace Fixture;\npublic static class Unformatted { public static int Value=>1; }\n",
    );
    await addFile(
      "windows/src/Syntax.cs",
      "namespace Fixture;\npublic class Broken {\n",
    );
    await addFile(
      "windows/src/Strings.cs",
      'namespace Fixture;\n\npublic static class Strings\n{\n    public static string Value = @"first\nsecond";\n    // preserved comment\n}\n',
    );
    await addFile(
      "windows/src/CRLF.cs",
      "namespace Fixture;\r\n\r\npublic class CrLf\r\n{\r\n    public int Value => 1;\r\n}\r\n",
    );
    await addFile(
      "windows/src/LF.cs",
      "namespace Fixture;\n\npublic class Lf\n{\n    public int Value => 1;\n}\n",
    );
    await addFile("shared/fixtures.json", '{"unchanged":true}\n');
    await addFile("windows/src/NonTarget.xaml", "<Grid />\n");
  }
  await git("init", "-q");
  await git("config", "user.email", "format-integration@test.invalid");
  await git("config", "user.name", "Format Integration");
  await git("add", ".");
  await git("commit", "-qm", "fixtures");
  io = outputStreams();
  const toolsRoot = path.join(repoRoot, ".devin/format/tools");
  spawn = async (cmd, args, options) => {
    const capture =
      language === "swift"
        ? cmd.endsWith("swift-format")
        : cmd.endsWith("csharpier");
    const result = await defaultSpawn(
      cmd,
      args,
      capture ? { ...options, stdio: ["ignore", "pipe", "pipe"] } : options,
    );
    if (capture) {
      io.stdout.write(result.stdout ?? "");
      io.stderr.write(result.stderr ?? "");
    }
    return result;
  };
  runner = createRunner({
    root,
    toolsRoot,
    spawn,
    stdout: io.stdout,
    stderr: io.stderr,
  });
});

after(async () => {
  await Promise.all(
    temporary.map((directory) =>
      rm(directory, { recursive: true, force: true }),
    ),
  );
});

test("FORMAT_TEST_LANGUAGE is explicit", () => {
  /* Given: 実ツール統合テストの言語指定がある */
  /* When: 環境変数を検証する */
  /* Then: 未指定または未知の指定を受け入れない */
  if (language !== "swift" && language !== "csharp") {
    assert.fail("FORMAT_TEST_LANGUAGE must be swift or csharp");
  }
});

if (language === "swift" || language === "csharp") {
  const targetFiles =
    language === "swift"
      ? [
          "RealtimeTranslator/Formatted.swift",
          "RealtimeTranslator/Unformatted.swift",
          "RealtimeTranslator/Syntax.swift",
          "RealtimeTranslator/Literals.swift",
        ]
      : [
          "windows/src/Formatted.cs",
          "windows/src/Unformatted.cs",
          "windows/src/Syntax.cs",
          "windows/src/Strings.cs",
          "windows/src/CRLF.cs",
          "windows/src/LF.cs",
        ];
  const only = async (files) => {
    await git("checkout", "-q", "--", ".");
    await git("add", ".");
    const removed = targetFiles.filter((file) => !files.includes(file));
    if (removed.length)
      await git("rm", "-q", "--cached", "--ignore-unmatch", "--", ...removed);
    io = outputStreams();
    runner = createRunner({
      root,
      toolsRoot: path.join(repoRoot, ".devin/format/tools"),
      spawn,
      stdout: io.stdout,
      stderr: io.stderr,
    });
  };
  const source = (relative) => path.join(root, relative);
  const run = (action) => runner.run(language, action);
  const formatted =
    language === "swift"
      ? "RealtimeTranslator/Formatted.swift"
      : "windows/src/Formatted.cs";
  const unformatted =
    language === "swift"
      ? "RealtimeTranslator/Unformatted.swift"
      : "windows/src/Unformatted.cs";
  const syntax =
    language === "swift"
      ? "RealtimeTranslator/Syntax.swift"
      : "windows/src/Syntax.cs";

  test("formatted input is check-stable and write-stable", async () => {
    /* Given: 手書き済みの整形済み入力がある */
    /* When: check と write を実行する */
    /* Then: 成功し、バイト列が変わらない */
    await only([formatted]);
    const before = await readFile(source(formatted));
    assert.equal(await run("check"), 0);
    assert.deepEqual(await readFile(source(formatted)), before);
    assert.equal(await run("write"), 0);
    assert.deepEqual(await readFile(source(formatted)), before);
  });

  test("unformatted input fails check and becomes stable after write", async () => {
    /* Given: 整形されていない入力がある */
    /* When: check 後に write する */
    /* Then: check は失敗し、write 後の check は成功する */
    await only([unformatted]);
    const before = await readFile(source(unformatted));
    assert.equal(await run("check"), 1);
    assert.deepEqual(await readFile(source(unformatted)), before);
    assert.match(
      io.stderrText + io.stdoutText,
      new RegExp(path.basename(unformatted)),
    );
    assert.equal(await run("write"), 0);
    assert.equal(await run("check"), 0);
  });

  test("write is idempotent", async () => {
    /* Given: write で整形できる入力がある */
    /* When: write を二度実行する */
    /* Then: 二度目はファイルバイト列を変更しない */
    await only([unformatted]);
    await run("write");
    const first = await readFile(source(unformatted));
    await run("write");
    assert.deepEqual(await readFile(source(unformatted)), first);
  });

  test("syntax errors report the file", async () => {
    /* Given: 構文エラーを含むファイルがある */
    /* When: check を実行する */
    /* Then: 非ゼロ終了しファイル名を診断する */
    await only([syntax]);
    assert.equal(await run("check"), 1);
    assert.match(
      io.stderrText + io.stdoutText,
      new RegExp(path.basename(syntax)),
    );
  });

  test("multiline literals, comments, and non-target files survive", async () => {
    /* Given: 複数行リテラル、コメント、非対象ファイルがある */
    /* When: write を実行する */
    /* Then: 内容と非対象ファイルを保持する */
    const fixture =
      language === "swift"
        ? "RealtimeTranslator/Literals.swift"
        : "windows/src/Strings.cs";
    await only([fixture]);
    const nonTarget = "shared/fixtures.json";
    const nonTargetBefore = await readFile(source(nonTarget));
    await run("write");
    const result = await readFile(source(fixture), "utf8");
    assert.match(result, /preserved text|first/);
    assert.match(result, /preserved comment/);
    assert.deepEqual(await readFile(source(nonTarget)), nonTargetBefore);
  });

  if (language === "csharp") {
    test("CRLF and LF source files preserve their line endings", async () => {
      /* Given: CRLF と LF の同じ CSharp ソースがある */
      /* When: write と check を実行する */
      /* Then: 各ファイルの改行を保持して安定する */
      await only(["windows/src/CRLF.cs", "windows/src/LF.cs"]);
      await run("write");
      const crlf = await readFile(source("windows/src/CRLF.cs"));
      const lf = await readFile(source("windows/src/LF.cs"));
      assert.ok(crlf.includes(Buffer.from("\r\n")));
      assert.equal(crlf.includes(Buffer.from("\n")), true);
      assert.equal(lf.includes(Buffer.from("\r\n")), false);
      assert.equal(await run("check"), 0);
    });
  }
}
