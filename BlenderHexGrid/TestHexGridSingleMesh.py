"""Correctness tests for GenerateHexGridSingleMesh.py, run inside Blender.

    blender --background --factory-startup --python TestHexGridSingleMesh.py

As with the per-object tests, these execute the real script text with only its settings constants
rewritten, so what is checked is the file a person would paste.

The adjacency test ignores the colouring formula and works from the built mesh: it derives each
cell's centre from its six vertices, finds neighbours by measuring distances between those
centres, and compares the material_index actually found on their polygons. The last test builds
the same grid through both scripts and checks they agree cell for cell, which is the thing most
likely to drift as one of them changes.
"""

import math
import os
import re
import sys
import time

import bpy
import numpy as np

FOLDER = os.path.dirname(os.path.abspath(__file__))
SCRIPT = os.path.join(FOLDER, "GenerateHexGridSingleMesh.py")
PER_OBJECT_SCRIPT = os.path.join(FOLDER, "GenerateHexGrid.py")
SQRT3 = math.sqrt(3.0)

# Mesh vertex coordinates are single precision, so distances between cells out at x = 60 carry
# about 1e-5 of error. The nearest non-neighbour sits at 3.0 against a neighbour's 1.732, so
# there is a wide gap to aim at and this still pins the pitch to four decimal places.
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


def run_generator(script=SCRIPT, **settings):
    with open(script, "r", encoding="utf-8") as stream:
        source = stream.read()
    for key, value in settings.items():
        source, count = re.subn(r"^{0} = [^\n#]+".format(key), "{0} = {1!r}".format(key, value),
                                source, count=1, flags=re.MULTILINE)
        if count != 1:
            raise AssertionError("could not find a setting named {0} in {1}".format(key, script))
    namespace = {"__name__": "__main__", "__file__": script}
    exec(compile(source, script, "exec"), namespace)
    return namespace


def reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def grid_mesh():
    return bpy.data.objects["HexGrid"].data


def cell_centres(mesh, cells):
    """Each cell's centre, averaged from its own six vertices."""
    coordinates = np.empty(len(mesh.vertices) * 3, dtype=np.float32)
    mesh.vertices.foreach_get("co", coordinates)
    return coordinates.reshape(cells, 6, 3).mean(axis=1)


def cell_materials(mesh, cells):
    """Each cell's material index, taken from the first of its four polygons."""
    indices = np.empty(len(mesh.polygons), dtype=np.int32)
    mesh.polygons.foreach_get("material_index", indices)
    return indices.reshape(cells, 4)


def test_one_object():
    print("One object", flush=True)
    reset()
    run_generator(WIDTH=12, HEIGHT=8, COLOR_COUNT=7, ADD_CAMERA=False)
    cells = 96
    grid = bpy.data.collections["HexGrid"]
    check("the grid is a single object", len(grid.objects) == 1,
          "got {0}".format(len(grid.objects)))
    mesh = grid_mesh()
    check("one mesh datablock holds every cell",
          len([block for block in bpy.data.meshes if block.users > 0]) == 1)
    check("six vertices per cell", len(mesh.vertices) == cells * 6,
          "got {0}".format(len(mesh.vertices)))
    check("four polygons per cell", len(mesh.polygons) == cells * 4,
          "got {0}".format(len(mesh.polygons)))
    check("every polygon is a triangle",
          all(len(polygon.vertices) == 3 for polygon in mesh.polygons))
    check("edges were built", len(mesh.edges) > 0, "got {0}".format(len(mesh.edges)))


