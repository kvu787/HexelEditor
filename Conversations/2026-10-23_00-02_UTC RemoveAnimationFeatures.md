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
