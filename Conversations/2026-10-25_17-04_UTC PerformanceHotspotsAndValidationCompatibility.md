# Performance hotspots and validation compatibility

## User

explain how the rendering works in this app

## Assistant

# How rendering works

The whole canvas is drawn as **one rectangle with a shader**. Nothing in the app ever draws a hexagon for
artwork -- the shader works out which cell each pixel falls in. There is no CPU fallback; the app refuses to
start without a GPU (`App.xaml.cs:77`).

## The pieces

| File | Role |
| ------------------------------------------------------------------- | ------------------------------------------------------------------- |
| `HexCanvas.cs`     | `SKElement` in the window; owns pan/zoom, drives each frame          |
| `ArtworkCache.cs`  | Raster of composited cell colours, kept incrementally up to date     |
| `GpuHexRenderer.cs`| GL thread, SkSL shader, readback                                     |
| `CanvasGeometry.cs`| Hex layout in canvas units, for the CPU overlays only                |
| `EditorSession.cs` | Tracks which cells changed                                           |

## One frame, start to finish

`HexCanvas.Paint` runs on WPF's UI thread:

1. **Backdrop** -- clear to `#F3F3F3`, then a dot grid (every 24 units, r=0.7) drawn with ordinary
   antialiased Skia, in canvas coordinates scaled by the device scale.
2. **Drain the dirt** -- `artwork.MarkDirty(session.TakeDirtyCells())`, every frame regardless, so an edit
   made while something else was being drawn is never lost.
3. **Artwork** -- `gpu.Render(document, artwork, deviceWidth, deviceHeight, Zoom * scale, offsetX * scale,
   offsetY * scale, ShowGrid)` returns an `SKBitmap` at **device resolution**, blitted at 1:1 with nearest
   sampling (exact, and the cheapest path).
4. **Overlays** -- selection fill, line/rectangle previews and the hover cursor ring are still CPU Skia
   paths, drawn over the bitmap in canvas space (`Translate(offset); Scale(Zoom)`). The cursor ring gets a
   white halo so it reads over both pale and dark artwork, and stroke widths are divided by `Zoom` to stay
   constant on screen.

## The colour raster

The shader needs a cell's colour in one texture lookup, so layer compositing happens **once per cell
change**, not per pixel per frame. `ArtworkCache` keeps a BGRA8888 `SKBitmap` installed over a *pinned*
`int[]`, so writing a cell writes the bitmap with no copy at all.

Two quirks worth knowing:

- **Two pixels per cell** along the axis where alternate lines are offset (plus one extra pixel). At one
  pixel per cell the half-cell offset of odd rows falls between texels and cannot be stored; at two it is
  exactly one texel, so the raster holds the real hex geometry.
- Each cell is `ColorMath.Over(document.CompositeAt(index), background)` -- composited over the opaque canvas
  background, so the raster is opaque and premultiplied vs. straight alpha does not matter.

Only the dirty rectangle is recomposited. `EditorSession` accumulates the smallest column/row rectangle
touched since last read, so a brush stroke on a two-million-cell canvas costs the handful of cells it
touched. A `Version` counter (bumped only when a cell actually changed) tells the renderer whether to
re-upload the texture -- **panning and zooming upload nothing**.

## The GL side

Everything GL lives on its own thread, owning a hidden 16x16 GLFW `NativeWindow` purely as a context holder,
a `GRContext`, and the compiled `SKRuntimeEffect`. The comment records why: the first version created the
context on the WPF UI thread and the *whole* window -- toolbar included -- then never painted.

Communication is one request slot plus two semaphores. The UI thread blocks for the millisecond or two a
frame takes, with a 2-second timeout after which the renderer is declared finished rather than waited on
again. There is exactly one instance process-wide (`Shared`), because GLFW binds to the thread that first
creates a window -- a second renderer would fail and take the only drawing path with it.

The frame is drawn into a budgeted GPU `SKSurface` and then **read back** into a bitmap. That is deliberate:
`SKElement` hands WPF a bitmap, and the verification harness captures with `RenderTargetBitmap`, which
cannot see GPU content. Readback keeps one path for the real window and the headless checks, at 1.3 ms.

