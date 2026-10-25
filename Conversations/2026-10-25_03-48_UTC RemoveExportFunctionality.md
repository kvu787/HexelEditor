# Remove Export Functionality

## User

remove all export functionality including export png, export svg, and any other export features. then, do any refactoring/simplification that this enables

## Assistant

Done, in two commits.

**Remove PNG and SVG export** (`80e0b16`) - `ArtworkRenderer.ExportPng` and `ExportSvg` are gone, along with `ExportScale`, `MaximumExportSide`, `MaximumExportPixels` and `ExportScaleFor`, which existed only to keep an image of a two million cell canvas down to something that could be written and opened. `MainWindow.Export`, the two click handlers, the top-right Export PNG button and both File menu items went with them. The Save button, which sat beside the export one with the plain style, now carries the accent as the only command up there. Three usings fell out of `ArtworkRenderer`: `System.Globalization`, `System.IO` and `System.Text`.

`VerifyEditor` had been checking the editor's drawing through the export: it wrote a PNG of the reloaded example, sampled every cell, and sampled the midpoint between opaque neighbours to prove no background showed between them. Those two claims are about the canvas rather than about files, so they now go through the same `ArtworkRenderer.Draw` path the editor draws with, rendered offscreen and sampled the same way. `VerifyBackgroundLayer` became `VerifyCanvasSurface`, which carries both the solid-background check it already had and the seam check, and runs in both orientations - the flat-top transparent export was the only flat-top rendering cover there was. The report lines changed from `PNG export`, `SVG polygons`, `Flat-top transparent export` and `Canvas background` to `Artwork rendering` and `Canvas surface`, and `Scripts/Verify.ps1` expects the new ones.

**Draw the canvas background unconditionally** (`c0579ef`) - the export was the only caller that drew artwork with no background under it, so `Draw`, `DrawMesh` and `DrawExact` lost the `background` flag and the branches that read it.

`Documentation/EditorPreview.png` was showing the Export PNG button, so it is now a capture of the editor as it is, taken from the validation display. The README drops the export bullet, the "Export is unaffected by any of this" line, the whole paragraph describing PNG and SVG output, and the mention of exports in the session log and in the project layout; "GIF export is not implemented" became "image export is not implemented".

`pwsh -File ./Scripts/Validate.ps1` passed on the committed state: build with no warnings, 62 unit tests, every verification line reported - including `Artwork rendering: all 1280 cell colors verified` and `Canvas surface: background and seams verified across 40 cells in both orientations` - the real window on NE18NZ2 at 3840 x 2400 with 57 distinct colours, and nothing shown off that display, no focus taken, no cursor confined.

One run in between failed its disruption check: Ace Combat 8 took the foreground 10 seconds in and confined the cursor to its own 2560 x 1440 window. That was the machine being used, not the editor - the editor's window only ever appeared at X=5120, on NE18NZ2 - and the run before it and the run after it both passed on identical source.

## User

One run in between failed its disruption check: Ace Combat 8 took the foreground 10 seconds in and confined the cursor to its own 2560x1440 window. That was the machine being used, not the editor — the editor's window only ever appeared at X=5120 — and the runs either side of it passed on identical source. The new preview image is a downscale of the 4K validation capture, so its side-bar text is smaller than the old 1440x960 one; getting that back would mean running the editor outside Validate.ps1, which I didn't do.

regarding this, can't you non-disruptively launch the app on NE18NZ2, set the window size to 1440x960, and get the screenshot that way? currently, the screenshot at C:\Users\k\Repository\HexelEditor\Documentation\EditorPreview.png is bad because it's not pixel perfect, the text is way too small because window size can be huge on the 4k NE18NZ2 display, and there are weird borders at the left, right, and bottom but not top that seem like some kind of issue or error.

## Assistant

There is a better source than a window capture, and the borders were a real defect. Two commits.

**Crop a window capture to what the window draws** (`d2be0d8`) - the borders are the invisible resize border Windows leaves around a window: `GetWindowRect` takes it in, eight pixels at the left, the right and the bottom and none at the top, and `PrintWindow` draws from that rect's corner and never draws into it, so those pixels came back black. Measured on the old capture: black at x=0..7, x=3832..3839 and y=2392..2399, and content from y=0 at the top. `Save-WindowImage` now asks DWM for the frame that is really on screen and crops to it. That alone left one black pixel on each of the three sides, because the outermost pixel of the DWM frame is the border DWM draws itself and the window does not draw that either, so the frame is taken in by one more pixel on each side that had an invisible border to begin with. The display pass on NE18NZ2 went from 3840 x 2400 with an eight pixel black edge to 3824 x 2392 with none.

