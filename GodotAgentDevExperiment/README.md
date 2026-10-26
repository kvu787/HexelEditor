# Godot agent development experiment

An experiment in developing, testing and measuring a Godot 4.7.2 .NET (C#) application on a machine that someone else is using, without disturbing them. The application is a small stand-in for a hex level editor: an orthographic overhead camera over a hex ground that can be painted, a Blender-exported piece drawn with the SimplePaint shader, a menu bar, dialogs and shortcuts. Everything is built in code. The findings are in [Findings.md](Findings.md).

Requirements: Godot 4.7.2 .NET at `%UserProfile%\Program\Godot_v4.7.2-stable_mono_win64`, the .NET 10 SDK, and PowerShell 7. The Godot editor is never opened; the editor executable runs the import pass headless and runs the application.

```powershell
pwsh -File ./Build.ps1                                     # headless import pass, then dotnet build
pwsh -File ./Run.ps1                                       # opens the application for a person, in front
pwsh -File ./Scripts/Test.ps1                              # the engine-free core under xUnit
pwsh -File ./Scripts/Validate.ps1                          # everything, watched: the entry point while the machine is in use
pwsh -File ./Scripts/Measure.ps1                           # frame timing on NE18NZ2, inside the engine and through PresentMon
pwsh -File ./Scripts/StartOnDisplay.ps1 -Display NE18NZ2   # the window on NE18NZ2 without focus, unwatched
```

`Build.cmd` and `Run.cmd` wrap the first two for File Explorer. `Run.cmd` is for a person using the application; an agent working while someone uses the machine goes through the scripts in `Scripts` instead. Every run writes to a timestamped folder under `LogOutput`.

| Folder        | What it holds                                                                    |
| ------------- | -------------------------------------------------------------------------------- |
| `Application` | The Godot project: `project.godot`, the C# sources, the shaders                  |
| `Core`        | Hex grid mathematics with no engine dependency, shared with the application      |
| `Tests`       | xUnit tests of the core                                                          |
| `Assets`      | `LBuilding.glb`, exported by Blender in background mode by `ExportLBuilding.py`  |
| `Scripts`     | Validation, measurement and the helpers they share with the WPF editor's scripts |

## What a validation run does

1. Checks the disruption guard's own rules.
2. Runs the import pass and the build.
3. Runs the core's xUnit tests.
4. Runs the application's in-engine checks headless: no display server at all.
5. Runs the same checks with real rendering on a Win32 desktop of their own, once per renderer: D3D12, Vulkan and OpenGL.
6. With `-Export`, exports the standalone release build without a window and runs the same checks on it.
7. Puts the real window on NE18NZ2 without focus, runs the checks there, and photographs it from outside.
8. Reports whether anything of the run became visible on a user display, took the foreground, captured the mouse or confined the cursor.

The in-engine checks (`--checks`) drive the interface through `Input.ParseInputEvent`: a button click, a click and a drag stroke on the ground, shortcut keys, the File menu by mouse and keyboard, a confirmation dialog and the file dialog, the top-down toggle, a capture of the viewport, and a timing measurement over a brush stroke. Each check reports passed, failed or skipped, and the launcher requires every one of them to have reported.
