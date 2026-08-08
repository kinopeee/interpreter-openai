#!/usr/bin/env bash
# CI 向け: Developer ID 証明書の一時キーチェーン投入と notarytool 公証・staple。
# 証明書・APIキー・パスワードはリポジトリに置かず、GitHub Actions secrets からのみ渡す。
set -euo pipefail

STATE_FILE="${RUNNER_TEMP:-/tmp}/macos-codesign-state.env"
DEFAULT_TEAM_ID="D8YZR4M6QB"

usage() {
  cat >&2 <<'EOF'
Usage:
  ci-macos-codesign.sh import-keychain
  ci-macos-codesign.sh unlock-keychain
  ci-macos-codesign.sh notarize-staple <path-to-app>
  ci-macos-codesign.sh cleanup
EOF
  exit 2
}

write_state() {
  local key="$1"
  local value="$2"
  touch "$STATE_FILE"
  chmod 600 "$STATE_FILE"
  # 既存キーを置き換えて追記する。
  if grep -q "^${key}=" "$STATE_FILE" 2>/dev/null; then
    local tmp
    tmp="$(mktemp "${STATE_FILE}.XXXXXX")"
    grep -v "^${key}=" "$STATE_FILE" >"$tmp"
    mv "$tmp" "$STATE_FILE"
    chmod 600 "$STATE_FILE"
  fi
  # source 可能なよう値を shell-escape する。
  printf '%s=%q\n' "$key" "$value" >>"$STATE_FILE"
}

load_state() {
  if [[ -f "$STATE_FILE" ]]; then
    # shellcheck disable=SC1090
    source "$STATE_FILE"
  fi
}

require_env() {
  local name="$1"
  if [[ -z "${!name:-}" ]]; then
    echo "missing required environment variable: $name" >&2
    exit 1
  fi
}

import_keychain() {
  require_env MACOS_CERTIFICATE_P12_BASE64
  require_env MACOS_CERTIFICATE_PASSWORD

  local keychain_path="${RUNNER_TEMP:-/tmp}/realtime-translator-signing.keychain-db"
  local keychain_password
  keychain_password="$(openssl rand -base64 32)"
  local cert_path="${RUNNER_TEMP:-/tmp}/developer-id-application.p12"

  rm -f "$keychain_path" "$cert_path"
  echo "$MACOS_CERTIFICATE_P12_BASE64" | base64 --decode >"$cert_path"
  chmod 600 "$cert_path"

  security create-keychain -p "$keychain_password" "$keychain_path"
  security set-keychain-settings -lut 21600 "$keychain_path"
  security unlock-keychain -p "$keychain_password" "$keychain_path"
  # PKCS#12 は証明書+秘密鍵の aggregate。-t cert だと秘密鍵が落ちる。
  security import "$cert_path" \
    -P "$MACOS_CERTIFICATE_PASSWORD" \
    -A \
    -t agg \
    -f pkcs12 \
    -k "$keychain_path"
  # GUIプロンプト無しで codesign / security が鍵を使えるようにする。
  security set-key-partition-list \
    -S apple-tool:,apple:,codesign: \
    -s \
    -k "$keychain_password" \
    "$keychain_path"

  # 空白を含むパスを壊さないよう、1行=1キーチェーンとして配列化する。
  local user_keychains=()
  while IFS= read -r kc; do
    [[ -n "$kc" ]] || continue
    user_keychains+=("$kc")
  done < <(security list-keychains -d user | sed 's/"//g')
  # 一時キーチェーンを先頭に置き、既定キーチェーンも検索対象に残す。
  security list-keychains -d user -s "$keychain_path" "${user_keychains[@]}"

  local identity="${MACOS_DEVELOPER_ID_IDENTITY:-}"
  if [[ -z "$identity" ]]; then
    identity="$(
      security find-identity -v -p codesigning "$keychain_path" \
        | awk -F'"' '/Developer ID Application/ { print $2; exit }'
    )"
  fi
  if [[ -z "$identity" ]]; then
    echo "could not resolve Developer ID Application identity after import" >&2
    security find-identity -v -p codesigning "$keychain_path" >&2 || true
    exit 1
  fi

  local team_id="${MACOS_TEAM_ID:-$DEFAULT_TEAM_ID}"

  write_state KEYCHAIN_PATH "$keychain_path"
  write_state KEYCHAIN_PASSWORD "$keychain_password"
  write_state CERT_PATH "$cert_path"
  # identity / team / keychain path だけ後続ステップへ渡す（パスワードは state に閉じる）。
  if [[ -n "${GITHUB_ENV:-}" ]]; then
    {
      echo "CODESIGN_KEYCHAIN_PATH=$keychain_path"
      echo "CODESIGN_IDENTITY=$identity"
      echo "CODESIGN_TEAM_ID=$team_id"
    } >>"$GITHUB_ENV"
  fi
  if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
    {
      echo "identity=$identity"
      echo "team_id=$team_id"
      echo "keychain=$keychain_path"
    } >>"$GITHUB_OUTPUT"
  fi

  echo "Imported Developer ID certificate into temporary keychain."
  echo "Using identity: $identity"
  echo "Using team id: $team_id"

  rm -f "$cert_path"
  write_state CERT_PATH ""
}

