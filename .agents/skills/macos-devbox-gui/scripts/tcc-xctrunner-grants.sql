-- Template: temporary ScreenCapture grant for XCTest-driven screencapture.
-- Disposable Devbox only. Apply via tcc-temp-grant.sh after editing if needed.
-- Default client is com.apple.XCTRunner (bundle id, CLIENT_TYPE=0).
-- Adjust CLIENT / CLIENT_TYPE to the measured tccd Sub:/Resp: values.
-- Restore only with tcc-restore-backup.sh (pre-grant .backup). Partial DELETE
-- restore is unsupported because this file deletes matching rows first.

BEGIN IMMEDIATE;
DELETE FROM access
WHERE client = 'com.apple.XCTRunner'
  AND client_type = 0
  AND service = 'kTCCServiceScreenCapture';
INSERT INTO access
  (service,client,client_type,auth_value,auth_reason,auth_version,
   indirect_object_identifier,flags)
VALUES
('kTCCServiceScreenCapture','com.apple.XCTRunner',0,2,4,1,'UNUSED',0);
COMMIT;
