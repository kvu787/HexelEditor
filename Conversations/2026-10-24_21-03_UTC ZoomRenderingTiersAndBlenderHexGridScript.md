# Zoom rendering tiers and a Blender hex grid script

## User

![Screenshot at 86 percent zoom with the grid on](Images/2026-10-24_21-03_UTC%20ZoomRenderingTiersAndBlenderHexGridScript%201.png)
![Screenshot at 37 percent zoom with the grid off](Images/2026-10-24_21-03_UTC%20ZoomRenderingTiersAndBlenderHexGridScript%202.png)
![Screenshot at 37 percent zoom with the grid on](Images/2026-10-24_21-03_UTC%20ZoomRenderingTiersAndBlenderHexGridScript%203.png)
![Screenshot at 21 percent zoom with the grid checked but not drawn](Images/2026-10-24_21-03_UTC%20ZoomRenderingTiersAndBlenderHexGridScript%204.png)

"C:\Users\k\Pictures\Screenshots\Screenshot 2026-10-24 125038.png"
"C:\Users\k\Pictures\Screenshots\Screenshot 2026-10-24 125052.png"
"C:\Users\k\Pictures\Screenshots\Screenshot 2026-10-24 125057.png"
"C:\Users\k\Pictures\Screenshots\Screenshot 2026-10-24 125101.png"

help me understand why the grid looks noticeable "different" at different zoom levels.

it seems like anti-aliasing is turned off or something past a certain zoom out because I start noticing "jaggies" at that point.
for example, i don't see any jaggies in "C:\Users\k\Pictures\Screenshots\Screenshot 2026-10-24 130424.png", but when i zoom out a notch, i suddenly see them in "C:\Users\k\Pictures\Screenshots\Screenshot 2026-10-24 130428.png"

another thing i notice is that the hex cells seem to become square-ish or rectangle cells past a certain zoom point

![Screenshot at 75 percent zoom, smooth](Images/2026-10-24_21-03_UTC%20ZoomRenderingTiersAndBlenderHexGridScript%205.png)
![Screenshot at 65 percent zoom, jagged](Images/2026-10-24_21-03_UTC%20ZoomRenderingTiersAndBlenderHexGridScript%206.png)

## Assistant

Both things you noticed are real, and both are deliberate choices in `ArtworkRenderer.cs` -- not a bug, but the thresholds are more visible than they should be.

### There are three renderers, and only one antialiases

`Draw` picks a detail level from how many pixels a cell occupies:

```
cellPixels = sqrt(3) * 12 * dpiScale * Zoom
```

Your display is 96 DPI (I checked -- `LOGPIXELSX = 96`, `DESKTOPHORZRES = 2560`), so `dpiScale` is exactly 1 and `cellPixels = 20.78 * Zoom`. That gives:

| Zoom          | cellPixels | Path         | How cells are drawn                        | Antialiased |
| ------------- | ---------- | ------------ | ------------------------------------------ | ----------- |
| >= 67.4%      | >= 14      | `DrawExact`  | `SKPath` hexagons, one fill per colour     | Yes         |
| 33.7% - 67.4% | 7 - 14     | `DrawMesh`   | 4 triangles per cell via `DrawVertices`    | No          |
| 14.4% - 33.7% | 3 - 7      | `DrawMesh`   | 2 triangles -- an axis-aligned rectangle   | No          |
| < 14.4%       | < 3        | `DrawRaster` | cached bitmap, linear-filtered blit        | (filtered)  |

The constants are `ExactAbovePixels = 14`, `HexagonAbovePixels = 7`, `RasterBelowPixels = 3`.

### Why 75% to 65% specifically

That is exactly the `DrawExact` to `DrawMesh` boundary. 75% gives 15.6 px/cell, above 14, so you get `SKPaint { IsAntialias = true }`. 65% gives 13.5 px/cell, below 14, so you drop to `DrawVertices`, which Skia rasterises with no coverage antialiasing at all. I cropped and 8x-magnified the same region of both your screenshots: at 75% the diagonals carry grey transition pixels, at 65% every pixel is pure black or pure white. It is a hard switch at one notch of the x1.15 zoom ladder, which is why it reads as something turning off.

There is a second, smaller jump at the same boundary. `DrawExact` inflates each hexagon by `Bleed()` so antialiased neighbours overlap instead of leaving hairline seams -- at 75% that is +1.15 on a radius of 12, about 9.6% oversized. `DrawMesh` uses the exact radius, since flat triangles have no seams to hide. So cells also shrink ~10% relative to their spacing as you cross 67.4%, making strokes look slightly thinner on top of the jaggies.

### Why cells become rectangles

Below 33.7% zoom, `DrawMesh` swaps `MeshFan` for `BandFan`, which emits the *band* a cell occupies -- `sqrt(3)*r` across by `1.5*r` down -- as two triangles. That is a literal rectangle; the hexagon corners are gone entirely. Halving the triangle count halves the per-cell setup cost, which is what the mesh path spends its time on. Your 21% screenshot confirms it: magnified, every cell is a pure axis-aligned box. Cells still sit in the right places, including the half-cell row offset, so the artwork stays correctly shaped at a distance -- only the cell silhouette is wrong.

### Two rough edges worth knowing

- **Grid overlay cuts out at 30%**, via `zoom >= 0.3` -- a plain zoom check, not a pixel-size check. That is why your 21% screenshot has Grid ticked but shows nothing.
- **30%-33.7% is inconsistent.** In that window the grid overlay draws true antialiased hexagon outlines (`DrawGrid` always uses `Hexagon`) while the fills underneath are rectangles. At 32% -- a reachable notch on the ladder -- you get hexagon lines over square cells that do not line up.

Also, because the fill thresholds scale with DPI but the grid's 30% does not, these boundaries land at different zoom percentages on a scaled display.

The cheapest real improvement is turning on antialiasing for the mesh tier: `DrawVertices` itself cannot antialias, but the 7-14 px band could use `DrawExact`'s path batching without the per-colour grouping, or the `ExactAbovePixels` threshold could simply drop to around 7 so the jaggy-hexagon tier disappears and only the rectangle tier remains unsmoothed. I have not measured what that costs on your 500x250 canvas, so I would want to profile before committing to either.

## User

explain what each renderer does

## Assistant

Here is what each one actually does.

### The dispatcher

`Draw` measures one number -- how many pixels a cell spans -- by pulling the scale out of `canvas.TotalMatrix`, then hands off:

```csharp
var cellPixels = (float)(Math.Sqrt(3) * CellRadius * CanvasScale(canvas));
switch (DetailFor(cellPixels, cache != null))
```

The `cache != null` is worth noting: `DetailFor` returns `Exact` whenever no cache was supplied, and `ExportPng`/`ExportSvg` pass none. So **exports always take the exact path** no matter what scale they render at. The three tiers exist purely for interactive drawing.

### Shared: picking which cells to touch

All three paths (and `DrawGrid`, and the selection/cursor loops in `HexCanvas`) start from `VisibleCells`. Rather than asking each cell whether it is on screen, it inverts the layout -- divide the viewport rect by the cell pitch `across = sqrt(3)*r`, `down = 1.5*r` -- and gets a column/row span directly:

```csharp
var minColumn = (int)Math.Floor(viewport.Left / across) - 1;
var maxColumn = (int)Math.Ceiling(viewport.Right / across) + 1;
```

That is the difference between work that scales with the *document* and work that scales with the *window*. Testing two million cells costs the same whether nine or nine hundred are visible. The +/-1 margin is deliberate slop, since cells overlap their neighbours' bands and alternate rows sit half a cell across.

### 1. `DrawExact` -- >= 14 px/cell

The faithful one. For each visible cell it adds a pre-built hexagon path at that cell's centre into a `SKPathBuilder` **keyed by colour**:

```csharp
if (!groups.TryGetValue(color, out var group))
    groups.Add(color, group = new SKPathBuilder { FillType = SKPathFillType.Winding });
group.AddPath(cell, (float)x, (float)y);
```

So the draw cost is one `DrawPath` per distinct colour, not per cell, and a run of same-coloured cells fills as the single shape it looks like.

Three things only this path does:

**Antialiasing.** `new SKPaint { IsAntialias = true }`. This is the only tier where fills get coverage AA.

**Seam bleed.** The hexagon is built at `CellRadius + Bleed(scale)`, not `CellRadius`. Two antialiased fills that merely *touch* each cover part of the pixels along their shared edge, and neither reaches full coverage, so the backdrop shows through as a hairline. A whole canvas of those hairlines reads as a grid even with the grid turned off. So cells are grown until one side or the other covers every edge pixel whole. The 0.75 px of overlap is converted into a radius delta by dividing by cos(30 degrees), since an edge sits closer to the centre than a corner does -- hence `SeamOverlap * 2 / sqrt(3) / scale`, capped at `CellRadius / 8`.

**A layer for translucency.** If any cell has alpha < 255, those overlaps would blend *twice* and draw a seam of their own, a darker one. So it opens a layer and switches to `SKBlendMode.Src`, where each cell replaces what it covers.

The background is also handled here: every visible cell goes into a separate `backgroundCells` path that is filled **first**, so no cell's bleed paints over a neighbour's artwork.

Cost: a dictionary lookup, a path append, and a `HexGeometry.Center` call per cell, plus path tessellation. Affordable only because cells this big are necessarily few.

### 2. `DrawMesh` -- 3 to 14 px/cell

Raw triangles. Corner offsets are computed once up front, then just added to each cell centre -- no trig per cell.

Two subtleties:

**The paint must be white.** Skia *modulates* vertex colours against the paint colour, and a default `SKPaint` is black -- which would make every cell black. Hence `new SKPaint { Color = SKColors.White }` with `SKBlendMode.Modulate`.

**Part-filled batches get sliced.** Skia takes the vertex count from the array length, so the last batch of a frame is copied down to size with `vertices[..filled]` -- one allocation per frame rather than per cell.

The two fans are where the rectangle question comes from:

- `MeshFan` -- the 6 corners, fanned from corner 0 into 4 triangles, **12 vertices per cell**.
- `BandFan` -- the *band* the cell occupies as 2 triangles, **6 vertices per cell**. A literal axis-aligned rectangle.

Setting up a triangle costs the same whatever its size, and setup is what this path spends its time on, so halving the count roughly halves the frame.

What it gives up: `DrawVertices` has no coverage antialiasing at all. Note there is no bleed here either, and that is correct rather than an oversight -- flat triangles meeting exactly produce no seam.

### 3. `DrawRaster` -- below 3 px/cell

