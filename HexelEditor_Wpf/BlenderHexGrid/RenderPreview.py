"""Render a preview of the generated grid, to look at what the script actually produces.

    set HEXGRID_WIDTH=40 & set HEXGRID_HEIGHT=24 & set HEXGRID_OUTPUT=C:\\Temp\\Preview.png
    blender --background --factory-startup --python RenderPreview.py

Cycles on the CPU, because this runs headless and an emission shader needs no light, no bounces
and no denoising: one sample is already exact. The axes are drawn in as thin emissive strips so
the +X +Y placement is visible rather than merely asserted.
"""

import math
import os
import re

import bpy

SCRIPT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "GenerateHexGrid.py")


def emissive(name, color):
    material = bpy.data.materials.new(name)
    material.use_nodes = True
    tree = material.node_tree
    tree.nodes.clear()
    output = tree.nodes.new("ShaderNodeOutputMaterial")
    emission = tree.nodes.new("ShaderNodeEmission")
    emission.inputs["Color"].default_value = color
    emission.inputs["Strength"].default_value = 1.0
    tree.links.new(emission.outputs["Emission"], output.inputs["Surface"])
    return material


def axis_strip(name, length, thickness, horizontal, color):
    """A flat bar along an axis, sitting just under the grid so it does not hide cells."""
    mesh = bpy.data.meshes.new(name)
    long_side, short_side = (length, thickness) if horizontal else (thickness, length)
    mesh.from_pydata(
        [(0, 0, -0.01), (long_side, 0, -0.01), (long_side, short_side, -0.01), (0, short_side, -0.01)],
        [], [[0, 1, 2], [0, 2, 3]])
    mesh.update()
    mesh.materials.append(emissive(name, color))
    strip = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(strip)
    return strip


def main():
    width = int(os.environ.get("HEXGRID_WIDTH", "40"))
    height = int(os.environ.get("HEXGRID_HEIGHT", "24"))
    colors = int(os.environ.get("HEXGRID_COLORS", "7"))
    output = os.environ.get("HEXGRID_OUTPUT", os.path.join(os.path.dirname(SCRIPT), "Preview.png"))

    with open(SCRIPT, "r", encoding="utf-8") as stream:
        source = stream.read()
    for key, value in (("WIDTH", width), ("HEIGHT", height), ("COLOR_COUNT", colors),
                       ("ADD_CAMERA", True), ("PROGRESS_ROWS", 0)):
        source, found = re.subn(r"^{0} = [^\n#]+".format(key), "{0} = {1!r}".format(key, value),
                                source, count=1, flags=re.MULTILINE)
        if found != 1:
            raise AssertionError("could not find a setting named {0}".format(key))

    bpy.ops.wm.read_factory_settings(use_empty=True)
    namespace = {"__name__": "__main__", "__file__": SCRIPT}
    exec(compile(source, SCRIPT, "exec"), namespace)

    extent_x, extent_y = namespace["grid_extent"](width, height, 1.0)
    axis_strip("AxisX", extent_x * 1.15, 0.35, True, (1.0, 1.0, 1.0, 1.0))
    axis_strip("AxisY", extent_y * 1.15, 0.35, False, (1.0, 1.0, 1.0, 1.0))

    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.device = "CPU"
    scene.cycles.samples = 1
    scene.cycles.use_denoising = False
    resolution = int(os.environ.get("HEXGRID_RESOLUTION", "1600"))
    scene.render.resolution_x = resolution
    scene.render.resolution_y = max(200, int(resolution * extent_y / extent_x))
    scene.render.filepath = output
    scene.render.image_settings.file_format = "PNG"

    # Frame the grid plus a margin, so the axes and the empty space beyond them are both visible.
    camera = scene.camera
    camera.data.ortho_scale = max(extent_x, extent_y) * 1.18
    camera.location = (extent_x / 2, extent_y / 2, max(extent_x, extent_y))

    bpy.ops.render.render(write_still=True)
    print("PREVIEW_WRITTEN " + os.path.abspath(output), flush=True)


if __name__ == "__main__":
    main()
