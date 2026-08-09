-- Template: temporary Accessibility + PostEvent grant for guievent.swift.
-- Disposable Devbox only. Apply via tcc-temp-grant.sh after editing clients.
-- Replace CLIENT with the measured tccd Sub:/Resp: responsible path or bundle id.
-- Replace CLIENT_TYPE with 1 for absolute paths, 0 for bundle ids.
-- Do not grant kTCCServiceListenEvent unless tccd shows a ListenEvent denial.
-- Do not add ScreenCapture here; use tcc-screencapture-grants.sql for capture.

BEGIN IMMEDIATE;
DELETE FROM access
WHERE client = 'CLIENT'
  AND client_type = CLIENT_TYPE
  AND service IN (
    'kTCCServiceAccessibility',
    'kTCCServicePostEvent'
  );
INSERT INTO access
  (service,client,client_type,auth_value,auth_reason,auth_version,
   indirect_object_identifier,flags)
VALUES
('kTCCServiceAccessibility','CLIENT',CLIENT_TYPE,2,4,1,'UNUSED',0),
('kTCCServicePostEvent','CLIENT',CLIENT_TYPE,2,4,1,'UNUSED',0);
COMMIT;
