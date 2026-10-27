# Lower-level stack for Veehiicuul and Automatou

## User

refer to the projects in "C:\Users\k\Repository" for context.

i'm planning out how to have ai write veehiicuul and automatou in a lower-level tech stack, namely: dx12, c/c++/rust. this will also move more creation/building functionality away from blender and into the game itself.

for example, for veehiicuul, i will use blender to create and export gltf/glb models of vehicles and decorative objects. then in veehiicuul, there will be a level creator in which you create a nurbs curve and the track barrier is automatically created live, and then you place vehicle start location, checkpoints, and decorative objects from the exported blender models. also, the level creator will let you paint hex grid terrain.

similarly, for automatou, blender will be used to create unit meshes, building meshes, and possibly hex cell terrain base decorative meshes which will then be used by a level creator inside of automatou. you will use the automatou creator to size the units and buildings to their hex cell footprints, and then create hex cell maps with different colors or possible terrain textures or meshes on hex cells to indicate different terrain, and then place unit and building pieces, and build the simulation from there.

the main thing i want to do is to eliminate dependency on things like unity, godot, or other framework-like things and instead depend on lower-level components such as dx12, Microsoft.GameInput, windows apis, etc. the motivation for this is my switch from primarily solo-human-driven development to ai-driven development. as a solo dev, it was beneficial to use things like godot instead of writing my game at a lower level because it provided higher-level abstractions made it easier to translate my design vision into an actual game. the cost of things like godot is that the development cost increases significantly if i wanted lower-level access to things or wanted to do something that ran counter to godot's abstractions, and that i had to settle with unsatisfying workarounds to issues/quirks with godot engine because it would be too expensive to fork it and do "root-cause fixes" and maintain my own fork. another downside is that frameworks prioritize serving a scope of use cases that is much wider than my own specific games and uses, so there's a lot of stuff that reduces performance or increases complexity and understanding effort that doesn't benefit my use cases. since transitioning to a primarily ai-driven workflow, the cost/benefit analysis has changed significantly and it now makes more sense for ai to handle the implementation complexity and work in a way that is more bespoke to my use cases and less encumbered by framework baggage.

## Assistant

I'm surveying your repos — the existing C++/DX12 attempts (Veehiicuul2/3, 3dTestScene, InputLatency) matter a lot here, so I'm reading what they reached and where they stalled.

## Assistant

Still gathering context: I've read the earlier DX12 attempts, the Godot/WPF validation findings and the Automatou design notes. Now I'm checking toolchains before I write the recommendation.

## Assistant

**I agree with the direction, and your own repos make the case better than the general argument does. They also show where the real risk is: not writing low-level code, but keeping the agent on the game and checking what it builds.**

## What your repos already show

- **You've tried this once, and it stalled on process, not code.** Veehiicuul2 and Veehiicuul3 (October 8 and 9) each reached about 7,000 lines of C++/DX12 within a day: SimplePaint, GameInput, NURBS tracks, 2D driving and a custom GPU UI. Then 20 of Veehiicuul3's 41 non-conversation commits went to snapshot promotion, launcher gates, checkpoints and verification records, and both were stopped with physical checks still pending. A lot of their scope went into an in-app Catmull-Clark modeler and UI controls like a knot-vector text field. Keeping Blender as the modeler already removes the biggest piece of that.
- **The validation you built for HexelEditor carries over, and gets simpler.** [Findings.md](GodotAgentDevExperiment/Findings.md) notes that IsolatedDesktop.ps1, DisruptionGuard.ps1, Windowing.ps1 and CaptureWindow.ps1 know nothing about either framework. About half of its fifteen Godot quirks (GetMousePosition calling GetCursorPos, the forced WS_EX_APPWINDOW, no_focus making the window topmost, the CBT hook needed to place it) exist only because someone else creates the window and reads the input. In your own app you create the window at the bottom of the Z-order without activation, all input goes through one queue a test can feed, and the render target can be read back directly. That's your framework argument in concrete form.
- **There are references to port against.** TrackBuilder.py (1,621 lines) already turns closed outlines into track, ground, islands and barriers, with eleven committed fixture files and golden geometry hashes. HexelEditor's core has tested hex geometry, brushes, flood fill and undo. Automatou2 is about 3,100 lines of C# with the automaton contract in its own assembly. SimplePaint has a written specification and GPU tests. An agent porting against these has a definition of correct instead of inventing one.

