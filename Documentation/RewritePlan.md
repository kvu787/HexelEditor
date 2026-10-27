# Rewrite plan

Veehiicuul and Automatou are being rewritten in Rust on Direct3D 12 and other Windows APIs, with no game engine or framework. Kevin directs the creative work; models do the implementation. Blender stays the modeling tool, and building levels moves out of Blender and into the games themselves.

Both games target Windows 11 x64 only.

## Veehiicuul

A game about driving vehicles around tracks that you build inside the game.

- **Art.** Vehicles and decorative objects are modeled in Blender and exported as glb files. The game draws them with the SimplePaint shader under a fixed orthographic 3/4 overhead camera.
- **Level creator.** It's part of the game, and you work in it directly:
  - Draw a NURBS curve. The track barrier is generated from it live, as the curve changes.
  - Place the vehicle's start location, the checkpoints and decorative objects, using the models exported from Blender.
  - Paint hex grid terrain.
- **Driving.** Vehicles are driven with a gamepad through GameInput, using the models in [MovementMechanics.md](../../Veehiicuul/Documentation/MovementMechanics.md) rather than realistic physics. Movement, collision and the rest of the gameplay happen on a flat 2D plane; the 3D view only presents it.
- **Feel.** Low input latency and smooth, consistent frames matter.
- **One program.** Editing a level and driving it are two modes of the same game, working on the same level data.

## Automatou

A world-builder-and-runner: you build a world, place the units and buildings of several factions in it, and watch what happens from an all-seeing view. There's no player faction, score or victory screen. The interest comes from emergence: relatively simple parts producing complex results when they interact.

- **Simulation.** Turn-based and deterministic, on a hex grid. Each unit's automaton is its own memory and decision logic: sense, remember, think, act. Automata see the world only through a defined contract and limited perception, and they choose actions that suit their faction's theme rather than optimal play.
- **Experimenting.** Step or run the simulation, take checkpoints, rewind exactly or rewind with changed tuning, and inspect what each unit intends and why. See [Specification.md](../../Automatou/Automatou2/Specification.md).
- **Art.** Unit meshes, building meshes and possibly decorative hex terrain bases are modeled in Blender and exported as glb files, drawn with SimplePaint. The orthographic 3/4 overhead view of a miniature, LEGO-like world is a hard requirement. Units turn between six facings. See [ArtDirection.md](../../Automatou/Automatou1/Documentation/ArtDirection.md).
- **Creator.** It's part of the game, and you work in it directly:
  - Size each unit and building model to its hex cell footprint. A unit of size S occupies a center cell and S - 1 complete rings around it: 1, 7, 19, 37 cells and so on. Buildings may have any footprint made of whole cells.
  - Create hex cell maps, showing terrain on each cell by color, texture or mesh.
  - Place unit and building pieces, then build and run the simulation from there.
- **Open question.** How very long models, such as a carrier or a serpent, fit regular hexagonal footprints. The provisional preferences are in ArtDirection.md.

## Allowed dependencies

This is the complete list. Anything not on it is not allowed: no other crate, library, tool or Windows component, including ones that a listed crate would bring in through a feature that isn't listed. An agent that thinks it needs something else proposes it to Kevin instead of adding it. A new dependency goes into this list before it goes into code.

### Language and build tools

| Dependency                                        | Version    | Used for                                           |
| ------------------------------------------------- | ---------- | -------------------------------------------------- |
| Rust, edition 2024, target x86_64-pc-windows-msvc | Stable     | All game, editor, simulation and platform code     |
| cargo, rustfmt, clippy                            | With Rust  | Building and testing, formatting, lints            |
| MSVC, from Visual Studio 2026                     | 14.51      | Linking; compiling the GameInput header check      |
| Windows SDK                                       | 10.0.26100 | Import libraries; dxc                              |
| dxc, from the Windows SDK                         | 1.9        | Compiling HLSL shaders at build time               |
| PowerShell                                        | 7          | Build.ps1, Run.ps1, Validate.ps1 and other scripts |
| Git                                               | Any        | Version control                                    |

