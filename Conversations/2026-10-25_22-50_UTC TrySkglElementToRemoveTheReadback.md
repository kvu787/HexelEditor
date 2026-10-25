# Try SKGLElement to remove the readback

Continues `2026-10-25_18-02_UTC OptimiseEveryPerformanceHotspot.md`, which removed nine of the ten hotspots
and left the frame readback with the reasons for leaving it.

## User

Go ahead and try the SKGLElement change to remove the readback, and back it out if the display pass can't be
made to work.

## Assistant

Tried, and backed out. It fails earlier than either of the risks I had named.

### What was tried

In two stages, so that the gating unknown was answered before anything was restructured. Stage one was the
smallest possible spike: change the base class from `SKElement` to `SKGLElement` and nothing else, keeping
the readback, to find out whether a GL-composited canvas works in this window and survives the display pass
at all. Stage two, moving the shader and its textures into the element's own context, was never reached.

### What it proved

The canvas could not be constructed:

```
GLFWException: Can only create windows on the Glfw main thread. (Thread from which Glfw was first created).
   at OpenTK.Windowing.Desktop.NativeWindow..ctor
   at OpenTK.Wpf.DxGlContext.GetOrCreateSharedOpenGLContext
   at OpenTK.Wpf.GLWpfControl.Start
   at SkiaSharp.Views.WPF.SKGLElement.Initialize
   at HexelEditor.Desktop.HexCanvas..ctor
```

`SKGLElement` derives from `GLWpfControl`, and `GLWpfControl` gets its context by creating an OpenTK GLFW
window. `GpuHexRenderer` has already created one on the render thread it owns, and GLFW allows windows only
from the thread that first used it.

This is the same constraint `GpuHexRenderer.Shared` already records as its reason for being a singleton.
What is new is that it reaches the WPF element too, because the element gets its context the same way. The
two cannot coexist, so removing the readback would mean one GL context owned by the element on the interface
thread -- which is where this renderer's history records a context stopping the whole window from painting
-- with the offscreen path the pixel-exact checks depend on rendered through it as well. That is a change to
how the editor is put together, not an optimisation, so it stopped there.

Whether `PrintWindow` can capture a `D3DImage`-composited surface was never reached and remains unanswered.

### What the attempt cost, and what it found

The spike disturbed the machine, which should be said plainly. When the canvas failed to build, the editor's
unhandled-exception handler put a message box on the primary display and gave it the foreground, while a run
launched with `--show-without-focus` was supposed to be confined to NE18NZ2. The disruption watch caught it
and failed the run:

```
A window of the run took the foreground: HexelEditor window 'Hexel Editor' [#32770]
A dialog opened outside NE18NZ2: HexelEditor window 'Hexel Editor' [#32770]
```

That is a real gap rather than a one-off: any crash in a placed run could do it. Such a run now logs and
exits without a dialog, as verification and measurement already did, and `KeepFocusAway` is settled before
the window is built rather than after -- the editor focuses its canvas while setting its first tool, which
is part of the construction the flag is meant to cover.

### Where things stand

The spike is reverted; `HexCanvas` is an `SKElement` again. `Scripts/Validate.ps1` passes in full: guard
rules, build, tests, hidden-host verification, display pass on NE18NZ2 at 3824 x 2392 with 57 distinct
colours, and nothing of the run outside that display, taking the foreground or confining the cursor.

The readback stays at 1.14 ms at a window's size and 3.70 ms at the validation display's, inside frames of
1.3 ms and 4.0 ms against the 8.4 ms a 119 Hz display allows.
