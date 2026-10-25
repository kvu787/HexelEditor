# Remove the readback with one context on the interface thread

Continues `2026-10-25_22-50_UTC TrySkglElementToRemoveTheReadback.md`, which tried swapping the canvas element
and backed it out.

## User

i had opus 5.5 extra try this: C:\Users\k\Repository\HexelEditor\Conversations\2026-10-25_22-50_UTC TrySkglElementToRemoveTheReadback.md

i've changed the model to fable 5.1 high and i want you to try and optimize this:
The readback stays at 1.14 ms at a window's size and 3.70 ms at 4K, inside frames of 1.3 ms and 4.0 ms against the 8.4 ms your 119 Hz display allows. If you ever want it gone, the honest framing is that it's a renderer-threading project -- one context on the UI thread, with the pixel-exact checks re-pointed at it -- not a swap.

## Assistant

The readback is gone. A frame that cost 1.3 ms at a window's size and 4.0 ms at 4K now costs 0.05 to 0.17 ms, and the full validation passes, display pass included.

**What changed.** The canvas is now a `GLWpfControl` rather than an `SKElement`. Its framebuffer is a Direct3D 9 surface shared with GL through the NV_DX_interop extension and shown through a `D3DImage`, so the renderer draws straight into what WPF composites and nothing crosses the bus. There is one GL context in the process, the element's, current on the interface thread. `GpuHexRenderer` no longer owns a thread, a GLFW window or a context: the first canvas builds it over the element's context as soon as the element has started. That was the threading project the previous conversation named, and it was what allowed the element to exist at all, since GLFW refuses a second thread's window.

**Why the window paints.** The renderer's history said a context on the interface thread blanked the whole window, which was the reason for the thread. That record also said the failure was never isolated from a software-rendering experiment running at the same time. It was not real: the display pass puts the real window on NE18NZ2 at 3824 x 2392 with 57 distinct colours, upright, grid and paper included, and `PrintWindow` captures the D3DImage-composited canvas exactly as it captured the bitmap one.

**The checks are re-pointed.** `GpuHexRenderer.Render` still exists for the pixel-exact checks and draws through the same `Draw` the window uses, into an offscreen surface of the same context, then reads that back. A new check closes the remaining gap: it makes the canvas draw a frame into its own framebuffer, reads that frame back while the framebuffer is still the canvas's to read, and compares it with the renderer's offscreen frame of the same view. All 803,880 pixels match. They did not at first. Ten pixels sat on the other side of the grid line's edge because the framebuffer has GL's bottom-left origin and the offscreen surface had a top-left one, which makes Skia plumb the coordinates into the shader differently and the driver round a `step` threshold differently. The offscreen surface now has the framebuffer's origin, and the check is exact.

**Measured, from `Scripts/Measure.ps1`:**

| Frame                                | With the readback | Without |
| ------------------------------------ | ----------------: | ------: |
| Example canvas                       |           1.28 ms | 0.05 ms |
| Large canvas, zoomed out             |           1.29 ms | 0.08 ms |
| Large canvas at 4K                   |           4.04 ms | 0.12 ms |
| Large canvas at 4K, one cell changed |           3.70 ms | 0.17 ms |

Paint one cell went from 1.31 ms to 0.15 ms end to end, and a pan from 1.18 ms to 0.07 ms. The run was a busy one on the processor, so its whole-canvas recomposite figures came back higher than the previous run's; the document says so.

**Validated.** `pwsh -File ./Scripts/Validate.ps1` passes in full: guard rules, build, 40 tests, the hidden-host verification with the new "Canvas frame" line, the display pass on NE18NZ2, and the disruption watch with nothing of the run outside that display, taking the foreground or confining the cursor. I also looked at the captured window myself: the artwork is upright and the colours are right.

**Not verified.** Only a frame's cost on the editor's own thread is measured. The compositor's and the display's share cannot be timed on a desktop that is not displayed, so whether the window now reaches all 119 frames a second while painting is reasoned rather than measured.

Two commits carry the work, one for the code and one for the document, and this record follows them.

## User

i'm not using the pc atm. can you verify this?

Not verified. Only a frame's cost on the editor's own thread is measured. The compositor's and the display's share cannot be timed on a desktop that is not displayed, so whether the window now holds all 119 frames a second while painting is reasoned rather than measured.

## User (sent while the work was running)

you can use the non-NE18NZ2 displays since i'm not using the pc currently

## Assistant

Not yet, and the reason is on your side of the machine: the session is locked. The lock screen is the foreground window on every display, so nothing I put on a display is shown, and the pointer input I drove went to the lock screen rather than to the editor. That means two things only you can change before this can be measured.

**What I found.** NE18NZ2 runs at 75 Hz; the two PA278QGV displays are the 120 Hz ones, so the question is about them, and your permission to use them is what makes the measurement possible. The plan was a real brush drag across the editor on the primary 120 Hz display while PresentMon records every frame the editor presents and when each reaches the display. I wrote the scripts for that, and ran them. The drag was driven for eight seconds each time, the cursor was put back where it was, and the editor was closed without a dialog.

**Why there is no result.** With the lock screen up the editor is not shown, so it presented nothing to measure, and the first two drags clicked and moved on the lock screen rather than on the editor, which only brought up its sign-in prompt. Driving the stroke through window messages instead reaches the editor, but still shows nothing on a display, and PresentMon recorded no frames from any process while locked, so there is also no evidence yet that this shell can see presents at all; PresentMon states that it needs elevation to attribute processes, and I will not raise a UAC prompt on a machine nobody is at.

**What I need from you.** Unlock the session and leave it unlocked for a few minutes, then say so. If you can also run the capture from an elevated PowerShell, that gives the strongest answer, since PresentMon then sees the displayed time of every frame; otherwise I will measure WPF's own render cadence from inside the editor, which needs no elevation and tells how many frames the window renders a second, though not when each one lit the display.

Nothing in the repository changed this turn beyond this record. The scripts live in my scratchpad and the trial captures in LogOutput, which is ignored.
