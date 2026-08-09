#!/bin/bash
# Disposable Devbox only. Restores TCC.db from the pre-grant sqlite3 .backup.
# This is the only supported restore path; do not DELETE rows by client list.
# Requires the same TCC_BACKUP (and preferably TCC_BACKUP_DIR) printed by
# tcc-temp-grant.sh — there is no silent default backup filename.
set -euo pipefail

DB="${TCC_DB:-/Library/Application Support/com.apple.TCC/TCC.db}"
BACKUP_DIR="${TCC_BACKUP_DIR:-/tmp/rt-tcc}"

reject_unsafe_path() {
  local label="$1"
  local value="$2"
  if [[ "$value" == *$'\n'* || "$value" == *$'\r'* || "$value" == *"'"* || "$value" == *'"'* || "$value" == *'..'* ]]; then
    echo "$label contains disallowed characters (.., quote, or newline)" >&2
    exit 2
  fi
}

if [[ -z "${TCC_BACKUP:-}" ]]; then
  echo "TCC_BACKUP is required (use the path printed by tcc-temp-grant.sh)" >&2
  exit 2
fi

reject_unsafe_path "TCC_DB" "$DB"
reject_unsafe_path "TCC_BACKUP_DIR" "$BACKUP_DIR"
reject_unsafe_path "TCC_BACKUP" "$TCC_BACKUP"

mkdir -p "$BACKUP_DIR"
BACKUP_DIR="$(cd "$BACKUP_DIR" && pwd -P)"
base="$(basename "$TCC_BACKUP")"
if [[ ! "$base" =~ ^[A-Za-z0-9._-]+$ ]]; then
  echo "TCC_BACKUP basename must match [A-Za-z0-9._-]+" >&2
  exit 2
fi
# Rebuild as a direct child of the resolved backup directory.
BACKUP="$BACKUP_DIR/$base"

if [[ ! -f "$BACKUP" ]]; then
  echo "missing backup: $BACKUP" >&2
  echo "treat this Devbox TCC state as untrusted; do not DELETE by client list." >&2
  exit 1
fi

sudo killall tccd 2>/dev/null || true
printf '.bail on\n.restore %s\n' "$BACKUP" | sudo sqlite3 "$DB"

check="$(sudo sqlite3 "$DB" 'PRAGMA integrity_check;')"
[[ "$check" == "ok" ]] || {
  echo "integrity_check failed after restore: $check" >&2
  exit 1
}

sudo killall tccd 2>/dev/null || true
echo "restored TCC.db from $BACKUP"
