# Findings: developing a Godot 4.7.2 C# application without disturbing anyone

The question was to what extent an agent can develop, test and measure a Godot 4.7.2 .NET application on this machine while someone is using it, with NE18NZ2 the only display it may touch. The answer, from building and running the application in this folder: to the same extent as the WPF editor, with less machinery, and with one cost the WPF editor does not have, a taskbar button during the pass that puts the window on NE18NZ2. Everything below was done while the machine was in use, with Age of Empires IV in the foreground for part of it, and the disruption guard watching every run that put a window anywhere.

## What works, and how

| Activity                                  | Without disturbing anyone  | How                                                                                                    |
| ----------------------------------------- | -------------------------- | ------------------------------------------------------------------------------------------------------ |
| Import pass and build                     | Yes                        | `godot --headless --editor --import`, then `dotnet build`. No window at any point.                     |
| Restoring the engine's NuGet packages     | Yes, offline               | Godot ships them in `GodotSharp\Tools\nupkgs`; `NuGet.Config` points there.                            |
| Unit tests of engine-free code            | Yes                        | A plain .NET class library under xUnit, as for the WPF editor.                                         |
| Driving the real interface                | Yes                        | `Input.ParseInputEvent` from inside the process: hit testing, focus, popups, dialogs all real.         |
| Interface checks with no display server   | Yes                        | `--headless`. Everything but pixels; the window has to be given a size in code.                        |
| Real rendering with no display            | Yes                        | A Win32 desktop of its own. D3D12, Vulkan and OpenGL all rendered; frames read back inside the engine. |
| The real window at real pixel density     | Yes, with a taskbar button | NE18NZ2, `no_focus` through `override.cfg`, position converted to the engine's coordinates.            |
| Photographing the window from outside     | Yes                        | `PrintWindow`, as for the WPF editor; wait for the checks first or the picture is the boot splash.     |
| Frame timing inside the engine            | Yes                        | Frame interval, the renderer's own CPU and GPU times, and the cost of a texture upload, per frame.     |
| Frame timing from outside, to the display | Yes, unelevated            | PresentMon on NE18NZ2, as a member of Performance Log Users; present mode, latency to display.         |
| Exporting a standalone release build      | Yes                        | `godot --headless --export-release`, once a solution file sits next to `project.godot`.                |
| Running the exported build                | Yes                        | On a desktop of its own, through the same checks: `Validate.ps1 -Export`.                              |
| Exporting assets from Blender             | Yes                        | `blender -b --python`, which opens no window; custom properties come through as glTF extras.           |
| The Godot editor's own interface          | No                         | Not needed for any of the above. If it ever is, that is what a second machine would be for.            |
| Real mouse and keyboard, through Windows  | No                         | `SendInput` reaches the displayed desktop only. The same limit as the WPF editor's `Exercise.ps1`.     |
| Click-to-photon latency                   | No                         | PresentMon times it from real clicks, which are the thing that cannot be made here.                    |

The checks the application runs on itself, in every mode: a button click, a click and a drag stroke on the hex ground, Ctrl+Z, tool shortcut keys, the File menu opened by mouse and navigated by keyboard, a confirmation dialog answered by a click on its OK button, the file dialog opened and cancelled, the glTF piece under SimplePaint with its parameters read from Blender's custom properties, the top-down camera toggle, a capture of the viewport with the painted pixel checked for its color, and a timing measurement over a brush stroke. Fifteen checks; thirteen pass headless and two are skipped there for want of a renderer; all fifteen pass under each of the three renderers on a desktop of their own and on NE18NZ2.

## What the engine does that has to be known

Each of these cost a failed run or a wrong number before it was understood. They are the real content of this experiment.

