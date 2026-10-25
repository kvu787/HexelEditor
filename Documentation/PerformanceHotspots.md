# Performance hotspots

All ten were measured and all ten were removed. The figures below are before and after, both from
`Scripts/Measure.ps1`.

The before figures come from one run, `LogOutput/Measurement_2026-10-25_10-01-10`; the after figures from
the run taken once each change was in, and the frame figures from `LogOutput/Measurement_2026-10-25_15-57-58`,
taken once the readback was gone. All with `pwsh -File ./Scripts/Measure.ps1`, which runs the editor in its
measurement mode on a Win32 desktop of its own and times its own objects: the real renderer, the real canvas
control, the real session and the real panels. Figures are medians in milliseconds over the run count the
report states. Re-run the script to refresh them; the raw samples sit beside each report in `Measurement.json`.

One caveat on reading them: the machine belongs to someone who is using it, and a run taken while they are
shows it. Medians move by several milliseconds between runs and the highest samples by far more. Where a
figure here looks steadier than the run it came from, it is because the quiet runs and the busy ones agree
on their lowest samples and disagree on everything else. The run the frame figures come from was a busy one
on the processor -- its whole-canvas recomposite came back at 5.2 ms against 1.2 ms in the run before -- and
a quiet one on the GPU, which is the part those figures are about.

Machine: Intel Core Ultra 9 275HX, 64 GB DDR5-5600, NVIDIA GeForce RTX 5090 Laptop GPU. The OpenGL context
lands on the discrete GPU over PCIe, which the session log records on every run. That mattered while a frame
was read back: reading it crossed the bus here, and would not have on the integrated GPU.

What is measured and what is not: the editor's own per-frame work. Not the compositor, not the present, and
not the wait for the display, because none of those can be measured on a desktop that is not being
displayed, which is where validation requires this to run. The display refreshes at 119 Hz, so a frame has
about 8.4 ms before it misses one.

## What was done

| #  | Hotspot                                   |       Before |     After | What changed                                             |
| -- | ----------------------------------------- | -----------: | --------: | -------------------------------------------------------- |
| 1  | Selection highlights, a path a cell       |      2637 ms |  0.015 ms | a mask the shader reads, instead of a path a cell        |
| 2  | Colour texture re-uploaded whole          |       9.2 ms |   0.08 ms | only the texels the edit wrote are sent                  |
| 3  | Copying the frame onto the canvas surface |  4.4/14.7 ms |      0 ms | the frame is read straight into the surface's own pixels |
| 4  | Reading the frame back from the GPU       | 1.14/3.70 ms |      0 ms | the frame is drawn into the framebuffer WPF shows        |
| 5  | Saving and loading a project              |   478/272 ms |  40/24 ms | a layer's cells go out and come back in one call         |
| 6  | The dotted backdrop                       |   3.0/5.3 ms |      0 ms | the shader works the pattern out per pixel               |
| 7  | Whole-canvas editing operations           |    29-160 ms |  12-57 ms | the undo record, the fill's neighbours, and four queries |
| 8  | Recompositing every cell                  |        11 ms |    1.2 ms | the rows are composited at the same time                 |
| 9  | The selection as a `HashSet<int>`         |       4.1 ms |   0.03 ms | a bit a cell                                             |
| 10 | Rebuilding the palette's 256 swatches     |       2.1 ms |   0.10 ms | the swatches are kept and updated                        |

What that adds up to, for the things a person waits on, on a 2000 x 1000 canvas:

| Interaction                      |   Before |   After |
| -------------------------------- | -------: | ------: |
| Paint one cell                   | 18.45 ms | 0.15 ms |
| Drag the brush over 60 cells     | 18.33 ms | 0.36 ms |
| Pan the canvas                   |  8.31 ms | 0.07 ms |
| Undo a whole-canvas change       | 29.53 ms | 11.9 ms |
| Hide or show a layer             | 29.79 ms | 11.9 ms |
| A frame with everything selected |  2646 ms | 0.08 ms |
| A frame while painting, at 4K    | 32.99 ms | 0.18 ms |