def test_cell_geometry():
    print("Cell geometry", flush=True)
    reset()
    run_generator(WIDTH=10, HEIGHT=7, COLOR_COUNT=7, ADD_CAMERA=False)
    mesh = grid_mesh()
    cells = 70
    expected_area = 3 * SQRT3 / 2

    areas = np.empty(len(mesh.polygons), dtype=np.float64)
    mesh.polygons.foreach_get("area", areas)
    per_cell = areas.reshape(cells, 4).sum(axis=1)
    # Four triangles that tile a hexagon exactly sum to its area. Overlapping or missing
    # triangles would not, so this catches a wrong fan as well as a wrong count.
    check("each cell's four triangles tile a hexagon exactly",
          bool(np.all(np.abs(per_cell - expected_area) < 1e-5)),
          "worst {0:.6f} against {1:.6f}".format(per_cell.max(), expected_area))

    coordinates = np.empty(len(mesh.vertices) * 3, dtype=np.float32)
    mesh.vertices.foreach_get("co", coordinates)
    corners = coordinates.reshape(cells, 6, 3)
    spans_x = corners[:, :, 0].max(axis=1) - corners[:, :, 0].min(axis=1)
    spans_y = corners[:, :, 1].max(axis=1) - corners[:, :, 1].min(axis=1)
    # Flat-top means corners at 0 and 180 degrees, so every cell is 2r wide and sqrt(3)r tall.
    check("every cell is flat-top",
          bool(np.all(np.abs(spans_x - 2.0) < TOLERANCE) and np.all(np.abs(spans_y - SQRT3) < TOLERANCE)),
          "width {0:.4f} height {1:.4f}".format(spans_x.max(), spans_y.max()))

    normals = np.empty(len(mesh.polygons) * 3, dtype=np.float32)
    mesh.polygons.foreach_get("normal", normals)
    # A fan wound the wrong way would face -Z and the grid would be backwards to the camera.
    check("every polygon faces +Z", bool(np.all(normals.reshape(-1, 3)[:, 2] > 0.999)),
          "worst {0:.4f}".format(normals.reshape(-1, 3)[:, 2].min()))


def test_quadrant():
    print("Position in the world", flush=True)
    reset()
    namespace = run_generator(WIDTH=25, HEIGHT=14, COLOR_COUNT=7, ADD_CAMERA=False)
    mesh = grid_mesh()
    coordinates = np.empty(len(mesh.vertices) * 3, dtype=np.float32).reshape(-1, 3)
    flat = np.empty(len(mesh.vertices) * 3, dtype=np.float32)
    mesh.vertices.foreach_get("co", flat)
    coordinates = flat.reshape(-1, 3)

    check("the grid is flat on the XY plane", float(np.abs(coordinates[:, 2]).max()) < TOLERANCE,
          "worst Z {0:.6f}".format(float(np.abs(coordinates[:, 2]).max())))
    check("no geometry reaches into -X or -Y",
          coordinates[:, 0].min() >= -TOLERANCE and coordinates[:, 1].min() >= -TOLERANCE,
          "min x {0:.6f} min y {1:.6f}".format(coordinates[:, 0].min(), coordinates[:, 1].min()))
    check("the bounding box starts at the origin",
          abs(float(coordinates[:, 0].min())) < TOLERANCE and abs(float(coordinates[:, 1].min())) < TOLERANCE,
          "min x {0:.6f} min y {1:.6f}".format(coordinates[:, 0].min(), coordinates[:, 1].min()))

    expected_width, expected_height = namespace["grid_extent"](25, 14, 1.0)
    check("the bounding box matches the reported extent",
          abs(float(coordinates[:, 0].max()) - expected_width) < TOLERANCE
          and abs(float(coordinates[:, 1].max()) - expected_height) < TOLERANCE,
          "{0:.4f} x {1:.4f} against {2:.4f} x {3:.4f}".format(
              coordinates[:, 0].max(), coordinates[:, 1].max(), expected_width, expected_height))
    check("the object itself sits at the origin",
          bpy.data.objects["HexGrid"].location.length < TOLERANCE)