## The shader

`HexGeometry.FromPoint` and `Center`, rewritten as SkSL:

```
pixel -> (coord - pan) * invScale -> axial (closed form) -> cube-round -> offset (col,row)
      -> reject if outside the document (transparent)
      -> texel lookup, nearest
      -> grid darkening
```

- **Cube rounding** picks the nearest hex from a fractional axial coordinate: the three cube coordinates sum
  to zero, so whichever moved furthest is recomputed from the other two.
- **The grid is analytic**, not geometry. For the cell's centre it takes the max projection onto the three
  edge normals; the slack against the apothem (`radius * sqrt(3)/2`) is the distance to the boundary, so a
  pixel within `gridWidth` of an edge is darkened. Exact at every zoom, free per cell. Below **4 px per
  cell** it is switched off, since it would only be a grey wash.
- **No antialiasing anywhere** -- one sample per pixel, hard cell edges by design. Both orientations are in
  the shader with a branch on a uniform, which costs nothing since every pixel in a frame takes the same
  side.

The header records why this replaced the CPU renderer: on a 2M-cell canvas at 2456x1270, the old path cost
9 ms zoomed in and **133 ms** at worst zoom, and needed three detail tiers that went jaggy below 67%. The
shader is **0.04 ms to draw, 1.3 ms to read back, flat at every zoom** -- it costs what the *window* costs,
not what the artwork costs.

## What triggers a repaint

`session.Changed` -> `MainWindow.SessionChanged`: always `InvalidateVisual()`, and on a **structural** change
(layer hidden, reordered, opacity, new background) also `InvalidateArtwork()`, because the raster holds
composited colour so all of it is stale. Pixel edits only ever mark their rectangle.

One detail I would flag: `Paint` passes `offsetY * scaleX` -- the *horizontal* scale for the vertical pan.
The comment says this is intentional, taking pixels to be square because WPF scales a display uniformly, and
it keeps the renderer's zoom and pan consistent. It holds on every real display, but it would skew on a
hypothetical non-uniform one.

## User

> The frame is drawn into a budgeted GPU SKSurface and then read back into a bitmap. That's deliberate:
> SKElement hands WPF a bitmap, and the verification harness captures with RenderTargetBitmap, which cannot
> see GPU content. Readback keeps one path for the real window and the headless checks, at 1.3 ms.

let's say we didn't care about verification and we just wanted the most performant editing experience. how
would you change this?

## Assistant

# Where the time actually goes

Drawing the artwork costs **0.04 ms**. Everything else in the path is copying full frames around:

| Step | 2456x1270 @ 4 B | Cost |
| ---------------------------------------------------------- | --------- | ---------------- |
| Shader draw into the GPU surface | -- | 0.04 ms (measured) |
| `surface.ReadPixels` -> `readback` bitmap | 12.5 MB | 1.3 ms (measured) |
| `canvas.DrawBitmap` into SKElement's `WriteableBitmap` | 12.5 MB | ~1-2 ms (my estimate) |
| WPF's compositor uploading that bitmap back to the GPU | 12.5 MB | ~1-2 ms (my estimate) |
| `SKImage.FromBitmap(raster)` -- on every frame a cell changed | 16 MB | ~2-4 ms (my estimate) |

Only the first two numbers are yours; the rest is arithmetic at ~8-12 GB/s. But the shape is clear: **a
painted frame spends roughly 150x longer copying pixels than drawing them**, and the pixels make a full round
trip GPU -> CPU -> GPU for no reason other than `SKElement` wanting a bitmap.

So the answer is not "make the shader faster." It is "stop copying."

## 1. `SKElement` -> `SKGLElement`

The package you already reference ships it:

```
SkiaSharp.Views.WPF 4.150.2
  +- OpenTK.GLWpfControl 4.2.3
       +- OpenTK.Windowing.Desktop   <- where your NativeWindow comes from
```

`SKGLElement` is GLWpfControl underneath: it renders into a GL framebuffer and hands that to WPF through
`D3DImage` via `WGL_NV_DX_interop` -- a GPU-side share, no CPU roundtrip. Its `PaintSurface` gives you a
`GRContext`-backed `SKSurface`, which is exactly what `RenderCore` builds by hand today.