## The readback, and how it went

**The frame used to be read back from the GPU**: 1.14 ms at a window's size, 3.70 ms at the validation
display's, and once the other nine were gone it was the whole of a frame. It is gone now. A frame is
0.05 ms on the example canvas, 0.08 ms on a two million cell canvas at a window's size, and 0.12 ms at the
4K display's; the GPU's own share of those is 0.04 to 0.09 ms, and the rest is recording the draw.

| Frame                                | With the readback | Without |
| ------------------------------------ | ----------------: | ------: |
| Example canvas                       |           1.28 ms | 0.05 ms |
| Large canvas, zoomed out             |           1.29 ms | 0.08 ms |
| Large canvas at 4K                   |           4.04 ms | 0.12 ms |
| Large canvas, one cell changed       |           1.31 ms | 0.12 ms |
| Large canvas at 4K, one cell changed |           3.70 ms | 0.17 ms |

Why it was there: the canvas was an `SKElement`, which hands WPF a bitmap on the processor, so a frame drawn
on the GPU had to come back across the bus to reach it, and WPF then uploaded it again to show it. Why it
could not simply be swapped for `SKGLElement`: that element gets its GL context by creating an OpenTK GLFW
window on the interface thread, `GpuHexRenderer` had already created one on a thread of its own, and GLFW
allows windows only from the thread that first made one, so the element's constructor threw and the window
never opened. The two could not coexist.

So the renderer's threading was dismantled rather than the element swapped. There is now one GL context in
the process, created by the canvas element through `GLWpfControl` and current on the interface thread, and
`GpuHexRenderer` owns no thread and no context of its own: the first canvas builds it over the element's
context as soon as the element has started. `HexCanvas` extends `GLWpfControl` directly rather than
`SKGLElement`, because `SKGLElement` builds a `GRContext` of its own on first paint and the renderer's
textures have to live in the same one; the canvas wraps the element's framebuffer as a Skia surface of the
renderer's context each frame and paints into that. The framebuffer is a Direct3D 9 surface shared with GL
through `WGL_NV_DX_interop`, shown through a `D3DImage`, so the frame is drawn where it is shown.

Three things this was expected to break, and what happened to each:

**The window painting at all.** The renderer's history recorded a GL context on the interface thread
stopping the whole window from painting, toolbar included, and that was the reason for the thread. The
record also says the failure was never cleanly isolated from a software-rendering experiment that was going
on at the same time, and it was not real: the display pass puts the real window on NE18NZ2 at 3824 x 2392
with 57 distinct colours, upright, with the grid and the paper, through a context on the interface thread.

**The display pass capturing it.** `PrintWindow` with `PW_RENDERFULLCONTENT` captures the
`D3DImage`-composited canvas as it captured the bitmap one. The same 57 colours.

**The pixel-exact checks.** They sample frames from `GpuHexRenderer.Render`, which still exists and still
reads back -- into an offscreen surface of the same context, through the same `Draw` the window uses, with
the same shader, textures and uniforms. What differs is only the surface, so a new check holds the two
surfaces together: `VerifyCanvasRendered` makes the canvas draw a frame into its own framebuffer, reads
that frame back while the framebuffer is still the canvas's to read, and compares it with the renderer's
offscreen frame of the same view, pixel for pixel. All 803,880 match. They did not at first: ten of them
sat on the other side of the grid line's edge, because the framebuffer has GL's bottom-left origin and the
offscreen surface had a top-left one, and Skia plumbs a pixel's coordinates into the shader differently for
the two, which the driver then rounds differently at a `step` threshold. The offscreen surface now has the
framebuffer's origin, and the check is exact.

