-- Template: temporary TCC grants for a custom UI-test target app.
-- Disposable Devbox only. Back up with `sqlite3 "$DB" ".backup '...'"` first.
-- Replace TARGET_BUNDLE_ID with the measured app bundle id (client_type=0)
-- or an absolute path (client_type=1). Grant only services denied in tccd logs.
-- guievent.swift posting typically needs Accessibility + PostEvent, not ListenEvent.

BEGIN IMMEDIATE;
DELETE FROM access
WHERE client IN ('TARGET_BUNDLE_ID')
  AND service IN (
    'kTCCServiceAccessibility',
    'kTCCServicePostEvent'
  );
INSERT INTO access
  (service,client,client_type,auth_value,auth_reason,auth_version,
   indirect_object_identifier,flags)
VALUES
('kTCCServiceAccessibility','TARGET_BUNDLE_ID',0,2,4,1,'UNUSED',0),
('kTCCServicePostEvent','TARGET_BUNDLE_ID',0,2,4,1,'UNUSED',0);
COMMIT;
