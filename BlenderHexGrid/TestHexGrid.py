"""Correctness tests for GenerateHexGrid.py, run inside Blender.

    blender --background --factory-startup --python TestHexGrid.py

The tests execute the real script text, with only its settings constants rewritten, so what is
checked is the file a person would paste into the Text Editor rather than a copy of its logic.

The adjacency test deliberately ignores the colouring formula and works from the built scene:
it finds each cell's neighbours by measuring distances between object positions, then compares
the materials it actually finds there. A colouring bug that was wrong in both the generator and
a re-implementation would still be caught.
"""

import math
import os
import re
import sys
import time

import bpy

SCRIPT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "GenerateHexGrid.py")
SQRT3 = math.sqrt(3.0)

# Blender keeps object locations as single-precision floats, so a cell out at x = 58 already
# carries about 1e-5 of representation error and a distance between two of them carries more.
# The nearest non-neighbour sits at 3.0 against a neighbour's 1.732, so there is a wide gap to
# aim at: this still pins the pitch to four decimal places without failing on float width.
TOLERANCE = 1e-4

failures = []
passes = []


def check(name, condition, detail=""):
    if condition:
        passes.append(name)
        print("  PASS  {0}".format(name), flush=True)
    else:
        failures.append("{0}: {1}".format(name, detail))
        print("  FAIL  {0}  {1}".format(name, detail), flush=True)


def run_generator(**settings):
    """Execute the real script with its settings constants rewritten."""
    with open(SCRIPT, "r", encoding="utf-8") as stream:
        source = stream.read()
    for key, value in settings.items():
        pattern = r"^{0} = [^\n#]+".format(key)
        replacement = "{0} = {1!r}".format(key, value)
        source, count = re.subn(pattern, replacement, source, count=1, flags=re.MULTILINE)
        if count != 1:
            raise AssertionError("could not find a setting named {0} in {1}".format(key, SCRIPT))
    namespace = {"__name__": "__main__", "__file__": SCRIPT}
    exec(compile(source, SCRIPT, "exec"), namespace)
    return namespace


def reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def material_of(cell):
    return cell.data.materials[0].name if cell.data.materials else None


class CellIndex:
    """Groups cell positions into buckets so neighbours can be found without an N-squared sweep.

    Buckets are deliberately wider than the neighbour distance. Sized exactly at the spacing, a
    cell whose coordinate lands on a bucket boundary -- which every cell in an even column does,
    since its y is an exact multiple of the spacing -- falls on whichever side single-precision
    error happens to pick, and then misses every neighbour on one side.
    """

    def __init__(self, cells, radius):
        self.spacing = SQRT3 * radius
        self.bucket = self.spacing * 1.05
        self.buckets = {}
        for cell in cells:
            self.buckets.setdefault(self.key(cell), []).append(cell)

    def key(self, cell):
        return (int(math.floor(cell.location.x / self.bucket)),
                int(math.floor(cell.location.y / self.bucket)))

    def around(self, cell):
        """Yields (other, distance) for every cell in the nine buckets around this one."""
        column, row = self.key(cell)
        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                for other in self.buckets.get((column + dx, row + dy), ()):
                    if other is not cell:
                        yield other, (other.location - cell.location).length

    def touching(self, cell):
        """The cells sharing an edge with this one, found by distance rather than by index."""
        return [other for other, distance in self.around(cell)
                if abs(distance - self.spacing) < TOLERANCE]