**The whole-canvas editing operations are now the slowest things here**: flood fill 57 ms, move a
selection over the canvas 47 ms, clear every cell 24 ms, rotate 32 ms. All of them run on the thread handling
input, with no feedback while they do. What is left in them is the per-cell work itself rather than anything
around it.

## Two cautions the hard way

The sub-rectangle upload was written twice. The first version kept a GL texture of its own and updated it
with `glTexSubImage2D`, which is the direct way to say it, and it was fastest: 0.015 ms against the 0.08 ms
the second version costs.

It also left the real window blank -- not the canvas, the whole window, toolbar included -- while every
headless check passed. It was found by the display pass on NE18NZ2 and nothing else, and bisecting put it on
that commit exactly.

Raw GL calls into a context Skia is also driving are not worth what they cost here. The second version does
the same work through a surface Skia owns, for 0.065 ms more a frame. The same rule now covers
`GLWpfControl`, which binds its framebuffer and sets its viewport with GL calls of its own around every
frame: the canvas tells Skia to assume nothing about the GL state before it draws.

The second caution is from the first attempt at `SKGLElement`. When the canvas failed to build, the editor's
unhandled-exception handler put a message box on whichever display Windows chose and gave it the foreground
-- during a run launched with `--show-without-focus`, which exists precisely so that a run can share a desk
with the person using it. A run nobody is watching has nobody to answer a dialog, so it now logs and exits
instead, as the headless modes already did. The disruption watch is what noticed; it is also what the watch
is for.

## The ten hotspots as they were found

| #  | Hotspot                                            | Cost                                                                         | Where it is                                       |
| -- | -------------------------------------------------- | ---------------------------------------------------------------------------- | ------------------------------------------------- |
| 1  | Selection highlights, one path per cell            | **2637 ms a frame** with everything selected                                 | `HexCanvas.Paint`, the selection loop             |
| 2  | Colour texture re-uploaded whole on any cell edit  | **9.2 ms a frame** while painting                                            | `GpuHexRenderer.RenderCore`, `SKImage.FromBitmap` |
| 3  | Copying the frame onto the canvas surface          | 4.4 ms, **14.7 ms** at 4K                                                    | `HexCanvas.Paint`, `canvas.DrawBitmap`            |
| 4  | Reading the frame back from the GPU                | 1.14 ms, **3.70 ms** at 4K                                                   | `GpuHexRenderer.RenderCore`, `ReadPixels`         |
| 5  | Saving and loading a project                       | **478 ms** and **272 ms**                                                    | `ProjectFile`, one `uint` at a time               |
| 6  | The dotted backdrop                                | 3.0 ms, **5.3 ms** at 4K                                                     | `HexCanvas.Paint`, 4000 antialiased circles       |
| 7  | Whole-canvas editing operations                    | flood fill **160 ms**, move 123 ms, rotate 91 ms, clear 59 ms, copy 29 ms    | `EditorSession`                                   |
| 8  | Recompositing every cell after a structural change | 11 ms, and it forces hotspot 2                                               | `ArtworkCache.Composite`                          |
| 9  | The selection as a `HashSet<int>`                  | 4.1 ms to select all, 3.7 ms per mouse move while dragging a large selection | `EditorSession.Selection`                         |
| 10 | Rebuilding the palette's 256 swatches              | 2.1 ms, on every colour pick and every structural change                     | `MainWindow.RefreshPalette`                       |

## What a person actually waits for

End to end, on a 2000 x 1000 canvas zoomed to fit, in a 2110 x 1292 canvas area. The operation and the
frame that follows it are separate because they are fixed in different places.

| Interaction                  | The operation | The frame |      Both |
| ---------------------------- | ------------: | --------: | --------: |
| Paint one cell               |          0.10 |     18.37 | **18.45** |
| Drag the brush over 60 cells |          0.19 |     18.13 | **18.33** |
| Pan the canvas               |          0.02 |      8.29 |  **8.31** |
| Hide or show a layer         |          2.21 |     27.15 | **29.79** |
| Undo a whole-canvas change   |          2.24 |     27.29 | **29.53** |

