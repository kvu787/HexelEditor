# Hexel Editor

A desktop art editor for a hexagonal grid, built with C#, .NET 10, WPF, and SkiaSharp. Development and releases target Windows 11 x64. The interface is a light, grayscale theme modelled on Visual Studio Code's default light theme: a menu bar, a tool rail, the canvas with its document tab, and collapsible side bar sections.

![Hexel Editor](Documentation/EditorPreview.png)

Double-click `Build.cmd` at the repository root to build the editor, then double-click `Run.cmd` to start it. The wrappers use modern PowerShell. From a terminal:

```powershell
pwsh -File ./Build.ps1
pwsh -File ./Run.ps1
```

Build restores NuGet dependencies and creates the Release build. Run exits with an error if that build is missing; it never builds the app. Run starts the editor from a fresh copy of the build under `Artifacts/Run`, so an open editor never locks the build output and a build can run while the editor is open. Each run makes its own copy; copies left by editors that have exited are deleted on the next run. The "Neon tide" example opens with three editable layers. Use File > New canvas for a blank document, or open `Examples/NeonTide.hexel`.

## Editing

- Pointy-top and flat-top grids, up to 256 columns and 256 rows.
- Pencil and eraser with hexagonal brushes, connected flood fill, eyedropper, lines, and outlined or filled rectangles.
- Rectangular selections, cell copying and pasting, movement, and exact 60-degree rotations. Transforms clip at the canvas boundary and can be undone.
- Layers with names, visibility, locking, opacity, and ordering, each holding its own cells.
- Editable color values (`#RRGGBB` or `#AARRGGBB`) and a project palette. The palette opens as a 16 by 16 grid of 256 colors:
  a grey ramp from black to white, then one row per hue shaded dark to light. Colors you add appear below the grid.
- A solid canvas background under every cell, white on a new canvas and changeable at any time from the Color side bar section or View > Canvas background color. It is saved with the project and can be undone like any other edit.
- Stroke-level undo/redo, a saved-state indicator, and a prompt before replacing unsaved artwork.
- Compressed `.hexel` projects and transparent PNG and SVG export.

The current release supports up to 32 layers, a 256 by 256 canvas, and 512 palette colors, 256 of which a new project starts with. Undo history retains up to 200 commands within a 64 MiB budget, keeping at least the most recent command. Copy/paste uses an internal cell clipboard. Animation, standard bitmap import, and GIF export are not implemented.

## Shortcuts

| Action                              | Shortcut                              |
| ----------------------------------- | ------------------------------------- |
| Pencil / eraser / fill / picker     | B / E / G / I                         |
| Line / rectangle / selection        | L / U / M                             |
| Erase with current brush            | Right mouse button                    |
| Temporarily pick a color            | Alt + click                           |
| Decrease / increase brush radius    | [ / ]                                 |
| Pan                                 | Space + drag, or middle mouse + drag  |
| Zoom                                | Mouse wheel at cursor, or + / -       |
| Fit canvas / toggle grid            | F / H                                 |
| New / open / save                   | Ctrl+N / Ctrl+O / Ctrl+S              |
| Save as                             | Ctrl+Shift+S                          |
| Undo / redo                         | Ctrl+Z / Ctrl+Y, or Ctrl+Shift+Z      |
| Copy / cut / paste at cursor        | Ctrl+C / Ctrl+X / Ctrl+V              |
| Select all / deselect               | Ctrl+A / Ctrl+D                       |
| Move selected cells                 | Arrow keys                            |
| Rotate clockwise / counterclockwise | Ctrl+R / Ctrl+Shift+R                 |
| Clear selected cells or layer       | Delete                                |
| Deselect                            | Esc                                   |

Export the artwork using the top-right Export PNG button. File also contains the SVG export. PNG uses a 16-pixel hexagon radius with transparency and no editor grid or canvas background; cells that hold no color stay transparent. SVG preserves hexagon geometry and alpha. Layer order, visibility, and opacity affect exports.

## Build and verification

Install the .NET 10 x64 SDK and PowerShell 7 if they are not already available. Visual Studio is optional. The SDK includes the WPF build tools. NuGet downloads require internet access on the first restore. Dependency versions and lock files are checked into the repository.

```powershell
pwsh -File ./Build.ps1
pwsh -File ./Scripts/Test.ps1
pwsh -File ./Scripts/Verify.ps1
pwsh -File ./Scripts/Publish.ps1
```

