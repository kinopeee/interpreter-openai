---
name: macos-speech-injection-testing
description: How to verify real speech -> subtitle -> translation end-to-end on a macOS Devbox that has no physical microphone, by injecting TTS audio through the BlackHole virtual audio device, and how to capture video/screenshot evidence of the subtitle overlay.
---

# macOS Devbox speech-injection verification

Use this skill when a macOS Devbox task needs evidence that **real speech audio**
reaches the RealtimeTranslator app and produces on-screen source subtitles and
translations. Devboxes have no physical microphone; this procedure injects audio
through the **BlackHole 2ch** virtual device instead, which exercises the real
capture path (AVAudioEngine default input -> Realtime API -> subtitle overlay).

Prerequisites (usually from [macos-devbox-gui](../macos-devbox-gui/SKILL.md)):

- App built (`xcodegen generate` + `xcodebuild ... build`, unsigned-shim flags
  `CODE_SIGNING_ALLOWED=NO CODE_SIGNING_REQUIRED=NO` if no signing cert) as a
  **DEBUG** build and started via `./scripts/run.sh`. The status-file poll
  below exists only in DEBUG; Release writes nothing. An unsigned build
  verifies pipeline/rendering behavior only — it does NOT exercise production
  signing or Hardened Runtime entitlement behavior (e.g. the signed
  audio-input entitlement), so treat its evidence as functional/visual, not
  production-equivalent.
- OpenAI API key stored in Keychain via Settings, mic consent toggle ON, and the
  app's mic TCC permission granted via the OS prompt.
- Read `.agents/skills/macos-devbox-gui/SKILL.md` first; its `scripts/guievent.swift`
  sends the Control+Option+Space hotkey, and its notes cover `screencapture` and
  the logical-vs-Retina coordinate mismatch.

## Mechanism

BlackHole is a loopback device: audio played to its **output** appears on its
**input**. `afplay` playback lands directly in the app's AVAudioEngine input.

Verified on the `namespace-devin-macos` Devbox image, where BlackHole 2ch ships
preinstalled and is already the default input/output. Other images may have no
audio devices at all — confirm presence first. If BlackHole is missing,
`brew install blackhole-2ch` is a last resort on a disposable Devbox only
(audio drivers often need a reload or reboot). If install is not viable,
stop; the microphone path is untestable and this skill does not apply.

## 1. Confirm BlackHole exists and is the default device

```bash
system_profiler SPAudioDataType | grep -A8 "BlackHole"
```

Expect the device to be listed at all, plus `Default Input Device: Yes`,
`Default Output Device: Yes`, `Default System Output Device: Yes`
(Existential Audio Inc, 2ch in/out). If the grep finds nothing, BlackHole is
not installed — install it first (see Mechanism).

If it is NOT the default: `SwitchAudioSource`/`audiodevice` are typically not
installed. Switch either via the Audio MIDI Setup GUI, or a short Swift script
setting `kAudioHardwarePropertyDefaultInputDevice` /
`kAudioHardwarePropertyDefaultOutputDevice` / `...DefaultSystemOutputDevice`
on the BlackHole device. Set routing BEFORE starting recording — AVAudioEngine
grabs the default input when recording starts, not when the app launches.

## 2. Generate a TTS speech file

```bash
say -v Kyoko -o /tmp/ja_speech.aiff "こんにちは。今日はリアルタイム翻訳のテストをしています。東京では桜が咲き始めました。とても良い天気ですね。"
afinfo /tmp/ja_speech.aiff   # expect ~10s, AIFC works as-is; no afconvert needed
```

Use 2-3 natural Japanese sentences (~10s). The default volume is sufficient —
no gain needed. A real recorded wav/aiff of speech works too.

## 3. Start screen recording BEFORE driving the app

Pre-flight the TCC grant first — on a fresh Devbox `screencapture` cannot
prompt, and a denied capture exits silently producing no video:

```bash
rm -f /tmp/tcc_probe.png
/usr/sbin/screencapture -x /tmp/tcc_probe.png && file /tmp/tcc_probe.png   # expect "PNG image data"
```

