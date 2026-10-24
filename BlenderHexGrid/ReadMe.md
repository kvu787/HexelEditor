# Blender Hex Grid

A Blender script that builds a flat-top hexagon grid where every cell is its own mesh object of
four triangles, coloured from a palette of unlit materials so that no two touching cells share a
colour.

![A 26 by 15 grid rendered with seven colours](GridPreview.png)

## Using it

1. Open Blender 4.5 or newer and switch an area to the Text Editor.
2. Paste in [GenerateHexGrid.py](GenerateHexGrid.py), or open it with **Text > Open**.
3. Edit `WIDTH` and `HEIGHT` near the top.
4. Press **Run Script**.

Progress goes to the system console (**Window > Toggle System Console** on Windows), because a
script run from the Text Editor freezes the interface until it finishes. On a large grid that is
the only sign it is still working.

## Settings

All of them sit in one block at the top of the script.

| Setting         | Default     | What it does                                                            |
| --------------- | ----------- | ----------------------------------------------------------------------- |
| `WIDTH`         | `2000`      | Cells across.                                                            |
| `HEIGHT`        | `1000`      | Cells down.                                                              |
| `CELL_RADIUS`   | `1.0`       | Centre to corner. Cells touch exactly at any radius.                     |
| `COLOR_COUNT`   | `7`         | Palette size. 3 is the minimum that can work; 7 gives the rosette.       |
| `SHARE_MESHES`  | `True`      | Cells of one colour share a mesh datablock.                              |
| `ADD_CAMERA`    | `True`      | Add a top-down orthographic camera framing the grid.                     |
| `PLAIN_COLORS`  | `True`      | Set the scene's view transform to Standard so unlit colours render true. |
| `GRID_NAME`     | `"HexGrid"` | Collection the cells go into, and the prefix for their names.            |
| `PROGRESS_ROWS` | `25`        | Print a progress line every this many rows. `0` turns progress off.      |

## What the grid is

- **Flat-top hexagons**, matching HexelEditor's `HexOrientation.FlatTop`. Corners at 0, 60, 120,
  180, 240 and 300 degrees, so each cell has a flat edge top and bottom and a point left and right.
- **Four triangles per cell**, fanned from corner 0, which is the same decomposition
  `ArtworkRenderer.MeshFan` uses.
- **Flat on the XY plane at Z = 0**, occupying the **+X +Y quadrant only**. The bounding box starts
  exactly at the origin, so cell (0, 0) is the bottom-left one and nothing has a negative coordinate.
- **Unlit materials**, an emission shader each, with the viewport display colour set to match so
  Solid shading shows the palette without going into Material Preview.

Re-running replaces the previous grid rather than stacking a second one on top of it. Nothing
outside the grid's own collection is touched, apart from the view transform when `PLAIN_COLORS`
is on.

## The colouring

Hex centres form a triangular lattice. Taking a cell's axial coordinates `(q, r)`, the colour is

```
(q + 2r) mod COLOR_COUNT
```

Across the six neighbour directions `(1,0) (1,-1) (0,-1) (-1,0) (-1,1) (0,1)` that expression
changes by 1, -1, -2, -1, 1 and 2 respectively. None of those are zero modulo any `k >= 3`, so no
two touching cells can land on the same colour whatever the palette size, and no search or
backtracking is needed. Three is the chromatic number of this lattice, so `COLOR_COUNT` below 3
is rejected rather than attempted.

## Cost

Measured with Blender 4.5.14 on a 24 core desktop, building only -- no saving, no rendering.

| Grid          |     Cells | Time    | Peak memory | Per cell |
| ------------- | --------: | ------- | ----------: | -------: |
| 400 x 200     |    80,000 | 2.8 s   |     0.40 GB |    35 us |
| 1000 x 500    |   500,000 | 20.8 s  |     1.71 GB |    42 us |
| 2000 x 1000   | 2,000,000 | 89.3 s  |     6.19 GB |    45 us |

The cost is Blender's per-object overhead, not the geometry. Four triangles a cell is not what
makes a large grid expensive, and halving that count would not help.

Two million objects build in a minute and a half, but that is only the building. Expect the
viewport to be heavy afterwards, and prefer Solid shading. `SHARE_MESHES` matters here: it is what
lets the viewport draw the grid in `COLOR_COUNT` batches rather than one per cell.

### A trap worth knowing about

Blender keeps each kind of datablock in a list sorted by name. When a new name sorts after
everything already in the list the insert is cheap; when it does not, Blender scans backwards to
find the spot and creating *n* objects becomes O(n squared).

Naming cells `HexGrid_<column>_<row>` without padding does exactly that, because `_10_` sorts
before `_2_`. Measured at 80,000 cells:

| Naming                                     | Per cell  | 80,000 cells |
| ------------------------------------------ | --------: | -----------: |
| `Name_<column>_<row>`, unpadded             | 2085 us   |      167 s   |
| `Name_r<row>_c<column>`, zero-padded        |   31 us   |        2.5 s |

So cells are named `HexGrid_r0000_c0000`, zero-padded, row before column, and built row by row,
which keeps every insert at the end of the list. The test suite locks this in: it recovers each
cell's column and row from where it actually sits and checks that sorting by name reproduces
row-major order.

## Tests

```
pwsh -File ./Test.ps1
```

or double-click `Test.cmd`. Add `-Measure` to also time the generator at increasing sizes, up to
two million cells, which takes several minutes. Output lands in a timestamped folder under
`LogOutput`.

[TestHexGrid.py](TestHexGrid.py) runs the real script text with only its settings constants
rewritten, so what is tested is the file you would paste rather than a copy of its logic. The
adjacency tests deliberately ignore the colouring formula and work from the built scene: they
find each cell's neighbours by measuring distances between object positions and compare the
materials actually found there, so a colouring bug that was wrong in both the generator and a
re-implementation would still be caught.

## Other files

| File                                       | What it is                                                     |
| ------------------------------------------ | -------------------------------------------------------------- |
| [GenerateHexGrid.py](GenerateHexGrid.py)   | The script to paste into Blender. The deliverable.              |
| [TestHexGrid.py](TestHexGrid.py)           | Correctness tests, run inside Blender.                          |
| [MeasureHexGrid.py](MeasureHexGrid.py)     | Times and measures one grid size, driven by environment vars.   |
| [RenderPreview.py](RenderPreview.py)       | Renders the preview image above, with the axes drawn in.        |
| [Test.ps1](Test.ps1)                       | Runs the tests, and the measurements with `-Measure`.           |
