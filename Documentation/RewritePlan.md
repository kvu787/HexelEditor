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

- **Rust stable**, edition 2024, target x86_64-pc-windows-msvc: all game, editor, simulation and platform code.
- **cargo, rustfmt and clippy**, which come with Rust: building and testing, formatting, and lints.
- **MSVC 14.51**, from Visual Studio 2026: linking, and compiling the GameInput header check.
- **Windows SDK 10.0.26100**: import libraries, and dxc.
- **dxc 1.9**, from the Windows SDK: compiling HLSL shaders at build time.
- **PowerShell 7**: Build.ps1, Run.ps1, Validate.ps1 and other scripts.
- **Git**: version control.

### Windows components

Everything here except GameInput is part of Windows 11.

- **Win32**: the window, messages, raw mouse and keyboard, the cursor, timing, and watching the export folder.
- **Direct3D 12 and DXGI**: rendering and presenting frames.
- **DirectWrite**: text.
- **WIC**: decoding textures inside glb files.
- **COM and the Shell file dialogs**: opening and saving levels.
- **GameInput redistributable 3.5.283**: gamepads. Its three runtime files, GameInputRedist.dll, GameInputBridge.dll and GameInputRawInputProxy.exe, ship beside the executable and are copied from Veehiicuul2/ThirdParty/GameInput.

The C runtime is linked statically (`-C target-feature=+crt-static`), so the games don't need the Visual C++ Redistributable.

### Crates

- Every windows crate uses the same version as windows-bindgen.
- Cargo.lock is committed, and every crate is vendored with `cargo vendor`, so builds work offline and no version changes without a commit.

#### Run time

These are compiled into the games.

- **windows-core 0.100**: COM support for the generated bindings and the GameInput declarations.
- **windows-link 0.100**: linking to Windows DLLs.
- **windows-result 0.100**: HRESULT and Windows errors.
- **windows-strings 0.100**: Windows string types.
- **serde 1.0, with the derive feature**: reading and writing level files and glTF JSON.
- **serde_core 1.0**: serde's core traits.
- **serde_json 1.0**: JSON.
- **itoa 1.0**: integer formatting, for serde_json.
- **zmij 1.0**: floating-point formatting, for serde_json.
- **memchr 2**: fast byte search, for serde_json.

#### Build time

These run while compiling and aren't part of the games.

- **windows-interface 0.100**: the interface macro, for the GameInput declarations.
- **windows-implement 0.100**: implementing a COM interface in Rust, only where Windows requires one.
- **serde_derive 1.0**: serde's derive macros.
- **syn 2**: parsing Rust code, for the macros.
- **quote 1.0**: generating Rust code, for the macros and windows-bindgen.
- **proc-macro2 1.0**: token streams, for the macros and windows-bindgen.
- **unicode-ident 1.0**: identifier rules, for syn and proc-macro2.

#### Tool and tests

These run only when the bindings are regenerated or checked.

- **windows-bindgen 0.100**: generating the Windows bindings and checking them.
- **windows-metadata 0.100**: reading Microsoft's metadata, for windows-bindgen.
- **windows-default 0.100**: Microsoft's metadata, for windows-bindgen.

### Content, verification and measurement

- **Blender 4.5.14, with its bundled glTF exporter**: modeling, and exporting glb files headless with `blender -b --python`.
- **D3D12 debug layer**, from Windows 11's Graphics Tools: failing a run on any API misuse.
- **WARP**, part of Windows 11: GPU checks that don't depend on the graphics card.
- **PresentMon 2.6.0**: frame pacing and latency.
- **PIX**, optional: inspecting a captured frame.
- **HexelEditor's validation scripts**, from this repository: IsolatedDesktop.ps1, DisruptionGuard.ps1, Windowing.ps1 and CaptureWindow.ps1, ported for validating without disturbing anyone.

### Rules

- **Windows APIs are used only through generated bindings.** windows-bindgen generates them from Microsoft's metadata into one committed file. To use a new Windows API, add its name to the list the script reads and regenerate. Never edit the generated file; a test regenerates it and fails if it differs.
- **GameInput is the one exception.** It isn't in Microsoft's metadata, so its declarations are written by hand with windows-core's interface macro. A C++ program compiled against the real GameInput.h checks every declared method position and struct layout. There's no hand-written binding without that check.
- **`unsafe` lives in the platform crate only.** Every other crate has `#![forbid(unsafe_code)]`.
- **C++ is used only for the GameInput header check.** No C.
- **Shaders are HLSL.**

### Not allowed

Examples of what this list rules out, so nobody has to ask:

- **Game engines and frameworks**: Godot, Unity, Unreal, Bevy, Fyrox. Use the games' own code instead.
- **Window, input and graphics layers**: winit, wgpu, SDL, GLFW, gilrs. Use Win32, Direct3D 12 and GameInput directly instead.
- **UI toolkits**: Dear ImGui, egui, Qt, WinUI, web views. Use the games' own immediate-mode UI instead.
- **Other Windows bindings**: the windows and windows-sys crates. Use the generated bindings instead.
- **C and C++ libraries**: D3D12MA, DirectXTex, DirectXMath, MiniEngine. Use the games' own code instead.
- **Microsoft SDKs beyond those listed**: the D3D12 Agility SDK. Use Windows 11's built-in Direct3D 12 instead.
- **Asset crates**: gltf, image. Use the games' own glb loader and WIC instead.
- **Math and utility crates**: glam, nalgebra, rand. Use the games' own code instead.

### Built in the repositories instead

Where a piece has existing code or a spec behind it, that's named after it.

- **GameInput declarations.** Checked against GameInput.h by the header check.
- **Vector, matrix and quaternion math.**
- **glb loader**: the binary container, plus structs for the glTF fields Blender writes. Checked against exports of Kevin's Blender models.
- **Renderer**: GPU memory and uploads, SimplePaint, the orthographic 3/4 camera, picking. Ported from SimplePaint's specification and the GPU tests in Veehiicuul/3dTestScene_CppDx12.
- **Immediate-mode editor UI**, with DirectWrite text.
- **Track geometry**: NURBS evaluation, curve offsetting, triangulation, barrier meshes. Ported from Veehiicuul/Blender/TrackBuilder/TrackBuilder.py and its fixtures.
- **Hex grid**: math, painting, flood fill, undo. Ported from HexelEditor_Wpf's core and its tests.
- **Driving**: vehicle movement, barrier collision, checkpoints, laps. Ported from MovementMechanics.md and Veehiicuul2's Racing2D.
- **Automatou's simulation**: the automaton contract and programs. Ported from Automatou2.
- **Footprint sizer.** Follows Automatou1's ArtDirection.md.
- **Deterministic random numbers.**
- **A stable hash**, for state-hash tests.
- **Logging**, to LogOutput.
- **Driven input, frame readback and replay.** Ported from the checks in HexelEditor_Wpf and GodotAgentDevExperiment.
