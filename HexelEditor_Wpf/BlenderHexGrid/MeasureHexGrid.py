"""Time and measure GenerateHexGrid.py at one grid size, inside Blender.

    set HEXGRID_WIDTH=500 & set HEXGRID_HEIGHT=200
    blender --background --factory-startup --python MeasureHexGrid.py

Like the tests, this executes the real script text with only its settings constants rewritten,
so what is measured is the file a person would paste rather than a copy of it. Nothing is saved;
the point is what building the grid costs in time and memory, not what a .blend would weigh.
"""

import ctypes
import json
import os
import re
import sys
import time

import bpy

SCRIPT = os.environ.get(
    "HEXGRID_SCRIPT",
    os.path.join(os.path.dirname(os.path.abspath(__file__)), "GenerateHexGrid.py"))


def peak_memory_gb():
    class Counters(ctypes.Structure):
        _fields_ = [("cb", ctypes.c_uint32), ("PageFaultCount", ctypes.c_uint32),
                    ("PeakWorkingSetSize", ctypes.c_size_t), ("WorkingSetSize", ctypes.c_size_t),
                    ("QuotaPeakPagedPoolUsage", ctypes.c_size_t), ("QuotaPagedPoolUsage", ctypes.c_size_t),
                    ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t), ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
                    ("PagefileUsage", ctypes.c_size_t), ("PeakPagefileUsage", ctypes.c_size_t)]

    counters = Counters()
    counters.cb = ctypes.sizeof(Counters)
    # GetCurrentProcess returns the pseudo-handle -1. Left at the default restype ctypes hands
    # back a 32-bit int, which widens to 0x00000000FFFFFFFF rather than an all-ones handle, and
    # the call then fails and reports nothing.
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
    width = int(os.environ.get("HEXGRID_WIDTH", "200"))
    height = int(os.environ.get("HEXGRID_HEIGHT", "100"))
    share = os.environ.get("HEXGRID_SHARE", "1") != "0"

    with open(SCRIPT, "r", encoding="utf-8") as stream:
        source = stream.read()
    # WIDTH, HEIGHT and ADD_CAMERA exist in both generators; the rest belong to one or the other,
    # so a setting the chosen script does not have is skipped rather than treated as an error.
    required = {"WIDTH", "HEIGHT", "ADD_CAMERA"}
    for key, value in (("WIDTH", width), ("HEIGHT", height), ("SHARE_MESHES", share),
                       ("ADD_CAMERA", False), ("PROGRESS_ROWS", 0)):
        source, count = re.subn(r"^{0} = [^\n#]+".format(key), "{0} = {1!r}".format(key, value),
                                source, count=1, flags=re.MULTILINE)
        if count != 1 and key in required:
            raise AssertionError("could not find a setting named {0}".format(key))

    bpy.ops.wm.read_factory_settings(use_empty=True)
    started = time.perf_counter()
    exec(compile(source, SCRIPT, "exec"), {"__name__": "__main__", "__file__": SCRIPT})
    elapsed = time.perf_counter() - started

    cells = width * height
    print("HEXGRID_MEASUREMENT " + json.dumps({
        "script": os.path.basename(SCRIPT),
        "width": width,
        "height": height,
        "cells": cells,
        "shareMeshes": share,
        "objects": len(bpy.data.objects),
        "polygons": sum(len(mesh.polygons) for mesh in bpy.data.meshes),
        "seconds": round(elapsed, 3),
        "microsecondsPerCell": round(elapsed / cells * 1e6, 2),
        "peakMemoryGb": round(peak_memory_gb(), 3),
        "kilobytesPerCell": round(peak_memory_gb() * (1024 ** 2) / cells, 3),
    }), flush=True)


if __name__ == "__main__":
    main()
    # Tearing two million objects down on exit costs as much as building them, and the numbers are
    # already printed, so leave that to the operating system.
    sys.stdout.flush()
    os._exit(0)
