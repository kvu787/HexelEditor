"""Build a hex grid with GenerateHexGridSingleMesh.py and save it as a .blend.

    set HEXGRID_WIDTH=2000 & set HEXGRID_HEIGHT=1000
    set HEXGRID_OUTPUT=C:\\...\\Output\\HexGrid2000x1000.blend
    blender --background --factory-startup --python MakeDemoFile.py

MakeDemo.ps1 drives this. Like the tests, it runs the real script text with only its settings
constants rewritten, so the saved file is what pasting that script would have produced.

Only the single-mesh generator is offered here. Two million separate objects weigh several
gigabytes on disk and open slowly enough to be unpleasant, which is the whole reason the
single-mesh approach exists.
"""

import ctypes
import json
import os
import re
import time

import bpy

FOLDER = os.path.dirname(os.path.abspath(__file__))
SCRIPT = os.path.join(FOLDER, "GenerateHexGridSingleMesh.py")


def peak_memory_gb():
    class Counters(ctypes.Structure):
        _fields_ = [("cb", ctypes.c_uint32), ("PageFaultCount", ctypes.c_uint32),
                    ("PeakWorkingSetSize", ctypes.c_size_t), ("WorkingSetSize", ctypes.c_size_t),
                    ("QuotaPeakPagedPoolUsage", ctypes.c_size_t), ("QuotaPagedPoolUsage", ctypes.c_size_t),
                    ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t), ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
                    ("PagefileUsage", ctypes.c_size_t), ("PeakPagefileUsage", ctypes.c_size_t)]

    counters = Counters()
    counters.cb = ctypes.sizeof(Counters)
    kernel32 = ctypes.windll.kernel32
    kernel32.GetCurrentProcess.restype = ctypes.c_void_p
    handle = kernel32.GetCurrentProcess()
    for library, name in ((ctypes.windll.psapi, "GetProcessMemoryInfo"),
                          (kernel32, "K32GetProcessMemoryInfo")):
        query = getattr(library, name, None)
        if query is not None and query(ctypes.c_void_p(handle), ctypes.byref(counters), counters.cb):
            return counters.PeakWorkingSetSize / (1024 ** 3)
    raise OSError("could not read this process's memory counters")


def main():
    width = int(os.environ.get("HEXGRID_WIDTH", "2000"))
    height = int(os.environ.get("HEXGRID_HEIGHT", "1000"))
    colors = int(os.environ.get("HEXGRID_COLORS", "7"))
    output = os.path.abspath(os.environ["HEXGRID_OUTPUT"])
    compress = os.environ.get("HEXGRID_COMPRESS", "1") != "0"

    with open(SCRIPT, "r", encoding="utf-8") as stream:
        source = stream.read()
    for key, value in (("WIDTH", width), ("HEIGHT", height), ("COLOR_COUNT", colors),
                       ("ADD_CAMERA", True), ("PLAIN_COLORS", True)):
        source, found = re.subn(r"^{0} = [^\n#]+".format(key), "{0} = {1!r}".format(key, value),
                                source, count=1, flags=re.MULTILINE)
        if found != 1:
            raise AssertionError("could not find a setting named {0}".format(key))

    bpy.ops.wm.read_factory_settings(use_empty=True)
    started = time.perf_counter()
    exec(compile(source, SCRIPT, "exec"), {"__name__": "__main__", "__file__": SCRIPT})
    built = time.perf_counter()

    # Leave the saved file looking at the grid in Solid material shading, so opening it shows
    # the colours without waiting for EEVEE to compile shaders for eight million polygons.
    for screen in bpy.data.screens:
        for area in screen.areas:
            if area.type != "VIEW_3D":
                continue
            space = area.spaces.active
            space.shading.type = "SOLID"
            space.shading.color_type = "MATERIAL"
            space.overlay.show_floor = False
            space.overlay.show_axis_x = False
            space.overlay.show_axis_y = False
            space.clip_end = 100000.0

    os.makedirs(os.path.dirname(output), exist_ok=True)
    bpy.ops.wm.save_as_mainfile(filepath=output, compress=compress)
    saved = time.perf_counter()

    mesh = bpy.data.objects["HexGrid"].data
    print("DEMO_FILE " + json.dumps({
        "output": output,
        "width": width,
        "height": height,
        "cells": width * height,
        "vertices": len(mesh.vertices),
        "polygons": len(mesh.polygons),
        "materials": len(mesh.materials),
        "compressed": compress,
        "buildSeconds": round(built - started, 2),
        "saveSeconds": round(saved - built, 2),
        "fileGb": round(os.path.getsize(output) / (1024 ** 3), 3),
        "peakMemoryGb": round(peak_memory_gb(), 2),
    }), flush=True)


if __name__ == "__main__":
    main()