**Take the preview image from the verification render** (`5c4ee00`) - launching at 1440 x 960 on NE18NZ2 would work, but there is already something better and it is where the old preview came from: `App.OnStartup` renders the real interface to a `RenderTargetBitmap` at exactly 1440 x 960 and 96 DPI during every verification run, and writes it as `Editor.png` in the run folder. No window manager, no display scaling, no title bar, no DWM frame, and pixel exact. That image is now the preview, at the same 106% fitted zoom and the same framing the old one had, with the Export PNG button gone from the menu bar.

`pwsh -File ./Scripts/Validate.ps1` passed on the committed state: 62 unit tests, every verification line, the display pass at 3824 x 2392 with 58 distinct colours, and nothing shown off NE18NZ2, no focus taken, no cursor confined.

## User

One intermediate run tripped the disruption check on Ace Combat 8 again, same as before; the run after it, on the committed code, was clean.

what do you mean by this? does anything need to be fixed with the workflow?

## Assistant

Two of my validation runs failed the "did not disturb the machine" step because of the game, not the editor. Looking properly, the guard has two defects.

The events from the 21:13:44 run, in order: a `CursorClipChanged` to `0,-958 8960,1454` at 20050 ms, `MouseCaptured` by AceCombat8 at 20722 ms, then a `CursorClipChanged` to `1,1 2559,1439` at 20738 ms. `0,-958 8960,1454` is the whole virtual desktop - `SystemInformation.VirtualScreen` matches it exactly - so that first event is the game releasing the cursor, and the guard called it "The cursor was confined to 0,-958 8960,1454". It read the cursor being freed as a confinement. Then the game grabbed it again and the guard blamed the run for that too.

The first defect is at `Scripts/DisruptionGuard.ps1`: `CursorClipChanged` was handled before the `if (-not $observation.Watched) { continue }` line that filters every other kind, and the sampler hard-coded `Watched = true` on every clip. So any process on the machine confining the cursor failed the run, which on a harness whose premise is sharing a desk with an active user is not an edge case. The second is the released-clip reading above.

Attributing clips is safe because Windows only honours `ClipCursor` for the process owning the foreground window - which is why the clip appeared 16 ms after AceCombat8 took the foreground and dropped when it lost it - and the guard already fails outright if a watched window takes the foreground. Either a watched process never gets the foreground and its clip would not stick, or it does and the foreground check has already failed.

Worth saying plainly: my own response to the failure was to re-run until it went green, which is the habit a noisy guard breeds and the wrong one, since a real regression would have looked identical to me in that moment.

## User

okay, fix as you see fit

## Assistant

Fixed in one commit (`97fd1b8`).

A clip change now carries the window that was in front when it happened, and its `Watched` flag comes from that window's process, so clip changes go through the same attribution as every other observation. A clip covering the whole virtual desktop is read as the cursor released rather than confined. `Validate.ps1` reports clip changes that belonged to someone else as context on a passing run, since they are the machine being used while it ran, which is the condition the whole harness exists to survive.

The judging moved out of `Stop-DisruptionWatch` into `Get-DisruptionViolations`, which takes observations rather than a live watcher, because a real disruption cannot be produced on demand without doing the very thing the guard exists to prevent. `Scripts/TestDisruptionGuard.ps1` feeds it hand-written observations across ten rules: a full-desktop clip and a wider-than-desktop clip are released, a clip made while someone else was in front is not the run's, a clip made while the run's own window was in front is a violation, and the six window rules unchanged. `Validate.ps1` runs it as its first step, because every other step is judged by those rules.

I checked the rules have teeth by restoring the old judging temporarily: exactly the three clip rules failed, and the window rules still passed.

`pwsh -File ./Scripts/Validate.ps1` passed on the committed state: the ten guard rules, 62 unit tests, every verification line, the display pass at 3824 x 2392 with 58 distinct colours, and nothing of the run off NE18NZ2, in the foreground, or confining the cursor. One earlier run of it skipped the display pass on its own, because an editor titled "Untitled *" was open that none of my runs had started, and the pass refuses to touch an editor it did not launch.