## Language: I'd pick Rust

- **The compiler checks the code you won't read.** In AI-driven work, bugs that become compile errors instead of crashes or silent corruption are worth more than they are to a human author. Veehiicuul3 had an iterator stepping past the end that only its Debug build caught.
- **cargo replaces CMake, presets, Visual Studio discovery and a test framework.** `cargo test` runs the headless geometry and simulation tests.
- **Automatou's sandbox becomes enforceable.** Automatou2 keeps programs away from the world through assembly references. A programs crate that depends only on a contract crate, with `#![forbid(unsafe_code)]`, enforces the same boundary. In C++ nothing stops a program from reaching into the world.

The costs are real. Rust isn't installed; rustup with the MSVC toolchain would use your VS 2026 build tools, and installing it needs your OK. D3D12 through Microsoft's `windows` crate is all `unsafe` and wordier than C++. I don't think GameInput is in that crate's metadata, so its COM interfaces would probably be hand-bound from GameInput.h, a few hundred lines; that's worth checking first. Microsoft's DX12 samples and helpers (D3D12MA, DirectXTex, MiniEngine) are C++. But rendering is maybe a fifth of each game. The rest (track geometry, hex maps, simulation, editor state) is ordinary safe code, which is where Rust pays off.

Pick C++20 instead if you want to use Microsoft's C++ libraries directly, or if the GameInput binding turns out to be painful. C isn't worth it: without RAII or generics the agent writes more code and gets more of it wrong, for no gain over a C++ subset.

## Structure

- **Don't build a shared engine.** A library both games depend on turns back into a framework that serves two sets of needs. Build the base layer inside Veehiicuul, copy it into Automatou, and let the copies diverge; an agent can port a fix across when you want it. That also fits Automatou's rule that projects build without their siblings.
- **Keep the base layer small:** Win32 window and message pump; DX12 device, swap chain, frames, uploads and descriptors; dxc at build time; glb loading, with SimplePaint parameters as glTF extras (already proven in GodotAgentDevExperiment); the orthographic 3/4 camera; GameInput plus raw mouse and keyboard; DirectWrite text; a small immediate-mode editor UI, not a widget toolkit; file dialogs; logging to LogOutput.
- **Keep domain code free of Windows:** track generation, hex maps and the simulation, so they test headless in milliseconds.
- **Put the editor and the game in one executable**, with Edit and Play modes over the same level data. Watch Blender's export folder and reload a glb when it changes, so an export shows up in the level editor at once. Blender exports stay headless (`blender -b --python`), which already works.
- **Build the validation hooks into the app from day one:** a driven-input queue, render target readback, deterministic replay (input stream in, state hash out), and the D3D12 debug layer with GPU-based validation, where any message fails the run.

## Order

Start with Veehiicuul. It's smaller, it's real-time, and it exercises the renderer and the input-latency work you've already measured.

1. Window, DX12 clear, LogOutput and a ported Validate.ps1, before any feature.
2. The car glb under SimplePaint with the 3/4 orthographic camera, reloading on export.
3. Hex terrain painting and level save and load.
4. NURBS curve editing with live barriers, ported from TrackBuilder.py and checked against its fixtures.
5. Start position, checkpoints and decorations, then Play mode with the MovementMechanics.md models and GameInput.
6. Frame pacing and latency with PresentMon.

Then Automatou, from a copy of the base layer: hex map painting, a unit and building library from glb with a footprint sizer, the simulation ported from Automatou2 with state-hash tests, then step, run, rewind and the inspector. The footprint sizer is also the experiment your oblong-unit discussion asked for: the carrier and serpent at several ring sizes, with overhang, on screen.

## Keeping the agent on the game

- End each slice with something you can play or use, not a row in a parity table.
- Build one validation entry point once and reuse it. No snapshot promotion or verified-build bookkeeping unless you ask for it.
- Build forward from the design, not feature by feature from the Godot versions. Port behavior only where there's a reference to check against.
- Keep a dependency allowlist in each AGENTS.md (for Rust: `windows`, `serde` and `serde_json`, maybe a glTF parser, and nothing that owns the window, the loop or the renderer), vendored so builds work offline.

The language is the one decision that has to come first; everything after the first slice depends on it.

This conversation is recorded in [Conversations](Conversations/2026-10-27_18-43_UTC%20LowerLevelStackForVeehiicuulAndAutomatou.md) and committed.
