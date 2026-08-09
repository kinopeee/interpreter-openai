-- Template: remove only the temporary XCTest ScreenCapture rows added by
-- tcc-xctrunner-grants.sql. Prefer restoring the pre-grant `.backup` instead.
-- Disposable Devbox only. Stop tccd before mutating TCC.db.

BEGIN IMMEDIATE;
DELETE FROM access
WHERE client = 'com.apple.XCTRunner'
  AND service = 'kTCCServiceScreenCapture'
  AND client_type = 0
  AND auth_value = 2
  AND auth_reason = 4
  AND indirect_object_identifier = 'UNUSED';
COMMIT;