def test_geometry():
    print("Mesh geometry", flush=True)
    reset()
    run_generator(WIDTH=12, HEIGHT=8, COLOR_COUNT=7, ADD_CAMERA=False, PROGRESS_ROWS=0)
    grid = bpy.data.collections["HexGrid"]
    cells = list(grid.objects)
    check("cell count is width times height", len(cells) == 96, "got {0}".format(len(cells)))

    radius = 1.0
    expected_area = 3 * SQRT3 / 2 * radius * radius
    bad_vertices = bad_faces = bad_area = 0
    for cell in cells:
        mesh = cell.data
        if len(mesh.vertices) != 6:
            bad_vertices += 1
        polygons = list(mesh.polygons)
        if len(polygons) != 4 or any(len(polygon.vertices) != 3 for polygon in polygons):
            bad_faces += 1
        # Four triangles that tile the hexagon exactly sum to its area. Overlapping or missing
        # triangles would not, so this catches a wrong fan as well as a wrong count.
        if abs(sum(polygon.area for polygon in polygons) - expected_area) > 1e-6:
            bad_area += 1
    check("every cell mesh has 6 vertices", bad_vertices == 0, "{0} wrong".format(bad_vertices))
    check("every cell mesh has 4 triangles", bad_faces == 0, "{0} wrong".format(bad_faces))
    check("the 4 triangles tile the hexagon exactly", bad_area == 0, "{0} wrong".format(bad_area))

    # Flat-top means corners at 0 and 180 degrees, so the silhouette is wider than it is tall.
    mesh = cells[0].data
    xs = [vertex.co.x for vertex in mesh.vertices]
    ys = [vertex.co.y for vertex in mesh.vertices]
    check("cells are flat-top", abs((max(xs) - min(xs)) - 2.0) < 1e-6 and abs((max(ys) - min(ys)) - SQRT3) < 1e-6,
          "width {0:.4f} height {1:.4f}".format(max(xs) - min(xs), max(ys) - min(ys)))
    check("cells are flat in Z", all(abs(vertex.co.z) < 1e-9 for vertex in mesh.vertices))


def test_quadrant():
    print("Position in the world", flush=True)
    reset()
    namespace = run_generator(WIDTH=25, HEIGHT=14, COLOR_COUNT=7, ADD_CAMERA=False, PROGRESS_ROWS=0)
    cells = list(bpy.data.collections["HexGrid"].objects)

    # Every corner of every cell, in world space. Cells carry no rotation or scale, so a vertex
    # lands at the object's location plus its local offset.
    xs, ys, zs = [], [], []
    for cell in cells:
        for vertex in cell.data.vertices:
            xs.append(cell.location.x + vertex.co.x)
            ys.append(cell.location.y + vertex.co.y)
            zs.append(cell.location.z + vertex.co.z)

    check("the grid is flat on the XY plane", max(abs(z) for z in zs) < TOLERANCE,
          "worst Z {0:.6f}".format(max(abs(z) for z in zs)))
    check("no geometry reaches into -X or -Y", min(xs) >= -TOLERANCE and min(ys) >= -TOLERANCE,
          "min x {0:.6f} min y {1:.6f}".format(min(xs), min(ys)))
    # Starting the box exactly at the origin rather than merely inside the quadrant is what makes
    # the grid line up with anything else placed at the origin.
    check("the bounding box starts at the origin", abs(min(xs)) < TOLERANCE and abs(min(ys)) < TOLERANCE,
          "min x {0:.6f} min y {1:.6f}".format(min(xs), min(ys)))

    expected_width, expected_height = namespace["grid_extent"](25, 14, 1.0)
    check("the bounding box matches the reported extent",
          abs(max(xs) - expected_width) < TOLERANCE and abs(max(ys) - expected_height) < TOLERANCE,
          "{0:.4f} x {1:.4f} against {2:.4f} x {3:.4f}".format(
              max(xs), max(ys), expected_width, expected_height))

    # The first cell by name has to be the bottom-left one, or the grid is mirrored against the
    # editor's own idea of where cell (0, 0) lives.
    origin_cell = min(cells, key=lambda cell: cell.name)
    check("the first cell by name is the bottom-left cell",
          all(origin_cell.location.x <= cell.location.x + TOLERANCE for cell in cells)
          and all(origin_cell.location.y <= cell.location.y + TOLERANCE for cell in cells),
          "{0} at {1:.3f},{2:.3f}".format(
              origin_cell.name, origin_cell.location.x, origin_cell.location.y))


