#!/usr/bin/env bash
# Windows ソリューションのうち、Linux でもビルドできる Core だけを検証する。
# Platform / App（net10.0-windows）と自己完結 publish は Windows runner が必要なので
# .github/workflows/windows.yml 側に残す。
#
# Origin + Depot CI（.depot/workflows/windows-core.yml）の正本。
# 使い方: ./scripts/ci-windows-core.sh
set -euo pipefail

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT/windows"

CORE_PROJECT="src/RealtimeTranslator.Core/RealtimeTranslator.Core.csproj"
CORE_TESTS="tests/RealtimeTranslator.Core.Tests/RealtimeTranslator.Core.Tests.csproj"

dotnet restore "$CORE_PROJECT"
dotnet restore "$CORE_TESTS"
dotnet build "$CORE_PROJECT" --configuration Release --no-restore
dotnet build "$CORE_TESTS" --configuration Release --no-restore
dotnet test "$CORE_TESTS" --configuration Release --no-build

"$ROOT/scripts/ci-dotnet-audit.sh" "$CORE_PROJECT"
"$ROOT/scripts/ci-dotnet-audit.sh" "$CORE_TESTS"
