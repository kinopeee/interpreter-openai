#!/usr/bin/env bash
set -euo pipefail

# RealtimeTranslator は macOS 用 Swift/AppKit アプリ本体（Xcode 必須）に加えて、
# クロスプラットフォームな .NET 10 Core ライブラリ（windows/）と
# shared/ の契約フィクスチャを持つ。Cloud Agent は Linux のため、
# ここでは Linux でビルド・テスト可能な .NET Core ライブラリの依存を用意する。
# macOS アプリ本体（xcodegen / xcodebuild）は Linux では扱えない。

DOTNET_VERSION="10.0.100"
DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

# .NET SDK は本来スナップショット側に焼き込むが、スナップショット無しで
# install が走った場合にも復旧できるよう、存在チェック付きで冪等に導入する。
if ! command -v dotnet >/dev/null 2>&1; then
  if [ ! -x "$DOTNET_ROOT/dotnet" ]; then
    installer="$(mktemp)"
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$installer"
    bash "$installer" --version "$DOTNET_VERSION" --install-dir "$DOTNET_ROOT"
    rm -f "$installer"
  fi
  if [ ! -e /usr/local/bin/dotnet ] && command -v sudo >/dev/null 2>&1; then
    sudo ln -sf "$DOTNET_ROOT/dotnet" /usr/local/bin/dotnet || true
  fi
fi
export PATH="$DOTNET_ROOT:$PATH"

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root/windows"

# 依存復元とビルド。TreatWarningsAsErrors 有効のため警告も検出される。
dotnet restore RealtimeTranslator.slnx
dotnet build RealtimeTranslator.slnx --configuration Release --no-restore

echo "install.sh: .NET Core ライブラリの復元とビルドが完了しました。"
