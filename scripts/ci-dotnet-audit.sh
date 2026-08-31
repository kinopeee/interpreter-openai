#!/usr/bin/env bash
# dotnet list package --vulnerable は脆弱性があっても終了コード 0 を返すため、
# JSON 出力で advisory 件数を数えて非 0 にする。
# GitHub windows ワークフローの Audit 判定と同じルール。
#
# 使い方:
#   ./scripts/ci-dotnet-audit.sh windows/src/RealtimeTranslator.Core/RealtimeTranslator.Core.csproj
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "usage: $0 <project-or-solution>" >&2
  exit 1
fi

TARGET="$1"
if [[ ! -e "$TARGET" ]]; then
  echo "missing project or solution: $TARGET" >&2
  exit 1
fi

dotnet list "$TARGET" package --vulnerable --include-transitive

dotnet list "$TARGET" package --vulnerable --include-transitive --format json --output-version 1 |
  python3 -c '
import json, sys

report = json.load(sys.stdin)
problems = report.get("problems") or []
if problems:
    print(f"dotnet list package reported {len(problems)} problem(s)", file=sys.stderr)
    sys.exit(1)

vulnerability_count = 0
for project in report.get("projects") or []:
    for framework in project.get("frameworks") or []:
        packages = list(framework.get("topLevelPackages") or [])
        packages.extend(framework.get("transitivePackages") or [])
        for package in packages:
            if package is None:
                continue
            vulnerability_count += len(package.get("vulnerabilities") or [])

if vulnerability_count > 0:
    print(
        f"vulnerable packages found: {vulnerability_count} advisory binding(s)",
        file=sys.stderr,
    )
    sys.exit(1)
'
