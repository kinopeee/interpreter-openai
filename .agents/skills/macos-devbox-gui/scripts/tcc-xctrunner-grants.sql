-- Template: temporary TCC grants for XCTest-driven screencapture.
-- Disposable Devbox only. Back up with `sqlite3 "$DB" ".backup '...'"` first.
-- Adjust clients to the measured tccd Sub:/Resp: values before applying.
-- Default: ScreenCapture for com.apple.XCTRunner (bundle id, client_type=0).
-- Do not add kTCCServiceListenEvent unless tccd shows a ListenEvent denial.

BEGIN IMMEDIATE;
DELETE FROM access
WHERE client IN ('com.apple.XCTRunner')
  AND service IN (
    'kTCCServiceScreenCapture'
  );
INSERT INTO access
  (service,client,client_type,auth_value,auth_reason,auth_version,
   indirect_object_identifier,flags)
VALUES
('kTCCServiceScreenCapture','com.apple.XCTRunner',0,2,4,1,'UNUSED',0);
COMMIT;
