"""Build a flat-top hexagon grid where every cell is its own four-triangle mesh object.

Paste this into Blender's Text Editor, edit the settings block below, and press Run Script.
Progress goes to the system console (Window > Toggle System Console on Windows), because a
script run from the Text Editor freezes the interface until it finishes.

The grid is built into a collection named by GRID_NAME. Running the script again replaces that
collection and the objects inside it, so re-running never stacks two grids on top of each other.
Nothing outside that collection is touched.

The grid lies flat on the XY plane at Z = 0 and occupies the +X +Y quadrant only: its bounding
box starts exactly at the origin, so cell (0, 0) is the bottom-left one and no vertex anywhere
in the grid has a negative coordinate.

The layout follows HexelEditor's FlatTop convention. In offset coordinates a cell's centre is

    x = radius       + 1.5 * radius * column
    y = radius * r3h + sqrt(3) * radius * (row + 0.5 * (column & 1))

which is HexGeometry.Center composed with HexGeometry.FromOffset, simplified, and then pushed
by one radius across and half a cell height up so the leftmost corner and the lowest edge land
on the axes rather than hanging over them. r3h above is sqrt(3) / 2.
"""

import colorsys
import math
import time

import bpy

# ----------------------------------------------------------------------------------------------
# Settings
# ----------------------------------------------------------------------------------------------

WIDTH = 2000           # Cells across.
HEIGHT = 1000          # Cells down.

CELL_RADIUS = 1.0      # Centre to corner. Cells touch exactly at any radius.
COLOR_COUNT = 7        # Palette size. 3 is the smallest that can work; 7 gives the rosette pattern.

SHARE_MESHES = True    # Cells of one colour point at one mesh datablock. See the note below.
ADD_CAMERA = True      # Drop a top-down orthographic camera framing the whole grid.
PLAIN_COLORS = True    # Switch the scene's view transform to Standard. See the note below.
GRID_NAME = "HexGrid"  # Collection the cells go into, and the prefix for their names.

PROGRESS_ROWS = 25     # Print a progress line every this many rows. 0 turns progress off.

# PLAIN_COLORS is the one setting here that reaches outside the grid's own collection. Blender
# 4.x renders through the AgX view transform by default, which rolls off saturation to make lit
# scenes photographic. On flat unlit colour it just makes the palette muddy: what you set is not
# what you see. Standard displays the colours as specified, which is the point of unlit material.
# Turn it off if the scene you are pasting into relies on its own view transform.

# SHARE_MESHES is worth understanding before turning it off. Every cell is its own mesh object
# holding four triangles either way; the flag only decides whether cells of the same colour point
# at one mesh datablock or each carry a private copy of the same six vertices. Sharing costs
# nothing in appearance, lets the viewport draw the grid in COLOR_COUNT batches instead of one per
# cell, and is the difference between a large grid fitting in memory and not. Turn it off only if
# you intend to edit cells individually.

# ----------------------------------------------------------------------------------------------

SQRT3 = math.sqrt(3.0)

# The four triangles fan from corner 0, the same decomposition the editor's own mesh renderer uses.
FAN = [[0, 1, 2], [0, 2, 3], [0, 3, 4], [0, 4, 5]]

# Six neighbour directions in axial coordinates.
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


def hexagon_corners(radius):
    """Flat-top: corner c sits at 60c degrees, so corners 0 and 3 are the left and right points."""
    return [(math.cos(math.radians(60.0 * corner)) * radius,
             math.sin(math.radians(60.0 * corner)) * radius, 0.0) for corner in range(6)]


def grid_extent(columns, rows, radius):
    """The finished grid's bounding box, which starts at the origin and runs into +X +Y.

    The height gains half a row once there is more than one column, because odd columns are the
    ones carrying the half-cell offset and a single-column grid has none.
    """
    width = 1.5 * radius * (columns - 1) + 2 * radius
    height = SQRT3 * radius * (rows + (0.5 if columns > 1 else 0.0))
    return width, height


def hexagon_mesh(name, radius, material):
    """Six vertices, four triangles, centred on the origin."""
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(hexagon_corners(radius), [], FAN)
    mesh.update()
    if material is not None:
        mesh.materials.append(material)
    return mesh


