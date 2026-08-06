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

# floating な https://dot.net/v1/dotnet-install.sh ではなく、レビュー可能な
# commit 固定 URL + SHA-256 検証でインストーラを取得する。
DOTNET_INSTALL_COMMIT="5147e32300a8e908f5d737c8cff63a76b4b63531"
DOTNET_INSTALL_URL="https://raw.githubusercontent.com/dotnet/install-scripts/${DOTNET_INSTALL_COMMIT}/src/dotnet-install.sh"
DOTNET_INSTALL_SHA256="082f7685e156738a1b2e2ed8381a621870d4ce8e8c59278034556f05c186eb2e"

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

persist_dotnet_path() {
  local marker="# RealtimeTranslator Cloud Agent DOTNET_ROOT"
  local bashrc="${HOME}/.bashrc"
  touch "$bashrc"
  if ! grep -qF "$marker" "$bashrc"; then
    {
      echo ""
      echo "$marker"
      echo "export DOTNET_ROOT=\"${DOTNET_ROOT}\""
      echo "export PATH=\"\${DOTNET_ROOT}:\${PATH}\""
    } >>"$bashrc"
  fi
}

# .NET SDK は本来スナップショット側に焼き込むが、スナップショット無しで
# install が走った場合にも復旧できるよう、存在チェック付きで冪等に導入する。
if ! has_required_sdk dotnet && ! has_required_sdk "$DOTNET_ROOT/dotnet"; then
  installer="$(mktemp)"
  curl -fsSL \
    --connect-timeout 10 \
    --max-time 120 \
    --retry 3 \
    --retry-delay 2 \
    "$DOTNET_INSTALL_URL" -o "$installer"
  actual_sha="$(sha256sum "$installer" | awk '{print $1}')"
  if [ "$actual_sha" != "$DOTNET_INSTALL_SHA256" ]; then
    echo "install.sh: dotnet-install.sh の SHA-256 が一致しません (got ${actual_sha})." >&2
    rm -f "$installer"
    exit 1
  fi
  bash "$installer" --version "$DOTNET_VERSION" --install-dir "$DOTNET_ROOT"
  rm -f "$installer"
fi

export PATH="$DOTNET_ROOT:$PATH"
persist_dotnet_path

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
