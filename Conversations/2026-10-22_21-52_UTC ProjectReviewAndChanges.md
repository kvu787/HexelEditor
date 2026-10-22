# Project Review And Changes

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

## Applications

### Running

If you create a runnable application, create files called `Build.cmd` and `Run.cmd` that respectively build and run the application when double-clicked from File Explorer.
These must be located at the root of the application's folder in the Git repo.
These must be simple wrappers for PowerShell scripts named `Build.ps1` and `Run.ps1` which contain the actual logic to minimize the amount of batch code written.
Run.cmd must exit if it doesn't discover a build of the application at the place that Build.cmd outputs to.
If the application doesn't need to be "built" for it to be run (such as a PowerShell script), then omit Build.cmd and Build.ps1.

### Logging

When creating an application, create a folder called `LogOutput` at the root of the application's folder in the git repo.
For each run of the application, a folder must be created in LogOutput and named with the current timestamp. This PowerShell code shows what the name of the folder should be:

```powershell
$logFolderPath = "$env:UserProfile\Repository\Godot\VsyncStutterTest\LogOutput\$(Get-Date -Format "yyyy-MM-dd_HH-mm-ss")"
New-Item -ItemType "Directory" -Path $logFolderPath
```

Any logs for that application session must be put in that log folder.
`LogOutput/` must be gitignored.

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
</INSTRUCTIONS>

## User

review this project and make changes as you see fit

## Assistant

I'll start by exploring the repository to understand what's here.

## Assistant

Artifacts dominate. Let me look at what's actually tracked.

## Assistant

`temp.md` is unrelated scratch (notes about uninstalling Claude) - I'll leave it untouched. Now the source.

## Assistant

Let me start a build while I read the remaining files.

## Assistant

Build passed. Running the full verification gate in the background.

## Assistant

All green - 32 tests pass, desktop verification passes. Now let me verify a couple of suspected issues before changing anything.

## Assistant

Confirmed: `#Requires` propagates through dot-sourcing; legacy PowerShell is blocked by policy here anyway. Now implementing. First group - document limits in one place and a bounded manifest read.

## Assistant

Now tests for the core changes.

## Assistant

The tampered-manifest test depends on exact JSON whitespace - let me make it robust instead.

## Assistant

Let me make that test non-vacuous - confirm the untampered manifest actually passes validation.

## Assistant

The test caught a real regression in my own change: deserializing from a span (unlike from a stream) rejects a UTF-8 BOM. Fixing both that and the test's expectation.

## Assistant

All 53 pass, and the baseline case now proves the manifest really is accepted. Let me review this diff before committing.

## Assistant

(chapter marker: Desktop interaction fixes - unsaved-work prompt ordering, consistent command handling, palette feedback)

## Assistant

Now the canvas rendering pass - selection highlights aren't viewport-culled the way the artwork is.

## Assistant

Final full gate plus a publish check.

## Assistant

All green: 53 tests, desktop verification, and publish. Now the conversation record.

## Assistant

Two of my commit messages used British spellings that clash with the repo's own prose. These commits are local and unpushed, so let me fix them properly.

## Assistant

History rebuilt identically. Now the conversation record.

## Assistant

The project was already in good shape: it built clean with warnings as errors, all 32 tests passed, and the desktop verification and publish paths worked before I touched anything. So the review went looking for real defects rather than rewrites.

Four commits, each reviewable on its own.

**Document limits in one place, bounded manifest read.** The palette cap, frame duration range and brush radius limit were bare literals spread across two or three files each, so changing one could leave the others disagreeing. They became constants on `HexDocument` and `EditorSession`, with every check and message deriving from them. `ProjectFile.Load` trusted the uncompressed length an archive declares for `Document.json`; that length is never verified against the deflate stream, so a crafted project could expand past the stated 128 KiB limit. The manifest is now read into a bounded buffer. Switching from a stream to bytes dropped the stream reader's byte order mark handling, which the new tests caught before the commit, so the preamble is now skipped explicitly. A canvas built with a bad height also blamed `width`; both messages now name the right parameter and carry the value.

**Unsaved-work prompt ordering.** File > New canvas asked for the name, size and orientation first and only then whether to save the open artwork, so cancelling that prompt discarded everything just typed. The prompt now comes first. Select all, Deselect and Escape bypassed `Act`, so unlike every other command they neither stopped playback nor finished an in-flight gesture. That also let the Ctrl switch's `default` arm sit in the middle, where the `Ctrl+R` arm after it reads as unreachable. The palette's + Add button did nothing visible when the color was a duplicate or the palette was full; both cases now report in the status bar.

**Selection highlight culling.** The artwork pass skipped cells outside the visible rectangle, but the selection and shape previews did not. A fully selected 256 by 256 canvas drew 65,536 highlights per repaint regardless of zoom, each a save, translate, two path fills and a restore. They now share the artwork's visibility test. Fit and the zoom controls also disagreed on the maximum zoom (12 against 30), so fitting a canvas under about seven columns wide left it smaller than the controls allowed.

**PowerShell version and README.** `Scripts/Common.ps1` checks `$IsWindows`, which does not exist in Windows PowerShell, so the legacy host failed on an undefined variable under strict mode instead of saying what was wrong. A `#Requires -Version 7.0` directive now states it and reaches every workflow that dot-sources the helper.

Tests went from 32 to 53, covering color text rejection and format round trips, frame duration boundaries, palette duplicate and limit behavior, which canvas dimension an error names, and manifests that are well formed but state values outside the documented limits. The full gate passes: 53 tests, desktop verification including the per-cell PNG color check, and the self-contained publish.

Two things I left alone. `temp.md` at the repository root is untracked scratch about uninstalling an unrelated application, so it is yours to keep or delete rather than mine. And the `.hexel` reader still accepts cel buffers whose declared length matches but whose compressed stream is adversarial; the reader only ever consumes `CellCount` values, so the exposure is bounded and a fix would add more than it buys.
