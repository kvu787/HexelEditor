# Godot rewrite for agent development

## User

let's say hexel editor was completely rewritten as a godot 4.7.2 app. would this be better, worse, or the same for development by opus 5.5 high/extra?

## Assistant

Checking Godot 4.7.2's own source, which is already on this machine, for the few facts the answer depends on.

## User

also, the agent would be able to choose between gdscript or c# as it sees fit

## Assistant

**Better, on balance, as long as it's Godot with C# and one thing checks out first.** The gain isn't that Godot is a better UI toolkit than WPF; it's worse at that. The gain is that most of the effort in this repo went into problems WPF creates and Godot mostly doesn't have.

**Where the effort has gone so far.** About 3,500 of the 8,600 lines are validation and measurement: the scripts, `MainWindowVerification`, `MainWindowMeasurement`, `PointerInput` and `KeyboardInput`. A large share of the commits either fights WPF's dependence on the real OS input state or gets a GPU canvas to coexist with WPF (the `SKGLElement` attempt, the readback, drawing into the framebuffer WPF shows).

**Better in Godot**

- **Driven input stays inside the engine.** `Input.parse_input_event` feeds Godot's own hit testing, focus and popups, and the modifiers travel in the event. The WPF quirks this repo worked around (Ctrl read from the OS key state, the pointer re-read as 30,000 units away once focus is taken, hit testing that needs a visible window) have no counterpart there. Godot calls `SetCapture` from its window procedure when a real button message arrives (`display_server_windows.cpp:6554`), so driven clicks can't capture the real mouse.
- **Menus and dialogs are embedded by default** (`display/window/subwindows/embed_subwindows` is true, `project_settings.cpp:1767`). Submenus, the new canvas dialog and Godot's own `FileDialog` are drawn inside the main window, not as separate OS windows. That removes the popup appearing at the top left of the primary display. It could also lift the "no modal dialogs" rule: Ctrl+N, Ctrl+O and Ctrl+S become testable because driven input can answer the dialog.
- **Not taking focus is one setting.** With `display/window/size/no_focus`, the main window is shown with `SW_SHOWNA`. Without it, `show_window` calls `SetForegroundWindow` (`display_server_windows.cpp:1972`).
- **The canvas is what Godot is built for.** A shader on a quad that samples a cell texture is Godot's normal case. There's no interop layer, no OpenTK and no readback, and tests can read the viewport texture from inside the engine. You also already know Godot shaders.
- **The engine source is on this machine**, so questions about its behavior can be answered by reading the code, as I just did. The WPF behavior here was mostly discovered by trial and error.
- **`HexelEditor.Core` and its 755 lines of xUnit tests carry over unchanged.** It has no WPF reference, so only the Desktop layer gets rewritten.

**Worse in Godot**

- **The UI.** Godot can lay out a VS Code-style editor (the Godot editor proves it), but its text isn't drawn with DirectWrite/ClearType and there's no native menu bar on Windows. DPI scaling takes more manual work, and an idle editor keeps redrawing unless low-processor mode is on. Expect more iterations to make it feel like a polished Windows app. XAML is something I know very well.
- **How current the API is.** WPF hasn't changed in a decade. Godot's API still changes between minor versions, and 4.7 is newer than much of what I learned from. The local source and `doc/classes` cut the cost, but expect more first-attempt API mistakes.
- **No partial texture upload.** `texture_2d_update` takes a whole image (`rendering_server.h:119`), and so does `RenderingDevice::texture_update` (`rendering_device.h:467`). Hotspot 2 in `PerformanceHotspots.md` (9.2 ms down to 0.08 ms) depends on uploading only the changed texels. In Godot you'd split the canvas into tiles or scatter the changes with a compute shader. It's solvable, but that work would have to be redone.
- **Project-file friction.** `.tscn` files carry `uid://` references, there's the `.godot` import cache, and headless runs need an `--import` pass first. Building the UI in code avoids most of this.

**One thing to test first.** I don't know whether Godot's Vulkan or D3D12 renderer will create and present a swapchain on a Win32 desktop of its own, which `IsolatedDesktop.ps1` relies on.

- If it won't, validation has to run on the displayed desktop, with the window on NE18NZ2 and `no_focus` set.
- Because input stays inside the engine, that may well be fine, but `DisruptionGuard.ps1` would have to prove it. That script works with any engine and carries over.
- The Compatibility (OpenGL) renderer is the fallback.

This question decides whether validation gets simpler or just different, and an hour-long test answers it.

**GDScript or C#: I'd write all of it in C#.**

- The cell loops (flood fill, compositing and undo records over 2 million cells) need compiled code. GDScript is interpreted and roughly ten times slower or worse on tight loops.
- C# keeps the compile-time checking that currently catches my mistakes, with warnings treated as errors.
- Mixing the two languages in one project means calls go through `Variant`, and the languages don't share types.

An all-GDScript rewrite would be worse than the current app. Godot's C# SDK defaults to `net8.0`, so either the Godot project targets net10 or Core gets multi-targeted. That's worth confirming in the same test.

The effort level doesn't change the verdict. Where the app goes next does: mostly canvas work (more tools, larger canvases, animation coming back) favors Godot. Mostly panels, dialogs and settings favors WPF.
