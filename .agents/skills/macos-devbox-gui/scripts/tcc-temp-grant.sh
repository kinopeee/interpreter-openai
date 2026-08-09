#!/bin/bash
# Disposable Devbox only. Never run on a physical Mac or shared machine.
# Applies one grant SQL file under a consistent TCC.db backup, with restore on
# failure. Successful grants stay applied until you run tcc-restore-backup.sh.
set -euo pipefail

DB="${TCC_DB:-/Library/Application Support/com.apple.TCC/TCC.db}"
BACKUP="${TCC_BACKUP:-/tmp/TCC.db.bak}"
GRANT_SQL="${1:-}"

if [[ -z "$GRANT_SQL" || ! -f "$GRANT_SQL" ]]; then
  echo "usage: $0 <grant.sql>" >&2
  echo "edit the SQL template first so it grants only the denied Sub:/Resp: pair." >&2
  exit 2
fi

restore_backup() {
  local rc=$?
  trap - ERR INT TERM
  if [[ "${GRANT_APPLIED:-0}" -eq 1 ]]; then
    exit "$rc"
  fi
  if [[ -f "$BACKUP" ]]; then
    echo "restoring TCC.db from $BACKUP after failure" >&2
    sudo killall tccd 2>/dev/null || true
    sudo sqlite3 "$DB" <<SQL
.bail on
.restore '$BACKUP'
SQL
    local check
    check="$(sudo sqlite3 "$DB" 'PRAGMA integrity_check;')"
    if [[ "$check" != "ok" ]]; then
      echo "integrity_check failed after restore: $check" >&2
      exit 1
    fi
    sudo killall tccd 2>/dev/null || true
  fi
  exit "$rc"
}

trap restore_backup ERR INT TERM

sudo killall tccd 2>/dev/null || true
sudo rm -f "$BACKUP"
sudo sqlite3 "$DB" <<SQL
.bail on
.backup '$BACKUP'
SQL

backup_check="$(sudo sqlite3 "$BACKUP" 'PRAGMA integrity_check;')"
[[ "$backup_check" == "ok" ]] || {
  echo "backup integrity_check failed: $backup_check" >&2
  exit 1
}

GRANT_SQL_ABS="$(cd "$(dirname "$GRANT_SQL")" && pwd)/$(basename "$GRANT_SQL")"
sudo sqlite3 "$DB" <<SQL
.bail on
.read '$GRANT_SQL_ABS'
SQL

GRANT_APPLIED=1
sudo killall tccd 2>/dev/null || true
echo "grant applied from $GRANT_SQL; backup at $BACKUP"
echo "when finished, restore with: $(dirname "$0")/tcc-restore-backup.sh"