def test_cell_order():
    print("Cell ordering", flush=True)
    reset()
    columns, rows = 17, 9
    run_generator(WIDTH=columns, HEIGHT=rows, COLOR_COUNT=7, ADD_CAMERA=False)
    mesh = grid_mesh()
    centres = cell_centres(mesh, columns * rows)
    # The documented contract: cell (column, row) occupies vertices 6n and polygons 4n for
    # n = row * WIDTH + column. Anything reading the mesh back depends on this holding.
    worst = 0.0
    for column, row in ((0, 0), (1, 0), (0, 1), (16, 8), (9, 4), (16, 0), (0, 8)):
        index = row * columns + column
        wanted_x = 1.0 + 1.5 * column
        wanted_y = 0.5 * SQRT3 + SQRT3 * (row + 0.5 * (column & 1))
        worst = max(worst, abs(centres[index][0] - wanted_x), abs(centres[index][1] - wanted_y))
    check("cell (column, row) lands at index row * WIDTH + column", worst < TOLERANCE,
          "worst offset {0:.6f}".format(worst))


def test_adjacency():
    print("Tiling and adjacency", flush=True)
    reset()
    columns, rows = 40, 24
    run_generator(WIDTH=columns, HEIGHT=rows, COLOR_COUNT=7, ADD_CAMERA=False)
    mesh = grid_mesh()
    cells = columns * rows
    centres = cell_centres(mesh, cells)
    materials = cell_materials(mesh, cells)

    check("a cell's four polygons all carry its own colour",
          bool(np.all(materials.min(axis=1) == materials.max(axis=1))))
    colors = materials[:, 0]

    # Buckets are wider than the neighbour distance on purpose. Sized exactly at the spacing, a
    # cell whose coordinate lands on a bucket boundary -- which every even column does, its y
    # being an exact multiple of the spacing -- falls on whichever side float error picks and
    # then misses every neighbour on one side.
    spacing = SQRT3
    bucket = spacing * 1.05
    buckets = {}
    for index, centre in enumerate(centres):
        key = (int(math.floor(centre[0] / bucket)), int(math.floor(centre[1] / bucket)))
        buckets.setdefault(key, []).append(index)

    clashes = 0
    coincident = 0
    full = 0
    pairs = 0
    for index, centre in enumerate(centres):
        key = (int(math.floor(centre[0] / bucket)), int(math.floor(centre[1] / bucket)))
        found = 0
        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                for other in buckets.get((key[0] + dx, key[1] + dy), ()):
                    if other == index:
                        continue
                    distance = float(np.linalg.norm(centres[other] - centre))
                    if distance < TOLERANCE:
                        coincident += 1
                    elif abs(distance - spacing) < TOLERANCE:
                        found += 1
                        pairs += 1
                        if colors[other] == colors[index]:
                            clashes += 1
        full += found == 6

    check("no two cells sit in the same place", coincident == 0, "{0} coincident".format(coincident))
    check("interior cells have 6 neighbours at the exact pitch", full == 38 * 22,
          "{0} of an expected {1}".format(full, 38 * 22))
    check("no adjacent cells share a material", clashes == 0, "{0} clashing pairs".format(clashes))
    # The same figure the generator's own formula-based sweep counts, reached from geometry.
    check("the adjacency graph is the expected size", pairs == 5506, "{0} pairs".format(pairs))


def test_three_colors():
    print("Minimum palette", flush=True)
    reset()
    columns, rows = 30, 20
    run_generator(WIDTH=columns, HEIGHT=rows, COLOR_COUNT=3, ADD_CAMERA=False)
    mesh = grid_mesh()
    cells = columns * rows
    centres = cell_centres(mesh, cells)
    colors = cell_materials(mesh, cells)[:, 0]
    spacing = SQRT3
    bucket = spacing * 1.05
    buckets = {}
    for index, centre in enumerate(centres):
        buckets.setdefault((int(math.floor(centre[0] / bucket)), int(math.floor(centre[1] / bucket))),
                           []).append(index)
    clashes = pairs = 0
    for index, centre in enumerate(centres):
        key = (int(math.floor(centre[0] / bucket)), int(math.floor(centre[1] / bucket)))
        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                for other in buckets.get((key[0] + dx, key[1] + dy), ()):
                    if other != index and abs(float(np.linalg.norm(centres[other] - centre)) - spacing) < TOLERANCE:
                        pairs += 1
                        clashes += colors[other] == colors[index]
    check("three colours still separate every neighbour", clashes == 0,
          "{0} clashing pairs".format(clashes))
    check("the three-colour grid really is fully connected", pairs == 3402,
          "{0} adjacent pairs".format(pairs))


