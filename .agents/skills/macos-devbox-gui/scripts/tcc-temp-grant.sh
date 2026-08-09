#!/bin/bash
# Disposable Devbox only. Never run on a physical Mac or shared machine.
# Applies one grant SQL file under a consistent TCC.db backup, with restore on
# failure. Successful grants stay applied until you run tcc-restore-backup.sh.
#
# If TCC_BACKUP already exists, it is reused (not overwritten) so multiple
# grants in one investigation share one pre-grant snapshot. Set
# TCC_FORCE_BACKUP=1 to replace an existing backup deliberately.
set -euo pipefail

DB="${TCC_DB:-/Library/Application Support/com.apple.TCC/TCC.db}"
BACKUP_DIR="${TCC_BACKUP_DIR:-/tmp/rt-tcc}"
BACKUP="${TCC_BACKUP:-$BACKUP_DIR/TCC.db.bak}"
GRANT_SQL="${1:-}"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
FORCE_BACKUP="${TCC_FORCE_BACKUP:-0}"

reject_unsafe_path() {
  local label="$1"
  local value="$2"
  if [[ "$value" == *$'\n'* || "$value" == *$'\r'* || "$value" == *"'"* || "$value" == *'"'* ]]; then
    echo "$label contains disallowed quote or newline characters" >&2
    exit 2
  fi
}

if [[ -z "$GRANT_SQL" || ! -f "$GRANT_SQL" ]]; then
  echo "usage: $0 <grant.sql>" >&2
  echo "grant.sql must be one of the skill *-grants.sql templates (edited copy ok)." >&2
  exit 2
fi

reject_unsafe_path "TCC_DB" "$DB"
reject_unsafe_path "TCC_BACKUP" "$BACKUP"

GRANT_SQL_ABS="$(cd "$(dirname "$GRANT_SQL")" && pwd)/$(basename "$GRANT_SQL")"
reject_unsafe_path "GRANT_SQL" "$GRANT_SQL_ABS"

case "$GRANT_SQL_ABS" in
  "$SCRIPT_DIR"/*-grants.sql | /tmp/tcc-*-grants.sql | /tmp/tcc-grant.sql | /tmp/tcc-target-app.sql) ;;
  *)
    echo "refusing GRANT_SQL outside approved templates: $GRANT_SQL_ABS" >&2
    echo "use a skill *-grants.sql file or an edited copy named /tmp/tcc-*-grants.sql" >&2
    exit 2
    ;;
esac

case "$BACKUP" in
  "$BACKUP_DIR"/*)
    base="$(basename "$BACKUP")"
    if [[ ! "$base" =~ ^[A-Za-z0-9._-]+$ ]]; then
      echo "TCC_BACKUP basename must match [A-Za-z0-9._-]+" >&2
      exit 2
    fi
    ;;
  *)
    echo "TCC_BACKUP must be under $BACKUP_DIR" >&2
    exit 2
    ;;
esac

mkdir -p "$BACKUP_DIR"
chmod 700 "$BACKUP_DIR" 2>/dev/null || true

restore_backup() {
  local rc=$?
  trap - ERR INT TERM
  if [[ "${GRANT_APPLIED:-0}" -eq 1 ]]; then
    exit "$rc"
  fi
  if [[ -f "$BACKUP" ]]; then
    echo "restoring TCC.db from $BACKUP after failure" >&2
    sudo killall tccd 2>/dev/null || true
    # Paths validated above; keep CLI args as separate argv-style via printf.
    printf '.bail on\n.restore %s\n' "$BACKUP" | sudo sqlite3 "$DB"
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

if [[ -f "$BACKUP" && "$FORCE_BACKUP" != "1" ]]; then
  echo "reusing existing backup at $BACKUP (set TCC_FORCE_BACKUP=1 to replace)"
else
  rm -f "$BACKUP"
  printf '.bail on\n.backup %s\n' "$BACKUP" | sudo sqlite3 "$DB"
  backup_check="$(sudo sqlite3 "$BACKUP" 'PRAGMA integrity_check;')"
  [[ "$backup_check" == "ok" ]] || {
    echo "backup integrity_check failed: $backup_check" >&2
    exit 1
  }
fi

# Apply SQL via stdin redirection — never interpolate the path into .read.
sudo sqlite3 "$DB" <"$GRANT_SQL_ABS"

GRANT_APPLIED=1
sudo killall tccd 2>/dev/null || true
echo "grant applied from $GRANT_SQL_ABS; backup at $BACKUP"
echo "when finished, restore with: TCC_BACKUP=$BACKUP $SCRIPT_DIR/tcc-restore-backup.sh"