At 4K the same frames cost 32.99 ms while painting and 24.60 ms while panning. So the editor draws at
roughly 30 to 55 frames a second while being drawn on, against a display that offers 119, and every one of
those frames is spent on the thread that is handling the input.

## The frame, in parts

A frame is five things, and only one of them is the shader. The last column is a 2000 x 1000 canvas with one
cell changed since the last frame, which is what every frame of a brush stroke looks like.

| Part                      | Example canvas | Large, zoomed out | Large at 4K | Large, one cell changed |
| ------------------------- | -------------: | ----------------: | ----------: | ----------------------: |
| Composite changed cells   |          0.000 |             0.000 |       0.000 |                   0.005 |
| Upload the colour texture |          0.000 |             0.000 |       0.000 |                   4.640 |
| Record the draw           |          0.030 |             0.022 |       0.035 |                   4.568 |
| GPU runs the shader       |          0.085 |             0.080 |       0.156 |                   0.147 |
| Read the frame back       |          1.145 |             1.141 |       3.702 |                   1.201 |
| **Whole frame**           |      **1.282** |         **1.292** |   **4.037** |              **10.613** |

Two things to take from this.

The shader costs **0.08 to 0.16 ms**, not the 0.04 ms recorded when the GPU renderer was built. That
earlier figure was taken around the draw call and `Flush`, which hand the work to the GPU without waiting
for it, so it measured submitting the frame rather than drawing it. The measurement harness separates the
two with a `GL.Finish`, which is why its frame timing is switched off unless something is measuring. Across
runs the GPU's own share wandered between 0.08 ms and 5 ms for the same frame: on a laptop GPU that is the
device clocking down between frames, not the editor, and the low figures are the ones that describe the
work.

**Editing a single cell costs 9.2 ms of texture work**, split between `SKImage.FromBitmap` copying the
whole 16 MB raster (4.64 ms) and the draw call that then uploads it to the GPU (4.57 ms, where an untouched
frame records 0.02 ms). It follows the canvas, not the edit: the same dab costs 0.005 ms on the example
canvas, whose raster is 80 KB.
This contradicts the earlier conclusion that "painting is not worth optimising", which was measured on a
500 x 250 canvas where the raster is about 1 MB. At 2000 x 1000 it is sixteen times that.

## Each hotspot, and whether it can go without breaking validation

Validation means `pwsh -File ./Scripts/Validate.ps1` continuing to pass: the build, 40 unit tests, the
hidden-host verification with its exact-pixel checks, the display pass on NE18NZ2 captured with
`PrintWindow`, and the disruption watch over the whole run. "Needs the checks edited" is called out
separately from "cannot be done within the rules", because they are not the same thing.

### 1. Selection highlights, 2637 ms a frame

`Paint` walks every cell of the viewport's bounding box and draws a hexagon path for each selected one. With
everything selected on a 2000 x 1000 canvas it walks 1,149,148 cells per frame, and every mouse move asks
for a frame. `Ctrl+A` on a large canvas makes the editor unusable, not slow.

**Yes, and nothing in validation touches it.** No check draws a selection: the verification renders through
`GpuHexRenderer.Render` with `grid: false` and no selection at all, and the unit tests assert what the
selection *contains*, never how it is drawn. The fix is to stop drawing per cell -- a selection mask texture
sampled by the shader that already exists, tinted per pixel, which makes the cost follow the window instead
of the canvas. Keeping it on the CPU and caching one path for the whole selection would also work and is a
smaller change.

### 2. The colour texture, 9.2 ms a frame while painting

`SKImage.FromBitmap(raster)` runs whenever `ArtworkCache.Version` moves, which is whenever any cell changed,
and it copies and re-uploads all 16 MB. The dirty rectangle that `ArtworkCache` already tracks is thrown
away at the upload boundary.

