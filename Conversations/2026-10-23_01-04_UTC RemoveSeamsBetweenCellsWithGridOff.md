# Remove Seams Between Cells With Grid Off

## User

the "no grid" option isn't working properly. I still see "lines" between the hex cells when i uncheck grid.

you can see in the screenshot of the render view of "C:\Users\k\Repository\HexelEditor\HexGridExample.blend" that there are no gaps or lines between the hex cells that i drew by hand. this is what HexelEditor should look like when grid is turned off, except with perfectly proportioned regular hexagon cells.

![Hexel Editor with the grid unchecked and lines still showing between cells](Images/2026-10-23_01-04_UTC%20RemoveSeamsBetweenCellsWithGridOff%201.png)

![Blender render view of HexGridExample.blend with no gaps between hand-drawn hex cells](Images/2026-10-23_01-04_UTC%20RemoveSeamsBetweenCellsWithGridOff%202.png)

## User

(sent while the work was in progress)

fyi, when i changed viewport samples to 1 and disabled temporal reprojection in blender, i got some jaggies at the cell boundaries but still no gaps. this is acceptable.

![Blender with viewport samples at 1 and temporal reprojection off, jagged cell boundaries and no gaps](Images/2026-10-23_01-04_UTC%20RemoveSeamsBetweenCellsWithGridOff%203.png)

## User

(sent while the work was in progress)

it seems like copy/pasting screenshots results in some kind of compression, which is unacceptable in this context. you need to see the exact pixels of the screenshots.

here are the file paths to the screenshots:
"C:\Users\k\Pictures\Screenshots\Screenshot 2026-10-22 173340.png"
"C:\Users\k\Pictures\Screenshots\Screenshot 2026-10-22 173610.png"
"C:\Users\k\Pictures\Screenshots\Screenshot 2026-10-22 180050.png"

## Assistant

The lines were not a grid. They were what antialiasing leaves between two shapes that only touch.

Each cell was its own antialiased fill. Along a shared edge, each of the two cells covers roughly half of those pixels, and compositing one half-covered fill over another never reaches full coverage, so part of the paper behind them kept showing through. Every edge in the canvas did this at once, which is why it read as a grid.

Three changes in `ArtworkRenderer.Draw`:

- Cells of one color are collected into a single path and filled once, so a shared edge inside one color is not a boundary at all.
- Every cell reaches three quarters of a pixel past its own edges. Half a pixel is not enough: both sides are still partly covered and about a tenth of the background survives. At three quarters, one side or the other covers each edge pixel whole, whatever angle a hexagon's edges take. The overlap is measured in device pixels from the canvas matrix, so it is the same thickness at every zoom and in the PNG export.
- Cells one can see through would blend twice where they overlap and draw a darker seam of their own. They are filled into a layer where each cell replaces what it covers, which is skipped entirely when every cell is opaque.

The SVG export carries the same overlap for opaque cells and leaves see-through ones exact, since nothing there can hold a layer the way a canvas can.

Desktop verification only sampled cell centers, which a seam never touches. It now samples the midpoint between every pair of opaque neighbors and requires full alpha there. That check failed on the first attempt, at half a pixel of overlap, which is how the three-quarter figure was arrived at.

Measured on the exported example (1125 x 778, 1280 cells), pixels below full alpha away from the artwork's outer edge:

| Overlap                | Interior pixels below full alpha |
| ---------------------- | -------------------------------- |
| None (before)          | 67,645                           |
| Half a pixel           | 4,120                            |
| Three quarters (now)   | 0                                |

In the same 70 x 70 window of flat sky, which is wider than a cell and so always crosses an edge, measured on the original screenshots rather than pasted copies:

| Screenshot                       | Distinct colors in that window |
| -------------------------------- | ------------------------------ |
| Before, `Screenshot ... 173340`  | 96                             |
| After, same artwork and color    | 1                              |

Validated on NE18NZ2 with `Examples/NeonTide.hexel` open: with the grid unchecked, the flat regions of the sky and the bands of the sun are single fields of color with no hairlines, at the 114% fit and zoomed to 236%. With the grid checked, the outlines come back and the hexagons are regular. Rendering the same document at layer opacity 110 showed the darker overlap hairline before the layer change and none after. The exported SVG rendered in a browser with no seams. Build with no warnings, 51 unit tests, and the desktop verification run all pass.

Jaggies were never part of the trade: edges are still antialiased, and the gaps are gone.

Four commits: the renderer fix, the verification check, the README, and this conversation.
