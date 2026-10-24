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
