#!/usr/bin/env bash
# Windows 実装のうち Linux / macOS でも回せる Core の restore / build / test / audit。
# Depot CI の正本。solution 全体（Platform / App / publish）は回さない。
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WINDOWS_DIR="$ROOT/windows"
TEST_PROJ="tests/RealtimeTranslator.Core.Tests/RealtimeTranslator.Core.Tests.csproj"

if ! command -v python3 >/dev/null 2>&1; then
  echo "python3 が必要です（dotnet list の脆弱性判定用）。" >&2
  exit 1
fi

if [ ! -f "$WINDOWS_DIR/$TEST_PROJ" ]; then
  echo "missing $TEST_PROJ" >&2
  exit 1
fi

# global.json は CWD から探索される。windows/ で実行して SDK 10 を固定する。
# PATH 先頭の dotnet が古い系統だと見つからないため、満たす SDK を選ぶ。
cd "$WINDOWS_DIR"
select_dotnet() {
  local candidate root
  local candidates=()
  if [ -n "${DOTNET_ROOT:-}" ] && [ -x "${DOTNET_ROOT}/dotnet" ]; then
    candidates+=("${DOTNET_ROOT}/dotnet")
  fi
  if command -v dotnet >/dev/null 2>&1; then
    candidates+=("$(command -v dotnet)")
  fi
  if [ -x "${HOME}/.dotnet/dotnet" ]; then
    candidates+=("${HOME}/.dotnet/dotnet")
  fi
  if [ -x /usr/local/share/dotnet/dotnet ]; then
    candidates+=("/usr/local/share/dotnet/dotnet")
  fi
  for candidate in "${candidates[@]}"; do
    root="$(cd "$(dirname "$candidate")" && pwd)"
    if DOTNET_ROOT="$root" PATH="$root:$PATH" dotnet --version >/dev/null 2>&1; then
      export DOTNET_ROOT="$root"
      export PATH="$root:$PATH"
      return 0
    fi
  done
  echo "windows/global.json を満たす .NET SDK が見つかりません。" >&2
  return 1
}
select_dotnet

echo "dotnet SDK: $(dotnet --version)"
echo "Restore Core tests..."
dotnet restore "$TEST_PROJ"

echo "Build Core tests..."
dotnet build "$TEST_PROJ" --configuration Release --no-restore

echo "Test Core..."
dotnet test "$TEST_PROJ" --configuration Release --no-build

echo "Audit packages..."
dotnet list "$TEST_PROJ" package --vulnerable --include-transitive
report="$(dotnet list "$TEST_PROJ" package --vulnerable --include-transitive --format json --output-version 1)"
printf '%s\n' "$report" | python3 -c '
import json
import sys

text = sys.stdin.read()
start = text.find("{")
if start < 0:
    print("no JSON in dotnet list output", file=sys.stderr)
    sys.exit(1)
report = json.loads(text[start:])
problems = report.get("problems") or []
if problems:
    print(f"dotnet list package reported {len(problems)} problem(s)", file=sys.stderr)
    sys.exit(1)

count = 0
for project in report.get("projects") or []:
    for framework in project.get("frameworks") or []:
        packages = list(framework.get("topLevelPackages") or [])
        packages.extend(framework.get("transitivePackages") or [])
        for package in packages:
            if package is None:
                continue
            count += len(package.get("vulnerabilities") or [])

if count:
    print(f"vulnerable packages found: {count} advisory binding(s)", file=sys.stderr)
    sys.exit(1)
print("no vulnerable packages")
'
