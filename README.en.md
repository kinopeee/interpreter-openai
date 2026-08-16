# Realtime Translator

[日本語](README.md) | [English](README.en.md)

An always-on realtime Japanese–English subtitle app powered by OpenAI Realtime Translation. There is a macOS edition (menu bar) and a Windows edition (system tray).

It streams microphone audio to OpenAI’s `gpt-live-transcribe` and `gpt-realtime-translate`, and shows source text and translation as a paired subtitle. The first MVP does not play translated audio.

For Windows setup, see [Windows](#windows). The sections below describe the macOS edition.

## Privacy and code signing

- [Privacy Policy](PRIVACY.md)
- [Code Signing Policy](CODE_SIGNING_POLICY.md)

The project is preparing an application to the SignPath Foundation for Windows
code signing. Windows release artifacts published before approval are unsigned.
After approval, releases signed through SignPath will carry this credit:

> Free code signing provided by SignPath.io, certificate by SignPath Foundation

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
2. For local development only, set `OPENAI_API_KEY` in a non-shared user scheme, launch once, then remove the variable after Keychain import

To build and launch from the CLI:

```bash
./scripts/run.sh
```

Note: `run.sh` launches via `open`, so shell environment variables may not reach the app. In that case, enter the key in Settings (or import once from a non-shared user scheme). Passing the key with `open --args` is not allowed.

## Usage

1. Launch the app. It does not appear in the Dock; you will see the menu bar item and the subtitle overlay.
2. On first launch, allow microphone access, then complete OpenAI-send consent and API key save in Settings.
3. Start recording from the menu bar, or with `Control + Option + Space`, and speak.
4. Japanese audio is translated to English; English audio is translated to Japanese automatically.
5. Stop recording with the same control.

## Settings

Open Settings from the menu bar. There are three tabs.

### General

| Item | Description |
| --- | --- |
| Model / translation direction / subtitle display / translated audio | Explains current behavior (not editable). Translated audio playback is not included in the MVP. |
| Display language | `Match system` / `Japanese` / `English`. Saved immediately; applied after you restart the app. Independent of translation direction (`languagePair`). |
| Consent to send microphone audio to OpenAI | Required before recording. Translation cannot start without consent. |
| API key | Save or delete in Keychain. On first run it can also be imported from the `OPENAI_API_KEY` environment variable. |

### Speech recognition

| Item | Description |
| --- | --- |
| Noise reduction | `Near-field mic` (`near_field`) or `Far-field mic` (`far_field`, default). Changes apply on the next recording start. |
| Recognition delay | `delay` for `gpt-live-transcribe`. Higher values improve short-utterance accuracy and slow subtitles. Default is `Low latency` (`low`). Options: fastest / low latency / balanced / high accuracy / highest accuracy. |
| Presets | Apply recognition prompt and keywords together (software development / business meeting / hackathon). |
| Recognition prompt | Context hints such as conversation domain (max 1,000 characters). |
| Keywords | Terms to prioritize (proper nouns, etc.). One word per line, max 64. `<` and `>` are stripped before send. |

Changes to prompt, keywords, and recognition delay are applied to the session within a few seconds even while recording.

### Subtitles & controls

| Item | Description |
| --- | --- |
| Font size | Subtitle text size (18–48 pt, default 32 pt). |
| Controls | Start/stop from the menu bar, or `Control + Option + Space`. |

## Architecture

```text
Microphone
  → AVAudioEngine
  → 24 kHz PCM16 mono / 100 ms frames
  → gpt-live-transcribe (always-on source deltas; delay default low, configurable; far-field noise reduction)
  → Realtime Translation WebSocket × 2
      - Before language detection: source-only + last 4 s preroll
      - target=en (after Japanese detection, flush from preroll → English translation)
      - target=ja (after English detection, flush from preroll → Japanese translation)
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

## Notes

- Microphone audio is sent to the OpenAI API. Source text and translations are received from the API.
- Offline translation is not supported.
- The API key is stored in Keychain and never written to logs.
- The MVP does not speak translated audio aloud.

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

The `windows` workflow runs the same steps on `windows-latest` and attaches a `RealtimeTranslator-win-x64` artifact.

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
3. Start recording from the tray **Start translation** item, or with `Control + Alt + Space`.
4. Japanese audio is translated to English; English audio is translated to Japanese automatically.
5. Stop with the same control. After stop, the current subtitle clears in about 5 seconds.

The subtitle overlay is click-through by default so it does not block apps behind it. To move it, turn on **Edit subtitle position** in the tray menu, drag the subtitle, then turn it off again (position is saved and clamped to the work area).

### Settings

Tabs and fields match macOS (General / Speech recognition / Subtitles & controls), with these Windows-specific differences:

| Item | Windows behavior |
| --- | --- |
| API key | Stored/deleted in Windows Credential Manager (generic credential `RealtimeTranslator:openai-api-key`). Never written to the settings file. |
| Start/stop | Tray menu, or `Control + Alt + Space`. |
| Subtitle position | Drag via tray **Edit subtitle position**, then save. |
| Settings path | `%LOCALAPPDATA%\RealtimeTranslator\settings.json` (font size, subtitle position, consent, recognition prompt/keywords/delay/noise reduction, display language). |

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

- Microphone audio is sent to the OpenAI API. Source text and translations are received from the API.
- The API key is stored in Credential Manager and never written to logs or the settings file.
- For on-device checks, see the **Windows** section in `VALIDATION.md`.
