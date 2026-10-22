# Application Build, Run, And Logging Compliance

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

</INSTRUCTIONS><environment_context>
  <cwd>C:\Users\k\Repository\HexelEditor</cwd>
  <shell>powershell</shell>
  <current_date>2026-10-22</current_date>
  <timezone>America/Los_Angeles</timezone>
  <filesystem><workspace_roots><root>C:\Users\k\Repository\HexelEditor</root><root>C:\Users\k\.codex\visualizations\2026\10\22\01a16567-8408-7180-ad9b-f955e77607ed</root></workspace_roots><permission_profile type="managed"><file_system type="restricted"><entry access="read"><special>:root</special></entry><entry access="write"><path>C:\Users\k\Repository\HexelEditor</path></entry><entry access="write"><path>C:\Users\k\.codex\visualizations\2026\10\22\01a16567-8408-7180-ad9b-f955e77607ed</path></entry><entry access="write"><special>:slash_tmp</special></entry><entry access="write"><special>:tmpdir</special></entry><entry access="read"><path>C:\Users\k\Repository\HexelEditor\.git</path></entry><entry access="read"><path>C:\Users\k\.codex\visualizations\2026\10\22\01a16567-8408-7180-ad9b-f955e77607ed\.git</path></entry><entry access="read"><path>C:\Users\k\Repository\HexelEditor\.agents</path></entry><entry access="read"><path>C:\Users\k\.codex\visualizations\2026\10\22\01a16567-8408-7180-ad9b-f955e77607ed\.agents</path></entry><entry access="read"><path>C:\Users\k\Repository\HexelEditor\.codex</path></entry><entry access="read"><path>C:\Users\k\.codex\visualizations\2026\10\22\01a16567-8408-7180-ad9b-f955e77607ed\.codex</path></entry><entry access="read"><path>C:\Users\k\Repository\HexelEditor\.aws</path></entry><entry access="read"><path>C:\Users\k\.codex\visualizations\2026\10\22\01a16567-8408-7180-ad9b-f955e77607ed\.aws</path></entry></file_system></permission_profile></filesystem>
</environment_context>

## User

<external_codex_apps_open_page>{"page_id":null}</external_codex_apps_open_page>

## User

ensure the repo conforms with the additions to agents.md

## Assistant

I'll read `AGENTS.md`, check the repo for gaps, and make the needed changes. I'll commit the conversation record separately.

## Assistant

The additions require root-level build/run wrappers and a timestamped log folder for every app session. The repo currently has a launch script that builds automatically and no session logging. I'll add the wrappers, make Run report a missing build, and add logging that also works when the executable starts directly.

## Assistant

The root wrappers are in place, and `Build.cmd` passes with no warnings. The app now logs startup, shutdown, project operations, and errors under `LogOutput`. I'm checking missing-build behavior, session logging, and the existing rendering and export checks.

## Assistant

All 32 tests pass, along with rendering, save/load, export, and session-log checks. `Run.cmd` also correctly exits without building when the build is missing, including from a path containing spaces. The standalone build passes too. I'm checking failure logging before committing.

## Assistant

Updated and committed the repo to comply.

- Added root [Build.cmd](C:/Users/k/Repository/HexelEditor/Build.cmd) and [Run.cmd](C:/Users/k/Repository/HexelEditor/Run.cmd) with PowerShell scripts.
- Run exits if the build is missing.
- Added timestamped session logs under ignored `LogOutput/`.
- Updated documentation and verification.

All 32 tests, rendering/export checks, and logging checks pass. The conversation is recorded in a separate commit.
