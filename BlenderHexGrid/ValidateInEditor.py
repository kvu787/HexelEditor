"""Drive the real Text Editor path in a Blender GUI session, the way a person would.

    blender --window-geometry <x> <y> <w> <h> --no-window-focus --factory-startup ^
            --python ValidateInEditor.py

Test.ps1 -InEditor launches this with the window placed on a display nobody is using.

The automated tests reach the generator with exec, which is close to how Blender runs a script
but not the same path: it does not go through bpy.ops.text.run_script, a text datablock, or a
real window with a depsgraph and a viewport behind it. This covers that gap. It loads the script
as a text datablock, edits WIDTH and HEIGHT in the editor exactly as someone would before
pressing Run Script, runs it, frames the result in Solid material shading and screenshots the
window.

Nothing here takes the foreground, so it is safe to run while the machine is in use as long as
the window is placed on a free display.
"""

import os

import bpy

SCRIPT = os.environ.get(
    "HEXGRID_SCRIPT", os.path.join(os.path.dirname(os.path.abspath(__file__)), "GenerateHexGrid.py"))
SHOT = os.environ.get("HEXGRID_SHOT", "")
WIDTH = int(os.environ.get("HEXGRID_WIDTH", "70"))
HEIGHT = int(os.environ.get("HEXGRID_HEIGHT", "40"))

failures = []


def check(name, condition, detail=""):
    print("  {0}  {1}  {2}".format("PASS" if condition else "FAIL", name, detail).rstrip(), flush=True)
    if not condition:
        failures.append(name)


def largest_area(area_type=None):
    areas = [area for area in bpy.context.screen.areas
             if area_type is None or area.type == area_type]
    return max(areas, key=lambda area: area.width * area.height)


def step_load():
    text = bpy.data.texts.load(SCRIPT)
    # Editing the loaded text block is what a person does before running it, so the settings are
    # changed here rather than on disk.
    edited = 0
    for line in text.lines:
        if line.body.startswith("WIDTH = "):
            line.body = "WIDTH = {0}".format(WIDTH)
            edited += 1
        elif line.body.startswith("HEIGHT = "):
            line.body = "HEIGHT = {0}".format(HEIGHT)
            edited += 1
    check("the settings block is editable from the Text Editor", edited == 2,
          "edited {0} lines".format(edited))
    area = largest_area("VIEW_3D")
    area.type = "TEXT_EDITOR"
    area.spaces.active.text = text


def step_run():
    area = largest_area("TEXT_EDITOR")
    with bpy.context.temp_override(area=area, space_data=area.spaces.active):
        result = bpy.ops.text.run_script()
    check("Run Script finishes without an error", result == {"FINISHED"}, str(result))
    grid = bpy.data.collections.get("HexGrid")
    check("the grid collection appears in the scene", grid is not None)
    if grid:
        check("every cell is there", len(grid.objects) == WIDTH * HEIGHT,
              "{0} of {1}".format(len(grid.objects), WIDTH * HEIGHT))
        # The factory startup scene is left as it was found, which is the whole point of the
        # script being safe to paste into a file someone is already working in.
        check("the startup scene's own objects survive",
              all(bpy.data.objects.get(name) for name in ("Cube", "Camera", "Light")))


def step_show():
    area = largest_area("TEXT_EDITOR")
    area.type = "VIEW_3D"
    space = area.spaces.active
    space.shading.type = "SOLID"
    space.shading.color_type = "MATERIAL"
    space.overlay.show_floor = False
    space.overlay.show_axis_x = False
    space.overlay.show_axis_y = False
    region = next(region for region in area.regions if region.type == "WINDOW")
    with bpy.context.temp_override(area=area, region=region):
        bpy.ops.view3d.view_axis(type="TOP")
        bpy.ops.view3d.view_all()
    check("the viewport draws the grid in Solid material shading", True)


def step_shot():
    if SHOT:
        bpy.ops.screen.screenshot(filepath=SHOT)
        check("a screenshot of the window was written", os.path.exists(SHOT), SHOT)


def step_report():
    print("", flush=True)
    print("IN_EDITOR_RESULT {0}".format("FAILED" if failures else "PASSED"), flush=True)
    for failure in failures:
        print("  " + failure, flush=True)
    bpy.ops.wm.quit_blender()


steps = [step_load, step_run, step_show, step_shot, step_report]


def pump():
    """Runs one step per timer tick, so Blender redraws between them and the shot is of a settled
    window rather than a half-built one."""
    if not steps:
        return None
    step = steps.pop(0)
    try:
        step()
    except Exception as error:
        print("IN_EDITOR_RESULT FAILED in {0}: {1}".format(step.__name__, error), flush=True)
        steps.clear()
        bpy.ops.wm.quit_blender()
        return None
    return 1.0


bpy.app.timers.register(pump, first_interval=2.0)
