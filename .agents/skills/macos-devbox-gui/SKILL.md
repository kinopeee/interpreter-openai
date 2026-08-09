---
name: macos-devbox-gui
description: How to diagnose and use the Aqua GUI in a macOS Devbox, including TCC responsible-process capture permissions, CGEvent input, live system modals, XCUITest, iOS Simulator, and clean evidence capture.
---

# Using the macOS Devbox GUI

Use this skill when a macOS Devbox task needs a real Aqua desktop: screenshots,
AppKit windows, mouse/keyboard input, menu-bar apps, or XCUITest evidence. The
workflow is deliberately evidence-first: **symptom → diagnosis → remediation →
verification**.

> **Security warning — disposable Devbox only:** Editing the system TCC database
> is appropriate only for a throwaway Devbox used for this investigation. Never
> do it on a physical Mac, a shared machine, or production. The procedure assumes
> SIP is disabled. Always back up the database, grant the minimum services needed,
> and restore the original state when finished.

## 0. First question: is Aqua really absent?

Do not diagnose GUI failure from `launchctl managername` alone.

### Facts measured in this Devbox

```text
launchctl managername
Background

launchctl print gui/501
session = Aqua
```

`WindowServer`, `Dock`, and `Finder` were running. AppKit reported one logical
screen at `1280x800`. SIP was disabled and passwordless `sudo` was available.

### Diagnosis

```bash
id -u
launchctl managername
launchctl print "gui/$(id -u)"
pgrep -af 'WindowServer|Dock.app|Finder.app'
csrutil status
swift - <<'SWIFT'
import AppKit
print("screens=\(NSScreen.screens.count)")
for screen in NSScreen.screens {
    print("frame=\(screen.frame)")
}
SWIFT
```

If `gui/$(id -u)` says `session = Aqua`, the GUI exists even when the manager
name is `Background`. Confirm the screen and WindowServer before investigating
TCC or application code.

## 1. Diagnose `screencapture` failures

### Symptom

```text
could not create image from display
```

### Root cause measured here

TCC did not evaluate only `/usr/sbin/screencapture`. The shell request was
attributed to the responsible parent process:

```text
responsible process = /opt/namespace/vmguest
Service kTCCServiceScreenCapture does not allow prompting; returning denied.
```

The decisive log shape was:

```text
Handling access request to kTCCServiceScreenCapture,
from Sub:{/opt/namespace/vmguest}
Resp:{TCCDProcess: identifier=a.out, pid=584,
      responsible_path=/opt/namespace/vmguest,
      binary_path=/opt/namespace/vmguest},
...
Service kTCCServiceScreenCapture does not allow prompting; returning denied.
```

### Diagnosis commands

Run these before changing TCC:

```bash
/usr/sbin/screencapture -x /tmp/devbox-before.png
sudo log show --last 2m --predicate 'process == "tccd"' --info \
  | grep -i -E 'ScreenCapture|Sub:|Resp:'
```

For a small Swift probe:

```swift
import CoreGraphics
print("preflight=\(CGPreflightScreenCaptureAccess())")
```

If using ScreenCaptureKit, the observed denial was:

```text
SCStreamErrorDomain Code=-3801
```

Inspect `Sub:` and `Resp:` in the tccd output. Grant the responsible process,
not merely the accessor binary.

## 2. Temporary TCC grant and restoration

> **Repeat the security warning:** this edits `/Library` system state. Do it only
> on the disposable Devbox described above. Back up first and restore afterward.

The TCC database used in the investigation was:

```bash
DB="/Library/Application Support/com.apple.TCC/TCC.db"
```

### Back up and grant

The measured path-based clients were `/opt/namespace/vmguest`, `/bin/bash`, and
`/bin/zsh`. Path clients use `client_type=1`; the observed allowed value was
`auth_value=2`.

Grant only the services denied for the operation you are diagnosing. Capture
`tccd` `Sub:` / `Resp:` first, then add the minimum set:

- `kTCCServiceScreenCapture` — `screencapture` / ScreenCaptureKit
- `kTCCServiceAccessibility` — AX trust and synthesizing input
- `kTCCServicePostEvent` — posting `CGEvent` mouse/keyboard events

