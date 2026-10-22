# Hexel Editor Technology Stack

## User

# AGENTS.md instructions for C:\Users\k\Repository\HexelEditor

<INSTRUCTIONS>
# Base template

## Style

For single and double quotes, only use the ASCII forms: ', "
Never use these: “, ”, ‘, ’, etc.

## Development platform compatibility

Support Windows 11 x64 as the only development platform.

## Folder and file naming

This only applies to things that we have the freedom to name as wanted.
Use CamelCase.
Use complete proper words. Don't use typical shortenings. Good: Source, Documentation. Bad: src, docs.

# PowerShell

Use modern PowerShell whose command should be `pwsh`, not legacy PowerShell.

## Git

When implementing stuff, avoid difficult-to-review "mega-commits".
When it makes sense, split work into multiple commits to make it easier to review.
Separate commits that record conversations from other commits.

## Markdown tables

Tables in Markdown must be padded and aligned in a way to make them easy to read in a plaintext editor, not only in a Markdown viewer.

## Mathematical notation in Markdown

Any mathematical notation in Markdown files (LaTeX, KaTeX, MathJax, etc) must display properly in VSCode's Markdown previewer, GitHub.com's Markdown displayer, and the markdown viewer in the Windows 11 ChatGPT app.

## PowerShell

All PowerShell scripts must use:

- Set-StrictMode -Version Latest
- $ErrorActionPreference = 'Stop'

## Conversations

Record verbatim and commit all conversations in a folder named `Conversations` located at the root of this Git repo.
Use one file per conversation.
Prefix these files with the PowerShell evaluation of `[DateTime]::UtcNow.ToString("yyyy-MM-dd_HH-mm") + "_UTC"`.
Prefix these commits with `[cnv]`.
If I attach images to prompts, save and record these in the conversation logs.
If the conversation begins with `dnr`, then do not record the conversation.
The conversation file name must also include a human-readable subject line.
So, the format for the conversation file name should be: `<timestamp> <subject>.md`.

## Application compatibility

Do not attempt to maintain any sort of application compatibility between different commits of the repo. This creates unwanted complexity.

## Target platform compatibility

Support Windows 11 x64 as the only target platform.

## Using physical displays

If you need a display, only use NE18NZ2.

</INSTRUCTIONS><environment_context>
  <cwd>C:\Users\k\Repository\HexelEditor</cwd>
  <shell>powershell</shell>
  <current_date>2026-10-22</current_date>
  <timezone>America/Los_Angeles</timezone>
  <filesystem><workspace_roots><root>C:\Users\k\Repository\HexelEditor</root><root>C:\Users\k\.codex\visualizations\2026\10\22\01a1653f-d59f-76f0-994a-c6b99b2390a7</root></workspace_roots><permission_profile type="managed"><file_system type="restricted"><entry access="read"><special>:root</special></entry><entry access="write"><path>C:\Users\k\Repository\HexelEditor</path></entry><entry access="write"><path>C:\Users\k\.codex\visualizations\2026\10\22\01a1653f-d59f-76f0-994a-c6b99b2390a7</path></entry><entry access="write"><special>:slash_tmp</special></entry><entry access="write"><special>:tmpdir</special></entry><entry access="read"><path>C:\Users\k\Repository\HexelEditor\.git</path></entry><entry access="read"><path>C:\Users\k\.codex\visualizations\2026\10\22\01a1653f-d59f-76f0-994a-c6b99b2390a7\.git</path></entry><entry access="read"><path>C:\Users\k\Repository\HexelEditor\.agents</path></entry><entry access="read"><path>C:\Users\k\.codex\visualizations\2026\10\22\01a1653f-d59f-76f0-994a-c6b99b2390a7\.agents</path></entry><entry access="read"><path>C:\Users\k\Repository\HexelEditor\.codex</path></entry><entry access="read"><path>C:\Users\k\.codex\visualizations\2026\10\22\01a1653f-d59f-76f0-994a-c6b99b2390a7\.codex</path></entry><entry access="read"><path>C:\Users\k\Repository\HexelEditor\.aws</path></entry><entry access="read"><path>C:\Users\k\.codex\visualizations\2026\10\22\01a1653f-d59f-76f0-994a-c6b99b2390a7\.aws</path></entry></file_system></permission_profile></filesystem>
</environment_context>

## User

<external_codex_apps_open_page>{"page_id":null}</external_codex_apps_open_page>

## User

i want you to implement a "hexel art editor" that is like aseprite but for a hexagonal grid instead of a square grid. what tech stack do you plan to use?

## Assistant

I'll check the repository first so the proposed stack fits what's already there. I'll also record this conversation as required by your repository instructions.

## Assistant

