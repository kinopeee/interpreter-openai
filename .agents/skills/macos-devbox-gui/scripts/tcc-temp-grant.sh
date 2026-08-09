#!/bin/bash
# Disposable Devbox only. Never run on a physical Mac or shared machine.
# Applies one grant SQL file under a consistent TCC.db backup, with restore on
# failure. Successful grants stay applied until you run tcc-restore-backup.sh.
#
# Default: create a unique backup path under /tmp/rt-tcc for this run.
# Multi-grant sessions must export an explicit TCC_BACKUP and reuse it; when
# that file already exists it is reused (not overwritten) unless
# TCC_FORCE_BACKUP=1. Reused and freshly created backups are integrity-checked.
set -euo pipefail

DB="${TCC_DB:-/Library/Application Support/com.apple.TCC/TCC.db}"
BACKUP_DIR="${TCC_BACKUP_DIR:-/tmp/rt-tcc}"
GRANT_SQL="${1:-}"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
FORCE_BACKUP="${TCC_FORCE_BACKUP:-0}"
BACKUP_READY=0
GRANT_APPLIED=0

reject_unsafe_path() {
  local label="$1"
  local value="$2"
  if [[ "$value" == *$'\n'* || "$value" == *$'\r'* || "$value" == *"'"* || "$value" == *'"'* || "$value" == *'..'* ]]; then
    echo "$label contains disallowed characters (.., quote, or newline)" >&2
    exit 2
  fi
}

require_backup_integrity() {
  local check
  check="$(sudo sqlite3 "$BACKUP" 'PRAGMA integrity_check;')"
  [[ "$check" == "ok" ]] || {
    echo "backup integrity_check failed: $check" >&2
    return 1
  }
}

# Resolve BACKUP_DIR to a physical absolute path and require BACKUP to be a
# direct child of that directory (blocks /tmp/rt-tcc/../escape.db).
resolve_backup_paths() {
  reject_unsafe_path "TCC_BACKUP_DIR" "$BACKUP_DIR"
  mkdir -p "$BACKUP_DIR"
  chmod 700 "$BACKUP_DIR" 2>/dev/null || true
  BACKUP_DIR="$(cd "$BACKUP_DIR" && pwd -P)"

  local base
  if [[ -n "${TCC_BACKUP:-}" ]]; then
    reject_unsafe_path "TCC_BACKUP" "$TCC_BACKUP"
    base="$(basename "$TCC_BACKUP")"
    if [[ ! "$base" =~ ^[A-Za-z0-9._-]+$ ]]; then
      echo "TCC_BACKUP basename must match [A-Za-z0-9._-]+" >&2
      exit 2
    fi
    # Rebuild from resolved directory + basename only (drops any .. parents).
    BACKUP="$BACKUP_DIR/$base"
  else
    # Unique per invocation so a previous investigation's snapshot is not reused
    # accidentally. Multi-grant flows must export TCC_BACKUP explicitly.
    BACKUP="$BACKUP_DIR/TCC.db.bak.$$"
  fi
}

if [[ -z "$GRANT_SQL" || ! -f "$GRANT_SQL" ]]; then
  echo "usage: $0 <grant.sql>" >&2
  echo "grant.sql must be one of the skill *-grants.sql templates (edited copy ok)." >&2
  exit 2
fi

reject_unsafe_path "TCC_DB" "$DB"
resolve_backup_paths

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

restore_backup() {
  local rc=$?
  trap - ERR INT TERM
  # Only restore after a validated pre-grant snapshot exists and before the
  # grant was marked applied. Never .restore from a partial/unvalidated backup.
  if [[ "$GRANT_APPLIED" -eq 1 || "$BACKUP_READY" -ne 1 ]]; then
    exit "$rc"
  fi
  if [[ -f "$BACKUP" ]]; then
    echo "restoring TCC.db from validated backup $BACKUP after failure" >&2
    sudo killall tccd 2>/dev/null || true
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
  require_backup_integrity
  BACKUP_READY=1
else
  # Write to a temp path first so a failed .backup cannot leave a partial file
  # at TCC_BACKUP for the ERR trap or a later restore helper to consume.
  tmp_backup="${BACKUP}.creating.$$"
  rm -f "$tmp_backup"
  if [[ "$FORCE_BACKUP" == "1" ]]; then
    rm -f "$BACKUP"
  fi
  if ! printf '.bail on\n.backup %s\n' "$tmp_backup" | sudo sqlite3 "$DB"; then
    rm -f "$tmp_backup"
    echo "sqlite3 .backup failed" >&2
    exit 1
  fi
  tmp_check="$(sudo sqlite3 "$tmp_backup" 'PRAGMA integrity_check;' || true)"
  if [[ "$tmp_check" != "ok" ]]; then
    rm -f "$tmp_backup"
    echo "backup integrity_check failed: ${tmp_check:-unavailable}" >&2
    exit 1
  fi
  mv "$tmp_backup" "$BACKUP"
  BACKUP_READY=1
fi

# Apply SQL via stdin redirection — never interpolate the path into .read.
sudo sqlite3 "$DB" <"$GRANT_SQL_ABS"

GRANT_APPLIED=1
sudo killall tccd 2>/dev/null || true
echo "grant applied from $GRANT_SQL_ABS; backup at $BACKUP"
echo "when finished, restore with:"
echo "  TCC_DB=$(printf '%q' "$DB") TCC_BACKUP_DIR=$(printf '%q' "$BACKUP_DIR") TCC_BACKUP=$(printf '%q' "$BACKUP") $SCRIPT_DIR/tcc-restore-backup.sh"