Do **not** grant `kTCCServiceListenEvent` by default. `guievent.swift` posts
events; it does not install an event tap or otherwise monitor input. Add
ListenEvent only when tccd shows a ListenEvent denial for a monitoring API.

Copy and run this only in the disposable environment.

Do **not** use `INSERT OR IGNORE`: if a matching row already exists with a
denied or weaker `auth_value`, SQLite skips the insert and the grant appears to
succeed while permissions stay unchanged. Delete the matching client/service
rows first, then insert the allowed rows so existing denials are upgraded.

Prefer `sqlite3 .backup` over `cp` so the snapshot is consistent even if WAL
or journal files are present. Stop `tccd` before mutating or restoring the DB.

```bash
sudo killall tccd 2>/dev/null || true
sudo sqlite3 "$DB" ".backup '/tmp/TCC.db.bak'"
sudo sqlite3 /tmp/TCC.db.bak 'PRAGMA integrity_check;'
sudo sqlite3 "$DB" <<'SQL'
BEGIN IMMEDIATE;
DELETE FROM access
WHERE client IN ('/opt/namespace/vmguest','/bin/bash','/bin/zsh')
  AND service IN (
    'kTCCServiceScreenCapture',
    'kTCCServiceAccessibility',
    'kTCCServicePostEvent'
  );
INSERT INTO access
  (service,client,client_type,auth_value,auth_reason,auth_version,
   indirect_object_identifier,flags)
VALUES
('kTCCServiceScreenCapture','/opt/namespace/vmguest',1,2,4,1,'UNUSED',0),
('kTCCServiceScreenCapture','/bin/bash',1,2,4,1,'UNUSED',0),
('kTCCServiceScreenCapture','/bin/zsh',1,2,4,1,'UNUSED',0),
('kTCCServiceAccessibility','/opt/namespace/vmguest',1,2,4,1,'UNUSED',0),
('kTCCServiceAccessibility','/bin/bash',1,2,4,1,'UNUSED',0),
('kTCCServiceAccessibility','/bin/zsh',1,2,4,1,'UNUSED',0),
('kTCCServicePostEvent','/opt/namespace/vmguest',1,2,4,1,'UNUSED',0),
('kTCCServicePostEvent','/bin/bash',1,2,4,1,'UNUSED',0),
('kTCCServicePostEvent','/bin/zsh',1,2,4,1,'UNUSED',0);
COMMIT;
SQL
# tccd relaunches on demand after the next TCC check
```

Retry and verify:

```bash
/usr/sbin/screencapture -x /tmp/devbox-after.png
file /tmp/devbox-after.png
sudo log show --last 2m --predicate 'process == "tccd"' --info \
  | grep -i -E 'ScreenCapture|Sub:|Resp:|Allowed'
```

The measured result after granting the responsible process was
`ReqResult(Auth Right: Allowed (System Set), ...)` and `screencapture -x`
returned `rc=0`.

### Restoration SQL

Restore from the backup taken before the grant. That is the only path that
preserves any pre-existing allowances for the same clients/services. Stop
`tccd` before restoring, then integrity-check the live DB:

```bash
sudo killall tccd 2>/dev/null || true
sudo sqlite3 "$DB" ".restore '/tmp/TCC.db.bak'"
sudo sqlite3 "$DB" 'PRAGMA integrity_check;'
```

If `.restore` is unavailable in the local `sqlite3`, copy the backup file onto
`$DB` only after `tccd` is stopped, then run `PRAGMA integrity_check` and
compare a client/service dump against the backup. Do **not** use a broad
`DELETE FROM access WHERE client IN (...) AND service IN (...)` as
“restoration”: it removes every matching row, including allowances that
existed before the temporary grant. If the backup file is missing, stop and
treat the Devbox TCC state as untrusted rather than deleting by client list.

Verify the post-restore rows match the backup snapshot:

```bash
sudo sqlite3 "$DB" \
  "SELECT service,client,client_type,auth_value FROM access
   WHERE client IN ('/opt/namespace/vmguest','/bin/bash','/bin/zsh')
   AND service IN ('kTCCServiceScreenCapture','kTCCServiceAccessibility',
                   'kTCCServicePostEvent');"
sudo sqlite3 /tmp/TCC.db.bak \
  "SELECT service,client,client_type,auth_value FROM access
   WHERE client IN ('/opt/namespace/vmguest','/bin/bash','/bin/zsh')
   AND service IN ('kTCCServiceScreenCapture','kTCCServiceAccessibility',
                   'kTCCServicePostEvent');"
```