**Yes, with no validation impact, and the existing checks cover the change well.** A persistent texture
updated with `glTexSubImage2D` over the dirty rectangle keeps `Render`'s signature and behaviour. The
verification hands `Render` a fresh `ArtworkCache` for most checks, which must still cause a full upload;
the `ReferenceEquals(colourSource, cache)` test already in the code handles exactly that, and
`VerifyRenderScales` reuses one cache across sixteen frames, so a missed upload would show up as a wrong
pixel and fail.

### 3. Copying the frame onto the canvas surface, 4.4 ms (14.7 ms at 4K)

### 4. Reading the frame back, 1.14 ms (3.70 ms at 4K)

These two are one decision. The frame is drawn on the GPU, read back to the CPU, and then copied again onto
the surface `SKElement` hands over, after which WPF uploads it to the GPU a third time to show it. At 4K
that is 18 ms a frame of pure copying around a 0.16 ms draw.

**Yes, and the dependency is already here**: `SkiaSharp.Views.WPF` brings `SKGLElement`, which exposes a
`GRContext` and a `CanvasSize`, and renders into a GL framebuffer shared with WPF rather than a bitmap. Both
copies disappear.

But this is the one with a real cost to the validation discipline. `MainWindowVerification` renders its
pixel-exact checks by calling `GpuHexRenderer.Render` and reading the bitmap it returns, so that path has to
stay. Today it is the same path the window uses, which is what the code comment defends: one renderer for
the real window and for the checks. Afterwards the checks would verify the shader through a second path, and
a fault in the live one -- a wrong device scale, say -- would no longer be caught by them.
`VerifyCanvasRendered` still works, since `SKGLElement` has `CanvasSize`.

The display pass is the open question. `Save-WindowImage` uses `PrintWindow` with `PW_RENDERFULLCONTENT`,
which its comment says captures composited content; whether that includes a `D3DImage`-composited GL surface
has to be tried, not assumed. If it does not, `Test-ImageHasContent` fails on a blank canvas and the display
pass needs another way to capture.

### 5. Saving 478 ms and loading 272 ms

`ProjectFile` writes and reads one `uint` at a time through `BinaryWriter` and `BinaryReader` -- two million
calls a layer -- into a `Deflate` stream set to `CompressionLevel.Optimal`.

**Yes, with no validation impact.** Writing the cell array as bytes in one call produces identical output,
so the format does not move and the round-trip checks in `VerifyEditor` and the unit tests confirm it.
Dropping to `CompressionLevel.Fastest` changes the bytes but not the format.

One caveat on this figure: the measurement document is a repeating pattern, so it compressed to 0.3 MB and
the deflate had an easy time of it. Real artwork compresses worse, so both the file and these times are a
floor rather than a typical case.

### 6. The dotted backdrop, 3.0 ms (5.3 ms at 4K)

About 4000 antialiased circles drawn one at a time, every frame, behind artwork that covers most of them.

**Yes.** Caching the pattern as a tiled `SKShader` and filling one rectangle has no validation impact at
all. Folding it into the shader's out-of-canvas branch is cheaper still, but it changes what the renderer
puts outside the canvas, where `VerifyCanvasSurface` and `RenderArtwork` sample a one-pixel margin -- so
that variant needs those checks read carefully first.

### 7. Whole-canvas editing operations, 29 to 160 ms

Flood fill converts an index to a coordinate and back for each of six neighbours per cell, twelve million
conversions on a full canvas. `CopySelection` makes two LINQ passes for its origin and then a 24 MB array of
structs, and `MoveSelection` and `RotateSelection` each build one. All of it runs on the input thread with
no feedback.

**Yes, with no validation impact.** The 40 unit tests pin the behaviour of fill, move, rotate, paste and
undo in detail, including rounding ties and clipped moves, which is exactly the safety net this kind of
change needs. They do not need editing.