def test_names_sort_into_creation_order():
    """Locks in the property that keeps object creation linear instead of quadratic.

    Blender inserts each new datablock into a name-sorted list. When the next name sorts after
    everything already there the insert is O(1); when it does not, Blender scans backwards and
    the build becomes O(n^2). Measured on this machine, names that break the order cost 2085
    microseconds a cell against 31 -- a minute's work turned into most of a day at two million
    cells. The grid is built row by row, so names must sort that way too, which needs the row
    before the column and both zero-padded.

    The dimensions here are chosen so unpadded numbers would sort wrongly in both parts: with
    130 columns and 12 rows, c10 would precede c2 and r10 would precede r2.
    """
    print("Name ordering", flush=True)
    reset()
    run_generator(WIDTH=130, HEIGHT=12, COLOR_COUNT=7, ADD_CAMERA=False, PROGRESS_ROWS=0)
    cells = list(bpy.data.collections["HexGrid"].objects)
    check("cell names are all distinct", len({cell.name for cell in cells}) == len(cells))

    # Recover each cell's column and row from where it actually sits, then confirm that sorting
    # by name gives the same sequence as sorting row by row. Reading the grid back from geometry
    # keeps this independent of how the names were built.
    def indices(cell):
        column = round((cell.location.x - 1.0) / 1.5)
        row = round((cell.location.y - SQRT3 / 2 - SQRT3 / 2 * (column & 1)) / SQRT3)
        return row, column

    by_name = [indices(cell) for cell in sorted(cells, key=lambda cell: cell.name)]
    check("names sort into row-major order", by_name == sorted(by_name),
          "first break at {0}".format(next((pair for pair, want in zip(by_name, sorted(by_name))
                                            if pair != want), None)))
    widths = {len(cell.name) for cell in cells}
    check("every cell name is the same length", len(widths) == 1, "widths {0}".format(sorted(widths)))

    # The same ordering matters for mesh datablocks when each cell carries its own.
    reset()
    run_generator(WIDTH=130, HEIGHT=12, COLOR_COUNT=7, SHARE_MESHES=False, ADD_CAMERA=False,
                  PROGRESS_ROWS=0)
    cells = list(bpy.data.collections["HexGrid"].objects)
    mesh_widths = {len(cell.data.name) for cell in cells}
    check("unique mesh names are the same length too", len(mesh_widths) == 1,
          "widths {0}".format(sorted(mesh_widths)))


def test_tiling_and_adjacency():
    print("Tiling and adjacency", flush=True)
    reset()
    run_generator(WIDTH=40, HEIGHT=24, COLOR_COUNT=7, ADD_CAMERA=False, PROGRESS_ROWS=0)
    grid = bpy.data.collections["HexGrid"]
    cells = list(grid.objects)
    index = CellIndex(cells, 1.0)
    clashes = 0
    coincident = 0
    full = 0
    for cell in cells:
        mine = material_of(cell)
        coincident += sum(1 for _, distance in index.around(cell) if distance < TOLERANCE)
        touching = index.touching(cell)
        full += len(touching) == 6
        clashes += sum(1 for other in touching if material_of(other) == mine)

    check("no two cells sit in the same place", coincident == 0, "{0} coincident".format(coincident))
    # Every cell away from the border must have all six neighbours at the exact spacing. A 40 x 24
    # grid has 38 x 22 of them; if the row offset or either pitch were wrong the count would
    # collapse, and an exact figure catches a grid that is subtly too loose or too tight.
    check("interior cells have 6 neighbours at the exact pitch", full == 38 * 22,
          "{0} of an expected {1}".format(full, 38 * 22))
    check("no adjacent cells share a material", clashes == 0, "{0} clashing pairs".format(clashes))


