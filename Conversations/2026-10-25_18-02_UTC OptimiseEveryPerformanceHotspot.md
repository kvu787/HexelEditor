# Optimise every performance hotspot

Continues `2026-10-25_17-04_UTC PerformanceHotspotsAndValidationCompatibility.md`, which measured the editor
and listed ten hotspots with a validation verdict each.

## User

before we continue, i need to clarify this:
from what i understand, the current validation workflow depends on copying some kind of render from the gpu
back to the cpu. is this accurate?

## Assistant

Yes -- but it is narrower than it looks, and one part of the stated reasoning no longer holds.

### What is actually load-bearing

There is exactly one GPU to processor copy in the codebase, `GpuHexRenderer.cs:300`, and it runs on every
frame. Three checks depend on its result:

| Check                    | What it does                                                        |
| ------------------------ | ------------------------------------------------------------------- |
| `VerifyArtworkRendering` | `GetPixel` at every cell's centre, exact colour match, no tolerance  |
| `VerifyCanvasSurface`    | `GetPixel` at cell centres and midpoints between adjacent cells      |
| `VerifyRenderScales`     | `GetPixel` at the frame centre, 4 zooms by 4 quadrants               |

All three call `GpuHexRenderer.Render(...)` directly and sample the `SKBitmap` it returns. So validation
needs the rendered pixels in processor memory, and that needs a readback.

But what they need is *a* render-to-bitmap path, not the window's path. Today they are the same path, which
is the property the code comment defends. Nothing forces the window's frames to be the ones read back.

### Two things that are not dependencies

- **`RenderTargetBitmap` is not in the chain.** The comment says the harness "captures the window with
  `RenderTargetBitmap`, which cannot see GPU content". It does write one, but nothing asserts on it.
  `Verify.ps1` checks the report text and the session log and never opens that PNG. The only check touching
  the canvas through WPF is `VerifyCanvasRendered`, and it just asserts `CanvasSize` is non-zero.
- **The display pass is a different mechanism.** Its `Editor.png` is a separate file written by
  `PrintWindow` against the real window. `Test-ImageHasContent` only wants 8 distinct colours across the
  whole window, which the toolbar, panels and 256-swatch palette supply on their own, so it would most
  likely still pass with a completely blank canvas. It proves the window drew, not that the canvas drew.

### What this means for the decision

Removing the readback from the live path is possible: keep `Render` as an offscreen frame-to-bitmap method
for the checks. The real cost is not technical but coverage: afterwards the exact-pixel checks verify a path
the window no longer uses, and the display pass is too weak to catch the difference.

## User

okay, go ahead and optimize all the hotspots in whatever order you think is best. at the end, report to me
what you ended up doing.

## Assistant

Nine of the ten are gone. The tenth is the frame readback, left deliberately, with the reasons below.

### What changed, in the order it was done

1. **The selection became a bit a cell.** `HashSet<int>` cost a hash lookup to ask about and ~30 MB to keep
   at two million cells. `CellSelection` is 250 KB and answers from a shift and a mask. Keeping the member
   names meant every caller and all 62 tests compiled and passed unchanged -- the earlier verdict that six
   tests would need editing was wrong.
2. **The selection and the dragged rectangle moved into the shader.** They were a hexagon path per cell over
   the viewport's bounding box: 1,149,148 cells a frame with everything selected, 2637 ms. Now a mask of one
   byte a cell that the shader reads, filled and outlined with the same blue and the same line width.
3. **Only the changed cells are uploaded.** `SKImage.FromBitmap` rebuilt the whole 16 MB raster whenever any
   of it changed. The dirty rectangle was already tracked and stopped at the upload.
4. **The canvas composites its rows at the same time.** A cell writes texels no other cell writes, so the
   rows are independent. 11 ms to about 1.2.
5. **The paper moved into the shader, and the frame is read where it is wanted.** Four thousand antialiased
   circles a frame became a pattern worked out per pixel; and with the frame opaque everywhere it no longer
   has to be blended onto the window's surface, so the second full-frame copy went too.
6. **Project cells are written and read in one call** rather than one `BinaryWriter` call a cell.
7. **The whole-canvas operations walk the canvas once.** The undo record was a two million entry dictionary;
   flood fill worked each cell's coordinate out six times; copying walked the selection three times; where a
   paste lands was four chained queries.
8. **The palette's swatches are kept and updated** rather than 256 buttons being built on every colour pick
   and every undo.

### The one that had to be written twice

The sub-rectangle upload was first done with a GL texture of its own and `glTexSubImage2D`. It was the
fastest version -- 0.015 ms against the 0.08 ms the replacement costs -- and it left the real window blank:
not the canvas, the whole window, toolbar included, while every headless check passed.

That is the same failure this renderer's own header records from owning a GL context on the wrong thread.
The display pass on NE18NZ2 was the only thing that caught it, and bisecting the display pass put it on that
commit exactly. Rewritten through a surface Skia owns, which does the same work inside the rules Skia
expects.

### What is left, and why

The frame is still read back from the GPU: 1.14 ms at a window's size, 3.70 ms at the validation display's.
It is now essentially the whole of a frame, but a frame is 1.3 ms against the 8.4 ms a 119 Hz display
allows, so the renderer no longer limits anything. Removing it means `SKGLElement`, which would put the
shader and its textures in the interface thread's context, leave the pixel-exact checks verifying a renderer
the window does not use, and depend on `PrintWindow` seeing a `D3DImage`-composited surface -- which has to
be tried, not assumed. That is a decision about the validation discipline rather than an optimisation.

### Results

| Interaction                      | Before   | After   |
| -------------------------------- | -------: | ------: |
| Paint one cell                   | 18.45 ms | 1.31 ms |
| Drag the brush over 60 cells     | 18.33 ms | 1.44 ms |
| Pan the canvas                   |  8.31 ms | 1.18 ms |
| Undo a whole-canvas change       | 29.53 ms | 11.9 ms |
| Hide or show a layer             | 29.79 ms | 11.9 ms |
| A frame with everything selected |  2646 ms | 1.43 ms |
| A frame while painting, at 4K    | 32.99 ms | 3.70 ms |
| Save a large project             |   478 ms |   40 ms |
| Load one                         |   272 ms |   24 ms |

### Validation

`pwsh -File ./Scripts/Validate.ps1` passes: the guard rules, the build, the tests and the hidden-host
verification, the display pass on NE18NZ2 at 3824 x 2392 with 57 distinct colours, and nothing of the run
outside that display, taking the foreground or confining the cursor.
