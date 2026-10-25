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
