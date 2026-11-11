# Realtime Translator

[日本語](README.md) | [English](README.en.md)

An always-on realtime subtitle app powered by OpenAI Realtime Translation. Choose Japanese ↔ English (`ja-en`, the default), Japanese ↔ Spanish (`ja-es`), or English ↔ Spanish (`en-es`). There is a macOS edition (menu bar) and a Windows edition (system tray).

It streams microphone audio to OpenAI’s `gpt-live-transcribe` and `gpt-realtime-translate`, and shows source text and translation as a paired subtitle. Translated audio playback is not supported.

For Windows setup, see [Windows](#windows). The sections below describe the macOS edition.

## Privacy and code signing

- [Privacy Policy](PRIVACY.md)
- [Code Signing Policy](CODE_SIGNING_POLICY.md)

Windows release artifacts are unsigned. The project applied to the SignPath
Foundation free code-signing program, but the September 2026 review did not
approve the application because the project does not yet show the level of
public adoption and external references the program requires. The project may
reapply once it gains broader recognition. Until a signing path is established,
Windows releases stay unsigned and SHA-256 verification is recommended. If
signing starts, this section and the release notes will name the signing
provider and carry its credit.

## Requirements

- macOS 26 or later
- Apple Silicon
- Xcode 26 / XcodeGen
- Internet connection (required while recording)
- OpenAI API key (BYOK) with billing configured

## Download (releases)

Get the prebuilt `RealtimeTranslator-<tag>-macos-arm64.zip` from [Releases](https://github.com/kinopeee/interpreter-openai/releases). Tagged macOS artifacts are signed with a Developer ID Application certificate, notarized, and stapled. The SHA-256 checksum is not inside the zip; it is the matching Release asset `RealtimeTranslator-<tag>-macos-arm64.zip.sha256`. Verify it before launching, then move the app to `/Applications`.

```bash
# Run this in the directory where you placed the zip and .sha256
# (/Applications itself is write-protected and yields
# `ditto: .: Operation not permitted`)
cd ~/Downloads

shasum -a 256 -c RealtimeTranslator-<tag>-macos-arm64.zip.sha256

ditto -x -k RealtimeTranslator-<tag>-macos-arm64.zip .
ditto RealtimeTranslator.app /Applications/RealtimeTranslator.app
open /Applications/RealtimeTranslator.app
```

## Setup

```bash
brew install xcodegen   # if not already installed
cd /path/to/interpreter-openai
xcodegen generate
open RealtimeTranslator.xcodeproj
```

Register your API key in one of these ways:

1. Enter and save it in the app Settings via `SecureField` (recommended)
2. Environment-variable import is for local macOS Debug builds only. On each launch, a set `OPENAI_API_KEY` is imported if Keychain is empty. To import a different key, first delete the saved key in Settings, then set the variable in a non-shared user scheme and launch the app. Remove the variable after import. Release builds do not import it.

To build and launch from the CLI:

```bash
./scripts/run.sh
```

Note: `run.sh` launches via `open`, so shell environment variables may not reach the app. In that case, enter the key in Settings (or, for a Debug build, import once from a non-shared user scheme). Passing the key with `open --args` is not allowed.

## Usage

1. Launch the app. It does not appear in the Dock; you will see the menu bar item and the subtitle overlay.
2. On first launch, allow microphone access, then complete OpenAI-send consent and API key save in Settings.
3. In Settings, choose a pair under **Translation direction**: Japanese ↔ English (`ja-en`, default), Japanese ↔ Spanish (`ja-es`), or English ↔ Spanish (`en-es`). Changes apply the next time recording starts.
4. Start recording from the menu bar, or with `Control + Option + Space`, and speak.
5. Either language in the selected pair is translated to the other (for example, `ja-en` translates Japanese to English and English to Japanese).
6. Stop recording with the same control.

If macOS Microphone Mode is **Voice Isolation** and you need to capture the other speaker through speakers, switch it to **Standard**.

## Settings

Open Settings from the menu bar. There are three tabs.

### General

| Item | Description |
| --- | --- |
| Model / subtitle display / translated audio | Explains current behavior (not editable). Translated audio playback is not supported. |
| Translation direction | `Japanese ↔ English` (`ja-en`, default) / `Japanese ↔ Spanish` (`ja-es`) / `English ↔ Spanish` (`en-es`). Translates both ways between the selected languages. Changes apply the next time recording starts. |
| Display language | `Match system` / `Japanese` / `English`. Saved immediately; applied after you restart the app. Independent of translation direction (`languagePair`). |
| Consent to send microphone audio to OpenAI | Required before recording. Translation cannot start without consent. |
| API key | Save or delete in Keychain. On each macOS Debug launch, `OPENAI_API_KEY` is imported only when set and Keychain is empty. Existing keys are never overwritten. Delete a saved key before importing a different one. Release builds do not import it. |

### Speech recognition

| Item | Description |
| --- | --- |
| Noise reduction | `Near-field mic` (`near_field`) or `Far-field mic` (`far_field`, default). Changes apply on the next recording start. |
| Recognition delay | `delay` for `gpt-live-transcribe`. Higher values improve short-utterance accuracy and slow subtitles. Default is `Low latency` (`low`). Options: fastest / low latency / balanced / high accuracy / highest accuracy. |
| Automatically adjust microphone level | Adaptive gain to make quiet voices easier to hear (on by default). Changes apply on the next recording start. |
| Presets | Apply recognition prompt and keywords together (software development / business meeting / hackathon). |
| Recognition prompt | Context hints such as conversation domain (max 1,000 characters). |
| Keywords | Terms to prioritize (proper nouns, etc.). One word per line, max 64. `<` and `>` are stripped before send. |

Changes to prompt, keywords, and recognition delay are applied to the session within a few seconds even while recording.

### Subtitles & controls

| Item | Description |
| --- | --- |
| Font size | Subtitle text size (18–48 pt, default 32 pt). |
| Subtitle recording | **Record subtitles locally** is off by default. When enabled, confirmed subtitles are saved only on this Mac. Use **Export subtitles…** in the menu bar to export, or **Clear subtitle record** to delete the local record. The transcript is not additionally sent to OpenAI. |
| Controls | Start/stop from the menu bar, or `Control + Option + Space`. |
| Subtitle position | Drag with "Edit subtitle position" in the menu bar; turning editing off saves the position. With multiple displays the panel moves to the screen under the pointer. |
| Display screen | Pick a display in the "Subtitle display screen" menu to move the panel to that screen's bottom center and save it. While displays are mirrored, turn on PowerPoint Presenter View instead. Finishing position editing while displays are mirrored does not change the saved position. |

## Architecture

```text
Microphone
  → AVAudioEngine
  → 24 kHz PCM16 mono / 100 ms frames
  → gpt-live-transcribe (always-on source deltas; delay default low, configurable; far-field noise reduction)
  → Realtime Translation WebSocket × 2
      - Before language detection: source-only + last 4 s preroll
      - Connect targets for the two languages in the selected pair
      - After detection, route from preroll to the other language in that pair (e.g. `ja-en`: Japanese → target=en; English → target=ja)
  → Lane selection and subtitle alignment
  → Source + translation NSPanel subtitles
```

## Pricing

While recording, OpenAI API usage is billed. Audio is sent on one source transcription path and one translation path for the detected language. Fixed pricing is not guaranteed. See the latest rates on [OpenAI Pricing](https://developers.openai.com/api/docs/pricing).

## Testing

```bash
xcodegen generate
xcodebuild test \
  -scheme RealtimeTranslator \
  -destination 'platform=macOS' \
  -derivedDataPath ./build/DerivedData \
  -enableCodeCoverage YES
```

Contract checks (shared by GitHub `shared-contracts` and Depot):

```bash
./scripts/ci-shared-contracts.sh
```

Depot Windows Core checks:

```bash
./scripts/ci-windows-core.sh   # Windows Core only; runs on Linux
```

## Notes

- While recording, the app sends microphone audio, session settings such as recognition prompts and keywords, the API key in the authorization header, and a SHA-256 hash of a random installation identifier to OpenAI. Source text and translations are returned by the API. See the [Privacy Policy](PRIVACY.md) for details.
- Offline translation is not supported.
- The API key is stored in Keychain and never written to logs.
- Translated audio playback is not supported.

### macOS session diagnostics

macOS records local session-health diagnostics when a recording attempt starts. They are not sent to OpenAI; see the [Privacy Policy](PRIVACY.md) for what is recorded, where the files are stored, and how to delete them.

## Windows

A WPF app that lives in the system tray. Endpoints, models, audio format, routing, and subtitle semantics match the macOS edition; equivalence is checked with shared-contract fixtures under `shared/`.

### Requirements

- Windows 10 / Windows 11 (x64). Development verification is done on Windows Server 2022 (x64).
- Microphone
- Internet connection (required while recording)
- OpenAI API key (BYOK) with billing configured
- .NET 10 SDK when building from source

Release artifacts are published as self-contained, so end users do not need a .NET install. `scripts/publish-windows.ps1 -Runtime win-arm64` can produce ARM64 artifacts, but only x64 is officially verified (ARM64 is experimental).

### Build and packaging

```powershell
dotnet build windows/RealtimeTranslator.slnx -c Release
dotnet test  windows/RealtimeTranslator.slnx -c Release

# Self-contained package → artifacts/RealtimeTranslator-win-x64
pwsh -File scripts/publish-windows.ps1
# If PowerShell 7 is unavailable, Windows PowerShell also works (UTF-8 BOM script)
powershell -ExecutionPolicy Bypass -File scripts/publish-windows.ps1
```

The GitHub `windows` workflow runs the same steps on `windows-latest` and attaches a `RealtimeTranslator-win-x64` artifact. Origin PR CI uses Depot CI (`.depot/workflows/windows-core.yml`) to restore / build / test / audit Core on Linux. Platform / App / publish stay on GitHub.

### Download (releases)

Prebuilt packages are available from [Releases](https://github.com/kinopeee/interpreter-openai/releases). The SHA-256 checksum is not inside the zip; it is the matching Release asset `RealtimeTranslator-<tag>-win-x64.zip.sha256`. Verify before extracting and launching.

```powershell
$expected = (Get-Content .\RealtimeTranslator-<tag>-win-x64.zip.sha256 -Raw).Trim().Split()[0].ToLowerInvariant()
$actual = (Get-FileHash .\RealtimeTranslator-<tag>-win-x64.zip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $expected) { throw "SHA-256 mismatch: expected $expected, got $actual" }
```

After verification, extract the zip and run `RealtimeTranslator.App.exe` (self-contained; no .NET install required).

Pushing a `v*` tag runs the `release` workflow, which tests → builds → zips both Windows (win-x64) and macOS (arm64) and attaches them to the same Release. The macOS build is Developer ID signed, notarized, and stapled (same steps as [Download (releases)](#download-releases) above).

```powershell
git tag v0.1.0; git push origin v0.1.0
```

### Usage

1. Launch `RealtimeTranslator.App.exe`. No taskbar window appears—only the notification-area icon and subtitle overlay. A second instance is not allowed.
2. Right-click the tray icon → **Settings…**, complete OpenAI-send consent, and save your API key.
3. In **Settings**, choose a pair under **Translation direction**: Japanese ↔ English (`ja-en`, default), Japanese ↔ Spanish (`ja-es`), or English ↔ Spanish (`en-es`). Changes apply the next time recording starts.
4. Start recording from the tray **Start translation** item, or with `Control + Alt + Space`.
5. Either language in the selected pair is translated to the other (for example, `ja-en` translates Japanese to English and English to Japanese).
6. Stop with the same control. After stop, the current subtitle clears in about 5 seconds.

The subtitle overlay is click-through by default so it does not block apps behind it. To move it, turn on **Edit subtitle position** in the tray menu, drag the subtitle, then turn it off again (position is saved and clamped to the work area).

### Settings

Tabs and fields match macOS (General / Speech recognition / Subtitles & controls), with these Windows-specific differences:

| Item | Windows behavior |
| --- | --- |
| API key | Stored/deleted in Windows Credential Manager (generic credential `RealtimeTranslator:openai-api-key`). Never written to the settings file. |
| Start/stop | Tray menu, or `Control + Alt + Space`. |
| Subtitle position | Drag via tray **Edit subtitle position**, then save. |
| Subtitle recording | **Record subtitles locally** is off by default. When enabled, confirmed subtitles are saved only on this PC. Use **Export subtitles…** in the tray menu to export, or **Clear subtitle record** to delete the local record. The transcript is not additionally sent to OpenAI. |
| Settings path | `%LOCALAPPDATA%\RealtimeTranslator\settings.json` (`fontSize`, subtitle position, consent, `languagePair`, recognition prompt/keywords/delay/noise reduction, `automaticGainEnabled`, `recordSubtitles`, display language). The API key is not included. |

Prompt, keyword, and recognition-delay changes apply to the session within a few seconds even while recording. Noise-reduction changes apply on the next recording start.

### Architecture (Windows)

```text
Microphone
  → WASAPI (NAudio)
  → 24 kHz PCM16 mono / 100 ms frames
  → RealtimeTranslator.Core (codec / packetizer / gain / language detection / subtitle alignment; equivalent to macOS via shared contracts)
  → RealtimeTranslator.Platform (WASAPI, Credential Manager, single-instance, global hotkey, redacted logging)
  → RealtimeTranslator.App (WPF: tray, settings, click-through overlay)
```

### Notes (Windows)

- While recording, the app sends microphone audio, session settings such as recognition prompts and keywords, the API key in the authorization header, and a SHA-256 hash of a random installation identifier to OpenAI. Source text and translations are returned by the API. See the [Privacy Policy](PRIVACY.md) for details.
- The API key is stored in Credential Manager and never written to logs or the settings file.
- For on-device checks, see the **Windows** section in `VALIDATION.md`.