def test_palette():
    print("Palette", flush=True)
    reset()
    run_generator(WIDTH=20, HEIGHT=12, COLOR_COUNT=5, ADD_CAMERA=False, PROGRESS_ROWS=0)
    grid = bpy.data.collections["HexGrid"]
    palette = [material for material in bpy.data.materials if material.name.startswith("HexGridColor")]
    check("palette has COLOR_COUNT materials", len(palette) == 5, "got {0}".format(len(palette)))

    unlit = 0
    colors = set()
    for material in palette:
        nodes = material.node_tree.nodes
        emission = [node for node in nodes if node.type == "EMISSION"]
        principled = [node for node in nodes if node.type == "BSDF_PRINCIPLED"]
        if len(emission) == 1 and not principled:
            unlit += 1
            colors.add(tuple(round(channel, 5) for channel in emission[0].inputs["Color"].default_value))
    check("every material is unlit emission", unlit == len(palette), "{0} of {1}".format(unlit, len(palette)))
    check("palette colours are all distinct", len(colors) == len(palette), "{0} distinct".format(len(colors)))

    used = {material_of(cell) for cell in grid.objects}
    check("every palette entry is used", len(used) == 5, "used {0}".format(len(used)))
    check("viewport display colour matches the shader",
          all(tuple(round(channel, 5) for channel in material.diffuse_color) in colors for material in palette))


def test_three_colors():
    print("Minimum palette", flush=True)
    reset()
    run_generator(WIDTH=30, HEIGHT=20, COLOR_COUNT=3, ADD_CAMERA=False, PROGRESS_ROWS=0)
    cells = list(bpy.data.collections["HexGrid"].objects)
    index = CellIndex(cells, 1.0)
    clashes = 0
    pairs = 0
    for cell in cells:
        mine = material_of(cell)
        for other in index.touching(cell):
            pairs += 1
            clashes += material_of(other) == mine
    check("three colours still separate every neighbour", clashes == 0, "{0} clashing pairs".format(clashes))
    # Three is the chromatic number of this lattice, so this is the tightest the constraint gets.
    check("the three-colour grid really is fully connected", pairs == 3402,
          "{0} adjacent pairs".format(pairs))


def test_unique_meshes():
    print("Unique meshes", flush=True)
    reset()
    run_generator(WIDTH=10, HEIGHT=6, COLOR_COUNT=4, SHARE_MESHES=False, ADD_CAMERA=False, PROGRESS_ROWS=0)
    cells = list(bpy.data.collections["HexGrid"].objects)
    meshes = {cell.data.name for cell in cells}
    check("each cell gets its own mesh datablock", len(meshes) == 60, "got {0}".format(len(meshes)))
    check("unique meshes still carry a material", all(len(cell.data.materials) == 1 for cell in cells))

    reset()
    run_generator(WIDTH=10, HEIGHT=6, COLOR_COUNT=4, SHARE_MESHES=True, ADD_CAMERA=False, PROGRESS_ROWS=0)
    cells = list(bpy.data.collections["HexGrid"].objects)
    meshes = {cell.data.name for cell in cells}
    check("shared meshes collapse to one per colour", len(meshes) == 4, "got {0}".format(len(meshes)))


def test_rerun_is_idempotent():
    print("Re-running", flush=True)
    reset()
    run_generator(WIDTH=16, HEIGHT=10, COLOR_COUNT=7, ADD_CAMERA=False, PROGRESS_ROWS=0)
    first = len(bpy.data.collections["HexGrid"].objects)
    run_generator(WIDTH=16, HEIGHT=10, COLOR_COUNT=7, ADD_CAMERA=False, PROGRESS_ROWS=0)
    second = len(bpy.data.collections["HexGrid"].objects)
    check("a second run replaces rather than stacks", first == second == 160,
          "{0} then {1}".format(first, second))
    check("only one grid collection exists",
          len([c for c in bpy.data.collections if c.name.startswith("HexGrid")]) == 1)
    check("no orphaned cell objects are left behind",
          len([o for o in bpy.data.objects if o.name.startswith("HexGrid_")]) == 160)

    # A smaller re-run must not leave the larger grid's cells behind.
    run_generator(WIDTH=8, HEIGHT=5, COLOR_COUNT=7, ADD_CAMERA=False, PROGRESS_ROWS=0)
    check("shrinking the grid removes the surplus",
          len(bpy.data.collections["HexGrid"].objects) == 40,
          "got {0}".format(len(bpy.data.collections["HexGrid"].objects)))