One scaled blit of a bitmap maintained by `ArtworkCache`.

The interesting decision is the resolution: **two pixels per cell, not one.** At one pixel per cell, the half-cell offset of alternate rows falls between two pixels and cannot be stored, so the raster would describe a *rectangular* grid and the artwork would be drawn with its rows out of place. At two pixels, that offset is exactly one pixel.

The pixels live in a pinned `int[]` that the bitmap is installed over rather than copied into, so writing a cell writes the bitmap and a stroke costs no copy.

Updates are incremental. `MarkDirty` *accumulates* a dirty rectangle instead of applying it immediately, and `HexCanvas.Paint` drains `session.TakeDirtyCells()` into it **every frame regardless of zoom** -- otherwise an edit made while zoomed in would be lost by the time you zoomed out.

It is blitted with `SKFilterMode.Linear`, because several cells land in one screen pixel down here and picking just one of them would make the artwork shimmer as you pan.

Cost: one `DrawImage`, independent of document size.

### `DrawGrid` -- the overlay, shared by all three

Drawn above whichever tier ran, gated on `zoom >= 0.3`. Every visible cell's outline goes into one path and is stroked **once**. The `/ zoom` on `StrokeWidth` keeps the line ~0.65 px on screen at any magnification.

This always draws true hexagons at exactly `CellRadius`, with no bleed and always antialiased -- which is the mismatch flagged earlier: between 30% and 33.7% zoom it lays hexagon outlines over rectangular fills.

### Summary

| Tier   | Cells/frame | Geometry per cell    | AA       | Cost scales with |
| ------ | ----------- | -------------------- | -------- | ---------------- |
| Exact  | visible     | path hexagon + bleed | yes      | visible cells    |
| Mesh   | visible     | 12 or 6 vertices     | no       | visible cells    |
| Raster | 1 blit      | 2 px, precomposited  | filtered | window           |
| Grid   | visible     | path hexagon outline | yes      | visible cells    |

The progression is consistent: each step down drops a property that stops being perceptible at that size -- exact silhouette, then corners, then per-cell identity -- in exchange for cost that stops following the document.

## User

i want to see how a large hex cell grid would look like in the blender viewport.

create a blender 4.5.x file that has a 2000x1000 flat hex cell grid. each hex cell should be a mesh object composed of 4 triangles. create a palette of unlit materials for colors and assign materials to each cell in such a way that no adjacent cells share the same color.

## User (mid-turn)

actually, don't create/generate the .blend file yourself. instead, create and test a blender python script that accepts width and height arguments for the grid dimensions and generates the grid that i described: a 2000x1000 flat hex cell grid. each hex cell should be a mesh object composed of 4 triangles. create a palette of unlit materials for colors and assign materials to each cell in such a way that no adjacent cells share the same color.

## User (mid-turn)

i should be able to open blender, copy/paste your script in a text editor pane, set the width/height arguments to my liking, and simply execute the script and get the hex cell grid

## User (mid-turn)

also, the grid should be flat on the xy plane and it should only occupy the +x and +y quadrant

## Assistant

Built `BlenderHexGrid/GenerateHexGrid.py`, a paste-into-the-Text-Editor script with a settings
block at the top, plus a test suite, a measurement harness and a preview renderer.

### What it produces

- Flat-top hexagons matching `HexOrientation.FlatTop`, four triangles each fanned from corner 0,
  the same decomposition `ArtworkRenderer.MeshFan` uses.
- Flat on the XY plane at Z = 0, wholly inside the +X +Y quadrant with the bounding box starting
  exactly at the origin.
- A palette of unlit emission materials, with the viewport display colour set to match so Solid
  shading shows the palette too.
- Colour `(q + 2r) mod k` in axial coordinates. Across the six neighbour directions that changes
  by 1, -1, -2, -1, 1 and 2, none of which vanish mod any `k >= 3`, so no two touching cells can
  match whatever the palette size. `COLOR_COUNT` below 3 is rejected, since 3 is the chromatic
  number of a triangular lattice.
- Re-running replaces the previous grid rather than stacking a second one.

### The performance finding

The first working version took 91.7 s for 80,000 cells and was getting worse: 4x the cells cost
47x the time. Profiling ruled out the collection link, the name uniqueness check and the
`location` write -- all flat at ~85 us/object. The cause was the naming scheme. Blender keeps each
kind of datablock in a name-sorted list; a name that does not sort last forces a backward scan, so
`Name_<column>_<row>` unpadded (where `_10_` sorts before `_2_`) made object creation quadratic.

| Naming                               | Per cell | 80,000 cells |
| ------------------------------------ | -------: | -----------: |
| `Name_<column>_<row>`, unpadded      |  2085 us |      167 s   |
| `Name_r<row>_c<column>`, zero-padded |    31 us |        2.5 s |

Zero-padding and putting the row first, so creation order matches sort order, made it linear.
Measured after the fix:

| Grid        |     Cells | Time   | Peak memory | Per cell |
| ----------- | --------: | ------ | ----------: | -------: |
| 400 x 200   |    80,000 | 2.8 s  |     0.40 GB |    35 us |
| 1000 x 500  |   500,000 | 20.8 s |     1.71 GB |    42 us |
| 2000 x 1000 | 2,000,000 | 89.3 s |     6.19 GB |    45 us |

The cost is Blender's per-object overhead rather than the geometry, so four triangles a cell is
not what makes a large grid expensive.

### Testing

41 tests, run with `pwsh -File ./BlenderHexGrid/Test.ps1`. They execute the real script text with
only its settings constants rewritten, so what is tested is the file you would paste. The
adjacency tests ignore the colouring formula and work from the built scene, finding each cell's
neighbours by measuring distances between object positions, so a bug present in both the generator
and a re-implementation would still be caught.

Two test bugs were found and fixed along the way: a 1e-6 distance tolerance that single-precision
object locations cannot meet at coordinates near 60, and a spatial bucket sized exactly at the
search radius, which dropped neighbours for every cell whose coordinate landed on a bucket edge.

## User

okay, the hex cells don't have to be one object per cell. create/test/validate a separate approach that uses one mesh object to contain all the hex cells.

## User (mid-turn)

also, create a demo .blend file that has a 2000x1000 hex grid created from your script

## Assistant

Added `BlenderHexGrid/GenerateHexGridSingleMesh.py` beside the per-object script, plus its own
tests, a demo-file writer and a verifier.

### What changed

Same geometry -- flat-top hexagons, four triangles fanned from corner 0, same positions, same
`(q + 2r) mod k` colouring -- but all of it in one mesh object. A cell's colour is carried by the
`material_index` of its four polygons rather than by an object's material slot. A test builds the
same grid through both scripts and checks they agree cell for cell.

The mesh is filled with `add()` and `foreach_set()` from numpy buffers rather than `from_pydata`,
which would need Python lists of twelve million coordinate tuples. Cells are laid out row by row,
so cell `(column, row)` owns vertices `6n` to `6n+5` and polygons `4n` to `4n+3` for
`n = row * WIDTH + column`.

### Measured at two million cells

| | One object per cell | Single mesh |
| ------ | --------- | --------- |
| Build  | 89.3 s    | 8.3 s     |
| Memory | 6.19 GB   | 2.04 GB   |
| Each   | 45 us     | 4.2 us    |

The per-object cost was Blender's per-object overhead rather than the geometry. What the single
mesh gives up is selecting, moving, hiding or animating a cell on its own in Object Mode.

### The demo file

`MakeDemo.ps1` writes `Output/HexGrid2000x1000.blend`: two million cells, twelve million
vertices, eight million polygons, seven material slots. Builds in 9.5 s, saves in 0.4 s, reopens
in 0.7 s, 193 MB compressed. `Output` is gitignored -- far too large for the repository, and it
rebuilds from the script in seconds.

It is then opened again and checked, because saving and re-opening is where a mesh filled through
`foreach_set` would show damage. The adjacency check is exhaustive rather than sampled: all six
directions for all two million cells.

### Validation

41 + 44 automated checks pass, plus both scripts driven through a real Blender window's Text
Editor on NE18NZ2. The saved demo file was opened in a real window and screenshotted at three
zoom levels.

Worth knowing: zoomed all the way out the grid looks grey, and that is correct. Two thousand
columns across a thousand-pixel viewport is half a pixel per cell, so seven saturated hues
average to a neutral. Zoomed in, the colours are there and no two neighbours match.

## User

![Blender viewport with EEVEE, samples 0, temporal reprojection on](Images/2026-10-24_21-03_UTC%20ZoomRenderingTiersAndBlenderHexGridScript%207.png)

okay, i opened "C:\Users\k\Repository\HexelEditor\BlenderHexGrid\Output\HexGrid2000x1000.blend" and tested a few things. i tried zooming and panning with each different viewport render mode and with varying levels of samples (0/inf, 1, 16) and turning temporal reprojection on/off. for all combinations, performance is buttery smooth and i noticed no lag.

the thing i am confused by is that in an earlier conversation, i basically asked the agent if this app could simply render things like blender, and it didn't seem like the answer was a straightforward "yes". however, based on this experiment, it seems like this blender render approach has excellent perf and it provides the visuals that i want. by visuals, the grid looks good at any zoom level when anti-aliased. when anti-aliasing is turned off (i assume this means setting viewport samples to 1 in blender), it looks as expected.

so, can we have this hex editor app simply "render like blender" without any issues?

## Assistant

Short answer: **yes**, and your experiment is good evidence. Here's why the earlier answer hedged, and which parts of the hedge were actually justified.

### First, what the editor costs today

I benchmarked `ArtworkRenderer.Draw` directly into an offscreen surface -- a 2000x1000 canvas, 2456x1270 viewport, same call `HexCanvas.Paint` makes every frame:

| Zoom | Cell px | Tier   | Grid off     | Grid on  |
| ---: | ------: | ------ | -----------: | -------: |
| 2.00 |    41.6 | Exact  |       9.2 ms |  10.8 ms |
| 1.00 |    20.8 | Exact  |      22.7 ms |  29.8 ms |
| 0.75 |    15.6 | Exact  |      36.5 ms |  44.7 ms |
| 0.65 |    13.5 | Mesh   |      33.4 ms |  43.9 ms |
| 0.40 |     8.3 | Mesh   |      55.5 ms |  78.2 ms |
| 0.25 |     5.2 | Mesh   |      61.0 ms |  57.6 ms |
| 0.15 |     3.1 | Mesh   | **132.9 ms** | 123.7 ms |
| 0.10 |     2.1 | Raster |      26.3 ms |  27.2 ms |

