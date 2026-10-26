"""Open a saved hex grid .blend and check it survived the round trip.

    set HEXGRID_OUTPUT=C:\\...\\HexGrid2000x1000.blend
    blender --background --factory-startup --python VerifyDemoFile.py

MakeDemo.ps1 runs this straight after writing the file. Saving and re-opening is where a mesh
built through foreach_set would show any damage, so the file is read back rather than trusted,
and the checks are made against what the geometry actually says rather than against the numbers
that were printed on the way out.
"""

import json
import math
import os
import sys
import time

import bpy
import numpy as np

SQRT3 = math.sqrt(3.0)
TOLERANCE = 1e-3

failures = []


def check(name, condition, detail=""):
    print("  {0}  {1}  {2}".format("PASS" if condition else "FAIL", name, detail).rstrip(), flush=True)
    if not condition:
        failures.append(name)


def main():
    path = os.path.abspath(os.environ["HEXGRID_OUTPUT"])
    width = int(os.environ.get("HEXGRID_WIDTH", "2000"))
    height = int(os.environ.get("HEXGRID_HEIGHT", "1000"))
    colors = int(os.environ.get("HEXGRID_COLORS", "7"))
    cells = width * height

    started = time.perf_counter()
    bpy.ops.wm.open_mainfile(filepath=path)
    opened = time.perf_counter() - started
    print("Opened {0} in {1:.1f}s".format(os.path.basename(path), opened), flush=True)

    grid = bpy.data.objects.get("HexGrid")
    check("the grid object is in the file", grid is not None)
    if grid is None:
        return finish(path, opened)

    mesh = grid.data
    check("every vertex survived", len(mesh.vertices) == cells * 6,
          "{0} of {1}".format(len(mesh.vertices), cells * 6))
    check("every polygon survived", len(mesh.polygons) == cells * 4,
          "{0} of {1}".format(len(mesh.polygons), cells * 4))
    check("the material slots survived", len(mesh.materials) == colors,
          "{0} of {1}".format(len(mesh.materials), colors))
    check("the materials are still unlit",
          all(any(node.type == "EMISSION" for node in material.node_tree.nodes)
              for material in mesh.materials))

    coordinates = np.empty(len(mesh.vertices) * 3, dtype=np.float32)
    mesh.vertices.foreach_get("co", coordinates)
    coordinates = coordinates.reshape(-1, 3)
    check("the grid is still flat on the XY plane",
          float(np.abs(coordinates[:, 2]).max()) < TOLERANCE,
          "worst Z {0:.6f}".format(float(np.abs(coordinates[:, 2]).max())))
    check("the grid still starts at the origin",
          abs(float(coordinates[:, 0].min())) < TOLERANCE and abs(float(coordinates[:, 1].min())) < TOLERANCE,
          "min {0:.5f},{1:.5f}".format(coordinates[:, 0].min(), coordinates[:, 1].min()))
    check("nothing reaches into -X or -Y",
          coordinates[:, 0].min() >= -TOLERANCE and coordinates[:, 1].min() >= -TOLERANCE)

    expected_width = 1.5 * (width - 1) + 2.0
    expected_height = SQRT3 * (height + (0.5 if width > 1 else 0.0))
    check("the extent is what the layout says it should be",
          abs(float(coordinates[:, 0].max()) - expected_width) < TOLERANCE
          and abs(float(coordinates[:, 1].max()) - expected_height) < TOLERANCE,
          "{0:.3f} x {1:.3f} against {2:.3f} x {3:.3f}".format(
              coordinates[:, 0].max(), coordinates[:, 1].max(), expected_width, expected_height))

    indices = np.empty(len(mesh.polygons), dtype=np.int32)
    mesh.polygons.foreach_get("material_index", indices)
    per_cell = indices.reshape(cells, 4)
    check("each cell's four polygons still agree on a colour",
          bool(np.all(per_cell.min(axis=1) == per_cell.max(axis=1))))
    check("every material slot is used", len(set(per_cell[:, 0].tolist())) == colors,
          "{0} used".format(len(set(per_cell[:, 0].tolist()))))

    # Adjacency over the whole grid, computed from the saved coordinates rather than re-derived
    # from the formula: a cell's right-hand neighbour is the next column at the same row, and
    # its upper neighbour is the next row. Checking those two directions for every cell covers
    # every edge in the grid exactly once.
    centres = coordinates.reshape(cells, 6, 3)[:, :, :2].mean(axis=1).reshape(height, width, 2)
    colors_grid = per_cell[:, 0].reshape(height, width)
    column_index = np.arange(width)
    odd = (column_index & 1).astype(bool)

    across = np.abs(np.linalg.norm(centres[:, 1:] - centres[:, :-1], axis=2) - SQRT3)
    check("neighbouring columns sit at the exact pitch", float(across.max()) < TOLERANCE,
          "worst {0:.6f}".format(float(across.max())))
    check("no cell matches the colour of the column beside it",
          bool(np.all(colors_grid[:, 1:] != colors_grid[:, :-1])))

    up = np.abs(np.linalg.norm(centres[1:] - centres[:-1], axis=2) - SQRT3)
    check("neighbouring rows sit at the exact pitch", float(up.max()) < TOLERANCE,
          "worst {0:.6f}".format(float(up.max())))
    check("no cell matches the colour of the row above it",
          bool(np.all(colors_grid[1:] != colors_grid[:-1])))

    # The third pair of directions: an odd column's diagonal neighbour is the next row up in the
    # next column across, and an even column's is the row below.
    diagonal_ok = True
    if width > 1 and height > 1:
        even_pairs = colors_grid[1:, :-1][:, ~odd[:-1]] != colors_grid[:-1, 1:][:, ~odd[:-1]]
        odd_pairs = colors_grid[:-1, :-1][:, odd[:-1]] != colors_grid[1:, 1:][:, odd[:-1]]
        diagonal_ok = bool(np.all(even_pairs)) and bool(np.all(odd_pairs))
    check("no cell matches the colour of its diagonal neighbour", diagonal_ok)

    return finish(path, opened)


def finish(path, opened):
    print("", flush=True)
    print("DEMO_VERIFY " + json.dumps({
        "output": path,
        "openSeconds": round(opened, 2),
        "result": "FAILED" if failures else "PASSED",
        "failures": failures,
    }), flush=True)
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