def test_palette():
    print("Palette", flush=True)
    reset()
    run_generator(WIDTH=20, HEIGHT=12, COLOR_COUNT=5, ADD_CAMERA=False)
    mesh = grid_mesh()
    check("the mesh has one material slot per colour", len(mesh.materials) == 5,
          "got {0}".format(len(mesh.materials)))

    unlit = 0
    colors = set()
    for material in mesh.materials:
        nodes = material.node_tree.nodes
        emission = [node for node in nodes if node.type == "EMISSION"]
        principled = [node for node in nodes if node.type == "BSDF_PRINCIPLED"]
        if len(emission) == 1 and not principled:
            unlit += 1
            colors.add(tuple(round(channel, 5) for channel in emission[0].inputs["Color"].default_value))
    check("every material is unlit emission", unlit == 5, "{0} of 5".format(unlit))
    check("palette colours are all distinct", len(colors) == 5, "{0} distinct".format(len(colors)))
    check("viewport display colour matches the shader",
          all(tuple(round(channel, 5) for channel in material.diffuse_color) in colors
              for material in mesh.materials))

    used = set(cell_materials(mesh, 240)[:, 0].tolist())
    check("every material slot is used", used == {0, 1, 2, 3, 4}, "used {0}".format(sorted(used)))


def test_rerun_and_scene():
    print("Re-running", flush=True)
    reset()
    keeper = bpy.data.objects.new("UserCube", bpy.data.meshes.new("UserCubeMesh"))
    bpy.context.scene.collection.objects.link(keeper)

    run_generator(WIDTH=16, HEIGHT=10, COLOR_COUNT=7, ADD_CAMERA=False)
    first = len(grid_mesh().polygons)
    run_generator(WIDTH=16, HEIGHT=10, COLOR_COUNT=7, ADD_CAMERA=False)
    check("a second run replaces rather than stacks", first == len(grid_mesh().polygons) == 640,
          "{0} then {1}".format(first, len(grid_mesh().polygons)))
    check("only one grid object exists",
          len([o for o in bpy.data.objects if o.name.startswith("HexGrid")]) == 1)
    check("no orphaned mesh datablocks are left",
          len([m for m in bpy.data.meshes if m.name.startswith("HexGrid")]) == 1)

    run_generator(WIDTH=8, HEIGHT=5, COLOR_COUNT=3, ADD_CAMERA=False)
    check("shrinking the grid removes the surplus", len(grid_mesh().polygons) == 160,
          "got {0}".format(len(grid_mesh().polygons)))
    check("a smaller palette leaves no stale materials",
          len([m for m in bpy.data.materials if m.name.startswith("HexGridColor")]) == 3,
          "{0} materials".format(len([m for m in bpy.data.materials if m.name.startswith("HexGridColor")])))
    check("an unrelated object is untouched", bpy.data.objects.get("UserCube") is not None)


def test_rejects_bad_settings():
    print("Rejecting impossible settings", flush=True)
    for label, settings in (("two colours", {"COLOR_COUNT": 2}),
                            ("a zero dimension", {"WIDTH": 0}),
                            ("a zero radius", {"CELL_RADIUS": 0.0})):
        reset()
        base = {"WIDTH": 8, "HEIGHT": 5, "COLOR_COUNT": 7, "ADD_CAMERA": False}
        base.update(settings)
        try:
            run_generator(**base)
            check("{0} is rejected".format(label), False, "no error raised")
        except ValueError:
            check("{0} is rejected".format(label), True)