If the probe fails or produces a black/empty image, the responsible process
(`/opt/namespace/vmguest`) lacks ScreenCapture access — use the parent skill's
temporary-grant procedure (`scripts/tcc-temp-grant.sh`, restore with
`tcc-restore-backup.sh` at the end). Only then start recording:

```bash
/usr/sbin/screencapture -v -V 110 /tmp/speech_verify.mov &
```

Post-stop subtitle clear fires ~10-12s after the stop hotkey (a few s of
`closing` + ~5s retention), so record >=110s or keep the stop action early in
the clip; 90s cuts it too close. Back the clear evidence up with stills.

## 4. Drive the session (hotkey only — never coordinate clicks)

Coordinate clicks can open stray Finder windows; always use the hotkey via
`guievent.swift` (space = virtual key 49):

```bash
cd .agents/skills/macos-devbox-gui/scripts
/usr/sbin/screencapture -x /tmp/sp_01_idle.png                    # overlay shows the idle hint
swift guievent.swift key --flags control,option 49                # start recording
# Poll DEBUG status file first line (overwrite, not append). Abort on error.
ok=0
for _ in $(seq 1 60); do
  status=$(sed -n '1p' /tmp/realtimetranslator.status 2>/dev/null || true)
  case "$status" in
    listening) ok=1; break ;;
    error) echo "status file reports error; abort speech injection"; exit 1 ;;
  esac
  sleep 1
done
if [ "$ok" != 1 ]; then
  echo "timed out waiting for listening; abort speech injection"
  sed -n '1p' /tmp/realtimetranslator.status 2>/dev/null || true
  exit 1
fi
/usr/sbin/screencapture -x /tmp/sp_02_listening.png
afplay /tmp/ja_speech.aiff &                                      # audio -> BlackHole -> app mic
sleep 4 && /usr/sbin/screencapture -x /tmp/sp_03_playing.png      # source subtitle appears
sleep 4 && /usr/sbin/screencapture -x /tmp/sp_04_during.png       # source + translation streaming
sleep 3 && /usr/sbin/screencapture -x /tmp/sp_06_translation_done.png
sleep 8 && swift guievent.swift key --flags control,option 49     # stop (same hotkey)
sleep 1 && /usr/sbin/screencapture -x /tmp/sp_07_stopped_subs_remain.png  # "Ending recording..." subs remain
sleep 6 && /usr/sbin/screencapture -x /tmp/sp_08_closing.png      # subs still visible during closing
sleep 5 && /usr/sbin/screencapture -x /tmp/sp_09_idle.png         # cleared -> idle (post-stop clear)
sed -n '1p' /tmp/realtimetranslator.status                        # boot/connecting/listening/closing/idle
```

Timing notes:

- Play audio ONLY after `/tmp/realtimetranslator.status` (DEBUG builds) first
  line is `listening`; playing during `connecting` loses the audio. The file
  is overwritten each write (`status` then optional `translationState`);
  read the first line. Do not replace the poll with a fixed `sleep`.
- Verify subtitle content by eyeballing the overlay in the PNGs — the status
  file only carries state transitions, never subtitle text. Print only the
  first line; do not dump the file into logs or alerts.
- Expected: Japanese source text accumulates, then the English translation
  streams beneath it. Partial-translation rendering may keep small gaps
  (e.g. "...ve started") — that is Realtime API incremental output, not an
  app bug.
- To pull a frame from the .mov for evidence, a small AVAssetImageGenerator
  Swift script can dump a PNG at a given timestamp.

## 5. Cleanup

```bash
pids="$(pgrep -f '/RealtimeTranslator\.app/Contents/MacOS/RealtimeTranslator$' || true)"
[ -z "$pids" ] || kill $pids
```

If you applied a temporary TCC grant, restore with the same `TCC_DB` /
`TCC_BACKUP` via `.agents/skills/macos-devbox-gui/scripts/tcc-restore-backup.sh`.
If you never changed audio routing or the TCC database, there is nothing to
restore.