unlock_keychain() {
  load_state
  local keychain_path="${KEYCHAIN_PATH:-${CODESIGN_KEYCHAIN_PATH:-}}"
  local keychain_password="${KEYCHAIN_PASSWORD:-}"
  if [[ -z "$keychain_path" || -z "$keychain_password" ]]; then
    echo "keychain state missing; run import-keychain first" >&2
    exit 1
  fi
  security unlock-keychain -p "$keychain_password" "$keychain_path"
}

prepare_api_key() {
  require_env APP_STORE_CONNECT_KEY_ID
  require_env APP_STORE_CONNECT_ISSUER_ID
  require_env APP_STORE_CONNECT_KEY_P8

  local key_path="${RUNNER_TEMP:-/tmp}/AuthKey_${APP_STORE_CONNECT_KEY_ID}.p8"
  # secrets のリテラル \n も実改行へ正規化する。
  # BSD sed は置換側の \n を解釈しないため、Bash のパラメータ展開を使う。
  local key_pem="$APP_STORE_CONNECT_KEY_P8"
  key_pem="${key_pem//$'\r'/}"
  key_pem="${key_pem//\\n/$'\n'}"
  printf '%s\n' "$key_pem" >"$key_path"
  chmod 600 "$key_path"
  write_state API_KEY_PATH "$key_path"
  echo "$key_path"
}

notarize_staple() {
  local app_path="${1:?app path required}"
  if [[ ! -d "$app_path" ]]; then
    echo "app bundle not found: $app_path" >&2
    exit 1
  fi

  require_env APP_STORE_CONNECT_KEY_ID
  require_env APP_STORE_CONNECT_ISSUER_ID
  require_env APP_STORE_CONNECT_KEY_P8

  unlock_keychain

  echo "Verifying code signature before notarization..."
  codesign --verify --deep --strict --verbose=2 "$app_path"
  codesign -dv --verbose=2 "$app_path"

  # macOS mktemp はテンプレート末尾の X を要求するため、一時dir内に zip を置く。
  local submit_dir submit_zip
  submit_dir="$(mktemp -d "${RUNNER_TEMP:-/tmp}/notarize-submit.XXXXXX")"
  write_state SUBMIT_DIR "$submit_dir"
  submit_zip="${submit_dir}/RealtimeTranslator-notarize.zip"
  # notarytool 提出用の一時zip（拡張属性と署名を保持）。
  ditto -c -k --sequesterRsrc --keepParent "$app_path" "$submit_zip"

  local api_key_path
  api_key_path="$(prepare_api_key)"

  echo "Submitting for notarization via notarytool..."
  xcrun notarytool submit "$submit_zip" \
    --key "$api_key_path" \
    --key-id "$APP_STORE_CONNECT_KEY_ID" \
    --issuer "$APP_STORE_CONNECT_ISSUER_ID" \
    --wait \
    --timeout 30m

  rm -rf "$submit_dir"
  write_state SUBMIT_DIR ""

  echo "Stapling notarization ticket..."
  xcrun stapler staple "$app_path"
  xcrun stapler validate "$app_path"

  echo "Notarization and staple completed."
}

cleanup() {
  load_state
  if [[ -n "${KEYCHAIN_PATH:-}" && -f "${KEYCHAIN_PATH:-}" ]]; then
    security delete-keychain "$KEYCHAIN_PATH" 2>/dev/null || true
  fi
  if [[ -n "${CODESIGN_KEYCHAIN_PATH:-}" && -f "${CODESIGN_KEYCHAIN_PATH:-}" ]]; then
    security delete-keychain "$CODESIGN_KEYCHAIN_PATH" 2>/dev/null || true
  fi
  if [[ -n "${CERT_PATH:-}" && -f "${CERT_PATH:-}" ]]; then
    rm -f "$CERT_PATH"
  fi
  if [[ -n "${API_KEY_PATH:-}" && -f "${API_KEY_PATH:-}" ]]; then
    rm -f "$API_KEY_PATH"
  fi
  if [[ -n "${SUBMIT_DIR:-}" && -d "${SUBMIT_DIR:-}" ]]; then
    rm -rf "$SUBMIT_DIR"
  fi
  # AuthKey_*.p8 / 提出用一時dir の取りこぼしを掃除する。
  rm -f "${RUNNER_TEMP:-/tmp}"/AuthKey_*.p8 \
    "${RUNNER_TEMP:-/tmp}"/developer-id-application.p12 2>/dev/null || true
  rm -rf "${RUNNER_TEMP:-/tmp}"/notarize-submit.* 2>/dev/null || true
  rm -f "$STATE_FILE"
  echo "Cleaned up temporary signing materials."
}

main() {
  local cmd="${1:-}"
  case "$cmd" in
    import-keychain)
      import_keychain
      ;;
    unlock-keychain)
      unlock_keychain
      ;;
    notarize-staple)
      shift
      notarize_staple "${1:-}"
      ;;
    cleanup)
      cleanup
      ;;
    *)
      usage
      ;;
  esac
}

main "$@"
