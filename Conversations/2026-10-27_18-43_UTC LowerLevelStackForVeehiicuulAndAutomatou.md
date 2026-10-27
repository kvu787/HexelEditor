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

## User

is it better to have the agent use the windows crate or have it write its own bindings for windows apis

## User

Tool loaded.

## Assistant

Confirmed GameInput isn't in the `windows` crate (0.62.2 has no GameInput feature, and Win32::Gaming holds only Game Explorer and Xbox Live items). Now checking how finely windows-bindgen can filter, then I'll answer.

## Assistant

**Use Microsoft's generated bindings for everything that's in its metadata, and have the agent hand-write only what isn't. Right now that's only GameInput.**

## Why not hand-write everything

- **Hand-written bindings are the one place Rust can't check the agent.** A COM binding is a table of function pointers whose order has to match the header exactly. A skipped method in a base interface, two slots swapped, or a struct field of the wrong width all compile cleanly. At runtime they call the wrong function or corrupt memory. D3D12 is the worst case: ID3D12Device and ID3D12GraphicsCommandList each come as a chain of versions, with each version's methods appended after the last, and across D3D12 and DXGI that's hundreds of slots where a mistake is silent. That would be worse than C++, which at least compiles against the real header.
- **The `windows` crate isn't a framework.** It's generated from Microsoft's metadata, which is itself generated from the SDK headers. It has no event loop, no ownership model and no abstractions of its own; it's the Rust equivalent of `#include <d3d12.h>`. Your objections to frameworks don't apply to it: if one signature is ever wrong, you declare that one yourself.

## What it costs

- It's a big crate. Enable only the features you use; after the first build it's cached.
- It has frequent breaking releases. Pin the version and upgrade when you choose.
- It can lag the Agility SDK, so the newest D3D12 interfaces may be missing. Bind those few by hand, the same way as GameInput.

## Better still: generated bindings checked into the repo

windows-bindgen can generate just the functions and interfaces you name into one `bindings.rs`; Kenny Kerr's standalone example filters down to a single function. Regenerate it from a test or script when you decide to. You get Microsoft's layouts, a file that lists exactly which Windows APIs each game uses, nothing large to compile and no surprise upgrades. The generated COM code still needs the small `windows-core` crate for IUnknown, HRESULT and the interface plumbing. This is what I'd use.

## GameInput by hand, with a test that checks it

GameInput isn't in `windows` 0.62.2: there's no GameInput feature, and `Win32::Gaming` holds only Game Explorer, game statistics and Xbox Live items. Its header is C++ only (`namespace GameInput::v3`), 1,332 lines and seven interfaces. Veehiicuul2 calls about seven methods: GetCurrentReading, GetGamepadState, GetTimestamp, GetDevice, GetDeviceInfo, RegisterDeviceCallback and UnregisterCallback.

- Bind those interfaces with `windows-core`'s interface macro, so they look like the generated ones. Declare every method in header order, including the ones you don't call.
- Then check it mechanically. A small C++ test DLL, built with MSVC against the real GameInput.h, implements each bound interface with every method reporting its own name, and exports sizeof and offsetof for every bound struct. A Rust test calls each binding and checks that the right method answered and that every layout matches.

That turns the one unchecked spot into a checked one. I'd make it a rule in AGENTS.md: no hand-written binding without that test.

Sources:
- [windows crate features (0.62.2)](https://docs.rs/crate/windows/latest/features)
- [windows::Win32::Gaming](https://microsoft.github.io/windows-docs-rs/doc/windows/Win32/Gaming/index.html)
- [Standalone code generation](https://kennykerr.ca/rust-getting-started/standalone-code-generation.html)
- [Custom ffi bindings(?)](https://users.rust-lang.org/t/custom-ffi-bindings/142323)
