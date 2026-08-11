# Realtime Translator

A resident real-time Japanese–English subtitle app powered by OpenAI Realtime Translation. Available for macOS (menu bar) and Windows (system tray).

It streams microphone audio to OpenAI's `gpt-live-transcribe` and `gpt-realtime-translate`, and displays source text and translated subtitles as a pair. The initial MVP does not play back translated audio.

For Windows instructions, see [Windows](#windows). The sections below describe the macOS version unless noted otherwise.

**Languages:** [English](README.en.md) · [日本語](README.md)

## Requirements

- macOS 26 or later
- Apple Silicon
- Xcode 26 / XcodeGen
- Internet connection (required while recording)
- OpenAI API key (BYOK) and billing enabled on your account

## Download (releases)

Prebuilt `RealtimeTranslator-<tag>-macos-arm64.zip` is available from [Releases](https://github.com/kinopeee/interpreter-openai/releases). macOS artifacts attached to tagged releases are signed with Developer ID Application, notarized, and stapled. The SHA-256 checksum is not inside the zip; use the matching Release asset `RealtimeTranslator-<tag>-macos-arm64.zip.sha256`. Verify before launch, then move the app to `/Applications`.

```bash
# Run from the directory where you placed the zip and .sha256
# (/Applications is write-protected and may cause `ditto: .: Operation not permitted`)
cd ~/Downloads

shasum -a 256 -c RealtimeTranslator-<tag>-macos-arm64.zip.sha256

ditto -x -k RealtimeTranslator-<tag>-macos-arm64.zip .
ditto RealtimeTranslator.app /Applications/RealtimeTranslator.app
open /Applications/RealtimeTranslator.app
```

## Setup

```bash
brew install xcodegen   # if not already installed
cd /Users/yoo/dev/interpreter-openai
xcodegen generate
open RealtimeTranslator.xcodeproj
```

Register your API key using one of the following:

1. Set `OPENAI_API_KEY` in the Xcode scheme environment variables and launch once (auto-import into Keychain)
2. Enter the key in the app's settings screen via `SecureField` and save

To build and launch from the CLI:

```bash
./scripts/run.sh
```

Note: `run.sh` launches via `open`, so shell environment variables may not reach the app. In that case, import from Xcode or enter the key in settings. Passing the key via `open --args` is not supported.

## Usage

1. Launch the app. It does not appear in the Dock; only the menu bar icon and subtitle overlay are shown.
2. On first launch, grant microphone access and complete OpenAI transmission consent and API key setup in settings.
3. Start recording from the menu bar Start/Stop control, or press `Control + Option + Space`, and speak.
4. Japanese speech is translated to English; English speech is translated to Japanese.
5. Use the same control to stop recording.

## Settings

Open settings from the menu bar. There are three tabs:

### General

| Item | Description |
| --- | --- |
| Model / translation direction / subtitle display / translated audio | Describes current behavior (read-only). Translated audio playback is not available in the MVP. |
| Consent to send microphone audio to OpenAI | Required before recording starts. Translation cannot begin without consent. |
| API key | Save or delete in Keychain. On first launch, can also be imported from the `OPENAI_API_KEY` environment variable. |

### Speech recognition

| Item | Description |
| --- | --- |
| Noise reduction | `Near-field mic` (`near_field`) or `Meeting / far-field` (`far_field`, default). Changes apply on the next recording start. |
| Recognition delay | `delay` for `gpt-live-transcribe`. Higher values improve accuracy on short utterances but increase subtitle latency. Default is `Low latency` (`low`). Options: Fastest / Low latency / Balanced / High accuracy / Highest accuracy. |
| Preset | Applies recognition prompt and keywords together (Software development / Business meeting / Hackathon). |
| Recognition prompt | Context hint such as conversation domain (up to 1,000 characters). |
| Keywords | Terms to prioritize (proper nouns, etc.). One term per line, up to 64 terms. `<` and `>` are stripped before transmission. |

Changes to prompt, keywords, and recognition delay are reflected in the session within a few seconds, even while recording.

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
  → gpt-live-transcribe (always on, source deltas, delay default low, configurable, far-field noise reduction)
  → Realtime Translation WebSocket × 2
      - Before language detection: source only + rolling 4 s preroll
      - target=en (after Japanese detection, send from preroll, English translation)
      - target=ja (after English detection, send from preroll, Japanese translation)
  → Lane selection and subtitle alignment
  → NSPanel subtitles (source + translation)
```

## Pricing

OpenAI API usage is metered while recording. Audio is sent to one source transcription stream and one translation stream for the detected language. No fixed price is guaranteed. See [OpenAI Pricing](https://developers.openai.com/api/docs/pricing) for current rates.

## Tests

```bash
xcodegen generate
xcodebuild test \
  -scheme RealtimeTranslator \
  -destination 'platform=macOS' \
  -derivedDataPath ./build/DerivedData \
  -enableCodeCoverage YES
```

## Notes

- Microphone audio, source text, and translations are sent to the OpenAI API.
- Translation is not available offline.
- API keys are stored in Keychain and are not written to logs.
- The MVP does not read translated audio aloud.

## Windows

A WPF app that lives in the system tray. Endpoints, models, audio format, routing, and subtitle semantics match the macOS version; parity is verified with shared contract fixtures in `shared/`.

### Requirements

- Windows 10 / Windows 11 (x64). Development validation uses Windows Server 2022 (x64).
- Microphone
- Internet connection (required while recording)
- OpenAI API key (BYOK) and billing enabled on your account
- .NET 10 SDK (when building from source)

Published artifacts are self-contained, so end users do not need .NET installed. `scripts/publish-windows.ps1 -Runtime win-arm64` can produce ARM64 builds, but only x64 is formally validated (ARM64 is experimental).

### Build and publish

```powershell
dotnet build windows/RealtimeTranslator.slnx -c Release
dotnet test  windows/RealtimeTranslator.slnx -c Release

# Self-contained output to artifacts/RealtimeTranslator-win-x64
pwsh -File scripts/publish-windows.ps1
# If PowerShell 7 is unavailable, Windows PowerShell also works (script is UTF-8 BOM)
powershell -ExecutionPolicy Bypass -File scripts/publish-windows.ps1
```

The `windows` workflow runs the same steps on `windows-latest` and attaches the `RealtimeTranslator-win-x64` artifact.

### Download (releases)

Prebuilt packages are available from [Releases](https://github.com/kinopeee/interpreter-openai/releases). The SHA-256 checksum is not inside the zip; use the matching Release asset `RealtimeTranslator-<tag>-win-x64.zip.sha256`. Verify before extracting and launching.

```powershell
$expected = (Get-Content .\RealtimeTranslator-<tag>-win-x64.zip.sha256 -Raw).Trim().Split()[0].ToLowerInvariant()
$actual = (Get-FileHash .\RealtimeTranslator-<tag>-win-x64.zip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $expected) { throw "SHA-256 mismatch: expected $expected, got $actual" }
```

After verification, extract the zip and run `RealtimeTranslator.App.exe` (self-contained; no .NET install required).

Pushing a `v*` tag triggers the `release` workflow to test, build, and zip both Windows (win-x64) and macOS (arm64), attaching both to the same Release. The macOS build is Developer ID signed, notarized, and stapled (same steps as [Download (releases)](#download-releases) above).

```powershell
git tag v0.1.0; git push origin v0.1.0
```

### Usage

1. Launch `RealtimeTranslator.App.exe`. No taskbar window appears; only the notification area icon and subtitle overlay are shown. A second instance cannot be started.
2. Right-click the tray icon, open **Settings…**, and complete OpenAI transmission consent and API key setup.
3. Start recording from the tray **Start translation** item, or press `Control + Alt + Space`.
4. Japanese speech is translated to English; English speech is translated to Japanese.
5. Use the same control to stop. Subtitles clear about 5 seconds after stopping.

The subtitle overlay is click-through by default and does not block apps behind it. To reposition, turn on **Edit subtitle position** in the tray menu, drag the overlay, then turn it off again (position is saved and clamped to the work area).

### Settings

Tab layout and items match macOS (General / Speech recognition / Subtitles & controls), with these Windows-specific details:

| Item | On Windows |
| --- | --- |
| API key | Stored in Windows Credential Manager (generic credential `RealtimeTranslator:openai-api-key`). Not written to settings files. |
| Start/stop | Tray menu, or `Control + Alt + Space`. |
| Subtitle position | Drag to move while **Edit subtitle position** is enabled in the tray menu. |
| Settings file | `%LOCALAPPDATA%\RealtimeTranslator\settings.json` (font size, subtitle position, consent, recognition prompt/keywords/delay/noise reduction). |

Changes to prompt, keywords, and recognition delay are reflected in the session within a few seconds, even while recording. Noise reduction changes apply on the next recording start.

### Architecture (Windows)

```text
Microphone
  → WASAPI (NAudio)
  → 24 kHz PCM16 mono / 100 ms frames
  → RealtimeTranslator.Core (codec / packetizer / gain / language detection / subtitle alignment; parity via shared contracts with macOS)
  → RealtimeTranslator.Platform (WASAPI, Credential Manager, single-instance guard, global hotkey, redacted logging)
  → RealtimeTranslator.App (WPF: tray, settings, click-through overlay)
```

### Notes (Windows)

- Microphone audio, source text, and translations are sent to the OpenAI API.
- API keys are stored in Credential Manager and are not written to logs or settings files.
- For on-device validation steps, see **Windows** in `VALIDATION.md`.
