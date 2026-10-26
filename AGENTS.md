## Style

For single and double quotes, only use the ASCII forms: ', "
Never use these: “, ”, ‘, ’, etc.

## Development platform compatibility

Support Windows 11 x64 as the only development platform.

## Folder and file naming

This only applies to things that we have the freedom to name as wanted.
Use CamelCase.
Use complete proper words. Don't use typical shortenings. Good: Source, Documentation. Bad: src, docs.

## External tools

You may use the tools in `%UserProfile%\Program`.
You may refer to local copies of source repos in `%UserProfile%\Repository\External`.

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
Record and commit at the end of every turn.
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

If you are creating GUI apps or games, it may be helpful for you to launch the app on a display for validation or other tasks.
Assume that the user is actively using the machine. This means whatever you do, you want to minimize or ideally avoid any disruptions to the user's usage.
Assume that the only display that is never used by the user is NE18NZ2, which means you can use it freely.
If possible, never steal focus away from the user onto stuff that you launch.

After changing a runnable application, open it on NE18NZ2 and validate the change yourself as part of finishing the work. Don't ask for permission to open it, and don't ask whether the result looks right. Report what you verified and what you could not.

### Rules for validating the WPF editor in HexelEditor_Wpf

Assume the machine is in use and may be locked or unlocked. Both work; nothing here depends on which.

Run `pwsh -File ./HexelEditor_Wpf/Scripts/Validate.ps1`. That is the only way to check your own work on the editor. It builds, runs the tests, drives the real interface in a hidden host, puts the real window on NE18NZ2, and watches the whole run for anything that would disturb whoever is at the machine. It exits non-zero if a window of the run became visible off NE18NZ2 even for one frame, moved onto a user display, took the foreground, captured the mouse, or confined the cursor. Read the report it points at and say what passed.

Do not reach for `Run.ps1`, `Verify.ps1`, `Exercise.ps1` or `HexelEditor.exe` directly while the machine may be in use, and do not pass `-SameDesktop`. `Exercise.ps1` takes the foreground and types on the real keyboard by design, so it is for a machine nobody is using and nothing else. An offscreen window is not enough on the displayed desktop: the hidden host takes the foreground, the menu check captures the real mouse, and the submenu's popup appears at the top left of the primary display. `Validate.ps1` runs the hidden host on a Win32 desktop of its own, where none of that can reach anyone.

These are hard rules, so build new checks to fit them:

- Never call `SetForegroundWindow`, `Activate`, `Focus`, `BringIntoView`, `Topmost`, or `SwitchDesktop` in a validation path, and never inject input with `SendInput` on the displayed desktop. Gate anything that focuses on `App.KeepFocusAway`.
- Never show a window without placing it first. Place it from `SourceInitialized`, which runs after the handle exists and before the window is made visible, so it never appears anywhere else on the way.
- Keep validation windows out of the taskbar, so there is no button to appear or flash on the user's taskbar.
- Leave the system clipboard alone. A desktop of its own does not get its own clipboard; that belongs to the window station.
- A check that needs a real foreground, a modal dialog, or genuine mouse capture belongs on the editor's own desktop in `Validate.ps1`, not on the displayed one.
- Check pointer behavior by driving the pointer, through `PointerInput`, not by calling a handler or raising `MouseDown`. Raising an event skips hit testing, and a hand-made `MouseEventArgs` reports the physical pointer's position rather than the one it was given.
- Check shortcuts by striking the keys, through `KeyboardInput`. Never inject input with `SendInput`: it only feeds the desktop being displayed, so it does nothing on a desktop of its own and would type into the user's session on theirs.
- Keep pointer checks before keyboard checks, and do not take the Win32 focus. Giving WPF a keyboard focus makes it re-read the pointer position from a desktop that has no cursor, after which driven pointer moves stop working for the rest of the run.
- Do not add a check that opens a modal dialog, including `Ctrl+N`, `Ctrl+O` and `Ctrl+S`. Nothing can answer it and the run will hang until it times out.
