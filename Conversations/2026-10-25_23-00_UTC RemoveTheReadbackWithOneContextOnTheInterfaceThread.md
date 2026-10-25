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