60 fps is 16.7 ms. Only the most zoomed-in case gets there. The worst case is 7.5 fps, and that excludes the WPF blit, so real frames are a bit worse. I ran it with 16 and 256 colour palettes -- nearly identical, so this is genuine cost, not an artifact of my test data.

So your instinct is right: Blender is drawing *more* (8M triangles, all 2M cells at once) and doing it in a couple of milliseconds.

### Why Blender is fast

Nothing magic, and importantly nothing that depends on it being a 3D app:

1. **The mesh is uploaded to the GPU once.** Panning and zooming change a 4x4 matrix. Per-frame CPU geometry work is zero. That's the whole reason it stayed smooth across every mode you tried.
2. **8M triangles is a trivial GPU load.** Modern cards do hundreds of millions per second.
3. **MSAA is free in hardware**, and with viewport samples at 0 it keeps accumulating jittered sub-pixel samples while the view is still. That's the uniform antialiasing you liked.

The editor does the opposite: every frame it walks visible cells on the CPU, builds `SKPath` objects or vertex arrays in managed memory, and runs Skia's scanline rasteriser into a bitmap. `HexCanvas` derives from `SKElement`, which is SkiaSharp's **CPU** control -- it renders into a `WriteableBitmap`. Cost scales with visible cells x pixels, every frame, even when nothing changed. The three detail tiers exist to claw that back, and the jaggies you asked about at the top of this conversation are the price.

### Which parts of the hedge were real

Three of the usual objections don't hold here:

- *"Blender shows static geometry, an editor has to edit."* Solved already -- `ArtworkCache` maintains an incrementally-updated bitmap of composited cell colours with a dirty-rect system. That is already a colour texture.
- *"WPF can't host GPU content without airspace problems."* I checked the package: `SKGLElement` depends on **OpenTK.GLWpfControl**, not `WindowsFormsHost`. It composites as a normal WPF element. No airspace issue.
- *"You'd need a huge mesh."* No -- see below.

The one that **is** real: the validation harness renders offscreen in a hidden `HwndSource` on its own Win32 desktop with no display. A GL context there needs proving before anything else, because the whole testing discipline depends on it. That, plus keeping the CPU path as a fallback for RDP and machines without a usable GPU.

### Three paths, cheapest first

**A. Quality by motion instead of by zoom.** Blender does this too -- that's what temporal reprojection is. Draw the cheap tier while the view is moving, draw `DrawExact` once it settles. This permanently fixes the original complaint: every still frame is antialiased at *any* zoom, and the 133 ms worst case only gets paid once instead of every frame. No GPU, no new dependency -- a timer and a dirty flag.

**B. Swap `SKElement` for `SKGLElement`.** Same drawing code, Skia rasterises on the GPU. Real gains, but incomplete: `DrawExact` still rebuilds paths on the CPU every frame, so it moves half the problem.

**C. Stop drawing geometry at all.** Blender needs 8M triangles because it is a general 3D app with arbitrary meshes. **A hex grid is a regular tiling, so it can be evaluated instead of drawn:** one full-screen quad, and a fragment shader that maps pixel -> world -> axial coordinate -> sample the colour texture. Per-frame cost becomes O(pixels) -- independent of canvas size *and* zoom, so 2000x1000 costs the same as 50x25.

The pieces already exist in the codebase: `HexGeometry.FromPoint` is the closed-form pixel-to-cell lookup, and `ArtworkCache` is the texture. And `SKRuntimeEffect` / `SKRuntimeShaderBuilder` are present in SkiaSharp 4.150.2, so it can be prototyped on the current CPU backend and flipped to GPU later without restructuring. Antialiasing comes from the hexagon's signed-distance function rather than MSAA, which gives analytic edges at every zoom.

One caveat for C: when cells fall below a pixel you still need a mipmapped or pre-averaged texture read, or it will shimmer when panning -- the same problem the current raster tier solves with its linear filter.

So: not "without any issues", but the issues are smaller than the hedge implied, and C is strictly better than copying Blender's approach.

## User

what is temporal reprojection

## Assistant

It's the trick that lets a renderer keep the work it did in previous frames when the camera moves.

### The problem it solves

A clean image needs many samples per pixel -- for antialiasing, you need to know how much of the pixel each edge covers, which means sampling it at several sub-pixel positions. Doing all of that in one frame is expensive.

So instead you take **one sample per frame, jittered to a slightly different sub-pixel position each time**, and average over the last N frames. After 30 frames you have a 30-sample image for the price of one sample per frame. This is temporal accumulation, and it's what makes Blender's viewport keep visibly cleaning itself up for a second or so after you stop moving.

The catch: that average lives in a buffer indexed by screen position. The moment the camera moves, pixel (400, 300) is no longer looking at the same thing it was last frame, so the accumulated history is garbage and has to be thrown away. Which is why, without this, moving the view snaps you back to a raw single-sample image.

### What reprojection adds

Rather than discarding the history, you **warp it to fit the new view**. For each pixel in the new frame, work out where that same surface point appeared in the previous frame -- using the previous frame's view-projection matrix, plus depth or motion vectors -- and read the history buffer *there* instead of at the same screen coordinate.

"Temporal" = across frames. "Reprojection" = re-projecting old screen-space data into the new frame's screen space.

The result is that accumulated quality survives camera motion instead of resetting. That is exactly what the checkbox does in EEVEE: with it off, moving the view restarts accumulation from scratch; with it on, the previous result is warped into the new view and blended forward.

### Where it goes wrong

It's an approximation, and it fails in predictable ways:

- **Disocclusion** -- pixels showing something that was hidden last frame have no valid history. Panning constantly creates these along the leading edge of the screen.
- **Ghosting** -- anything whose appearance depends on the viewpoint (specular highlights, reflections) or that moves independently leaves smeared trails, because the warp assumes the colour is still valid.
- **Fast motion** -- the history is stale enough to be wrong everywhere.

Real implementations defend against this with *neighbourhood clamping*: before blending, clamp the history colour to the range of colours found in the current frame's neighbouring pixels. If it falls outside that range the history is clearly wrong, so it gets rejected and that pixel restarts. The same family of techniques appears in DLSS and FSR, and in VR under names like timewarp or asynchronous reprojection.

### Why your test was the best possible case for it

The hex grid is flat, static, orthographic and viewed straight down. That means:

- The reprojection is a **pure 2D translate-and-scale** -- no depth, no motion vectors, no perspective divide.
- Nothing is view-dependent, so there is no ghosting to speak of.
- The only disocclusion is the thin strip of new content scrolling in at the screen edge.

So the warp is essentially exact. That's a large part of why it felt buttery smooth across every combination tried -- it was exercising the one scenario where this technique has almost no failure modes.

### What it would mean for the editor

In a 2D editor, "temporal reprojection" collapses into something much simpler than the 3D version:

**For panning, it degenerates into scrolling the previous frame's bitmap and redrawing only the newly exposed strips at the edges.** If the pan is snapped to whole pixels there's no resampling at all, so it isn't even an approximation -- it's exact, with zero ghosting. A pan that exposes 40 pixels of new content costs 40 pixels of drawing instead of a full 2456x1270 re-render. Against the 133 ms worst case measured above, that is close to free.

Zooming is the harder half, because scaling the old frame does resample and does degrade -- so there you'd show the warped frame during the gesture and re-render properly once it settles.

## User

is temporal reprojection only relevant to anti-aliasing? what happens if you do no anti-aliasing?

## Assistant

No -- antialiasing is just its most visible customer. The underlying mechanism is "reuse pixels you already computed," and several different things want that.

### What else consumes it

Anything expensive and per-pixel that can be computed a bit at a time:

- **Ambient occlusion, screen-space reflections, raytraced shadows** -- all stochastic. They fire a handful of rays per pixel per frame and would look like noise on their own; accumulation is what resolves them.
- **Volumetrics and depth of field** -- expensive sampling spread over frames.
- **Soft/jittered shadows** -- in the screenshot there's a **Jittered Shadows** checkbox sitting directly under Samples, next to Temporal Reprojection. It's there because it's another accumulation consumer. That grouping in the UI is the giveaway: Samples doesn't mean "antialiasing quality," it means "how many frames of accumulated everything."
- **Upscalers** -- DLSS, FSR2, XeSS reproject several jittered low-resolution frames into one higher-resolution output. Temporal supersampling used to buy *resolution*, not edge quality.
- **VR timewarp and frame generation** -- reproject the last frame to hit a deadline when the next one isn't ready. Nothing to do with sampling quality; purely latency.

That last category splits the motivation in two:

| Motivation               | What reprojection buys                   | Needs AA?            |
| ------------------------ | ---------------------------------------- | -------------------- |
| **Quality**              | Amortise many samples over many frames   | yes, that's the point |
| **Throughput / latency** | Avoid recomputing a frame you already have | no                  |

### So what happens with no antialiasing

**You lose the quality benefit entirely.** With one unjittered sample per pixel and nothing else stochastic in the scene, every frame from a given camera position is byte-identical. There is nothing to average, so nothing for accumulation to improve. This matches what you saw: at samples=1 it looked plain aliased, and no amount of waiting cleaned it up.

**You keep the performance benefit.** Reusing a frame you already drew is cheaper than drawing it again whether or not that frame was antialiased. The 133 ms measured earlier doesn't care how pretty the result was.

**But the warp itself gets worse -- and this is the part people miss.** Reprojection almost always lands on non-integer offsets, so it has to resample the history buffer. An aliased image has full-strength high-frequency content right at the edges, so resampling it goes badly in one of two ways: nearest-neighbour makes edges crawl and shimmer as they snap between pixels, and bilinear smears them. An antialiased image is band-limited, which is exactly the condition under which resampling behaves.

So the relationship runs both ways. AA is a *consumer* of temporal reprojection, but it's also a *precondition* for reprojection to look good. Turning AA off doesn't just remove the reason to reproject -- it makes reprojecting actively uglier.

### The exception, which happens to be this case

There's one warp that needs no resampling at all: **a translation by a whole number of pixels.** That's a straight memory copy -- exact, lossless, no filtering decision, no shimmer, no smear, antialiased or not.

Which is precisely the hex editor's panning case. Snap pan deltas to whole pixels, blit the previous frame shifted, redraw only the newly exposed edge strips, and the result is bit-identical to a full redraw. The throughput win with none of the quality cost, and it works today on the Mesh tier that has no antialiasing at all.

Zooming is where it bites. Scaling the old frame is a non-integer warp, so with the current unantialiased tiers it would shimmer noticeably. There the honest approach is to accept a degraded preview during the gesture and re-render once it settles -- or to fix the antialiasing first, at which point zoom reprojection becomes viable too.