XCTest / target-app grant templates live in the skill scripts directory (not
`/tmp`):

```text
.agents/skills/macos-devbox-gui/scripts/tcc-xctrunner-grants.sql
.agents/skills/macos-devbox-gui/scripts/tcc-xctrunner-restore.sql
.agents/skills/macos-devbox-gui/scripts/tcc-target-app-grants.sql
.agents/skills/macos-devbox-gui/scripts/tcc-target-app-restore.sql
```

Always take a `.backup` first, edit the templates to match the measured
`Sub:` / `Resp:` clients, apply only denied services, then restore from the
backup when finished.

## 3. Coordinates and the reusable event helper

The bundled helper is:

```text
.agents/skills/macos-devbox-gui/scripts/guievent.swift
```

It supports `list`, `click`, `doubleclick`, and `key`:

```bash
cd /path/to/repository
swift .agents/skills/macos-devbox-gui/scripts/guievent.swift --help
swift .agents/skills/macos-devbox-gui/scripts/guievent.swift list
swift .agents/skills/macos-devbox-gui/scripts/guievent.swift click 500 200
swift .agents/skills/macos-devbox-gui/scripts/guievent.swift doubleclick 412 238
swift .agents/skills/macos-devbox-gui/scripts/guievent.swift key 8
swift .agents/skills/macos-devbox-gui/scripts/guievent.swift key --flags command 43
```

### Coordinate trap

- A full screenshot is `2560x1600` PNG because the display is 2x Retina.
- `CGWindowListCopyWindowInfo` bounds and CGEvent coordinates are logical
  `1280x800`, origin at the top-left.
- Passing PNG pixel coordinates directly to CGEvent misses the target; this
  happened during the investigation.
- Agent/browser image viewers may rescale the image again. Never estimate a
  click coordinate from the displayed image. First run `guievent.swift list`
  and choose a point from the target window's reported bounds.
- `NSEvent.mouseLocation` uses a bottom-left origin, so its printed y value
  appears vertically inverted relative to CGWindow/CGEvent coordinates.

### Input implementation and permissions

The helper moves the pointer with `CGWarpMouseCursorPosition`, posts
`leftMouseDown`/`leftMouseUp` to `.cghidEventTap`, and prints
`AXIsProcessTrusted()`. Double-click sets `mouseEventClickState` to `1` and
then `2` on two clicks; otherwise some applications interpret the pair as two
single clicks and do not launch. Key input uses
`CGEvent(keyboardEventSource:virtualKey:keyDown:)`; modifier flags are assigned
to the event. The measured environment had `AXIsProcessTrusted() == true`.

If the helper reports false or events do nothing, diagnose TCC responsible
processes before blaming the coordinates.

## 4. Live system modals

### Detection

The investigation observed live windows owned by:

- `UserNotificationCenter` at `layer=8`
- `universalAccessAuthWarn` at `layer=0`

Use:

```bash
swift .agents/skills/macos-devbox-gui/scripts/guievent.swift list
```

Inspect owner, layer, and bounds. A live dialog can block the intended app;
click its `Allow` or `Don't Allow` button using logical coordinates from its
reported bounds, then list windows again until it disappears.

### Important evidence lesson

A dialog-looking image inside a browser can be a previous screenshot, not a
live modal. In this investigation the earlier conclusion that the modal was
only a Chrome image was wrong: a later `CGWindowListCopyWindowInfo` listing
proved that live `UserNotificationCenter` and `universalAccessAuthWarn`
windows existed. Always confirm a modal with the current window list before
calling it live, and do not infer that an image is live merely because it looks
like a dialog.

## 5. macOS XCUITest

### Environment capability

macOS XCUITest UI interaction works in this environment. The independent
TextEdit test used:

```swift
let app = XCUIApplication(bundleIdentifier: "com.apple.TextEdit")
app.launch()
app.activate()
let window = app.windows.firstMatch
XCTAssertTrue(window.waitForExistence(timeout: 5))
window.click()
```

