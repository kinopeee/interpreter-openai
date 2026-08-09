-- Template: temporary Accessibility + PostEvent grant for a UI-test target app.
-- Disposable Devbox only. Apply via tcc-temp-grant.sh after editing placeholders.
-- Replace TARGET_CLIENT with the measured app bundle id or absolute path.
-- Replace TARGET_CLIENT_TYPE with 0 (bundle id) or 1 (absolute path).
-- Use the same TARGET_CLIENT_TYPE in every DELETE and INSERT below.
-- Grant only services denied in tccd logs. guievent posting typically needs
-- Accessibility + PostEvent, not ListenEvent.
-- Restore only with tcc-restore-backup.sh (pre-grant .backup).

BEGIN IMMEDIATE;
DELETE FROM access
WHERE client = 'TARGET_CLIENT'
  AND client_type = TARGET_CLIENT_TYPE
  AND service IN (
    'kTCCServiceAccessibility',
    'kTCCServicePostEvent'
  );
INSERT INTO access
  (service,client,client_type,auth_value,auth_reason,auth_version,
   indirect_object_identifier,flags)
VALUES
('kTCCServiceAccessibility','TARGET_CLIENT',TARGET_CLIENT_TYPE,2,4,1,'UNUSED',0),
('kTCCServicePostEvent','TARGET_CLIENT',TARGET_CLIENT_TYPE,2,4,1,'UNUSED',0);
COMMIT;