### Windows components

| Dependency                 | Version    | Used for                                                                             |
| -------------------------- | ---------- | ------------------------------------------------------------------------------------ |
| Win32                      | Windows 11 | Window, messages, raw mouse and keyboard, cursor, timing, watching the export folder |
| Direct3D 12 and DXGI       | Windows 11 | Rendering and presenting frames                                                      |
| DirectWrite                | Windows 11 | Text                                                                                 |
| WIC                        | Windows 11 | Decoding textures inside glb files                                                   |
| COM and Shell file dialogs | Windows 11 | Opening and saving levels                                                            |
| GameInput redistributable  | 3.5.283    | Gamepads                                                                             |

GameInput isn't part of Windows. Its three runtime files, GameInputRedist.dll, GameInputBridge.dll and GameInputRawInputProxy.exe, ship beside the executable and are copied from Veehiicuul2/ThirdParty/GameInput.

The C runtime is linked statically (`-C target-feature=+crt-static`), so the games don't need the Visual C++ Redistributable.

### Crates

| Crate                          | Version | When           | Used for                                                              |
| ------------------------------ | ------- | -------------- | --------------------------------------------------------------------- |
| windows-core                   | 0.100   | Run time       | COM support for the generated bindings and the GameInput declarations |
| windows-link                   | 0.100   | Run time       | Linking to Windows DLLs                                               |
| windows-result                 | 0.100   | Run time       | HRESULT and Windows errors                                            |
| windows-strings                | 0.100   | Run time       | Windows string types                                                  |
| windows-interface              | 0.100   | Build time     | The interface macro, for the GameInput declarations                   |
| windows-implement              | 0.100   | Build time     | Implementing a COM interface in Rust, only where Windows requires one |
| windows-bindgen                | 0.100   | Tool and tests | Generating the Windows bindings and checking them                     |
| windows-metadata               | 0.100   | Tool and tests | Reading Microsoft's metadata, for windows-bindgen                     |
| windows-default                | 0.100   | Tool and tests | Microsoft's metadata, for windows-bindgen                             |
| serde, with the derive feature | 1.0     | Run time       | Reading and writing level files and glTF JSON                         |
| serde_core                     | 1.0     | Run time       | serde's core traits                                                   |
| serde_derive                   | 1.0     | Build time     | serde's derive macros                                                 |
| serde_json                     | 1.0     | Run time       | JSON                                                                  |
| itoa                           | 1.0     | Run time       | Integer formatting, for serde_json                                    |
| zmij                           | 1.0     | Run time       | Floating-point formatting, for serde_json                             |
| memchr                         | 2       | Run time       | Fast byte search, for serde_json                                      |
| syn                            | 2       | Build time     | Parsing Rust code, for the macros                                     |
| quote                          | 1.0     | Build time     | Generating Rust code, for the macros and windows-bindgen              |
| proc-macro2                    | 1.0     | Build time     | Token streams, for the macros and windows-bindgen                     |
| unicode-ident                  | 1.0     | Build time     | Identifier rules, for syn and proc-macro2                             |

Build-time crates run while compiling and aren't part of the games. Tool crates run only when the bindings are regenerated or checked.

- Every windows crate uses the same version as windows-bindgen.
- Cargo.lock is committed, and every crate is vendored with `cargo vendor`, so builds work offline and no version changes without a commit.

### Content, verification and measurement

