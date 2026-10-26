# Comprehensive repo review with Fable 5.1

## User

this app was entirely written with opus 5.5 high/extra.
i'm curious about what results i will get with fable 5.1.
i changed the model to fable 5.1 extra. do a comprehensive analysis and review of the entire repo and make changes as you see fit.

## Assistant

Seven commits are on main, and the full validation passes on the final build: the build, 69 unit tests, every hidden-host check, the display pass on NE18NZ2, and the disruption watch with nothing of the run outside that display.

**What the review covered.** I read every source file, the tests, the scripts, the Blender tool and the documents, ran the baseline, and then fixed what was wrong rather than restyling what was not. The editing core, the history, the project format, the compositing, the shader and the scripts hold up well: limits are enforced before anything is allocated, saves are atomic, the structural records can share cells because history is undone in order, and the disruption guard's rules are tested on their own.

**What was wrong in the editor, and is fixed.**

- **The pointer and the picture disagreed at cell edges.** The core's hex rounding decides which cell the pointer is in and the shader decides which cell a pixel is in, and they broke ties differently. Math.Round rounds halves to the nearest even number while the shader rounds them up, and the two compared the coordinates in a different order. On the edge between two cells the cursor ring could sit on one cell while the pixel under it was drawn as the other. The rounding is now the shader's arithmetic term for term. Two new tests hold it there: the result is always a nearest cell, and it equals the shader's on a grid of points that includes the exact ties. The old code failed the second test at q = r = -7.5, among others, and passed the first, so it never chose a wrong cell, only a different one.
- **The texture mirror could show the wrong canvas.** The GPU copy of the artwork raster took a null region to mean the whole raster only when it had to allocate. With a surface of the right size already standing it returned the old snapshot and uploaded nothing, and the renderer asks for the whole raster exactly when the cache is one it has never seen. A second canvas with the first one's dimensions therefore drew the first one's cells. Nothing in the editor showed it, because each canvas element keeps one cache for its lifetime, but the measurement run drew its large canvases through fresh caches of one size and was reporting the first one's upload for all of them. Verification now draws three canvases of one size through fresh caches and checks that each shows its own colour. I ran that check against the old code first: it fails there with the stale colour, and passes after the fix.
- **Editing operations reported themselves twice.** A flood fill, line, rectangle, clear, paste, move or rotation ended its stroke, which reported the stroke, and then reported itself again, and each report is a header rebuild and a frame for the window. Each now reports once, and a test holds every operation to one report carrying the flags it should.
- **Dead code and stale comments.** The viewport span in the canvas geometry served the per-cell selection highlights that moved into the shader and had no callers left; its header and the README still said the selection was drawn on the CPU. The canvas also kept its right-button flag past the end of a gesture, so a stroke made through the session afterwards was labelled an erase.
- **Shortcuts.** Backspace clears like Delete and Ctrl+Shift+Z redoes like Ctrl+Y, and both have all along; the help dialog named neither and the README named one.

**What was wrong in the validation harness, found by running it.** The first full run failed its display pass with a blank capture, and every earlier passing run turns out to have had the second of these too.

- **The capture raced the first frame.** The photo was taken the moment the window appeared. You were running a game at the time, the hidden-host pass took 13 seconds instead of 6 under that load, and the photo landed before the window had drawn: a title bar over a blank client area, read as a window that could not draw. The capture now retries until it holds something, within the same deadline, so a window that really draws nothing still fails.
- **The editor was never closed.** The close request went through Process.CloseMainWindow, which finds no main window for a window kept out of the taskbar, so it did nothing, and every run's editor was killed ten seconds later without logging its exit. The close now goes to the window the capture found. The editor exits on its own in about a second, with the exit in its session log, and the display pass is ten seconds shorter.
- **A failed Verify lost its output.** Validate.ps1 assigned Verify's output from the pipeline, so a run that threw partway left only the error in Verify.txt. The lines are now kept as they stream.

**Validated.** `pwsh -File ./Scripts/Validate.ps1` on the final build: the guard rules, the build, 69 tests, every hidden-host line including the new "Renderer raster" one, the window's own frame matching the renderer's in all 803,880 pixels, the display pass at 3824 x 2392 with 57 distinct colours, and the watch with no foreground change and nothing outside NE18NZ2. I looked at the captured window myself: the example artwork, the grid, the palette and the layer list are all there.

**Not verified, and left alone.** I did not re-run Measure.ps1; nothing here changes the hot paths it times, beyond the measurement run now uploading the large canvas for every case it should have. The whole-canvas editing operations are still on the input thread, as the last record said. Testyo.hexel at the repository root looks like a hand-saved test project, so I left it for you to keep or remove.
