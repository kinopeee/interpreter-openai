---
name: windows-realtimetranslator-gui-testing
description: How to build, launch and GUI-test the Windows WPF RealtimeTranslator app (tray-resident) on a Windows VM, including where settings/transcripts live and how to exercise subtitle-transcript flows without a microphone.
---

# Testing the Windows RealtimeTranslator app (WPF, tray-resident)

## Build

The .NET SDK may be installed user-locally, under the user profile, or at
`C:\Program Files\dotnet`, and may be absent from PATH. Detect an existing
`dotnet.exe`, set `DOTNET_ROOT` to that directory, and prefix PATH with the
same value. Reuse this block for both build and launch.

```powershell
$sdkCandidates = @(
  "$env:LOCALAPPDATA\Microsoft\dotnet",
  "$env:USERPROFILE\dotnet",
  "${env:ProgramFiles}\dotnet"
)
# A runtime-only install also ships dotnet.exe, so validate that the host can
# resolve the SDK pinned by windows/global.json (run from the repo root).
$dotnetRoot = $sdkCandidates | Where-Object {
  $exe = Join-Path $_ 'dotnet.exe'
  (Test-Path -LiteralPath $exe) -and
    ((& $exe --version 2>$null) -match '^10\.') -and ($LASTEXITCODE -eq 0)
} | Select-Object -First 1
if (-not $dotnetRoot) {
  throw 'No dotnet.exe that resolves SDK 10 found (see windows/global.json).'
}
$env:DOTNET_ROOT = $dotnetRoot
$env:PATH = "$dotnetRoot;$env:PATH"
dotnet --version
dotnet build windows/RealtimeTranslator.slnx -c Release
dotnet test  windows/RealtimeTranslator.slnx -c Release
```

`windows/global.json` pins SDK `10.0.100` with `rollForward: latestFeature`. If
`dotnet --version` shows a different major, a different `dotnet.exe` won PATH.

If restore fails with "No sources found", add nuget.org once:
`dotnet nuget add source https://api.nuget.org/v3/index.json -n nuget.org`

## Launching the GUI

Output exe: `windows\src\RealtimeTranslator.App\bin\Release\net10.0-windows\RealtimeTranslator.App.exe`

**Important:** if the resolved SDK is user-local, launching the exe directly pops
a "install .NET Desktop Runtime" dialog. Reuse the same `$dotnetRoot`:

```powershell
$env:DOTNET_ROOT = $dotnetRoot
$env:PATH = "$dotnetRoot;$env:PATH"
$exePath = (Resolve-Path 'windows\src\RealtimeTranslator.App\bin\Release\net10.0-windows\RealtimeTranslator.App.exe').Path
Start-Process -FilePath $exePath
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

## Preparing real audio capture on Windows VMs

- Disabled audio services cannot be started. In an **elevated** PowerShell
  (`Set-Service` / `Start-Service` need Administrator), set both to Manual, then
  start them:

```powershell
Set-Service AudioEndpointBuilder -StartupType Manual
Set-Service Audiosrv -StartupType Manual
Start-Service AudioEndpointBuilder
Start-Service Audiosrv
```

- Then allow microphone access for desktop / NonPackaged apps (Settings →
  Privacy & security → Microphone → "Let desktop apps access your microphone").
- If `Get-PnpDevice -Class AudioEndpoint` returns no devices, use VB-Audio's
  official https://vb-audio.com/Cable/ installer. Extract the package and launch
  `VBCABLE_Setup_x64.exe` as administrator; click **Install Driver** and complete
  any driver-trust dialog. Confirm `CABLE Output` is `OK` before trying capture.
- The installer may recommend reboot but endpoints can become available without
  one. Check first; do not reboot a shared testing machine automatically.
- Installer completion may open a browser and shift tray icons. Reinspect the
  desktop before typing a credential or clicking an app control.

## Real reconnect testing without disrupting the devbox network

- For an offline-start test, back up the hosts file's exact bytes, temporarily map
  `api.openai.com` to both `127.0.0.1` and `::1`, then `Clear-DnsClientCache`.
  Run the whole test inside an elevated `try/finally` so an ordinary failure still
  restores the original bytes and flushes DNS (an abrupt process/VM kill can still
  leave the mapping in place — check `hosts` first on the next run). This affects
  new OpenAI connections only; it does not sever an already-established WebSocket.

```powershell
$hosts  = "$env:SystemRoot\System32\drivers\etc\hosts"
$backup = [IO.File]::ReadAllBytes($hosts)
try {
  Add-Content -LiteralPath $hosts -Value "127.0.0.1 api.openai.com", "::1 api.openai.com"
  Clear-DnsClientCache
  # ... start the app, observe reconnect attempts, stop ...
} finally {
  [IO.File]::WriteAllBytes($hosts, $backup)
  Clear-DnsClientCache
}
```
- Local refusal can take several seconds per connect attempt on Windows, so
  elapsed runtime includes connection failures as well as exponential backoff.
  Do not mistake that overhead for a backoff-policy violation.
- The overlay window title is `overlay.windowTitle` (`Realtime Translator
  subtitles` / `Realtime Translator 字幕`). The status banner TextBlock is
  named `StatusBannerTextBlock` in XAML but does **not** set
  `AutomationProperties.AutomationId`. Locate the banner by that window title
  plus its visible text (idle / connecting / reconnecting), and timestamp
  changes while recording the visible UI; never substitute UIA text for visual
  screenshot assertions.
- If a provided key is rejected, do not claim live Listening from a simulated
  server. When diagnosing credential-entry issues, the app's Credential Manager
  blob is **UTF-8**, not UTF-16; compare only a boolean against the environment
  secret, never output the credential.

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
