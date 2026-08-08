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

Match production DPI bootstrap in the harness csproj / startup:

- Set `<ApplicationHighDpiMode>PerMonitorV2</ApplicationHighDpiMode>` (same as
  `RealtimeTranslator.App.csproj`).
- Call `ApplicationConfiguration.Initialize()` before any WPF window is created
  (production does this via `DpiBootstrap` `[ModuleInitializer]`).

```csharp
ApplicationConfiguration.Initialize(); // before any WPF window; needs UseWindowsForms

var vm = new SubtitleOverlayViewModel();
var overlay = new SubtitleOverlayWindow(vm);   // real production XAML, BAML resolves fine
overlay.Width = 800; overlay.Left = 40; overlay.Show();
const double anchorBottom = 700; // chosen bottom edge for this harness instance
vm.FontSize = 30;
const string src = "これは二行になるくらいの日本語原文サンプルです。";
const string dst = "This is a sample English translation long enough to wrap.";
string? banner = null; // or SubtitleSnapshotBuilder.ConnectingBanner, etc.
vm.Apply(new SubtitleSnapshot(new LiveSubtitle(src, dst), banner));
_ = overlay.Dispatcher.InvokeAsync(() =>
{
    overlay.UpdateLayout();
    overlay.Top = anchorBottom - overlay.ActualHeight; // bottom-anchor after every Apply
}, DispatcherPriority.Loaded);
```

Gotchas learned the hard way:

- Add `UseWPF` **and** `UseWindowsForms` (the window uses `System.Windows.Forms.Screen`),
  then add `using` aliases for `Application`, `Button`, `Brushes`, `Color`, `Point`,
  `FontFamily`, `HorizontalAlignment`, `Orientation` — otherwise every WPF type is ambiguous.
- Do **not** call `ApplyPlacement`; it centers at 70% of the work area, capped at 1,200 DIP.
  Set `Left`/`Width` yourself and recompute `Top = anchorBottom - ActualHeight` after every
  `Apply` (production `OnRenderSizeChanged` only clamps — without this, height changes grow
  from the top and look like position jumps).
- Measure inside `Dispatcher.InvokeAsync(..., DispatcherPriority.Loaded)` after
  `UpdateLayout()`, or `ActualHeight` is stale. Prefer `InvokeAsync(Action, DispatcherPriority)`
  over `BeginInvoke(DispatcherPriority, Delegate)` — the latter does not accept a lambda.

## True before/after side by side

Run **two processes** of the harness at once, upper and lower half of the screen:

1. `git stash` the change, build, copy the **entire** App bin folder (not just
   `RealtimeTranslator.*.dll` — NAudio and other transitive deps are required) to
   `appbin-before/`, then `git stash pop` and build again into `appbin-after/` the same way.
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

Only compare 150%/200% results against production when the harness uses the same DPI
bootstrap (`ApplicationHighDpiMode=PerMonitorV2` + `ApplicationConfiguration.Initialize()`
before WPF startup). Where Settings > System > Display lets you change scaling, exercise
150% and 200% and record the result. On the standard virtual display used for verification
here, scaling is greyed out at "100% (Recommended)" — mark high-DPI behaviour *untested*
and exclude those scales from production comparisons. Do **not** edit the registry to
force scaling.

## Devin Secrets Needed

None. All overlay visual testing works offline without `OPENAI_API_KEY`.