That deletes, rather than rewrites, most of `GpuHexRenderer.cs`: the render thread, the two semaphores, the
one-slot request struct, the 2-second timeout, the `Shared`/GLFW-main-thread singleton, the hidden 16x16
`NativeWindow`, the `readback` bitmap, the `ReadPixels`, and the `DrawBitmap` blit in `Paint`. What is left
is the shader, the uniforms, and one `DrawRect` -- and the overlays draw into the same GPU surface instead of
a software one.

Your header comment records that a GL context on the UI thread once killed the whole window. Worth saying:
that was a bare GLFW `NativeWindow` fighting WPF's dispatcher. GLWpfControl exists to do this and does it by
owning the context properly and rendering inside WPF's render callback. I would treat it as very likely
fine, but it is the one item here that could rediscover a real problem.

## 2. Upload only the cells that changed

This is probably the biggest single editing-latency win, and it is independent of everything else:

```csharp
if (colours == null || colourVersion != cache.Version || !ReferenceEquals(colourSource, cache))
    colours = SKImage.FromBitmap(raster);   // copies + uploads the entire raster
```

`Version` bumps whenever any cell changed, so **one brush dab re-uploads all 16 MB**. The dirty rectangle is
already computed and already threaded through `MarkDirty`/`Refresh` -- it just gets thrown away at the upload
boundary. Keep a persistent GL texture, wrap it once with `SKImage.FromTexture`, and `glTexSubImage2D` the
dirty rect. A dab goes from 16 MB to a few hundred bytes.

## 3. Halve the texture -- half of it is dead

`ArtworkCache` writes each cell to two texels, and the shader reads only ever the first of them:

- cache (pointy): `start = column*2 + (row&1) + row*stride`, writes `start` and `start+1`
- shader (pointy): `texel.x = column*2 + (r - 2*floor(r*0.5)) + 0.5` -> with nearest sampling that resolves
  to exactly `start`

`start + 1` is never sampled, in either orientation. The comment's reasoning -- that the half-cell row offset
falls between texels -- holds if you index the texture by *position*, but the shader rounds to an integer
`column`/`row` before it touches the texture, so a plain `Width x Height` raster sampled at
`(column + 0.5, row + 0.5)` carries the same information. That is 2x less texture memory and 2x less upload
bandwidth, and it must change on both sides in one commit.

## 4. Selection into the shader

`Paint` loops `minColumn..maxColumn x minRow..maxRow` doing `Selection.Contains(index)` per cell.
`VisibleCells` clamps to the document, so **zoomed out with a large selection that is ~2M hash lookups per
frame**, on the UI thread, per mouse move. Make selection a 1-byte-per-cell mask texture updated on the same
dirty-rect path and tint it in the existing shader: the loop disappears and the cost becomes
per-pixel-on-screen like everything else. Line and rectangle previews can go the same way (both are trivial
in offset space); the cursor ring is a handful of hexagons and can stay as Skia paths.

## 5. Small change, free

Fold the dot backdrop into the shader -- it is the `outside the document` branch that currently returns
`half4(0.0)`, so it costs nothing instead of ~5,400 antialiased CPU circles per frame -- and hoist the
per-frame `SKPaint`s, `CanvasGeometry.Hexagon` path (six `cos`/`sin` per frame), and the uniform/shader
objects out of the hot path. Then `canvas.Clear` can go too, since every pixel gets written.

## The ceiling, if you want it

`D3DImage` interop still costs one GPU-side copy and ties you to WPF's composition cadence, usually a frame
of latency. The floor is a **child HWND hosted via `HwndHost`, with its own flip-model swapchain, presented
from a dedicated render thread** -- zero copies, direct present, tearing allowed if you want paint feedback
ahead of vsync, and input never blocked on the GPU. The price is airspace: nothing WPF can draw over that
rectangle, and you handle DPI, clipping and resize yourself. Your canvas is a plain grid cell with
`ClipToBounds` and nothing on top of it, so that constraint costs you nothing today -- it is what pro editors
do. I would only reach for it after 1-4, because 1-4 get you most of the way with a far smaller diff.

