-- Template: temporary ScreenCapture grant for shell-driven screencapture.
-- Disposable Devbox only. Apply via tcc-temp-grant.sh after editing clients.
-- Replace CLIENT with the measured tccd Sub:/Resp: responsible path or bundle id.
-- Replace CLIENT_TYPE with 1 for absolute paths, 0 for bundle ids.
-- Grant only the denied service. Do not add Accessibility/PostEvent/ListenEvent
-- unless tccd shows those denials for the same operation.

BEGIN IMMEDIATE;
DELETE FROM access
WHERE client = 'CLIENT'
  AND client_type = CLIENT_TYPE
  AND service = 'kTCCServiceScreenCapture';
INSERT INTO access
  (service,client,client_type,auth_value,auth_reason,auth_version,
   indirect_object_identifier,flags)
VALUES
('kTCCServiceScreenCapture','CLIENT',CLIENT_TYPE,2,4,1,'UNUSED',0);
COMMIT;