def color_of(column, row, count):
    """A proper colouring of the hex adjacency graph.

    Hex centres form a triangular lattice. Taking f(q, r) = (q + 2r) mod k, the difference across
    the six neighbour directions is 1, -1, -2, -1, 1 and 2 respectively, and none of those vanish
    mod k for any k >= 3. So no two touching cells can land on the same colour, whatever the
    palette size, and no search or backtracking is needed to place them.
    """
    return (column + 2 * (row - (column - (column & 1)) // 2)) % count


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


def clear_previous(name):
    """Remove an earlier run's collection and everything in it, and nothing else."""
    collection = bpy.data.collections.get(name)
    if collection is None:
        return 0
    removed = 0
    for cell in list(collection.objects):
        bpy.data.objects.remove(cell, do_unlink=True)
        removed += 1
    bpy.data.collections.remove(collection)
    # Mesh and material datablocks the removed cells were the last users of go too, so a re-run at
    # a different palette size does not leave the old one behind.
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
    print("Hex grid: {0} x {1} = {2:,} cells, {3:,} triangles, {4} colours".format(
        columns, rows, cells, cells * 4, count), flush=True)
    if cells > 250000:
        # Measured with Blender 4.5.14 on a 24 core desktop: about 42 microseconds and 3.6 KB per
        # cell, holding steady from eighty thousand cells up to two million. Your machine will
        # differ, but the shape will not. This is Blender's per-object overhead rather than the
        # geometry, which is why four triangles a cell is not what makes a large grid expensive.
        print("  That is a lot of objects. Reckon on roughly {0:.0f}s to build and {1:.1f} GB of".format(
            cells * 42e-6, cells * 3.6 / (1024 ** 2)), flush=True)
        print("  memory, then a viewport that will want Solid shading and some patience.", flush=True)

    removed = clear_previous(GRID_NAME)
    if removed:
        print("Removed {0:,} cells from a previous run".format(removed), flush=True)

    checked = verify_coloring(columns, rows, count)
    print("Colouring verified over {0:,} adjacent pairs".format(checked), flush=True)

    materials = [unlit_material("{0}Color{1:02d}".format(GRID_NAME, index), color)
                 for index, color in enumerate(palette_colors(count))]
    shared = [hexagon_mesh("{0}Cell{1:02d}".format(GRID_NAME, index), CELL_RADIUS, material)
              for index, material in enumerate(materials)] if SHARE_MESHES else None

    # Cells are built into a collection that is not in the scene yet. Linking an object into the
    # active view layer tags the depsgraph every time; holding the collection out until the end
    # turns two million of those into one.
    grid = bpy.data.collections.new(GRID_NAME)
    link = grid.objects.link
    new_object = bpy.data.objects.new
    new_mesh = bpy.data.meshes.new
    # Blender keeps each kind of datablock in a list sorted by name, and inserting a name that
    # does not belong at the end makes it scan backwards to find the spot. Names have to be
    # zero-padded, and rows have to come before columns, so that building the grid row by row
    # hands Blender a name that sorts after every name already there and the insert stays O(1).
    # Getting this wrong is not a small loss: unpadded Name_column_row costs 2085 microseconds a
    # cell against 31, which is the difference between a minute and a day at two million cells.
    cell_name = "{0}_r{{0:0{1}d}}_c{{1:0{2}d}}".format(
        GRID_NAME, len(str(rows - 1)), len(str(columns - 1)))
    mesh_name = GRID_NAME + "Mesh" + cell_name[len(GRID_NAME):]
    corners = hexagon_corners(CELL_RADIUS)
    across = 1.5 * CELL_RADIUS
    down = SQRT3 * CELL_RADIUS
    half = 0.5 * down
    # Pushing the whole grid by one radius across and half a cell height up puts the leftmost
    # corner and the lowest edge exactly on the axes, so the grid sits wholly in the +X +Y
    # quadrant instead of straddling the origin.
    origin_x = CELL_RADIUS
    origin_y = half
    build_started = time.perf_counter()

    for row in range(rows):
        row_y = origin_y + down * row
        for column in range(columns):
            index = (column + 2 * (row - (column - (column & 1)) // 2)) % count
            if SHARE_MESHES:
                mesh = shared[index]
            else:
                mesh = new_mesh(mesh_name.format(row, column))
                mesh.from_pydata(corners, [], FAN)
                mesh.update()
                mesh.materials.append(materials[index])
            cell = new_object(cell_name.format(row, column), mesh)
            cell.location = (origin_x + across * column, row_y + half * (column & 1), 0.0)
            link(cell)
        if PROGRESS_ROWS and row % PROGRESS_ROWS == 0 and row:
            done = (row + 1) * columns
            elapsed = time.perf_counter() - build_started
            print("  row {0:,}/{1:,}  {2:,} cells  {3:.1f}s  (~{4:.0f}s left)".format(
                row, rows, done, elapsed, elapsed / done * (cells - done)), flush=True)

    bpy.context.scene.collection.children.link(grid)
    if ADD_CAMERA:
        add_camera(GRID_NAME, columns, rows, CELL_RADIUS)
    if PLAIN_COLORS:
        settings = bpy.context.scene.view_settings
        if settings.view_transform != "Standard":
            print("Switched the view transform from {0} to Standard so the palette renders as set"
                  .format(settings.view_transform), flush=True)
            settings.view_transform = "Standard"

    total = time.perf_counter() - started
    print("Done: {0:,} objects, {1:,} mesh datablocks, {2} materials in {3:.1f}s".format(
        len(grid.objects), len(shared) if SHARE_MESHES else cells, count, total), flush=True)
    print("Viewport tip: Solid shading with Color set to Material shows the palette without EEVEE.",
          flush=True)
    return grid


build()