def test_other_scene_objects_survive():
    print("Leaving the rest of the scene alone", flush=True)
    reset()
    keeper = bpy.data.objects.new("UserCube", bpy.data.meshes.new("UserCubeMesh"))
    bpy.context.scene.collection.objects.link(keeper)
    run_generator(WIDTH=10, HEIGHT=6, COLOR_COUNT=7, ADD_CAMERA=False, PROGRESS_ROWS=0)
    run_generator(WIDTH=10, HEIGHT=6, COLOR_COUNT=7, ADD_CAMERA=False, PROGRESS_ROWS=0)
    check("an unrelated object is untouched by a run and a re-run",
          bpy.data.objects.get("UserCube") is not None)


def test_rejects_impossible_palette():
    print("Rejecting an impossible palette", flush=True)
    reset()
    try:
        run_generator(WIDTH=10, HEIGHT=6, COLOR_COUNT=2, ADD_CAMERA=False, PROGRESS_ROWS=0)
        check("two colours are rejected", False, "no error raised")
    except ValueError:
        check("two colours are rejected", True)
    reset()
    try:
        run_generator(WIDTH=0, HEIGHT=6, COLOR_COUNT=7, ADD_CAMERA=False, PROGRESS_ROWS=0)
        check("a zero dimension is rejected", False, "no error raised")
    except ValueError:
        check("a zero dimension is rejected", True)


def test_view_transform():
    print("View transform", flush=True)
    reset()
    run_generator(WIDTH=8, HEIGHT=5, COLOR_COUNT=7, ADD_CAMERA=False, PLAIN_COLORS=True,
                  PROGRESS_ROWS=0)
    check("Standard view transform is set when asked",
          bpy.context.scene.view_settings.view_transform == "Standard",
          "got {0}".format(bpy.context.scene.view_settings.view_transform))

    reset()
    bpy.context.scene.view_settings.view_transform = "AgX"
    run_generator(WIDTH=8, HEIGHT=5, COLOR_COUNT=7, ADD_CAMERA=False, PLAIN_COLORS=False,
                  PROGRESS_ROWS=0)
    check("the scene's own view transform is left alone when not asked",
          bpy.context.scene.view_settings.view_transform == "AgX",
          "got {0}".format(bpy.context.scene.view_settings.view_transform))


def test_camera():
    print("Camera", flush=True)
    reset()
    namespace = run_generator(WIDTH=30, HEIGHT=15, COLOR_COUNT=7, ADD_CAMERA=True, PROGRESS_ROWS=0)
    camera = bpy.context.scene.camera
    check("a camera is added and made active", camera is not None and camera.data.type == "ORTHO")
    width, height = namespace["grid_extent"](30, 15, 1.0)
    # The frame has to cover the grid's long side, or opening the file shows a cropped grid.
    check("the camera frames the whole grid", camera.data.ortho_scale >= max(width, height),
          "scale {0:.2f} vs span {1:.2f}".format(camera.data.ortho_scale, max(width, height)))
    check("the camera is centred on the grid",
          abs(camera.location.x - width / 2) < TOLERANCE and abs(camera.location.y - height / 2) < TOLERANCE,
          "at {0:.2f},{1:.2f} for a {2:.2f} x {3:.2f} grid".format(
              camera.location.x, camera.location.y, width, height))
    check("the camera clips past the grid", camera.data.clip_end > camera.location.z)


if __name__ == "__main__":
    started = time.perf_counter()
    for test in (test_geometry, test_quadrant, test_names_sort_into_creation_order,
                 test_tiling_and_adjacency, test_palette, test_three_colors,
                 test_unique_meshes, test_rerun_is_idempotent, test_other_scene_objects_survive,
                 test_rejects_impossible_palette, test_view_transform, test_camera):
        test()
    print("", flush=True)
    print("{0} passed, {1} failed in {2:.1f}s".format(
        len(passes), len(failures), time.perf_counter() - started), flush=True)
    for failure in failures:
        print("  " + failure, flush=True)
    sys.exit(1 if failures else 0)
