"""Build a flat-top hexagon grid as ONE mesh object holding every cell.

Paste this into Blender's Text Editor, edit the settings block below, and press Run Script.
Progress goes to the system console (Window > Toggle System Console on Windows), because a
script run from the Text Editor freezes the interface until it finishes.

This is the companion to GenerateHexGrid.py, which gives every cell its own object. The geometry
is identical -- flat-top hexagons, four triangles each, same positions, same colouring -- but it
all lands in a single mesh, and a cell's colour is carried by the material_index of its four
polygons rather than by an object's material slot. One object instead of two million is worth
roughly fifty times the build speed and a tenth of the memory; what it costs is the ability to
select, move or hide a cell on its own.

The grid lies flat on the XY plane at Z = 0 and occupies the +X +Y quadrant only: its bounding
box starts exactly at the origin, so cell (0, 0) is the bottom-left one and no vertex anywhere
has a negative coordinate.

Cells are laid out row by row, so the four polygons of cell (column, row) begin at index
(row * WIDTH + column) * 4, and its six vertices at (row * WIDTH + column) * 6.
"""

import colorsys
import math
import time

import bpy
import numpy as np

# ----------------------------------------------------------------------------------------------
# Settings
# ----------------------------------------------------------------------------------------------

WIDTH = 2000           # Cells across.
HEIGHT = 1000          # Cells down.

CELL_RADIUS = 1.0      # Centre to corner. Cells touch exactly at any radius.
COLOR_COUNT = 7        # Palette size. 3 is the smallest that can work; 7 gives the rosette.

ADD_CAMERA = True      # Drop a top-down orthographic camera framing the whole grid.
PLAIN_COLORS = True    # Switch the scene's view transform to Standard. See the note below.
BUILD_EDGES = True     # Give the mesh explicit edges. See the note below.
GRID_NAME = "HexGrid"  # The object, its mesh, and the prefix for the materials.

# PLAIN_COLORS is the one setting here that reaches outside the grid's own objects. Blender 4.x
# renders through the AgX view transform by default, which rolls off saturation to make lit
# scenes photographic. On flat unlit colour it just makes the palette muddy: what you set is not
# what you see. Standard displays the colours as specified, which is the point of unlit material.

# BUILD_EDGES asks Blender to derive the mesh's edge list from its faces. Nothing needs edges to
# display or render, but edit mode, the wireframe overlay and most modifiers do. It is the single
# most expensive step at two million cells, so turn it off if you only mean to look at the grid.

# ----------------------------------------------------------------------------------------------

SQRT3 = math.sqrt(3.0)

# The four triangles fan from corner 0, the same decomposition the editor's own mesh renderer
# uses, written out as the twelve corner indices of one cell.
FAN = np.array([0, 1, 2, 0, 2, 3, 0, 3, 4, 0, 4, 5], dtype=np.int32)

NEIGHBORS = ((1, 0), (1, -1), (0, -1), (-1, 0), (-1, 1), (0, 1))


def srgb_to_linear(channel):
    """Blender holds colour linearly, so a palette picked in sRGB converts or it reads washed out."""
    return channel / 12.92 if channel <= 0.04045 else ((channel + 0.055) / 1.055) ** 2.4


def palette_colors(count):
    """Evenly spaced hues, with saturation and value walked a little so neighbouring hues stay
    distinguishable rather than reading as one band of the same brightness."""
    colors = []
    for index in range(count):
        red, green, blue = colorsys.hsv_to_rgb(
            index / count, 0.55 + 0.18 * (index % 2), 0.98 - 0.14 * (index % 3))
        colors.append(tuple(srgb_to_linear(channel) for channel in (red, green, blue)) + (1.0,))
    return colors


def unlit_material(name, color):
    """An emission shader, which is what unlit means to both EEVEE and Cycles."""
    material = bpy.data.materials.new(name)
    material.use_nodes = True
    tree = material.node_tree
    tree.nodes.clear()
    output = tree.nodes.new("ShaderNodeOutputMaterial")
    emission = tree.nodes.new("ShaderNodeEmission")
    emission.inputs["Color"].default_value = color
    emission.inputs["Strength"].default_value = 1.0
    tree.links.new(emission.outputs["Emission"], output.inputs["Surface"])
    # Solid viewport shading reads this instead of the node tree, so the grid keeps its colours
    # without having to leave the viewport in Material Preview.
    material.diffuse_color = color
    material.roughness = 1.0
    return material