## Order I would do it in

**2 -> 3 -> 1 -> 4 -> 5.** Two and three are self-contained, need no new dependency, and cut the worst cost
while painting. One is the structural change and the one carrying real risk. Four matters at low zoom
specifically.

One consequence to name even though you said to set it aside: `Validate.ps1` captures with
`RenderTargetBitmap`, which cannot see GPU content, so that path would need its own `ReadPixels` capture.
That is fine -- readback as a *capture* path costs 1.3 ms when a screenshot is taken, instead of on every
frame.

## User

okay, a lot of this i don't fully understand. can the whole display pipeline be reduced to the "Shader draw
into the GPU surface" taking 0.04 ms?

## Assistant

Short answer: **no** -- and part of the reason is that the 0.04 ms does not mean what it looks like it means.
I should have caught that last turn.

## That number measures asking, not doing

From your own measurement notes (the GPU conversation, line 1552): *"Draw 0.04 ms, draw plus reading the
pixels back 1.3 ms."*

`DrawRect` followed by `context.Flush()` **queues** work for the GPU and returns immediately -- it does not
wait for the GPU to finish. So 0.04 ms is how long the CPU spent *writing down the order*. The first moment
anything waits for the GPU to actually execute is `ReadPixels`, inside the 1.3 ms.

Which means those two numbers do not decompose the way my table implied. The 1.3 ms is *GPU execution +
transfer + the stall*, mixed together, and nothing you have measured so far separates them. My own arithmetic
says a 3.1 Mpx pass with this shader is plausibly a few tens of microseconds on a fast GPU, so 0.04 ms might
be near the truth -- but that is my estimate, not your measurement.

## Even with zero copies, a frame is more than the draw

Here is everything that has to happen for one painted frame to reach your eye, after all the copying is gone:

1. Mouse event arrives, WPF dispatches it, cells get painted into the raster
2. Changed cells uploaded to the GPU texture (small, with dirty rects)
3. GPU runs the shader -- the real cost, currently unknown
4. Overlays (cursor ring, selection) drawn on top
5. The canvas frame is **composited** into the window alongside the toolbar and panels
6. The window is **presented** -- handed to Windows' compositor
7. The monitor's next refresh actually lights the pixels

Step 7 alone is **6.9 ms at 144 Hz, 16.7 ms at 60 Hz**, and nothing you do to the renderer shortens it.
Steps 5 and 6 are real work too. So the floor for "a change appears on screen" is not 0.04 ms -- it is
roughly one display refresh, and that is true of every application on the machine.

## What the copies are actually costing you

The useful way to think about it is not frame time, it is **budget**. On a 144 Hz display you get 6.94 ms per
frame. If a painted frame spends 2.5-6 ms shuffling pixels -- and it does that on the **UI thread**, which
sits blocked on the semaphore while the GL thread works -- then you are eating most of the budget on
transport, and a frame that overruns means the stroke visibly trails the cursor.

Worth knowing what "stall" means here, since it is the worst part: during `ReadPixels` the CPU waits for the
GPU to finish, and once it is finished the GPU waits for the CPU to come back with more work. Neither does
anything useful. A pipeline where the two overlap can be several times faster than one where they take
turns, even with identical total work.

Also: your validation display is 3840x2400, which is 9.2 Mpx -- **3x the frame those numbers were measured
at**. Each full-frame copy there is ~37 MB, not 12.5.

## So what is the realistic end state

Not "0.04 ms end to end." It is:

- CPU work per frame: well under a millisecond
- GPU work per frame: tens to a few hundred microseconds
- **Everything else: waiting for the monitor**

That is the goal -- not a smaller number, but a pipeline where the renderer is never the thing holding
anything up, and painting cannot miss a refresh. The 0.04 ms would describe the drawing, and the rest of the
frame would be idle, which is exactly what you want it to be.

## One correction to last turn

You *did* measure painting, and found it did not dominate -- *"painting costs about what panning does"* --
but that was on a **500 x 250** canvas, where the colour texture is about 1 MB. On 2000 x 1000 it is 16 MB,
16x more. So the upload fix is still worth doing, but it is my inference from the size difference, not
something your measurement contradicts or confirms.

