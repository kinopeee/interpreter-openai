#!/bin/bash
# Disposable Devbox only. Restores TCC.db from the pre-grant sqlite3 .backup.
# This is the only supported restore path; do not DELETE rows by client list.
set -euo pipefail

DB="${TCC_DB:-/Library/Application Support/com.apple.TCC/TCC.db}"
BACKUP="${TCC_BACKUP:-/tmp/TCC.db.bak}"

if [[ ! -f "$BACKUP" ]]; then
  echo "missing backup: $BACKUP" >&2
  echo "treat this Devbox TCC state as untrusted; do not DELETE by client list." >&2
  exit 1
fi

sudo killall tccd 2>/dev/null || true
sudo sqlite3 "$DB" <<SQL
.bail on
.restore '$BACKUP'
SQL

check="$(sudo sqlite3 "$DB" 'PRAGMA integrity_check;')"
[[ "$check" == "ok" ]] || {
  echo "integrity_check failed after restore: $check" >&2
  exit 1
}

sudo killall tccd 2>/dev/null || true
echo "restored TCC.db from $BACKUP"
