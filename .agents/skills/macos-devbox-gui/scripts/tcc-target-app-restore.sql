-- Template: remove only the temporary target-app rows added by
-- tcc-target-app-grants.sql. Prefer restoring the pre-grant `.backup` instead.
-- Disposable Devbox only. Replace TARGET_BUNDLE_ID to match the grant file.
-- Stop tccd before mutating TCC.db.

BEGIN IMMEDIATE;
DELETE FROM access
WHERE client = 'TARGET_BUNDLE_ID'
  AND client_type = 0
  AND auth_value = 2
  AND auth_reason = 4
  AND indirect_object_identifier = 'UNUSED'
  AND service IN (
    'kTCCServiceAccessibility',
    'kTCCServicePostEvent'
  );
COMMIT;
