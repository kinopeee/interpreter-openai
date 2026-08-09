-- Unsupported as a restore mechanism.
-- tcc-target-app-grants.sql deletes matching rows before insert, so a later
-- DELETE cannot recreate any pre-existing allow/deny state.
--
-- Restore only from the pre-grant sqlite3 backup:
--   .agents/skills/macos-devbox-gui/scripts/tcc-restore-backup.sh
--
-- Or manually:
--   sudo killall tccd 2>/dev/null || true
--   sudo sqlite3 "$DB" ".restore '/tmp/TCC.db.bak'"
--   sudo sqlite3 "$DB" 'PRAGMA integrity_check;'
--   sudo killall tccd 2>/dev/null || true

SELECT CAST('use tcc-restore-backup.sh / sqlite3 .restore; do not DELETE by client' AS TEXT);