Which points at the thing to do before any of this: **measure properly.** GL timer queries around the draw,
and a painting benchmark on a 2000x1000 canvas, would tell you how much of that 1.3 ms is the GPU drawing
versus the transfer, and whether the 16 MB upload actually hurts at that size. Right now we would both be
optimising on an estimate. Want me to build that measurement first?

## User

okay, do this for the entire hexel editor app:

* do analysis, experiments, and measurements and identify performance hotspots
* for each performance hotspot, tell me if it can be optimized away without breaking the validation flow

## Assistant

Built a measurement harness, measured the whole application, and wrote the findings up in
`Documentation/PerformanceHotspots.md`.

### What was built

- `Scripts/Measure.ps1` -- runs the editor in a new measurement mode on a Win32 desktop of its own, exactly
  as `Verify.ps1` does, under the disruption watch. Takes `-Sections` so one figure can be re-measured
  without the whole suite.
- `Source/HexelEditor.Desktop/MainWindowMeasurement.cs` -- the suite, as a partial of `MainWindow` beside
  the verification one, so it measures the real control, the real session and the real panels.
- Frame timing in `GpuHexRenderer` (composite / upload / record the draw / GPU / readback, separated with a
  `GL.Finish`) and paint timing in `HexCanvas` (backdrop / artwork / blit / overlays). Both off unless
  something is measuring.
- `HexCanvas.PaintOneFrame`, because WPF will not repaint a control on demand: it reuses what the visual
  drew last time, so rendering the tree into a bitmap produces a picture without producing a paint.
- The GL device is now logged on every run. The context lands on the RTX 5090 over PCIe, which is why
  readback costs what it does.

### What the measurements found

Three harness faults had to be fixed before the figures could be trusted: reusing one surface across paints
compounded the canvas transform until the scale had decayed to 1e-14; the dab colour repeated across cases,
so a "painting" case silently measured a frame with no edit in it; and `RenderTargetBitmap` never triggered
a paint at all.

The ten hotspots, worst first, are in the document with a validation verdict each. The headlines:

1. **Selection highlights: 2637 ms a frame** with everything selected on a 2000 x 1000 canvas. It draws one
   hexagon path per selected cell and walks 1,149,148 cells a frame. `Ctrl+A` makes the editor unusable.
   Nothing in validation draws a selection, so this is free to fix.
2. **The colour texture: 9.2 ms a frame while painting.** Confirmed the earlier suspicion and corrected the
   "painting is not worth optimising" note, which was measured on a 500 x 250 canvas where the raster is
   1 MB rather than 16 MB. The existing pixel checks cover the fix well.
3. **The shader costs 0.08 to 0.16 ms, not 0.04 ms.** That earlier figure was taken around the draw and
   `Flush`, which hand the work over without waiting, so it measured submitting rather than drawing.
4. **The two copies cost more than everything else on a 4K display:** 14.7 ms to blit plus 3.7 ms to read
   back, against a 0.16 ms draw.
5. **Saving a large project takes 478 ms, loading 272 ms**, one `uint` at a time.

Nine of the ten can be fixed with no change to any check. The exceptions: dropping the readback costs the
"one rendering path" property, since the pixel-exact checks call `GpuHexRenderer.Render` directly and would
then verify the shader through a second path, and the display pass's `PrintWindow` capture of a
`D3DImage`-composited surface has to be proved rather than assumed; and changing `Selection` from
`HashSet<int>` means editing six unit tests.

Also measured and *not* hotspots, so they are not chased later: the per-cell notification chain (a 60-cell
drag costs 0.19 ms in total), `UpdateHeader` (0.001 ms), compositing a dab (0.003 ms), and `CloneStructure`
(0.000 ms -- the cell sharing works).

### Validation

`pwsh -File ./Scripts/Validate.ps1` passed on the instrumented build: disruption guard rules, build, tests
and hidden-host verification, display pass on NE18NZ2 at 3824 x 2392 with 57 distinct colours, and nothing
of the run appeared outside that display, took the foreground or confined the cursor.
