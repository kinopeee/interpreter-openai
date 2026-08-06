#!/usr/bin/env bash
set -euo pipefail

# RealtimeTranslator は macOS 用 Swift/AppKit アプリ本体（Xcode 必須）に加えて、
# クロスプラットフォームな .NET 10 Core ライブラリ（windows/）と
# shared/ の契約フィクスチャを持つ。Cloud Agent は Linux のため、
# ここでは Linux でビルド・テスト可能な .NET Core ライブラリの依存を用意する。
# macOS アプリ本体（xcodegen / xcodebuild）は Linux では扱えない。
# Platform（net10.0-windows / WASAPI 等）は Linux ではビルドできないため Core のみ対象にする。

DOTNET_VERSION="10.0.100"
DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

# windows/global.json は 10.0.100 + rollForward:latestFeature を要求する。
# PATH 上に古い SDK があるだけでは不足なので、10.0.1xx 帯の有無を確認する。
has_required_sdk() {
  local probe="${1:-dotnet}"
  if [ -x "$probe" ] || command -v "$probe" >/dev/null 2>&1; then
    "$probe" --list-sdks 2>/dev/null | grep -q '^10\.0\.1'
  else
    return 1
  fi
}

# .NET SDK は本来スナップショット側に焼き込むが、スナップショット無しで
# install が走った場合にも復旧できるよう、存在チェック付きで冪等に導入する。
if ! has_required_sdk dotnet && ! has_required_sdk "$DOTNET_ROOT/dotnet"; then
  installer="$(mktemp)"
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$installer"
  bash "$installer" --version "$DOTNET_VERSION" --install-dir "$DOTNET_ROOT"
  rm -f "$installer"
  if [ ! -e /usr/local/bin/dotnet ] && command -v sudo >/dev/null 2>&1; then
    sudo ln -sf "$DOTNET_ROOT/dotnet" /usr/local/bin/dotnet || true
  fi
fi
export PATH="$DOTNET_ROOT:$PATH"

if ! has_required_sdk dotnet; then
  echo "install.sh: .NET SDK 10.0.1xx が見つかりません。" >&2
  exit 1
fi

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root/windows"

# 依存復元とビルド。TreatWarningsAsErrors 有効のため警告も検出される。
# Platform / Platform.Tests は net10.0-windows のため、Linux Cloud Agent では Core のみ。
dotnet restore src/RealtimeTranslator.Core/RealtimeTranslator.Core.csproj
dotnet build src/RealtimeTranslator.Core/RealtimeTranslator.Core.csproj \
  --configuration Release --no-restore
dotnet restore tests/RealtimeTranslator.Core.Tests/RealtimeTranslator.Core.Tests.csproj
dotnet build tests/RealtimeTranslator.Core.Tests/RealtimeTranslator.Core.Tests.csproj \
  --configuration Release --no-restore

echo "install.sh: .NET Core ライブラリの復元とビルドが完了しました。"