The result was:

```text
** TEST SUCCEEDED **
```

A typical invocation is:

```bash
xcodebuild test \
  -scheme GUIProbeMacTests \
  -destination 'platform=macOS' \
  -derivedDataPath /tmp/gui-probe-mac/DerivedData
```

### Diagnose custom-app failures in the right order

If a custom app reports `window.exists=false`, a `0x0` frame, or
`onScreen=false`, first run the app outside XCTest and inspect its own
WindowServer windows. The disposable GUIProbeMac initially had an application
startup defect: `NSApplication.shared`, its delegate, and `run()` were not
executed. A valid AppKit entry point was:

```swift
@main
struct GUIProbeMacMain {
    static func main() {
        let app = NSApplication.shared
        app.delegate = AppDelegate()
        app.run()
    }
}
```

Also inspect `NSApplication.setActivationPolicy(.regular)`,
`makeKeyAndOrderFront`, and `LSUIElement`/`LSBackgroundOnly` in Info.plist.
After repair, GUIProbeMac had an on-screen `320x212` window, but its Button
still remained `not hittable`; TextEdit continued to pass. This separates
environment capability from a remaining custom-app accessibility/window-state
problem.

The `app.isHittable` value is often false for the macOS application root even
when its child window is usable. Inspect `window`/element `exists`,
`isEnabled`, `isHittable`, and `frame` instead of using the root application
value alone.

### XCTest capture TCC

An XCTest-launched `screencapture` has a different responsible process:
`com.apple.XCTRunner`. Granting the shell process does not grant XCTRunner.
The measured XCTest ScreenCapture grant made the test-side `screencapture`
succeed; the corresponding Accessibility/PostEvent grant did not by itself
make GUIProbeMac's Button hittable. Use the skill templates
`scripts/tcc-xctrunner-*.sql` and `scripts/tcc-target-app-*.sql` (adjust
clients to the measured responsible process), then restore from the pre-grant
`.backup`.

## 6. iOS Simulator

The iOS Simulator path was fully usable headlessly and did not depend on the
macOS display-capture TCC path:

```bash
xcrun simctl list runtimes
xcrun simctl list devices
xcrun simctl boot <device-udid>
xcrun simctl list devices | grep Booted
xcrun simctl io booted screenshot /tmp/sim.png
file /tmp/sim.png
```

The measured screenshot was `1179x2556`, and iOS XCUITest passed. For a
throwaway UI test project, verify these common wiring points:

- destination is an iOS Simulator, not `platform=macOS`;
- the test target's `TEST_HOST` points to the app when using a hosted test;
- `PRODUCT_NAME` matches the built app;
- `TEST_TARGET_NAME` matches the app target;
- the selected simulator is booted before `xcodebuild test`.

## 7. Building without a Mac Development certificate

### Symptom

The prescribed build failed with:

```text
No signing certificate "Mac Development" found
```

### Non-repository workaround

Do not edit the repository build files just to bypass local signing. Create a
temporary shim outside the repository:

```bash
mkdir -p /tmp/rt-tools
cat >/tmp/rt-tools/xcodebuild <<'SH'
#!/bin/sh
exec /usr/bin/xcodebuild CODE_SIGNING_ALLOWED=NO CODE_SIGNING_REQUIRED=NO "$@"
SH
chmod +x /tmp/rt-tools/xcodebuild
```

Then put it first in `PATH` only for the required launcher:

```bash
PATH="/tmp/rt-tools:$PATH" OPENAI_API_KEY= ./scripts/run.sh
```

This preserves the repository's `scripts/run.sh` / LaunchServices path. It does
not use `CODE_SIGN_IDENTITY=""`; the measured workaround passed
`CODE_SIGNING_ALLOWED=NO CODE_SIGNING_REQUIRED=NO`.

### Important entitlement limitation

With signing disabled, the resulting app is effectively an unsigned build for
permission-sensitive validation. The repository's Hardened Runtime setting and
`com.apple.security.device.audio-input` configuration were not edited, but an
unsigned build cannot establish the same signed Hardened Runtime/entitlement
behavior as a properly signed production build. Use this workaround for visual
launch checks, not for proving microphone/TCC entitlement behavior.