1. **`Viewport.GetMousePosition` asks Windows for the real cursor.** `viewport.cpp:1495` calls `DisplayServer::mouse_get_position`, which is `GetCursorPos`. On a desktop with no cursor it answered 1,478,485,900. An application that is to be driven from inside must take positions from the events it receives and never from the viewport; `Control.GetLocalMousePosition` and `GetGlobalMousePosition` are the same call. The same is true of `PopupMenu` for a popup that is not embedded, which is one more reason to keep popups embedded.
2. **Screen coordinates start at the virtual desktop's corner, not the primary display's.** NE18NZ2 sits at Windows (5120, -958), so the engine's coordinate for a point on it is (x, y + 958). `--position 5160,-918` landed off every screen, and the engine clamped the window onto the primary display, which the disruption guard caught: "A window became visible outside NE18NZ2". `Scripts/StartOnDisplay.ps1` converts through `SystemInformation.VirtualScreen`.
3. **The main window always carries `WS_EX_APPWINDOW`** (`display_server_windows.cpp:2572`), so the taskbar shows "Godot Engine - 1 running window" on every taskbar for as long as the window is open: observed on `Shell_TrayWnd` and both `Shell_SecondaryTrayWnd`. `no_focus` gives `WS_EX_NOACTIVATE`, so the button does not flash and a click on it would not steal the foreground, but it is there. The engine's `--wid` embedding is the one path that omits the style, and it calls `SetForegroundWindow` on show, so it is not usable. What remains: accept the button for the display pass, which is the one pass that needs a display at all; set Windows to show buttons only on the taskbar of the display a window is on (`MMTaskbarMode`), a user setting, after which the button appears on NE18NZ2's taskbar alone; or a one-line engine patch.
4. **`no_focus` has to be a project setting.** The window is shown before any script runs, with `SetForegroundWindow` unless `no_focus` is set (`display_server_windows.cpp:1972`). Windows lets a background process take the foreground once the user has been idle long enough, which is how the WPF hidden host once took it, so this is not optional. `Scripts/StartOnDisplay.ps1` writes `override.cfg` with the setting, waits for the window, and removes the file, so a person's own run never picks it up.
5. **Consecutive mouse motion events in one frame are merged into the last** (`Input.UseAccumulatedInput`, on by default). A twelve-step stroke driven in one frame painted two cells. The checks turn it off; a real mouse never sends enough events for it to matter either way.
6. **`Performance.Monitor.TimeProcess` is the longest process time seen in the current second**, set once a second (`main.cpp:5136`), not the last frame's. It reported 445 ms for every frame: the startup frame. The engine's `ViewportSetMeasureRenderTime` and `ViewportGetMeasuredRenderTimeCpu/Gpu` are per frame and are what the measurement uses.
7. **`Image.SetPixel` truncates each channel to a byte** rather than rounding, so a color read back differs from the one written by up to one part in 255. Compare with a tolerance.
8. **A sampler that holds colors a person picked needs `source_color`.** Without it the texels are read as linear and a painted (0.85, 0.20, 0.15) came back on screen as (0.93, 0.49, 0.42). With it the pixel came back within 0.8 of 255 of what was painted.
9. **`ImageTexture.Update` uploads the whole texture.** 0.013 ms on a 48 x 40 grid; 0.335 ms on 1024 x 1024 (4 MB), per painted cell. A 2048 x 2048 map would cost about 1.3 ms a stroke frame, so a large map wants its cells in tiles, each uploaded only when a stroke touches it.
10. **Headless, the root window is a few pixels square.** The interface was laid out into nothing and the first click missed its button. `GetWindow().Size = (1280, 800)` makes the layout real; nothing is drawn either way.
11. **The import pass writes a `.uid` file next to every script and shader.** They are what scene files reference and belong in the repository.
12. **The export plugin wants a solution file next to `project.godot`**, named after the assembly; `.slnx` is accepted. The export builds the C# project itself, in its `ExportRelease` configuration.
13. **PresentMon without elevation cannot see a process start**, so `--terminate_on_proc_exit` never fires; it runs for a fixed time and is stopped once the application has exited. It still records every frame of the process by name.
14. **A window photographed as soon as it appears shows the engine's boot splash.** The display pass waits for the checks to have reported before it takes the picture.
15. **`Start-Process -WindowStyle Hidden` hides the engine's window for good.** Windows applies the hidden style to the first `ShowWindow(SW_SHOW)` of a program's main window, and the engine shows its window that way unless `no_focus` is set (`display_server_windows.cpp:1971`). With `no_focus` it uses `SW_SHOWNA`, which Windows leaves alone, so validation, which only ever started the window with `no_focus`, never saw it. A person's double-click on `Run.cmd` started an application with no visible window. `Run.ps1` is now the launch for a person alone and passes no window style; the agents' launch is `Scripts/StartOnDisplay.ps1`.

## Numbers