### 8. Recompositing every cell, about 11 ms, and it forces hotspot 2

Any structural change -- a layer hidden, reordered or made see-through, a new background, an undo of any of
them -- marks the whole raster stale, so the next frame recomposites two million cells and re-uploads 16 MB.
That is most of the 29.8 ms a hidden layer costs.

The measured figure moves about: `ArtworkCache.Refresh` over the whole canvas came back between 10.2 ms and
56 ms across five runs, because the first run allocates the raster and the garbage collector lands where it
lands. `CompositeAt` over every cell, which is the same arithmetic with no raster involved, is steady at
11.1 ms, so that is the number to work from.

**Yes, with no validation impact.** `Composite` is a pure loop over rows writing into a pinned array, and
this machine has 24 cores; a `Parallel.For` over the rows would cut it to a few milliseconds with identical
output, which the pixel-exact checks would confirm. Compositing only the visible rows first would help more
and is a larger change.

### 9. The selection as a `HashSet<int>`

4.1 ms to select all, 3.7 ms every mouse move while dragging a selection over the canvas, and roughly 30 MB
for a full one. A bit per cell would be 250 KB and need no hashing.

**Yes, but this is the one change that forces existing checks to be edited.** `EditorSession.Selection` is a
public `HashSet<int>`, and six unit tests use it as one -- `UnionWith`, `Order`, `Single`, `Count`. They
would be rewritten against the new type with equivalent assertions. That is ordinary work, not a conflict
with the rules, but it is worth knowing before starting.

### 10. Rebuilding 256 palette swatches, 2.1 ms

`RefreshPalette` clears the panel and builds 256 buttons, each with a rectangle, a style, a tooltip and an
automation name. It runs on every colour pick, and inside `RefreshPanels` on every structural change, so
every undo pays it too.

**Yes, with no validation impact,** as long as the controls keep their count and their automation names:
`VerifyPalette` checks the child count against the palette, that every swatch is square, that the chosen one
carries `Tag="Active"`, and that adding and undoing a colour adds and removes a child. Keeping the buttons
and updating their brush and tag satisfies all of it.

## Measured and not a hotspot

Worth recording so they are not chased later.

| Thing                                                  | Cost                                                       |
| ------------------------------------------------------ | ---------------------------------------------------------- |
| Compositing one cell, or a 17-cell dab                 | 0.000 to 0.006 ms                                          |
| `EditorSession.Paint` for one cell                     | 0.001 ms                                                   |
| A 60-cell brush drag, with every notification it sends | 0.19 ms                                                    |
| `UpdateHeader`                                         | 0.001 ms                                                   |
| `CloneStructure`, which shares cells                   | 0.000 ms                                                   |
| Creating a 2000 x 1000 document                        | 0.55 ms                                                    |
| Copying one with its cells                             | 0.72 ms                                                    |
| Rebuilding the layer list, 12 layers                   | 0.27 ms                                                    |
| Building the whole window, every control               | 8.5 ms                                                     |
| Startup, process start to the first measurement        | 1.0 to 1.4 s, of which the GPU renderer takes about 0.65 s |

The per-cell notification chain was a suspect and is not guilty: a drag that crosses 60 cells sends 60
notifications, rebuilds the header 60 times, and costs 0.19 ms in total.

## Order worth doing them in

1. **Hotspot 1**, the selection highlights. It is the only one that makes the editor unusable, and nothing in
   validation stands in the way.
2. **Hotspot 2**, the dirty-rectangle upload. Biggest win per line changed, and the checks already cover it.
3. **Hotspots 8, 5, 6, 7, 10** in whatever order suits. All are contained, none touches validation.
4. **Hotspots 3 and 4**, the two copies, by way of `SKGLElement`. Largest win on a 4K display, and the only
   one that costs the "one rendering path" property and needs the display-pass capture proved.
5. **Hotspot 9**, the selection representation, when the unit tests are worth editing for it.