## User

okay, i need to better understand some rendering fundamentals before we continue.
let's say i have a hex grid with a color for each hex cell and 4 triangles for each hex cell. there is also 32 bit float that basically controls the "zoom level". what is the simplest way this is performantly rendered to a 2560x1440 screen?

## Assistant

### The setup

Three pieces of data live on the GPU, and only one of them ever changes:

| Buffer   | Contents                                        | Size for 2M cells | Changes       |
| -------- | ----------------------------------------------- | ----------------: | ------------- |
| Vertex   | 6 corner offsets of **one** hexagon             |          48 bytes | never         |
| Index    | the 12 fan indices `0,1,2, 0,2,3, 0,3,4, 0,4,5` |          48 bytes | never         |
| Instance | one RGBA8 colour per cell                       |              8 MB | when you paint |
| Uniform  | zoom + pan, as 4 floats                         |          16 bytes | every frame   |

Note what's *not* there: per-cell positions. They aren't needed. Every hexagon is the same shape, and its centre is derivable from its index, so the vertex shader computes it. That's the difference between 8 MB and ~200 MB of geometry.

### The draw

One call per frame:

```
DrawElementsInstanced(TRIANGLES, indexCount: 12, instanceCount: N)
```

The GPU runs the vertex shader `6 x N` times -- once per corner, per cell:

```glsl
uniform vec2  uScale;    // zoom, baked into clip-space units
uniform vec2  uOffset;   // pan
uniform int   uWidth;    // cells across
uniform float uRadius;

const vec2 CORNERS[6] = vec2[6](/* flat-top: cos/sin of 0,60,...,300 */);

in uint aColor;          // per-instance
flat out vec4 vColor;    // "flat" = do not interpolate across the triangle

void main() {
    int column = gl_InstanceID % uWidth;
    int row    = gl_InstanceID / uWidth;

    vec2 centre = vec2(
        uRadius * (1.0 + 1.5 * float(column)),
        uRadius * (0.8660254 + 1.7320508 * (float(row) + 0.5 * float(column & 1)))
    );

    gl_Position = vec4((centre + CORNERS[gl_VertexID] * uRadius) * uScale + uOffset, 0.0, 1.0);
    vColor = unpackUnorm4x8(aColor);
}
```

```glsl
flat in vec4 vColor;
out vec4 fragColor;
void main() { fragColor = vColor; }
```

That is the entire renderer. **The zoom float is 4 bytes of a uniform write.** Changing it does not touch the geometry, which is the whole reason panning and zooming are free.