All medians over 240 frames of a brush stroke, from `LogOutput/Validation_2026-10-26_13-15-03`, the measurement run before it and the `-Export` validation after it, taken while the machine was in use, so the intervals carry whatever else the processor and GPU were doing; the renderer's own times are steadier. `Scripts/Validate.ps1` re-measures them on every run.

| Run                                | Renderer                    | Frame interval | Render, CPU side | Render, GPU | Upload per paint |
| ---------------------------------- | --------------------------- | -------------: | ---------------: | ----------: | ---------------: |
| Headless, 48 x 40                  | none                        |        6.90 ms |                - |           - |         0.006 ms |
| Own desktop, 1024 x 1024           | Forward+ on D3D12           |        9.01 ms |          0.12 ms |     0.04 ms |         0.323 ms |
| Own desktop, 1024 x 1024           | Forward+ on Vulkan          |       10.16 ms |          0.13 ms |     0.05 ms |         0.304 ms |
| Own desktop, 1024 x 1024           | Compatibility on OpenGL 3.3 |       11.22 ms |          0.30 ms |     0.03 ms |         0.326 ms |
| Own desktop, 1024 x 1024, exported | Forward+ on D3D12           |        2.68 ms |          0.09 ms |     0.03 ms |         0.515 ms |
| NE18NZ2, 48 x 40                   | Forward+ on D3D12           |        3.10 ms |          0.08 ms |     0.03 ms |         0.014 ms |
| NE18NZ2, 1024 x 1024               | Forward+ on D3D12           |        2.26 ms |          0.08 ms |     0.03 ms |         0.335 ms |

The first run after the shader was written put the OpenGL renderer's CPU-side time at 9.65 ms; the next run's was 0.30 ms. A first run compiles shaders, so one run's figure is not a figure. On the 48 x 40 grid the upload is 0.013 ms under every renderer.

PresentMon over the 1024 x 1024 run on NE18NZ2, 898 presented frames, DXGI, "Composed: Flip", sync interval 1:

| Measure                 |   Median |      p95 |
| ----------------------- | -------: | -------: |
| Between presents        |  2.31 ms | 10.95 ms |
| Between display changes |  4.16 ms |  4.21 ms |
| CPU busy                |  2.22 ms | 10.86 ms |
| CPU wait in Present     |  0.11 ms |  0.20 ms |
| GPU time                |  4.16 ms |  7.38 ms |
| CPU start to GPU start  | 10.57 ms | 13.00 ms |
| Render to present       | 10.66 ms | 13.00 ms |
| Present to display      | 18.03 ms | 20.27 ms |

The display changed every 4.16 ms, which is NE18NZ2's 240 Hz, and a frame reached it a median 18 ms after it was started. The application presented far more often than the display changed, because the window is composed by the desktop window manager rather than flipped to the display directly; that, the swapchain depth and vsync are the knobs end-to-end latency work turns, and this is the instrument that shows what each one did, without anyone having to look at the display. What it cannot show is click-to-photon, which needs a real click.

## Compared with the WPF editor's validation

What carried over unchanged: `IsolatedDesktop.ps1`, `DisruptionGuard.ps1`, `Windowing.ps1` and `CaptureWindow.ps1`, dot-sourced from the WPF editor's folder. They know nothing about either framework.

What became simpler. The WPF editor needs a hidden rendering host of its own, two input classes that post window messages and set the thread's key state (`PointerInput`, `KeyboardInput`, 300 lines), a rule that pointer checks precede keyboard checks because taking the focus breaks the pointer, a rule against opening any modal dialog, and a GPU path that had to be fought into WPF. Here the input driver is ninety lines (`DrivenInput.cs`) and needs no window message, no key state and no focus; menus and dialogs are embedded in the window and are opened, navigated and answered by driven input, so Ctrl+N, Ctrl+O and Ctrl+S would be checkable; the GPU is the engine's own, with no readback and no interop; and the same checks run in three places with no code that knows which.

What became harder. The window's position and `no_focus` have to be arranged from outside through `override.cfg` and a coordinate conversion, and the taskbar button cannot be arranged away.

## What a second machine would add

With everything above working here, a second machine or a virtual machine is for three things only: exercising the application through Windows' own input, as `Exercise.ps1` does for the WPF editor; click-to-photon latency; and any task that needs the Godot editor's own interface. A virtual machine without the RTX 5090 passed through would serve the first and third, and no timing measured there would mean anything. None of the three was needed to build this.
