---
name: wpf-subtitle-overlay-visual-testing
description: How to visually A/B test the Windows WPF SubtitleOverlayWindow (halo, line spacing, height reservation, markers) without a microphone or OpenAI API key, by hosting the real production overlay window from a throwaway harness and measuring ActualHeight/Top.
---

# Visually A/B testing the WPF subtitle overlay

Use this when a change (or a port of a macOS change) affects how `SubtitleOverlayWindow`
looks, and you need before/after evidence on a real screen — not a mock.

## Build / run basics

- `dotnet` may be at `C:\Program Files\dotnet` **or** user-local at
  `%LOCALAPPDATA%\Microsoft\dotnet`. Check both; set `PATH` **and** `DOTNET_ROOT`.
- `dotnet build windows/RealtimeTranslator.slnx -c Release`
- If restore fails with "No sources found":
  `dotnet nuget add source https://api.nuget.org/v3/index.json -n nuget.org`
- The app is tray-resident and shows no subtitles without a mic + API key, so do **not**
  try to drive the overlay through the real app for visual work.

## Host the real overlay from a throwaway harness (recommended)

Create a WPF exe **outside the repo** that `<Reference>`s the built
`RealtimeTranslator.App.dll` / `Core.dll` / `Platform.dll` (plus an `AfterTargets="Build"`
`Copy` of the whole App bin folder into `$(OutDir)`, otherwise transitive NAudio deps are missing).

```csharp
var vm = new SubtitleOverlayViewModel();
var overlay = new SubtitleOverlayWindow(vm);   // real production XAML, BAML resolves fine
overlay.Width = 800; overlay.Left = 40; overlay.Show();
vm.FontSize = 30;
vm.Apply(new SubtitleSnapshot(new LiveSubtitle(src, dst), banner));
```

Gotchas learned the hard way:

- Add `UseWPF` **and** `UseWindowsForms` (the window uses `System.Windows.Forms.Screen`),
  then add `using` aliases for `Application`, `Button`, `Brushes`, `Color`, `Point`,
  `FontFamily`, `HorizontalAlignment`, `Orientation` — otherwise every WPF type is ambiguous.
- Do **not** call `ApplyPlacement`; it centers at 70% of the work area. Set `Left`/`Width`
  yourself and emulate the app's bottom anchoring with `Top = anchorBottom - ActualHeight`
  after each update (that is what makes height changes visible as position jumps).
- Measure inside `Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ...)` after
  `UpdateLayout()`, or `ActualHeight` is stale.

## True before/after side by side

Run **two processes** of the harness at once, upper and lower half of the screen:

1. `git stash` the change, build, copy `RealtimeTranslator.*.dll` to `appbin-before/`, `git stash pop`.
2. Build the harness twice with `-p:AppBin=<dir> -p:BaseOutputPath=<out-dir>\`.
3. One instance owns a full-screen backdrop window (white / bright gradient / dark) plus a
   control panel; both poll a shared state file for background/scene/font size.
4. **Open the shared state file with `FileShare.ReadWrite` and retry on `IOException`.**
   Plain `File.WriteAllText`/`ReadAllText` from two processes crashes the harness with
   "The process cannot access the file" (shows up only in the Windows Application event log:
   `Get-EventLog -LogName Application -Newest 5 -EntryType Error`).
5. Have each instance append `role,bg,scene,font,ActualHeight,Top` to its own CSV — the
   numbers are the evidence; screenshots alone can't prove "the panel did not move".

## Porting SwiftUI values to WPF — known traps

- `lineSpacing(x)` in SwiftUI **adds** to the default leading; WPF `LineHeight` is the
  **absolute** line box. WPF default = `FontFamily.LineSpacing × FontSize` (Segoe UI ≈ 1.3333).
  So `lineSpacing = fontSize/10` becomes `LineHeight = FontSize * 1.4333`, **not** `* 1.1`.
  Also set `LineStackingStrategy="BlockLineHeight"` or `LineHeight` is ignored.
- SwiftUI `shadow(radius: r)` ≈ WPF `DropShadowEffect BlurRadius = 2r`, `ShadowDepth=0`.
  WPF allows only one `Effect` per element, so stack shadows by nesting `Border`s.
- SwiftUI `truncationMode(.head)` has **no** WPF equivalent; `TextTrimming` only trims the
  tail. Head truncation must be done in the view model / clipper.
- To keep window height fixed, give the text slots an explicit `Height` and replace
  `Visibility=Collapsed` with `Opacity` (Collapsed is what makes the window jump).
- Per-run opacity: use an alpha `Foreground` (e.g. `#8CFFFFFF` ≈ 0.55) on a second `<Run>`.

## Display scaling

Changing Windows display scaling to 150%/200% is **not possible** on the standard virtual
display here — Settings > System > Display shows "100% (Recommended)" greyed out. Report
high-DPI behaviour as *untested* rather than editing the registry.

## Devin Secrets Needed

None. All overlay visual testing works offline without `OPENAI_API_KEY`.