## 8. Launching this repository's macOS app

The repository app is menu-bar resident and has no ordinary main window. Use
the repository's prescribed sequence:

```bash
xcodegen generate
xcodebuild -scheme RealtimeTranslator \
  -destination 'platform=macOS' \
  -derivedDataPath ./build/DerivedData build
./scripts/run.sh
```

Do not launch the executable directly. Check the instance lock/process first;
the app must remain single-instance. Wait several seconds for the status item.

To open Settings:

1. Run `guievent.swift list` and inspect any live permission dialogs.
2. Clear live dialogs with `Allow` or `Don't Allow`.
3. Locate the Realtime Translator menu extra. In the measured session its
   Accessibility position was around `x=1025..1060`, `y=3..27`; use the current
   list/AX bounds rather than hard-coding that value.
4. CGEvent-click the status item.
5. CGEvent-click the menu item `設定…`.
6. Capture and inspect:

```bash
/usr/sbin/screencapture -x /tmp/realtime-translator-settings.png
```

The Settings window title is `Realtime Translator 設定`. Ensure the API-key
field is empty or masked and that no plaintext key is present. For a clean
deliverable, hide/close Chrome, Finder, Calculator, and unrelated windows
before capturing. A waiting subtitle overlay can remain visible:

```text
待機中 — Control + Option + Space で録音開始
```

Never set or print `OPENAI_API_KEY` for this visual check; the documented
launch workaround explicitly clears it.

## 9. Audio and recording limitations

### Measured device state

Read-only checks found no audio input device:

```bash
system_profiler SPAudioDataType
```

returned no device entry, and an AVFoundation enumeration equivalent to:

```swift
import AVFoundation
print(AVCaptureDevice.devices(for: .audio).count)
```

returned `0`. Therefore the real microphone path was not testable in this
environment. Do not infer microphone behavior from a visual launch check.

`ffmpeg` was not installed (`which ffmpeg` exited with status `1`). Possible
future capture alternatives are `/usr/sbin/screencapture -v` or an
AVFoundation-based recorder; neither was needed for the GUI evidence.

## 10. VNC and external access

Port `5900` was listening and returned:

```text
RFB 003.889
```

Screen Sharing was an on-demand launchd service. The observed authentication
security types were `30, 33, 35, 36`; no usable credentials were available.
`kickstart` existed but activation was not performed. `vmguest` also listened
on `10.0.0.2:5901`. Treat external connectivity and authentication as
unverified. Direct `screencapture` plus CGEvent is normally simpler when TCC
has been correctly diagnosed.

## 11. Troubleshooting checklist

1. Confirm `gui/$(id -u)` says `session = Aqua`; ignore `managername=Background`
   as a GUI verdict.
2. Capture the exact tccd `Sub:`/`Resp:` responsible process.
3. Back up TCC before any temporary grant; grant only required services.
4. Restart `tccd`, retry, capture logs, then restore the backup.
5. List current windows before clicking; use logical bounds, never screenshot
   pixels.
6. Clear live `UserNotificationCenter`/`universalAccessAuthWarn` dialogs.
7. For XCUITest, prove the target app is healthy outside XCTest before
   diagnosing hit testing.
8. For unsigned visual builds, keep the signing limitation separate from
   entitlement conclusions.
9. Keep API keys out of environment output, logs, status files, and screenshots.
10. Record the image path and the exact GUI action used so the evidence is
    reproducible.

## Evidence from the original investigation

The integrated report is `/tmp/devbox-gui-final-report.md`. Useful existing
artifacts include:

```text
/tmp/scA.png
/tmp/sim.png
/tmp/textedit-xcui.png
/tmp/gui-probe-standalone-final.png
/tmp/xcui_state_xctest.png
/tmp/demo1.png ... /tmp/demo7.png
.agents/skills/macos-devbox-gui/scripts/tcc-xctrunner-grants.sql
.agents/skills/macos-devbox-gui/scripts/tcc-xctrunner-restore.sql
.agents/skills/macos-devbox-gui/scripts/tcc-target-app-grants.sql
.agents/skills/macos-devbox-gui/scripts/tcc-target-app-restore.sql
```