def grid_extent(columns, rows, radius):
    """The finished grid's bounding box, which starts at the origin and runs into +X +Y.

    The height gains half a row once there is more than one column, because odd columns are the
    ones carrying the half-cell offset and a single-column grid has none.
    """
    width = 1.5 * radius * (columns - 1) + 2 * radius
    height = SQRT3 * radius * (rows + (0.5 if columns > 1 else 0.0))
    return width, height


def cell_arrays(columns, rows, radius, count):
    """Every cell's centre and colour, as whole arrays rather than a Python loop.

    Cells run row by row, so cell (column, row) is at index row * columns + column.
    """
    column = np.tile(np.arange(columns, dtype=np.int64), rows)
    row = np.repeat(np.arange(rows, dtype=np.int64), columns)
    odd = column & 1

    centre_x = radius + 1.5 * radius * column
    centre_y = 0.5 * SQRT3 * radius + SQRT3 * radius * (row + 0.5 * odd)

    # The colouring, in axial coordinates. Hex centres form a triangular lattice; taking
    # f(q, r) = (q + 2r) mod k, the difference across the six neighbour directions is 1, -1, -2,
    # -1, 1 and 2, and none of those vanish mod k for any k >= 3. So no two touching cells can
    # land on the same colour whatever the palette size, with no search needed to place them.
    # Numpy's remainder follows the sign of the divisor, so a negative r still lands in range.
    q = column
    r = row - (column - odd) // 2
    color = (q + 2 * r) % count

    return centre_x, centre_y, color


