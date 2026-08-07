---
name: windows-realtimetranslator-gui-testing
description: How to build, launch and GUI-test the Windows WPF RealtimeTranslator app (tray-resident) on a Windows VM, including where settings/transcripts live and how to exercise subtitle-transcript flows without a microphone.
---

# Testing the Windows RealtimeTranslator app (WPF, tray-resident)

## Build

The .NET SDK may be installed user-locally and not on PATH. Prefix commands:

```powershell
$env:PATH="$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"
dotnet build windows/RealtimeTranslator.slnx -c Release
dotnet test  windows/RealtimeTranslator.slnx -c Release
```

If restore fails with "No sources found", add nuget.org once:
`dotnet nuget add source https://api.nuget.org/v3/index.json -n nuget.org`

## Launching the GUI

Output exe: `windows\src\RealtimeTranslator.App\bin\Release\net10.0-windows\RealtimeTranslator.App.exe`

**Important:** if the SDK is user-local, launching the exe directly pops a
"install .NET Desktop Runtime" dialog. Set `DOTNET_ROOT` first:

```powershell
$env:DOTNET_ROOT="$env:LOCALAPPDATA\Microsoft\dotnet"
$env:PATH="$env:DOTNET_ROOT;$env:PATH"
Start-Process "<path>\RealtimeTranslator.App.exe"
```

The app has **no main window** — it is tray-resident plus a click-through subtitle overlay
banner near the bottom of the screen (「待機中 — Control + Alt + Space で録音開始」).
To reach the tray menu: click the notification-area chevron (`^`) in the taskbar to expand
hidden icons, then **right-click** the Realtime Translator icon in the popup.

## Where state lives

- Settings: `%LOCALAPPDATA%\RealtimeTranslator\settings.json` (plain JSON, one key per setting)
- Subtitle transcript: `%LOCALAPPDATA%\RealtimeTranslator\transcripts\session.txt` (UTF-8, no BOM)
- API key: Windows Credential Manager, `RealtimeTranslator:openai-api-key` — never in settings.json
- Logs: `AppLogger` writes to `System.Diagnostics.Trace` only. **No log file is produced**, so
  "does X leak into logs" is best answered by (a) confirming no file exists and (b) grepping
  settings.json / transcripts, plus reading the toast strings on screen.

Reading Japanese file content in PowerShell needs explicit encoding, otherwise it is mojibake:

```powershell
$path = "$env:LOCALAPPDATA\RealtimeTranslator\transcripts\session.txt"
[Console]::OutputEncoding=[Text.Encoding]::UTF8
Get-Content $path -Encoding UTF8
```

## Settings window layout

Three tabs: `一般` (consent checkbox + API key), `音声認識` (prompt/keywords/tuning),
`字幕・操作` (font size slider + subtitle-recording toggle). Changes are debounced (~800 ms)
before hitting disk — wait ~3 s before asserting on settings.json, or close the window to flush.

## Gates before recording can start

`BeginTranslation` returns early unless (1) the consent checkbox on the `一般` tab is checked
and (2) an API key is stored. To exercise start-of-session side effects without a real key, save
a syntactically-plausible dummy key; the session will later fail with「OpenAI APIキーが無効です」
but the synchronous start-path side effects (e.g. writing the transcript session marker) still run.

## Testing subtitle transcript flows without a microphone

Most VMs have no audio input, so live speech → subtitle → transcript cannot be driven. Options:

1. **Session marker via GUI**: toggle recording ON, press tray「翻訳を開始」→ `session.txt` gets
   `=== 録音開始 <ISO8601>`. Toggling OFF and repeating must create nothing. This is a strong
   opt-in/opt-out assertion that needs no audio.
2. **Populate realistic content** with a throwaway console project referencing the *built*
   `RealtimeTranslator.Platform.dll` / `RealtimeTranslator.Core.dll` and calling
   `SubtitleTranscriptStore.MarkSessionStart()` / `AppendEntry(src, dst)`. Keep it **outside the
   repo** and delete it afterwards; verify `git status --short` is empty.
3. **Tray item enablement is in-process**: `SetHasRecordedSubtitles` runs at startup and on
   append/clear only. If you populate `session.txt` externally, **restart the app** or the
   「字幕を書き出し…」/「字幕記録をクリア」items stay greyed out and clicks silently do nothing
   (easy to misdiagnose as a hung UI).
4. **Forcing a write failure** (to see the「字幕の記録に失敗しました」toast): populate
   `session.txt`, then set the file read-only, restart the app so the menu items are enabled,
   and use「字幕記録をクリア」→ OK. Locking the file from another process is less reliable than
   the read-only attribute. Remember to clear the attribute afterwards:

```powershell
$f = "$env:LOCALAPPDATA\RealtimeTranslator\transcripts\session.txt"
Set-ItemProperty -LiteralPath $f -Name IsReadOnly -Value $true
```

After the failure toast is confirmed, clear the attribute so later tests can write:

```powershell
if (Test-Path -LiteralPath $f) {
  Set-ItemProperty -LiteralPath $f -Name IsReadOnly -Value $false
}
```

## Gotchas

- Windows shows only the most recent tray balloon in the notification center, so two banners
  raised close together will hide one. Trigger banners one at a time.
- Confirmation dialogs are WinForms/WPF `MessageBox`; they can appear centered over whatever
  window has focus, not necessarily over the app.
- Always start from a clean profile for opt-in tests (local data **and** stored API key):

```powershell
Remove-Item -Recurse -Force "$env:LOCALAPPDATA\RealtimeTranslator" -ErrorAction SilentlyContinue
cmdkey /delete:RealtimeTranslator:openai-api-key
```

## Devin Secrets Needed

- `OPENAI_API_KEY` — only for live speech/translation validation (requires a mic or virtual
  audio cable). Paste the value into Settings → `一般` → API key and save so it lands in
  Windows Credential Manager (`RealtimeTranslator:openai-api-key`). Do **not** print the key
  to the console, Trace, or any file. All opt-in/persistence/export/clear/banner testing above
  works without it.