Three settings matter and all are subtractive: **4x MSAA on**, **no depth buffer** (it's 2D, nothing overlaps), **no blending** (cells are opaque). Depth and blending are pure bandwidth that isn't needed.

### Why this is fast

1. **Persistence.** The geometry is uploaded once and lives in VRAM. Per-frame CPU work is 16 bytes. Compare to the editor today, which rebuilds `SKPath` objects in managed memory *every frame* even when nothing changed.
2. **Parallelism.** Thousands of cores run those vertex and fragment shader invocations concurrently.
3. **Rasterisation is fixed-function silicon.** Turning a triangle into covered pixels isn't code, it's hardware, and it's the single most optimised thing on the chip.
4. **Clipping happens before rasterisation.** Zoom in and off-screen triangles die at the front of the pipeline for almost nothing. That's why zoomed-in is cheap no matter how big the canvas is.

### Where the cost actually lands

| Regime             | When                        | Cost driven by       | 2560x1440                                    |
| ------------------ | --------------------------- | -------------------- | -------------------------------------------- |
| **Fill-bound**     | zoomed in, cells are large  | pixels covered       | 3.7M fragments, trivial shader -> ~free       |
| **Geometry-bound** | zoomed out, cells sub-pixel | vertices + triangles | 12M verts, 8M tris -> a few ms                |

The second regime has a specific inefficiency: **GPUs rasterise in 2x2 pixel quads**. A triangle covering one pixel still costs a four-pixel quad, so three quarters of the fragment work is thrown away. Once cells drop below a pixel you're pushing 8M triangles through the pipeline to produce 3.7M pixels, and most of that effort is discarded.

### The honest caveat on "simplest"

The instanced-triangle approach is the simplest thing that is *also* fast, and worth writing first: MSAA handles antialiasing for free, there's no tricky maths, maybe 150 lines total.

But it is not the *best* approach. Since a hex grid is a regular tiling, the geometry can be deleted entirely: draw **two triangles** covering the screen, and have the fragment shader work out which cell each pixel falls in (`HexGeometry.FromPoint` is already that function) and look up its colour. Then vertex cost is 4 vertices, fragment cost is 3.7M pixels at ~20 ALU ops each, and **total cost is independent of cell count and of zoom**. Flat ~1 ms whether the canvas is 50x25 or 2000x1000, at any magnification. The price is implementing antialiasing via the hexagon's signed-distance function instead of getting MSAA free, plus a mipmapped colour lookup so sub-pixel cells don't shimmer.

## User

![The setup table from the previous answer, with Vertex and Index rows circled](Images/2026-10-24_21-03_UTC%20ZoomRenderingTiersAndBlenderHexGridScript%208.png)

i'm not understanding this part

## Assistant

Those two rows are really one idea, and it's the thing that makes the whole scheme work: **you store the shape of a hexagon once, not once per cell.**

### The vertex buffer

A vertex buffer normally holds every point you intend to draw. The naive version would hold 2,000,000 x 6 = 12 million points.

But every hexagon is the *same shape*. They differ only in where they sit. So instead of absolute positions, store the six corners **as offsets from a centre**, for a hexagon of radius 1:

```
corner 0 = ( 1.000,  0.000)   right point
corner 1 = ( 0.500,  0.866)   top right
corner 2 = (-0.500,  0.866)   top left
corner 3 = (-1.000,  0.000)   left point
corner 4 = (-0.500, -0.866)   bottom left
corner 5 = ( 0.500, -0.866)   bottom right
```

```
      2 ________ 1
       /        \
      /          \
   3 (            ) 0     flat-top: flat edges top and bottom,
      \          /        points at left and right
       \________/
      4          5
```

6 corners x 2 floats x 4 bytes = **48 bytes**. That is the entire shape library for two million cells.

The shader then does the placing. `gl_InstanceID` tells it which cell it's drawing, it computes that cell's centre from the column and row, and adds the corner offset. So "draw 2 million hexagons" becomes "draw this one 48-byte shape 2 million times, each at a different centre."

### The index buffer

The hexagon is 4 triangles, and a triangle needs 3 vertices, so the GPU needs **12 vertex references**. But there are only **6 distinct corners**. The fan reuses them:

```
triangle A = 0, 1, 2
triangle B = 0, 2, 3
triangle C = 0, 3, 4
triangle D = 0, 4, 5
```

Which written out is `0,1,2, 0,2,3, 0,3,4, 0,4,5` -- the twelve numbers in that table row. Corner 0 appears in all four triangles; corners 2, 3 and 4 appear in two each.

An index buffer is just that list of *which corner to use*, rather than the corner coordinates themselves. 12 indices x 4 bytes = **48 bytes**. (32-bit by default; with only 6 corners you could use 16-bit and halve it, but at this size it makes no difference.)

Without indices you'd write out the actual coordinates 12 times instead of 6, duplicating corner 0 four times over. That's the small win. The real win is that **the GPU caches transformed vertices**: when the same index comes up again, it reuses the vertex shader's result instead of recomputing it. So those 12 references cost 6 vertex shader runs, not 12 -- a straight 2x saving on the stage that dominates when zoomed out.

### Why both say "never"

Because a hexagon's shape never changes. Zooming doesn't change it -- that's the uniform, applied as a multiply. Panning doesn't change it. Painting a cell changes its *colour*, not its geometry. So those 96 bytes are uploaded once at startup and are still sitting there, untouched, when the program closes.

That's the punchline of the table. The only thing that genuinely varies per cell is one RGBA8 colour, which is why the whole grid is 8 MB.

### The contrast with what you already built

In `HexGrid2000x1000.blend`, every cell really does carry its own six vertices -- 12,000,000 of them, about 144 MB of coordinates, which is most of why that file is 193 MB.

Same grid, same four triangles per cell, same colours. Blender stores it that way because it's a general mesh editor: it has to let you drag any individual vertex, so it cannot assume the cells are identical. A purpose-built renderer *can* assume that, and the assumption is worth roughly 200 MB and a lot of memory bandwidth.

## User

okay, going back to the overall renderer, explain where is the part that associates color with a hex cell

## Assistant

It's one line of setup, and it's the least obvious line in the whole design.

### The short answer

```c
glVertexAttribDivisor(colourAttribute, 1);
```

That's the association. Everything else follows from it.

### Why that line does it

A vertex attribute normally advances **once per vertex**. The divisor changes the rate: with a divisor of 1, the attribute advances **once per instance** instead. So all six vertices of instance 17 read element 17 of the colour buffer, and then it moves on to element 18 for the next hexagon.

```c
glBindBuffer(GL_ARRAY_BUFFER, colourBuffer);
glBufferData(GL_ARRAY_BUFFER, cellCount * 4, colours, GL_DYNAMIC_DRAW);

glEnableVertexAttribArray(1);
glVertexAttribPointer(1, 4, GL_UNSIGNED_BYTE, GL_TRUE, 4, 0);  // 4 bytes -> vec4 in 0..1
glVertexAttribDivisor(1, 1);                                   // <-- one colour per cell
```

The `GL_TRUE` is the hardware normalising 0-255 into 0.0-1.0, so the shader just declares `in vec4 aColor` and gets usable floats with no unpacking.

### The association is positional, not explicit

There is no table mapping cells to colours, and no cell ID stored anywhere. **The array index *is* the cell ID.** Cell `n = row * WIDTH + column` has its colour at byte offset `n * 4`. The same row-major convention the single-mesh `.blend` uses, and the same one `ArtworkCache` already uses for its bitmap.

### One number, used twice

```
                    +-> gl_InstanceID --> column, row --> centre --> gl_Position
cell index n -------+
                    +-> divisor fetch --> aColor ---------------> vColor
```

The shader *computes* where cell `n` is, and the hardware *fetches* what colour cell `n` is, and they agree because both are driven by the same `n`. Nothing explicitly pairs them -- they're bound by sharing an index.

### Why `flat` matters

An output from the vertex shader is normally **interpolated** across the triangle from its three corners. All three corners here belong to the same instance and carry the same colour, so interpolating is wasted work, and barycentric rounding can bleed a sliver of a neighbour at shared edges. `flat` says "don't interpolate, take one vertex's value for the whole triangle."

### Painting a cell

```c
glBufferSubData(GL_ARRAY_BUFFER, cellIndex * 4, 4, &newColour);
```

A brush stroke covers a rectangle of cells, and because the buffer is row-major, that's one contiguous slice per row -- the same shape of update as `ArtworkCache.MarkDirty`.

### In the shader-tiling version it moves, but doesn't change

No instances, so no divisor. The colours live in a **texture**, and the fragment shader does the association itself:

```glsl
ivec2 cell = hexCellAt(worldPosition);        // HexGeometry.FromPoint
vec4 colour = texelFetch(uColours, cell, 0);
```

Still positional, still indexed by column and row, still one RGBA8 per cell.

### What the current code does instead

`ArtworkRenderer.DrawMesh` writes a colour **per vertex, every frame**, into a managed array interleaved with the positions. For 2 million cells that's 24 million colour values per frame, re-sending data that didn't change. The GPU design writes 4 bytes when a cell is painted and nothing at all when merely panning or zooming.

## User

okay, this is diving too deep into the actual shader code. let's take a step back and use pseudocode that i'll understand more quickly.

let's say in c# i have a 2d matrix of colors that represents a pointy-top hex grid where each matrix value is the color for the cell at the x,y index. now, use natural language and pseudocode to describe how that is efficiently rendered.

## Assistant

### The one idea

Sort the data by **how often it changes**, and only touch each part at its own rate:

| Thing                     | Changes        | Where it lives          |
| ------------------------- | -------------- | ----------------------- |
| The shape of a hexagon    | never          | GPU, uploaded once      |
| Which colour each cell is | when you paint | GPU, one small write per edit |
| Zoom and pan              | every frame    | 3 numbers sent per frame |

### The CPU side

```
// ---------- your data ----------
Color[,] cells;              // cells[x, y]
int width, height;

// ---------- once, at startup ----------
shape   = UploadToGpu(SixHexagonCorners());    // ~48 bytes
colours = UploadToGpu(Flatten(cells));         // width*height, 4 bytes each
                                               // flat index = y * width + x

// ---------- when the user paints ----------
void Paint(int x, int y, Color c) {
    cells[x, y] = c;
    colours.Update(index: y * width + x, c);   // 4 bytes, geometry untouched
}

// ---------- every frame ----------
void Draw(float zoom, Point pan) {
    gpu.Set("zoom", zoom);
    gpu.Set("pan", pan);
    gpu.DrawInstanced(shape, count: width * height);
}
```

That is the complete per-frame CPU cost: **three numbers and a draw command.** No loop over cells, no visibility test, no path building.

### What the GPU does with that

```
// Runs width*height*6 times, thousands simultaneously
VertexFunction(int cellIndex, int cornerIndex) {
    int x = cellIndex % width;
    int y = cellIndex / width;

    // Pointy-top: odd rows shift right by half a cell
    Point centre = ( sqrt(3) * radius * (x + 0.5 * (y & 1)),
                     1.5     * radius * y );

    Point world  = centre + shape[cornerIndex] * radius;
    Point screen = world * zoom + pan;

    return (screen, colours[cellIndex]);   // position, and this cell's colour
}

// Hardware now turns those triangles into pixels, then for each pixel:
FragmentFunction(Color colourFromAbove) {
    return colourFromAbove;                // flat fill, nothing to compute
}
```

### Why it's fast

1. **Nothing is re-uploaded.** The colour matrix sits in video memory between frames.
2. **Zoom is 4 bytes.** A multiply inside a function that was going to run anyway.
3. **The per-cell loop runs in parallel**, and the triangle-to-pixel step is dedicated silicon.
4. **Off-screen cells die early**, so zooming in gets cheaper, not more expensive.

Compare to what the editor does now:

```
foreach (cell in visibleCells) {
    path = BuildHexagonPath(centre);     // allocate, every frame
    canvas.Fill(path, cells[x, y]);      // software scanline fill
}
```

### The one case where this still isn't ideal

When cells are smaller than a pixel you're pushing millions of triangles to produce a few million pixels. The fix is to draw **one rectangle covering the screen**:

```
FragmentFunction(Point pixel) {
    Point world = (pixel - pan) / zoom;
    (int x, int y) = WhichHexCell(world);    // closed-form arithmetic
    return colours[x, y];
}
```

Cost becomes "number of pixels on screen", fixed, regardless of grid size or zoom. And `WhichHexCell` already exists: `HexGeometry.FromPoint`.

## User

okay, i'm still not really getting the `gpu.DrawInstanced(shape, count: width * height);` part. where exactly is the code that says "this screen pixel should be this color"

## Assistant

There isn't one. That's the thing that's tripping you up, and it's worth stating plainly:

**In this approach you never write code that iterates pixels.** You describe *shapes*. Deciding which pixels a shape covers is done by a fixed circuit on the GPU that you don't program and can't see.

### The mental flip

You're looking for this:

```
for each pixel on screen:
    figure out what colour it should be
```

But the GPU works the other way round:

```
here are some triangles
-> hardware works out which pixels each one covers
-> for each of those pixels, it calls your function to ask "what colour?"
```

So the line that says "this pixel is this colour" *is* `return colourFromAbove;` in the fragment function. It doesn't look like it because there's no pixel coordinate in it -- by the time the function is called, **which** pixel is already settled. The function only answers **what colour**.

### The loop you're looking for, written out

This is the hardware. You don't write it:

```
// DrawInstanced(shape, count) means: run this.

for i in 0 .. count-1:                          // <-- "count: width * height"
                                                //     i is the cell index
    corners = new Point[6]
    for c in 0..5:
        corners[c] = VertexFunction(i, c)       // <-- YOUR CODE #1

    for each triangle (a,b,c) in the 4-triangle fan:
        for each pixel p on the screen:         // <-- the pixel loop you wanted
            if p falls inside triangle (a,b,c):
                framebuffer[p] = FragmentFunction(colour)   // <-- YOUR CODE #2
```

Your two functions are callbacks into a loop that lives in silicon. `count: width * height` is just **the outer loop bound**.

(Real hardware only visits pixels inside each triangle's bounding box, works on 2x2 pixel blocks, and runs many concurrently. But the *meaning* is exactly that loop.)

### You already depend on this

When the editor calls `canvas.DrawPath(path, paint)`, Skia does the same thing: takes a shape, works out which pixels it covers, fills them. There's a rasteriser in there too. The only difference is **who runs the coverage loop** -- Skia in software on one core, the GPU in hardware across thousands of pixels at once.

### The other approach, which fits your intuition better

Draw **one rectangle covering the whole screen**, so the hardware loop collapses to:

```
for each pixel p on screen:
    framebuffer[p] = FragmentFunction(p)        // YOUR CODE -- and it gets p!
```

```
FragmentFunction(Point p) {
    Point world = (p - pan) / zoom;
    (int x, int y) = WhichHexCell(world);
    return colours[x, y];
}
```

|                        | You describe                | Hardware figures out       |
| ---------------------- | --------------------------- | -------------------------- |
| **Instanced triangles** | the shapes                  | which pixels they cover    |
| **Full-screen quad**    | which cell each pixel is in | nothing -- it calls you per pixel |

## User

this is still not really clicking for me. let's approach it with a different angle.

for the task of rendering a hex grid, the approach that seems the most straightforward to me is to convert that grid into a list of triangles and a list of colors that indicate what color each triangle should be. from what i understand about gpus is gpus are basically good at  drawing arbitrary 3d triangles onto the screen as long as you tell them what color each triangle should be. you can do this be writing a "shader" program, which could take a base color and shift it based on the angle of the triangle to achieve visual shading. or, in this case with the hex grid, the shader program would get a color index associated with the current triangle and then that integer would be mapped to the actual color, and that data would be sent by the cpu to the gpu and stored in vram i think.

so, you can see how my understanding differs a lot from the approach you described. help me understand your approach and correct any issues in my understanding .

## Assistant

The model is **essentially correct**, and jumping to an optimization without establishing that caused the confusion.

### What you have right

- "Convert the grid into a list of triangles and a list of colours" -- yes. That's exactly what the `.blend` is, and it ran smoothly at 8M triangles.
- "GPUs are good at drawing triangles as long as you tell them what colour" -- yes, that is the primitive.
- "A shader program" -- yes.
- "Shift a base colour by the angle to get shading" -- yes, ordinary lighting.
- "A colour index per triangle, mapped through palette data stored in VRAM" -- yes, and genuinely good. Indexed colour suits a pixel-art editor: one byte per cell instead of four, and a palette swap recolours the whole canvas by rewriting 256 values.

### Three corrections

**1. There are two shader stages, not one.**

- **Vertex shader** -- runs once per *vertex*. Decides **where** a point lands on screen.
- **Fragment shader** -- runs once per *pixel covered*. Decides **what colour** that pixel is.

Between them sits the rasteriser, which is fixed hardware.

**2. There is no "per-triangle" input slot.** The GPU only has per-vertex and per-pixel. So "a colour index per triangle" has to be smuggled in: put the index on all three of its vertices and mark it `flat`.

**3. No 3D here.** The grid is flat, nothing overlaps, so no depth buffer and no perspective.

### Your approach, written out properly

```
// --- CPU, once ---
Vertex[] vertices = new Vertex[cellCount * 6];   // { position, colourIndex }
uint[]   indices  = new uint[cellCount * 12];    // the 4-triangle fan per cell

for each cell (x, y):
    centre = HexCentre(x, y)
    for k in 0..5:
        vertices[cell*6 + k] = { centre + Corner[k], colourIndex[x, y] }
    indices[cell*12 .. +12] = fanPattern + cell*6

Upload(vertices); Upload(indices); Upload(palette)

// --- every frame ---
Draw(vertices, indices)        // 8 million triangles
```

```
VertexShader(Vertex v):
    output.position    = v.position * zoom + pan
    output.colourIndex = v.colourIndex         // flat: don't interpolate

FragmentShader(flat colourIndex):
    return palette[colourIndex]                // <- "this pixel is this colour"
```

Complete, correct, and it will run at 60 fps.

### The one observation that turns it into my version

```
vertices[cell*6 + k].position = HexCentre(x, y) + Corner[k]
```

Every one of those 12 million positions is **computed from two loop counters**. None of it is arbitrary data. So don't store it -- hand the GPU the two counters and let the vertex shader run that same line.

```
Corner[0..5]            // 48 bytes, shared by every cell
colourIndex[0..N-1]     // 1 byte per cell -> 2 MB

VertexShader(int cellIndex, int k):
    x = cellIndex % width
    y = cellIndex / width
    position = HexCentre(x, y) + Corner[k]        // the same line as above
    output.position    = position * zoom + pan
    output.colourIndex = colourIndex[cellIndex]
```

**The vertex shader runs the same loop body you were writing on the CPU to build the array -- it just runs it fresh each frame on the GPU instead of once into a giant buffer.**

### Side by side

|                               | Your version              | With instancing      |
| ----------------------------- | ------------------------- | -------------------- |
| Positions in VRAM             | 12M vertices, ~250 MB     | 6 corners, 48 bytes  |
| Colour in VRAM                | per vertex, duplicated 6x | per cell, 2 MB       |
| Where positions come from     | a CPU loop, once          | a GPU loop, per frame |
| Rebuild cost if grid resizes  | re-upload 250 MB          | change one number    |
| Triangles drawn per frame     | 8M                        | 8M                   |
| Pixels on screen              | 3.7M                      | 3.7M                 |

The right-hand column is not faster at drawing. It draws exactly the same thing. It just stops storing a quarter of a gigabyte of arithmetic.

### Suggestion

Build your version first. It's the one you already understand and have validated in Blender, and 250 MB of VRAM is not a problem. Instancing is a later refactor: delete the array-building loop, move those four lines into the vertex shader.

## User

is this conversation being recorded as per the agents.md

## Assistant

Partly, and it had fallen behind -- caught up in this turn. Details in the reply.

## User

i updated agents.md. reread it.

getting back to the original conversation:
i am somewhat familiar with fragment shaders because of this gdshader i wrote: "C:\Users\k\Repository\SimplePaintShaders". that's really the only hands-on shader experience i have.
i'm not really familiar with vertex shaders, because for the simple paint shader, i just wrote the part that received the face normal and computed what color a pixel should be.
so, explain vertex shaders

## Assistant

### You have already been using one

In `K01.gdshader` you start with:

```glsl
vec3 normal = normalize(NORMAL);
```

Where did `NORMAL` come from? Your mesh doesn't store a normal per pixel -- it stores one per **vertex**. The monkey head has maybe 500 of them, and it covers maybe 200,000 pixels on screen. Something had to turn 500 values into 200,000 values, transform them into view space, and hand each pixel its own.

That something is a vertex shader. Godot wrote it for you, which is why you never had to think about it.

You also have one written out explicitly, in `SchlickVertex.gdshader`.

### The division of labour

|              | Runs                  | Times, on your monkey | Answers                          |
| ------------ | --------------------- | --------------------: | -------------------------------- |
| `vertex()`   | once per vertex       |                  ~500 | where does this point land on screen |
| `fragment()` | once per covered pixel |              ~200,000 | what colour is this pixel        |

That 400:1 ratio is the entire reason the pipeline is split in two. Anything you can compute per vertex instead of per pixel is 400 times cheaper -- as long as you can tolerate the values being blended in between.

### The handoff is interpolation

A `varying` is the pipe between the two stages. The vertex shader writes it, the rasteriser blends it, the fragment shader reads it.

The blending is weighted by how close the pixel is to each of the triangle's three corners -- barycentric interpolation. A pixel dead centre gets roughly a third from each corner; a pixel hugging one corner gets almost all of its value from that corner.

This is why `NORMAL` varies smoothly across a face even though it's only stored at the corners, and it's why a low-poly sphere can look round instead of faceted. It is also the thing that `flat` turns **off** -- the qualifier from the hex grid discussion. `flat` says "don't blend, every pixel in this triangle gets the same value."

### Godot hides the hard part

In raw OpenGL, a vertex shader is mandatory and its one required job is to write `gl_Position` -- you do the model, view and projection matrix multiplies yourself.

Godot's `vertex()` is not the whole stage. It's a hook in the middle of a vertex shader Godot already wrote. You get `VERTEX` and `NORMAL` in model space, you adjust them if you want, and Godot applies `MODELVIEW_MATRIX` and `PROJECTION_MATRIX` afterwards. Omit `vertex()` entirely and it still runs; you just didn't intervene.

That's why your fragment-only shaders work: the stage was always there.

### Read your own file

`SchlickVertex.gdshader` is the canonical example, and it's yours:

```glsl
varying vec3 view_light_dir;          // the pipe

void vertex() {
    view_light_dir = (VIEW_MATRIX * vec4(WORLD_LIGHT_DIR, 0.0)).xyz;
}

void fragment() {
    vec3 light = normalize(view_light_dir);
    float facingRatio = clamp(dot(normalize(NORMAL), light), 0.0, 1.0);
    ...
}
```

Compare it to `GrayScale.gdshader`, which does the matrix multiply in the fragment stage:

```glsl
vec3 world_normal = normalize((INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz);
```

Same idea, 200,000 matrix multiplies instead of 500. That's the optimization the comment in `SchlickVertex` is describing, and it's a real one.

One honest caveat, since you're learning from it: `view_light_dir` comes out **identical at every vertex**, because `VIEW_MATRIX` and `WORLD_LIGHT_DIR` are both constant for the whole draw. So the interpolation does nothing, and the value could just as well be a `uniform` computed once in GDScript -- 1 multiply rather than 500. The saving over `GrayScale` is genuine; the vertex stage just isn't the cheapest place that particular value could live. It's a fine illustration of the mechanism regardless.

### Something to run that makes it obvious

```glsl
shader_type spatial;
render_mode unshaded;

uniform float _AMPLITUDE = 0.1;
uniform float _FREQUENCY = 4.0;

varying float wave;

void vertex() {
    wave = sin(VERTEX.x * _FREQUENCY + TIME);
    VERTEX.y += wave * _AMPLITUDE;          // actually moves the geometry
}

void fragment() {
    ALBEDO = mix(vec3(0.1, 0.2, 0.8), vec3(1.0), wave * 0.5 + 0.5);
}
```

Two things worth noticing. It **moves vertices**, which a fragment shader fundamentally cannot do -- by the time the fragment stage runs, the shape is already decided and rasterised. And it passes `wave` down the pipe, so you can see the interpolation: the colour bands are smooth even though `wave` was only computed at the corners.

Typical uses are all variations on that: wind on foliage, waves on water, skinning a character to its skeleton, billboarding a sprite to face the camera, flattening geometry for a 2D view.

### Why the hex grid one looked alien

This is probably the real source of the friction.

Almost every vertex shader you'll encounter **transforms geometry that already exists** -- a mesh was loaded, its vertices are sitting in a buffer, and the shader moves them around. That matches your model of a shader as something that receives data and processes it, which is exactly what your fragment shaders do.

The hex grid vertex shader does something different: it **invents** the geometry. There is no mesh. It gets handed two loop counters and computes positions from arithmetic:

```glsl
int column = gl_InstanceID % uWidth;
int row    = gl_InstanceID / uWidth;
vec2 centre = /* computed from column and row */;
```

That's a legal but unusual thing for a vertex shader to do, and nothing in your Godot experience would have prepared you for it. Both are ordinary vertex shaders; one processes input, the other generates it.

## User

okay, one thing this brought up that i want to clarify is how "smooth shading" works. let's say you have a fairly low resolution uv sphere in blender. there's an obvious visual difference between smooth shading off and on. how does "smooth shading" work?

## Assistant

**The geometry doesn't change at all.** Same vertices, same faces, same triangle count. The only thing that changes is which **normals** the renderer uses.

### The two cases

Take a cross-section of a sphere approximated by a few flat segments:

```
  flat shading                          smooth shading

      ^     ^     ^                        \    ^    /
      |     |     |                         \   |   /
   ---+-----+-----+---                   ----o---o---o----
      A     B     C                         v1  v2  v3

  one normal per FACE,                  one normal per VERTEX, each the
  constant across the whole face        average of the faces either side,
                                        then blended across each face
```

**Flat**: every pixel of face B uses face B's normal -- a single direction perpendicular to that triangle. Same normal everywhere means same lighting everywhere, so the face comes out one uniform tone and you see it as a distinct facet.

**Smooth**: each vertex gets a normal that is the **average of the normals of every face touching it** (usually weighted by the angle each face subtends there, so the result doesn't change when you re-tessellate). Those per-vertex normals are then interpolated across the triangle -- the exact `varying` mechanism from the last message. Every pixel gets a slightly different normal, so the lighting varies continuously and the facet boundary disappears.

### It is a deliberate lie

This is the part worth internalising: in the middle of a flat triangle, the interpolated normal points somewhere **the actual surface does not face**. The geometry is flat there; the shading claims it's curved.

That's the whole trick. Smooth shading doesn't make the model rounder, it makes the model *lie about its orientation* in a way that happens to match the rounder surface it was approximating.

### Through the pipeline

```glsl
varying vec3 v_normal;

void vertex() {
    v_normal = NORMAL;          // the averaged per-vertex normal
}
                                // <- rasteriser blends it across the triangle

void fragment() {
    vec3 n = normalize(v_normal);   // each pixel gets its own direction
}
```

The averaging itself happens at mesh level, not in the shader. Toggling Shade Smooth in Blender changes what normals are stored and handed to the GPU. The shader code is identical either way -- which is why your Simple Paint Shader never had to know about it.

### Why you wrote `normalize(NORMAL)`

In `K01.gdshader` you open with `vec3 normal = normalize(NORMAL);`, and that line is load-bearing for a reason you may not have had in mind.

Interpolating between two unit vectors gives you a point on the **chord** between them, not on the arc. The result is shorter than 1 -- most so halfway between two very different normals. Without renormalizing, your `facingRatio = clamp(normal.z, 0, 1)` would read slightly too low across the middle of every face, and you'd get faint dark banding exactly where the facets used to be.

So you already did the right thing; this is why.

### The silhouette gives it away

Interpolating normals cannot change the outline. A 12-segment UV sphere still has a 12-sided silhouette no matter how smooth the interior shading looks. That's the classic tell, and it's why the fix for a visibly polygonal edge is more geometry (or subdivision), never smooth shading.

### Sharp edges, and why a cube has 24 vertices

If averaging happened everywhere, a cube would come out looking like a soft blob -- each corner's normal averaged from three perpendicular faces, pointing diagonally outward.

So renderers allow a vertex to carry **more than one normal**, by splitting it. Blender does this via sharp edges, an auto-smooth angle threshold (faces meeting above N degrees don't average), or explicit custom split normals.

The consequence is that a "vertex" to the modeller and a "vertex" to the GPU are different things. A cube has **8** positions but **24** GPU vertices: each corner appears three times, once per face, each copy carrying a different normal. The GPU has no concept of "one position with three normals" -- if any attribute differs, it's a separate vertex.

Which is the same rule that came up with the hex grid, where cells don't share corner vertices with their neighbours: two cells of different colours meeting at a point need their own copies of that point, because the attributes differ.

### Gouraud vs Phong

Worth knowing because the names still get used:

| | What is interpolated | Cost | Problem |
| ---------- | -------------------------------- | ---------- | ------------------------------------------ |
| **Flat**   | nothing, one normal per face     | cheapest   | visible facets                             |
| **Gouraud**| the computed *colour* per vertex | cheap      | specular highlights vanish between vertices |
| **Phong**  | the *normal*, lit per pixel      | more       | none of consequence                        |

Modern GPUs and Blender both do the Phong variety: interpolate the normal, run the lighting in the fragment stage. Gouraud is what the vertex stage would do if you computed `ALBEDO` in `vertex()` and passed the colour down -- which is occasionally a worthwhile optimisation, and exactly the tradeoff `SchlickVertex.gdshader` is playing with.

### Your shader depends on this

`facingRatio = clamp(normal.z, 0.0, 1.0)` is "how directly does this point face the camera". With flat shading, every pixel on a face shares one `normal.z`, so your Schlick ramp produces **one flat tone per facet** -- the monkey would come out as a mosaic of solid patches.

The smooth gradients in your README images exist because the normals are interpolated. Your whole look is built on top of smooth shading; it's just that Blender did that part before your shader ever ran.

## User

okay, let's rewind and get back to the task at hand, which is improving the perf of hexel editor and ensuring it produces the visuals that i want.

re-answer this question:
let's say i have a hex grid with a color for each hex cell and 4 triangles for each hex cell. there is also 32 bit float that basically controls the "zoom level". what is the simplest way this is performantly rendered to a 2560x1440 screen?

## Assistant

Your original instinct was right, so here it is your way: a list of triangles and a list of colours, uploaded once, drawn with one call per frame.

### Build it once

Two buffers, both written at startup and then left alone.

**Positions.** For each cell, six corners in world space:

```
for each cell (x, y):
    centre = HexCentre(x, y)
    for k in 0..5:
        positions[cell*6 + k] = centre + Corner[k]
```

**Colours.** Six entries per cell, all the same value -- one per vertex, because the GPU has no per-triangle slot:

```
for each cell (x, y):
    for k in 0..5:
        colours[cell*6 + k] = cells[x, y]
```

Plus an index buffer with the fan, `0,1,2, 0,2,3, 0,3,4, 0,4,5` offset by `cell*6`.

Keep positions and colours in **separate** buffers rather than interleaved. It costs nothing and it means a cell's six colours are 24 contiguous bytes, so painting is one small write instead of six scattered ones.

|                 | 500 x 250 | 2000 x 1000 |
| --------------- | --------: | ----------: |
| Cells           |   125,000 |   2,000,000 |
| Vertices        |   750,000 |  12,000,000 |
| Triangles       |   500,000 |   8,000,000 |
| Positions (8 B) |      6 MB |       96 MB |
| Colours (4 B)   |      3 MB |       48 MB |
| Indices (4 B)   |      6 MB |       96 MB |
| **Total VRAM**  | **15 MB** |  **240 MB** |

Both are unremarkable for a modern card. You already proved the large one works -- that is what your `.blend` is.

### Per frame

```
SetUniform("uScale",  zoom-derived scale);
SetUniform("uOffset", pan);
DrawElements(TRIANGLES, indexCount);
```

That is the whole per-frame CPU cost: about 16 bytes and one call, whether the canvas is 500 x 250 or 2000 x 1000, and whether you are showing nine cells or two million.

### The two shaders

```glsl
// VERTEX -- runs once per vertex
uniform vec2 uScale;      // your zoom float, converted to clip-space units
uniform vec2 uOffset;     // pan

in vec2 aPosition;
in vec4 aColor;

flat out vec4 vColor;     // flat: do not interpolate, each triangle is one colour

void main() {
    gl_Position = vec4(aPosition * uScale + uOffset, 0.0, 1.0);
    vColor = aColor;
}
```

```glsl
// FRAGMENT -- runs once per covered pixel
flat in vec4 vColor;
out vec4 fragColor;

void main() {
    fragColor = vColor;
}
```

One multiply-add in the vertex stage, one assignment in the fragment stage. Your Godot paint shader does strictly more work per pixel than this does.

### Where the zoom float goes

Into `uScale`, and nowhere else. Changing the zoom is an 8-byte write to a uniform. No geometry is rebuilt, nothing is re-uploaded, no CPU loop runs.

That is the entire answer to "why is this fast". The current renderer spends 9 to 133 ms per frame rebuilding paths that did not change; this spends 16 bytes.

### What it costs at 2560 x 1440

- 3.7M pixels, fragment shader of one instruction -- effectively free
- 8M triangles at full zoom-out -- a few ms of triangle setup
- zoomed in, most triangles are clipped before rasterisation -- sub-millisecond

Single-digit milliseconds at the worst, and you have already observed exactly this in Blender.

### The visuals

Turn on **4x MSAA** when you create the framebuffer. One flag.

That is the part that fixes your original complaint. MSAA antialiases every edge at every zoom level, uniformly, in hardware. The three-tier system in `ArtworkRenderer` exists to buy back CPU time and the jaggies are what it costs you; none of that reasoning survives the move to a GPU, so all three tiers collapse into this one path.

Skip the depth buffer -- it is 2D and nothing overlaps -- and skip blending if cells are opaque. Both are pure bandwidth you do not need.

### Painting a cell

The one way this differs from Blender's static mesh. When cell `n` changes colour:

```
colourBuffer.Update(offset: n * 6 * 4, bytes: 24);   // its six vertices
```

A brush stroke is a rectangle of cells, and because cells are laid out row by row, that is one contiguous run per row. The same shape of update as `ArtworkCache.MarkDirty` already does.

Positions are never touched.

### Landing it in HexelEditor

The useful thing I found: **`opentk.glwpfcontrol` is already restored in your repo.** `SkiaSharp.Views.WPF` depends on it transitively, so `GLWpfControl` is available today with no new package reference. It composites as an ordinary WPF element -- no airspace problem, no `WindowsFormsHost`.

So the shape of the change is:

1. Host a `GLWpfControl` where `HexCanvas` currently sits.
2. Build the three buffers when a document is opened or resized.
3. Write the ~15 lines of shader above.
4. On paint, update the colour sub-range.
5. On pan/zoom, write two uniforms.

The overlays -- cursor ring, selection highlight, the hex grid lines -- are a few dozen outlines at most and are not a performance concern wherever you put them.

Two things to settle before committing to it. The validation harness renders offscreen in a hidden `HwndSource` on its own Win32 desktop, and a GL context there needs proving first. And you will want to keep the Skia path as a fallback for machines without a usable GPU.

### One thing deliberately left out

I led with instancing last time and it obscured the idea. Ignore it for now. Once this is working you can notice that every position in that 96 MB buffer is computed from two loop counters, and delete the buffer by moving those lines into the vertex shader. That is a refactor you make later, not a thing you need to understand now.

## User

ignore my level of understanding.

how would you do this:
let's say i have a hex grid with a color for each hex cell and 4 triangles for each hex cell. there is also float32 that basically controls the "zoom level" and a vec2_float32 that basically controls "canvas panning". what is the most performant way to render this to a 2560x1440 screen?

## Assistant

Drop the geometry entirely. The most performant renderer for a regular tiling doesn't rasterise cells -- it evaluates the tiling per pixel.

### Why

|                               | Instanced triangles | Full-screen fragment pass |
| ----------------------------- | ------------------- | ------------------------- |
| Draw calls                    | 1                   | 1                         |
| Vertex invocations @ 2M cells | 12,000,000          | **3**                     |
| Primitives                    | 8,000,000           | **1**                     |
| Fragments                     | 3.7M                | 3.7M                      |
| VRAM                          | ~240 MB             | **~16 MB**                |
| Cost at full zoom-out         | 2-5 ms              | **~0.5 ms**               |
| Cost at full zoom-in          | <1 ms               | ~0.3 ms                   |
| Cost dependency               | cell count x zoom   | **nothing**               |

The last row is the real argument. Geometry cost swings with zoom and canvas size; this is a flat per-frame cost that never changes.

### Setup

- **One texture**, RGBA8, `2*W x H`, mipmapped. Two texels per cell horizontally so the half-cell row offset lands on exactly one texel -- the same trick `ArtworkCache` already uses. 2000x1000 -> 16 MB + 5 MB of mips.
- **Three uniforms**: `invScale`, `pan`, `cellPixels`.
- **No vertex buffer, no index buffer.** Draw 3 vertices and synthesise a full-screen triangle from `gl_VertexID`. One oversized triangle, not a quad -- a quad's shared diagonal gets rasterised twice in 2x2 quads along its length.

```glsl
void main() {                                  // vertex
    vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
    gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}
```

### The fragment shader

```glsl
layout(binding = 0) uniform sampler2D uColours;   // 2W x H, mipmapped
uniform vec2  uInvScale;      // world units per pixel (1/zoom)
uniform vec2  uPan;
uniform vec2  uGrid;          // W, H in cells
uniform float uCellPixels;    // sqrt(3) * radius * zoom

out vec4 fragColor;
const float SQRT3 = 1.7320508;

ivec2 cellAt(vec2 w) {                         // pointy-top, radius 1
    vec2 ax = vec2(SQRT3/3.0 * w.x - w.y/3.0, 2.0/3.0 * w.y);
    float x = ax.x, z = ax.y, y = -x - z;
    float rx = round(x), ry = round(y), rz = round(z);
    vec3  d  = abs(vec3(rx-x, ry-y, rz-z));
    if      (d.x > d.y && d.x > d.z) rx = -ry - rz;
    else if (d.y > d.z)              ry = -rx - rz;
    else                             rz = -rx - ry;
    return ivec2(int(rx) + ((int(rz) - (int(rz) & 1)) >> 1), int(rz));  // odd-r offset
}

vec4 fetch(vec2 w) {
    ivec2 c = cellAt(w);
    if (any(lessThan(c, ivec2(0))) || any(greaterThanEqual(c, ivec2(uGrid)))) return vec4(0);
    return texelFetch(uColours, ivec2(c.x * 2 + (c.y & 1), c.y), 0);
}

void main() {
    vec2 w = (gl_FragCoord.xy - uPan) * uInvScale;

    if (uCellPixels < 3.0) {
        // Cells are sub-pixel. Skip the hex maths and let the texture unit average.
        fragColor = texture(uColours, w / vec2(SQRT3 * uGrid.x, 1.5 * uGrid.y));
        return;
    }

    // Cells are larger than a pixel. Rotated-grid 2x2 supersample of the exact tiling.
    vec2 d = uInvScale * 0.25;
    fragColor = 0.25 * ( fetch(w + vec2( d.x,  3.0*d.y))
                       + fetch(w + vec2(-3.0*d.x,  d.y))
                       + fetch(w + vec2( 3.0*d.x, -d.y))
                       + fetch(w + vec2(-d.x, -3.0*d.y)) );
}
```

### The three things that make it fast

**The branch is uniform.** `uCellPixels` is the same for every pixel in the frame, so the whole draw takes one side. Zero warp divergence.

**Zoomed out, the hex maths disappears.** Below ~3 px/cell the tiling is sub-pixel, so there is nothing to resolve. One filtered texture fetch. This is the case that kills the geometry approach and it's the cheapest case here.

**Zoomed in, 4 fetches hit the same cache lines.** Neighbouring pixels sample neighbouring texels.

### Antialiasing

**MSAA does nothing here** -- there's one triangle, and MSAA only antialiases geometric edges. Every hex edge is procedural. You must resolve it in the shader.

Rotated-grid 2x2 supersampling is the right call: exact, branchless, 4x the ALU on a shader that's ALU-trivial anyway. An analytic hexagon SDF is fewer instructions but needs the neighbouring cell's colour to blend against, which costs the fetch you just saved.

Blend the two branches over `cellPixels` in [2.5, 3.5] rather than switching hard, or you'll see a pop.

### Painting, and the one trap

Updating a cell is a 2-texel write. The trap is **mip regeneration**: a full chain rebuild on a 16 MB texture is ~0.5 ms, and doing that per brush dab would cost more than everything else combined.

Mips are only read in the zoomed-out branch, so mark them stale on edit and rebuild lazily -- on stroke end, or the first frame `cellPixels` drops below the threshold. While zoomed in they're never sampled, so staleness is invisible.

### Two things to not do

**Don't store palette indices in the sampled texture.** Averaging indices is meaningless, so the minified branch breaks. Keep RGBA8.

**Don't reach for a compute shader.** A full-screen fragment pass already gets the texture cache and optimal tile scheduling.

### And the free one

None of this runs if nothing changed. An idle editor should render zero frames.

### Expected

~0.3-0.6 ms per frame at 2560x1440, flat across every zoom level and canvas size, against the 9-133 ms the current CPU renderer measures on a 2000x1000 canvas.

## User

![The comparison table, with the vertex invocations row circled](Images/2026-10-24_21-03_UTC%20ZoomRenderingTiersAndBlenderHexGridScript%209.png)

explain this

## Assistant

### What the number counts

The vertex shader runs **once per vertex, every frame**. That row is how many times it runs to draw one frame.

### Why 12,000,000

In the geometry approach you are drawing actual hexagons. Each hexagon is 6 corners:

```
2,000,000 cells x 6 corners = 12,000,000 vertices
```

Every frame, the GPU runs the vertex shader 12 million times -- once for each corner of each hexagon -- to work out where that corner lands on screen. Pan by one pixel and it does all 12 million again.

(The index buffer is already accounted for. Each cell's 12 fan indices reference only 6 distinct corners, and the post-transform cache means 6 shader runs rather than 12.)

### Why 3

Because in the second approach **there are no hexagons**. Not as geometry. The GPU never hears about them.

You draw exactly one triangle, sized so it covers the entire screen:

```
 y=3  +
      |\
      | \
      |  \
 y=1  +---+\         <- the hypotenuse passes exactly through (1,1)
      |SCR|  \
      |EEN|    \
 y=-1 +---+------+
     x=-1 x=1   x=3
```

Clip space runs from -1 to 1 in both axes, so the screen is that inner box. A triangle at `(-1,-1)`, `(3,-1)`, `(-1,3)` contains it completely, and the GPU clips the overhang for free.

A triangle has 3 corners. The vertex shader runs 3 times. That's the whole frame -- for a 50x25 grid or a 20000x10000 one, it's 3.

### Where the hexagons went

Into arithmetic. The `cellAt()` function in the fragment shader takes a pixel's world position and computes which hexagon it falls inside, directly:

```glsl
vec2 ax = vec2(SQRT3/3.0 * w.x - w.y/3.0, 2.0/3.0 * w.y);   // world -> axial
... cube-round to the nearest cell ...
```

A hex grid is perfectly regular, so "which cell is this point in" is a closed-form calculation, not a search. There's no reason to store two million hexagons when you can compute the answer in about twenty instructions.

### Why this is the row that matters

Look at the **Fragments** row: 3.7M on both sides. Identical. Both approaches shade exactly the same pixels, because the screen is the same size either way.

So every bit of the saving comes from deleting the geometry front-end -- the 12M vertex invocations and the 8M primitive setups. That front-end produces nothing the fragment pass can't compute; it's pure overhead in service of describing a shape the GPU could have derived.

### The trade you're making

It isn't free. You swap:

| | Geometry | Fragment pass |
| ----------------- | ----------------------------- | -------------------------------- |
| Front-end cost    | 12M vertices, 8M primitives   | 3 vertices, 1 primitive          |
| Per-pixel cost    | ~0 (just interpolate a colour) | ~30 ALU + a texture fetch        |
| Scales with       | **cell count**                | **screen resolution**            |

You are trading work proportional to **cell count** for work proportional to **screen size**.

That's the whole argument, because your screen is 2560x1440 and always will be, while your canvas can be 2 million cells or 200 million. One of those numbers is bounded and the other isn't.

## User

okay, this approach sounds fine.

do this task: change hexel editor so that it looks visually like the blender experiment we did at all zoom levels, and runs performantly.

the approach you described sounds good, but if you need to make adjustments feel free to do so. your task is to achieve the described task as best as possible, not to stick rigidly to any specific rigid predefined implementation plan.

also, implement a check box toggle to turn anti-aliasing on/off.

## Assistant

Done, validated, and committed in `ea5cb45` and `bee77bb`. The approach survived mostly intact, but
two measurements changed it and I broke the machine's rules once on the way.

### What was measured first

An SkSL runtime shader on Skia's **CPU** backend: 105 ms a frame, flat at every zoom. Constant, as
predicted, and far too slow. That ruled out keeping everything on the CPU and settled that the GPU
was genuinely required rather than merely nicer.

An offscreen GL context in a hidden window, which the whole plan depended on: works. Draw 0.04 ms,
draw plus reading the pixels back 1.3 ms.

That second number decided the architecture. `SKElement` hands WPF a bitmap, and the verification
harness captures the window with `RenderTargetBitmap`, which cannot see GPU content at all -- a
`GLWpfControl` would have come back blank and taken the whole validation discipline with it.
Reading the pixels back into a bitmap keeps one rendering path for the real window and for the
headless checks, and 1.3 ms is affordable where 133 ms was not.

### What was built

`GpuHexRenderer` draws one rectangle over the window with a shader that runs
`HexGeometry.FromPoint` per pixel and looks the colour up. The colour texture is `ArtworkCache`'s
existing raster, which already holds composited colour at two texels a cell; it gained a `Version`
counter so the texture is only re-uploaded when a cell actually changed. The grid is computed from
the distance to the nearest cell edge, exact at any zoom. Antialiasing is a rotated-grid four tap
supersample, because MSAA cannot help when there is one triangle and every cell boundary is
procedural.

`ArtworkRenderer` stays as the fallback, reached automatically when no context can be created and
on demand with `HEXEL_DISABLE_GPU=1`.

### Measured after, grid on, 2000 x 1000 canvas at 2456 x 1270

| Zoom | Cell px | GPU     | CPU tiered |
| ---: | ------: | ------: | ---------: |
| 2.00 |    41.6 | 5.78 ms |    17.2 ms |
| 1.00 |    20.8 | 3.96 ms |    42.3 ms |
| 0.75 |    15.6 | 4.64 ms |    64.2 ms |
| 0.65 |    13.5 | 3.93 ms |    48.3 ms |
| 0.40 |     8.3 | 4.10 ms |    91.2 ms |
| 0.25 |     5.2 | 5.54 ms |    70.6 ms |
| 0.15 |     3.1 | 4.00 ms |   151.9 ms |
| 0.08 |     1.7 | 5.18 ms |    29.3 ms |

Flat 4 to 6 ms everywhere, inside a 60 fps budget at every zoom. Hexagons are hexagons at 25%,
where the band tier used to draw rectangles, and smooth at 65%, where the mesh tier had no
antialiasing. With the toggle off the edges are hard and stair-stepped, as intended.

### Three failures on the way, one of them mine against the rules

**A message box reached the primary display.** `IsChecked="True"` on the new checkbox raises
`Checked` while `InitializeComponent` is still running, before `EditorCanvas` exists. The
`NullReferenceException` surfaced as a WPF error dialog on a user display, which is exactly what
the validation rules forbid. Fixed by setting the initial state from the constructor and guarding
the handler.

**The window painted nothing at all.** With the GL context created on WPF's UI thread the real
window came back blank -- not the canvas, the whole window including the toolbar -- while the
headless checks kept passing. Moved all GL work onto a thread of its own.

**A fix that was not a fix.** Suspecting WPF's Direct3D compositing, I set
`RenderOptions.ProcessRenderMode = SoftwareOnly`. Still blank. Disabling the GPU renderer entirely
was *also* still blank, which proved the GPU was never the cause of that particular failure:
software compositing breaks `PrintWindow`, which is how the display pass captures. Reverted, and
everything passed.

Worth recording that the second and third overlapped, so the thread move was never cleanly
isolated. It is the right design regardless, and three consecutive validation runs pass with it.

### Validation

`pwsh -File ./Scripts/Validate.ps1`, three consecutive clean runs:

```
[  ok  ] Build, tests and hidden-host verification
[  ok  ] Display pass on NE18NZ2 - 3840 x 2400, 170 distinct colors
[  ok  ] Did not disturb the machine - none on a user display and none taking focus
```

170 distinct colours against 123 on the CPU path, which is the antialiasing showing up in the
capture. The session log confirms "GPU renderer ready" in both the hidden host and the real window,
so the path being validated is the real one.

## User

Leave them local for now.

## Assistant

Left local. Nothing pushed; the three commits sit on `main`.
