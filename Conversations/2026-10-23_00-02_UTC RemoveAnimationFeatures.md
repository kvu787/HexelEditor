# Remove Animation Features

## User

currently, i have no need for "animation" features. remove these entirely and do any refactoring that this enables.

## Assistant

Done, in four commits.

**Interface** (`d8fb82b`) - removed the timeline strip and its frame cards, playback and the Play button, onion skinning, the per-frame duration field, and the sprite-sheet export. The canvas now fills the space the timeline occupied. Two things fell out: `ArtworkRenderer.Thumbnail` had no caller left, and `EditorChange.Document` existed only so the frame thumbnail cache could be invalidated.

**Document model** (`336ed32`) - `HexFrame` is gone. A `HexLayer` now owns its own `uint[] Cells`, so a document is a list of layers rather than a list of frames each holding one buffer per layer.

| Before                             | After                      |
| ---------------------------------- | -------------------------- |
| `Document.Frames[f].Cells[l]`      | `Document.Layers[l].Cells` |
| `CompositeAt(index, frameIndex)`   | `CompositeAt(index)`       |
| `ValidateCapacity(layers, frames)` | `ValidateLayerCount(layers)` |
| `Cells/<frame>/<layer>.bin`        | `Cells/<layer>.bin`        |

`EditorSession` lost `ActiveFrame`, `SelectFrame`, `AddFrame`, `DeleteFrame`, and `SetFrameDuration`; the history records `PixelChange(Layer, ...)` and `EditorState(Document, Layer)` without a frame index. The manifest no longer carries `Durations`. `MaximumFrames`, `MinimumFrameDuration`, and `MaximumFrameDuration` are gone, and so is `MaximumTotalCells`: 256 x 256 cells across 32 layers is 2,097,152, which can no longer approach the old 16,777,216 ceiling, so only the layer limit is still checked.

No compatibility shim for old `.hexel` files, per `AGENTS.md`.

**Assets** (`ca5fabd`) - regenerated `Examples/NeonTide.hexel` as a single still with its three layers, and `Documentation/EditorPreview.png` from the verification render.

**Documentation** (`7ce4bad`) - README no longer describes frames, playback, onion skinning, or sprite sheets; the shortcut table drops Enter and `,` / `.`; three test names that called a layer's buffer a "cel" now say "cells".

Tests went from 53 to 51: the frame-duration range test is gone, the last-layer and last-frame deletion test is now about layers alone, and two frame/layer tests were rewritten around layers. `Scripts/Verify.ps1` passed at each step: build with no warnings, 51 unit tests, and the desktop verification run (save/load, PNG export, SVG, menu and dialog layout, session logging). Not opened on a physical display.

## User

Yes, run the editor on NE18NZ2 so I can look at it.

## Assistant

`WmiMonitorID` identifies NE18NZ2 as the BOE panel; matching its instance key against `EnumDisplayDevices` resolves it to `\\.\DISPLAY1`, 3840 x 2400 at X=5120, Y=-958, and not the primary display. So `Run.cmd` alone would most likely have opened the editor on the primary monitor. Launched it through `Run.ps1`, then moved the window onto that screen and maximized it: process 11992, responding, titled "Neon tide - Hexel Editor", with a clean session log and no errors.

## User

this is not the workflow i intended. when i ask you to make a change, you should open the application on NE18NZ2 and do any validation you deem necessary on your own.

you shouldn't ask me if i want the application opened, then open the app, then ask me if it looks good. I'm already going to do that after you complete your change. you doing asking me explicitly is a waste of your tokens and our time.

## Assistant

Recorded. Added the rule to `AGENTS.md` under "Using physical displays" so it governs future sessions, and saved it to my own memory for this repository.

From now on, finishing a change includes opening the app on NE18NZ2 and validating it myself, then reporting what I verified and what I could not. No asking to launch it, no asking whether it looks right.