The repository has no application code yet, and .NET 10 is already installed. I recommend C# with WPF for the editor interface and SkiaSharp for the canvas, with a hexagonal document model shared by the drawing tools, layers, and animation.

## Assistant

I plan to use **C# + .NET 10 + WPF + SkiaSharp**, targeting **Windows 11 x64**.

- **C# / .NET 10:** Application logic and editing engine. .NET 10 is an LTS release, and its SDK is already installed here. [Support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core).
- **WPF / XAML:** Menus, toolbars, color palette, layer panel, timeline, keyboard shortcuts, and dialogs. It fits your Windows-only requirement and supports custom styling for an Aseprite-inspired interface. [WPF documentation](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/).
- **SkiaSharp:** A custom drawing canvas for hexagons, selections, grid overlays, zooming, and panning. Its WPF integration supports software and GPU rendering. [Official examples](https://github.com/mono/SkiaSharp/blob/main/samples/Basic/WPF/README.md).
- **Plain C# editing core:** Integer hex coordinates, brush algorithms, flood fill, selections, layers, animation frames, and command-based undo/redo, kept independent of the interface.
- **Files and exports:** A ZIP-based project format containing JSON metadata and cell data; PNG and SVG exports.
- **Testing and builds:** xUnit for geometry, editing, undo, and file round trips; the .NET CLI with PowerShell scripts; a self-contained Windows x64 release.

The central design choice is to **store artwork as colored hexagonal cells**. Brushes, adjacency, selections, and 60-degree rotations would all operate directly on that grid.

I'd build painting, erasing, palettes, pan/zoom, undo/redo, save/load, and PNG export first, then add layers and animation.

## User

ignore any existing tools installed on this machine.
if you could use any tech stack you wanted, what would you use?

## Assistant

I'll choose based on the editor's needs and your Windows 11 x64 requirement, without considering installed tools.

## Assistant

With that constraint removed, my preference is **C++20 + Qt 6 Widgets + a custom GPU canvas**, still targeting Windows 11 x64.

- **C++20:** The document model, hex-grid algorithms, brushes, layers, animation, and undo/redo. It gives direct control over memory layout and allocation in the editing loop.
- **Qt 6 Widgets:** The desktop interface: menus, shortcuts, dialogs, dockable palettes, layer panels, and timeline. Its existing docking controls fit an editor well. [Qt docking documentation](https://doc.qt.io/qt-6.12/qdockwidget.html).
- **Direct3D 11 through Qt's QRhiWidget:** A batched renderer for hexagons, compositing, selections, and grid overlays. Qt provides the integration between that GPU canvas and the surrounding widgets. [Rendering documentation](https://doc.qt.io/qt-6/qrhiwidget.html).
- **CMake + Ninja + MSVC:** Builds, with pinned dependency versions.
- **Catch2 + CTest:** Tests for geometry, editing operations, undo, and file persistence.

I'd keep the editing core independent of Qt and render visible hexagons in batches. That gives us room to optimize large canvases without changing how documents and tools work.

**My reason for choosing this stack is control over the drawing experience alongside a mature desktop UI toolkit.** The cost is more C++ and renderer maintenance, which I'd accept for a substantial art editor.

## User

are you better at c# or c++

## Assistant

I'll answer in terms of my implementation strengths for this editor and add the exchange to the conversation log.

## Assistant

I'm using the openai-docs skill to keep the answer grounded in what I can reasonably claim about my capabilities.

## Assistant

**For this editor, I'd be more confident delivering reliable results in C#.**

Its managed memory, simpler build setup, and Windows UI tooling reduce the opportunities for implementation mistakes. C++ needs more care around object lifetimes, undefined behavior, and graphics resources.

That's an engineering judgment. The [official OpenAI guidance I checked](https://developers.openai.com/api/docs/guides/code-generation) doesn't establish a C# versus C++ capability ranking.

My earlier C++ recommendation prioritized low-level control. Factoring in delivery reliability, **I'd choose C# + .NET + WPF + SkiaSharp**.

## User

do i need to install/download anything additional for you to implement this in your desired c# tech stack

## Assistant

I'll check the SDK and build requirements to see whether anything needs a manual installation.

## Assistant

**No additional manual installation should be necessary.** I verified that this machine has:

- .NET 10 SDK (10.0.402, x64).
- WPF build tools and desktop reference assemblies.
- .NET 10 Windows Desktop Runtime.

I'll add SkiaSharp and test libraries as NuGet project dependencies; those can download automatically during setup. [SkiaSharp package](https://www.nuget.org/packages/SkiaSharp.Views.WPF).

Visual Studio is optional. The installed SDK provides the build tools for [WPF projects](https://learn.microsoft.com/en-us/dotnet/core/project-sdk/msbuild-props-desktop), so I can build and test from PowerShell.

You just need internet access for dependency downloads.