- Build compiles the complete solution in Release mode with warnings treated as errors.
- Test builds and runs the xUnit suite in process, without needing VSTest's interprocess service. It covers hex geometry, editing, compositing, selections, undo/redo, layer operations, and project persistence.
- Verify also starts a hidden WPF rendering host, renders the real editor, opens the menu bar, lays out the dialogs, saves and reloads the example, checks the PNG and SVG exports, including that nothing shows between neighboring opaque cells, renders the canvas background and samples every cell to confirm it is one solid color, lays out the palette grid and chooses and adds a color through its real controls, and checks session logging. Its logs and generated artifacts go to a timestamped folder under `LogOutput`. No physical display is used.
- Publish creates a self-contained application at `Artifacts/Release/HexelEditor.exe`. Copy the entire Release folder to another Windows 11 x64 computer; no separate .NET installation is needed. Publishing uses separate intermediate outputs from development builds.

Scripts store the CLI home, NuGet cache, test temporary files, and build outputs inside the ignored `Artifacts` directory or the projects' ignored `bin` and `obj` folders. The root build and run scripts share the executable path `Source/HexelEditor.Desktop/bin/Release/net10.0-windows10.0.26100.0/win-x64/HexelEditor.exe`. Both accept `-Configuration Debug` for a Debug build. `Run.ps1 -Project 'C:\Artwork\Scene.hexel'` opens a project at startup.

### Checking the editor on a physical display

Verify covers behavior without a display. To see the real interface at real pixel density while someone is using the machine, the editor can be put on a display it will not take the keyboard away from:

```powershell
pwsh -File ./Run.ps1 -Display NE18NZ2
pwsh -File ./Scripts/CaptureWindow.ps1 -OutputPath ./LogOutput/Editor.png
```

`-Display` fills the named monitor and starts the window without activating it: it is placed before its first frame, so it never appears on another display first, it carries `WS_EX_NOACTIVATE`, and the editor skips the focus calls it normally makes while loading. `CaptureWindow.ps1` photographs it with `PrintWindow`, which asks the window to draw itself and so needs neither the foreground nor an unobstructed view, and it fails if the foreground moved while it worked. Without `-Display` the editor opens normally, in front.

This pass is look-only. Reading the automation tree is safe, but `InvokePattern.Invoke` activates the window whatever the control does, so clicking belongs in the hidden host, where Verify raises the events on the real controls instead.

## Session logs

Every editor run creates `LogOutput/yyyy-MM-dd_HH-mm-ss` at the repository root, including direct executable launches. `Session.log` records startup, shutdown, project opens and saves, exports, and errors with exception details. Sessions started within the same second wait for the next available timestamp so their logs stay separate. `LogOutput/` is ignored by Git. For a published application copied outside the repository, `LogOutput` is created beside the executable.

## Source layout

- `Source/HexelEditor.Core`: document model, axial/offset geometry, six-neighbor algorithms, editing history, example artwork, and project serialization. It has no WPF or Skia dependency.
- `Source/HexelEditor.Desktop`: WPF interface, pointer and keyboard handling, Skia canvas, and image exporters. `App.xaml` holds the whole theme: color brushes and the control templates for buttons, inputs, menus, scroll bars, and side bar sections. The canvas uses Skia's software rendering through WPF with viewport culling; each hexel is data, rather than a separate WPF control. Cells that share a color are filled as one path, and every cell reaches a fraction of a pixel past its edges, so neighbors overlap rather than meet and no background shows between them when the grid is off.
- `Tests/HexelEditor.Tests`: xUnit tests for the editing core.
- `Build.cmd` / `Build.ps1` and `Run.cmd` / `Run.ps1`: repository-root build and run entry points. Run launches a copy of the build so that editing and building the repository never wait on a running editor.
- `Scripts`: shared PowerShell helpers, tests, verification, and publishing. The shared helper requires PowerShell 7 or later.
- `Documentation`: images used by this file.
- `Examples`: the saved example project.
- `Conversations`: the verbatim conversation history required by `AGENTS.md`.

Project files are ZIP archives containing `Document.json` and little-endian 32-bit ARGB buffers at `Cells/<layer>.bin`. Saves replace the destination only after writing a complete temporary archive. Loads validate dimensions, buffer lengths, and resource limits before creating cell buffers. The reader accepts the current format only; no migration or backward-compatibility layer is maintained.