def verify_coloring(columns, rows, count, samples=20000):
    """Check the colouring on a strided sweep of the real grid rather than trusting the algebra."""
    stride = max(1, (columns * rows) // samples)
    checked = 0
    for cell in range(0, columns * rows, stride):
        column, row = cell % columns, cell // columns
        q, r = column, row - (column - (column & 1)) // 2
        mine = (q + 2 * r) % count
        for deltaQ, deltaR in NEIGHBORS:
            neighborQ, neighborR = q + deltaQ, r + deltaR
            neighborColumn = neighborQ
            neighborRow = neighborR + (neighborQ - (neighborQ & 1)) // 2
            if not (0 <= neighborColumn < columns and 0 <= neighborRow < rows):
                continue
            if (neighborQ + 2 * neighborR) % count == mine:
                raise AssertionError("cell {0},{1} shares colour {2} with neighbour {3},{4}".format(
                    column, row, mine, neighborColumn, neighborRow))
            checked += 1
    return checked


def build_mesh(name, columns, rows, radius, count, materials):
    """One mesh holding every cell, filled through foreach_set rather than from_pydata.

    from_pydata would need Python lists of twelve million coordinate tuples and eight million
    face tuples, which costs more in building and holding those lists than the mesh itself. The
    add() and foreach_set() pair takes flat buffers straight from numpy instead.

    Cells do not share corner vertices with their neighbours. Sharing would save about two thirds
    of the vertices, but each cell would stop being a separable four-triangle fan, and touching
    cells of different colours need their own corners anyway.
    """
    cells = columns * rows
    centre_x, centre_y, color = cell_arrays(columns, rows, radius, count)

    angles = np.radians(60.0 * np.arange(6, dtype=np.float64))
    corner_x = np.cos(angles) * radius
    corner_y = np.sin(angles) * radius

    coordinates = np.empty((cells, 6, 3), dtype=np.float32)
    coordinates[:, :, 0] = centre_x[:, None] + corner_x[None, :]
    coordinates[:, :, 1] = centre_y[:, None] + corner_y[None, :]
    coordinates[:, :, 2] = 0.0

    # Corner 0 of cell c is vertex 6c, so the fan pattern shifts by six for every cell.
    loop_vertices = np.tile(FAN, cells) + np.repeat(np.arange(cells, dtype=np.int32) * 6, 12)
    loop_start = np.arange(cells * 4, dtype=np.int32) * 3
    material_index = np.repeat(color.astype(np.int32), 4)

    mesh = bpy.data.meshes.new(name + "Mesh")
    for material in materials:
        mesh.materials.append(material)
    mesh.vertices.add(cells * 6)
    mesh.loops.add(cells * 12)
    mesh.polygons.add(cells * 4)
    mesh.vertices.foreach_set("co", coordinates.ravel())
    mesh.loops.foreach_set("vertex_index", loop_vertices)
    mesh.polygons.foreach_set("loop_start", loop_start)
    mesh.polygons.foreach_set("material_index", material_index)
    mesh.update(calc_edges=BUILD_EDGES)
    return mesh


def clear_previous(name):
    """Remove an earlier run's object, mesh and materials, and nothing else."""
    removed = 0
    existing = bpy.data.objects.get(name)
    if existing is not None:
        bpy.data.objects.remove(existing, do_unlink=True)
        removed += 1
    collection = bpy.data.collections.get(name)
    if collection is not None:
        bpy.data.collections.remove(collection)
    for mesh in [block for block in bpy.data.meshes if block.users == 0 and block.name.startswith(name)]:
        bpy.data.meshes.remove(mesh)
    for material in [block for block in bpy.data.materials if block.users == 0 and block.name.startswith(name)]:
        bpy.data.materials.remove(material)
    return removed


def add_camera(name, columns, rows, radius):
    width, height = grid_extent(columns, rows, radius)
    camera_data = bpy.data.cameras.new(name + "Camera")
    camera_data.type = "ORTHO"
    camera_data.ortho_scale = max(width, height) * 1.02
    camera_data.clip_end = max(10000.0, max(width, height) * 4)
    camera = bpy.data.objects.new(name + "Camera", camera_data)
    camera.location = (width / 2, height / 2, max(width, height))
    bpy.context.scene.collection.objects.link(camera)
    bpy.context.scene.camera = camera
    return camera


def build():
    columns, rows, count = WIDTH, HEIGHT, COLOR_COUNT
    if columns < 1 or rows < 1:
        raise ValueError("WIDTH and HEIGHT must both be at least 1")
    # Hex centres form a triangular lattice, whose chromatic number is three. With fewer colours
    # than that no assignment can keep touching cells apart, so there is nothing to attempt.
    if count < 3:
        raise ValueError("COLOR_COUNT must be at least 3; a hex grid cannot be properly coloured with fewer")
    if CELL_RADIUS <= 0:
        raise ValueError("CELL_RADIUS must be greater than 0")

    cells = columns * rows
    started = time.perf_counter()
    print("Hex grid, single mesh: {0} x {1} = {2:,} cells, {3:,} triangles, {4:,} vertices, {5} colours"
          .format(columns, rows, cells, cells * 4, cells * 6, count), flush=True)

    removed = clear_previous(GRID_NAME)
    if removed:
        print("Replaced the grid from a previous run", flush=True)

    checked = verify_coloring(columns, rows, count)
    print("Colouring verified over {0:,} adjacent pairs".format(checked), flush=True)

    materials = [unlit_material("{0}Color{1:02d}".format(GRID_NAME, index), color)
                 for index, color in enumerate(palette_colors(count))]

    checkpoint = time.perf_counter()
    mesh = build_mesh(GRID_NAME, columns, rows, CELL_RADIUS, count, materials)
    print("Mesh built in {0:.1f}s".format(time.perf_counter() - checkpoint), flush=True)

    # validate() reports True when it had to repair something. Correctly built geometry needs no
    # repair, so a True here means this script got something wrong and quietly lost geometry.
    checkpoint = time.perf_counter()
    if mesh.validate(verbose=False):
        print("WARNING: Blender had to repair the mesh, so some geometry may be missing", flush=True)
    print("Mesh validated in {0:.1f}s".format(time.perf_counter() - checkpoint), flush=True)

    grid = bpy.data.collections.new(GRID_NAME)
    cell_object = bpy.data.objects.new(GRID_NAME, mesh)
    grid.objects.link(cell_object)
    bpy.context.scene.collection.children.link(grid)

    if ADD_CAMERA:
        add_camera(GRID_NAME, columns, rows, CELL_RADIUS)
    if PLAIN_COLORS:
        settings = bpy.context.scene.view_settings
        if settings.view_transform != "Standard":
            print("Switched the view transform from {0} to Standard so the palette renders as set"
                  .format(settings.view_transform), flush=True)
            settings.view_transform = "Standard"

    print("Done: 1 object, {0:,} vertices, {1:,} polygons, {2} material slots in {3:.1f}s".format(
        len(mesh.vertices), len(mesh.polygons), len(mesh.materials),
        time.perf_counter() - started), flush=True)
    print("Viewport tip: Solid shading with Color set to Material shows the palette without EEVEE.",
          flush=True)
    return cell_object


build()