def test_camera_and_view():
    print("Camera and view transform", flush=True)
    reset()
    namespace = run_generator(WIDTH=30, HEIGHT=15, COLOR_COUNT=7, ADD_CAMERA=True, PLAIN_COLORS=True)
    camera = bpy.context.scene.camera
    width, height = namespace["grid_extent"](30, 15, 1.0)
    check("a camera is added and made active", camera is not None and camera.data.type == "ORTHO")
    check("the camera frames the whole grid", camera.data.ortho_scale >= max(width, height),
          "scale {0:.2f} vs span {1:.2f}".format(camera.data.ortho_scale, max(width, height)))
    check("the camera is centred on the grid",
          abs(camera.location.x - width / 2) < TOLERANCE and abs(camera.location.y - height / 2) < TOLERANCE)
    check("Standard view transform is set when asked",
          bpy.context.scene.view_settings.view_transform == "Standard")

    reset()
    bpy.context.scene.view_settings.view_transform = "AgX"
    run_generator(WIDTH=8, HEIGHT=5, COLOR_COUNT=7, ADD_CAMERA=False, PLAIN_COLORS=False)
    check("the scene's own view transform is left alone when not asked",
          bpy.context.scene.view_settings.view_transform == "AgX")


def test_matches_per_object_script():
    """The two approaches must describe the same grid, or one of them has drifted."""
    print("Agreement with the per-object script", flush=True)
    columns, rows, count = 21, 13, 7

    reset()
    run_generator(WIDTH=columns, HEIGHT=rows, COLOR_COUNT=count, ADD_CAMERA=False)
    mesh = grid_mesh()
    single_centres = cell_centres(mesh, columns * rows)
    single_colors = cell_materials(mesh, columns * rows)[:, 0]
    single_names = [mesh.materials[index].name for index in single_colors]

    reset()
    run_generator(script=PER_OBJECT_SCRIPT, WIDTH=columns, HEIGHT=rows, COLOR_COUNT=count,
                  ADD_CAMERA=False, PROGRESS_ROWS=0)
    per_object = bpy.data.collections["HexGrid"].objects

    worst = 0.0
    mismatched = 0
    missing = 0
    for row in range(rows):
        for column in range(columns):
            index = row * columns + column
            name = "HexGrid_r{0:0{1}d}_c{2:0{3}d}".format(row, len(str(rows - 1)), column,
                                                          len(str(columns - 1)))
            cell = per_object.get(name)
            if cell is None:
                missing += 1
                continue
            worst = max(worst, abs(cell.location.x - float(single_centres[index][0])),
                        abs(cell.location.y - float(single_centres[index][1])))
            if cell.data.materials[0].name != single_names[index]:
                mismatched += 1

    check("every cell exists in both", missing == 0, "{0} missing".format(missing))
    check("cell centres agree between the two scripts", worst < TOLERANCE,
          "worst offset {0:.6f}".format(worst))
    check("colour assignment agrees between the two scripts", mismatched == 0,
          "{0} cells differ".format(mismatched))


if __name__ == "__main__":
    started = time.perf_counter()
    for test in (test_one_object, test_cell_geometry, test_quadrant, test_cell_order,
                 test_adjacency, test_three_colors, test_palette, test_rerun_and_scene,
                 test_rejects_bad_settings, test_camera_and_view, test_matches_per_object_script):
        test()
    print("", flush=True)
    print("{0} passed, {1} failed in {2:.1f}s".format(
        len(passes), len(failures), time.perf_counter() - started), flush=True)
    for failure in failures:
        print("  " + failure, flush=True)
    sys.exit(1 if failures else 0)