| Dependency                              | Version                   | Used for                                                                                                                       |
| --------------------------------------- | ------------------------- | ------------------------------------------------------------------------------------------------------------------------------ |
| Blender, with its bundled glTF exporter | 4.5.14                    | Modeling, and exporting glb files headless with `blender -b --python`                                                          |
| D3D12 debug layer                       | Windows 11 Graphics Tools | Failing a run on any API misuse                                                                                                |
| WARP                                    | Windows 11                | GPU checks that don't depend on the graphics card                                                                              |
| PresentMon                              | 2.6.0                     | Frame pacing and latency                                                                                                       |
| PIX                                     | Any                       | Optional: inspecting a captured frame                                                                                          |
| HexelEditor's validation scripts        | This repository           | IsolatedDesktop.ps1, DisruptionGuard.ps1, Windowing.ps1 and CaptureWindow.ps1, ported for validating without disturbing anyone |

### Rules

- **Windows APIs are used only through generated bindings.** windows-bindgen generates them from Microsoft's metadata into one committed file. To use a new Windows API, add its name to the list the script reads and regenerate. Never edit the generated file; a test regenerates it and fails if it differs.
- **GameInput is the one exception.** It isn't in Microsoft's metadata, so its declarations are written by hand with windows-core's interface macro. A C++ program compiled against the real GameInput.h checks every declared method position and struct layout. There's no hand-written binding without that check.
- **`unsafe` lives in the platform crate only.** Every other crate has `#![forbid(unsafe_code)]`.
- **C++ is used only for the GameInput header check.** No C.
- **Shaders are HLSL.**

### Not allowed

Examples of what this list rules out, so nobody has to ask:

| Kind                               | Examples                                     | Instead                                   |
| ---------------------------------- | -------------------------------------------- | ----------------------------------------- |
| Game engines and frameworks        | Godot, Unity, Unreal, Bevy, Fyrox            | The games' own code                       |
| Window, input and graphics layers  | winit, wgpu, SDL, GLFW, gilrs                | Win32, Direct3D 12 and GameInput directly |
| UI toolkits                        | Dear ImGui, egui, Qt, WinUI, web views       | The games' own immediate-mode UI          |
| Other Windows bindings             | The windows and windows-sys crates           | Generated bindings                        |
| C and C++ libraries                | D3D12MA, DirectXTex, DirectXMath, MiniEngine | The games' own code                       |
| Microsoft SDKs beyond those listed | The D3D12 Agility SDK                        | Windows 11's built-in Direct3D 12         |
| Asset crates                       | gltf, image                                  | The games' own glb loader; WIC            |
| Math and utility crates            | glam, nalgebra, rand                         | The games' own code                       |

### Built in the repositories instead

| Piece                                                                             | Ports from or checks against                                                |
| --------------------------------------------------------------------------------- | --------------------------------------------------------------------------- |
| GameInput declarations                                                            | GameInput.h, through the header check                                       |
| Vector, matrix and quaternion math                                                | -                                                                           |
| glb loader: the binary container, plus structs for the glTF fields Blender writes | Exports of Kevin's Blender models                                           |
| Renderer: GPU memory and uploads, SimplePaint, orthographic 3/4 camera, picking   | SimplePaint's specification and GPU tests in Veehiicuul/3dTestScene_CppDx12 |
| Immediate-mode editor UI, with DirectWrite text                                   | -                                                                           |
| NURBS evaluation, curve offsetting, triangulation, barrier meshes                 | Veehiicuul/Blender/TrackBuilder/TrackBuilder.py and its fixtures            |
| Hex grid math, painting, flood fill, undo                                         | HexelEditor_Wpf's core and its tests                                        |
| Vehicle movement, barrier collision, checkpoints, laps                            | MovementMechanics.md; Veehiicuul2's Racing2D                                |
| Automatou's simulation, automaton contract and programs                           | Automatou2                                                                  |
| Footprint sizer                                                                   | Automatou1's ArtDirection.md                                                |
| Deterministic random numbers                                                      | -                                                                           |
| A stable hash for state-hash tests                                                | -                                                                           |
| Logging to LogOutput                                                              | -                                                                           |
| Driven input, frame readback, replay                                              | HexelEditor_Wpf's and GodotAgentDevExperiment's checks                      |
