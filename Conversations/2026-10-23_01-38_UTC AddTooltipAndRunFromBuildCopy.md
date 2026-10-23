# Add Tooltip For Filled Shapes Toggle, Run From A Build Copy

## User

[Attached image: the Hexel Editor window with the "Filled shapes" checkbox circled in red]

![Hexel Editor with the Filled shapes toggle circled](Images/2026-10-23_01-38_UTC%20AddTooltipAndRunFromBuildCopy%201.webp)

add a tooltip for this toggle

## Assistant

The toggle is a plain `CheckBox` in the toolbar of `MainWindow.xaml`, and it was the only control in that strip without a tooltip. Tracing `FilledShapes` through the code shows what it actually controls:

| Location                   | Use                                                                           |
| -------------------------- | ----------------------------------------------------------------------------- |
| `HexCanvas.cs:117`         | The drag preview highlights the interior, not just the border                 |
| `HexCanvas.cs:178`         | `session.DrawRectangle(..., FilledShapes)` on commit                          |

So it affects the Rectangle tool only, and there is no keyboard shortcut to mention. The tooltip wording follows the existing ones in the toolbar ("Fit the canvas in the view (F)", "Larger brush (])"): short, and about what turning it on does.

```xml
<CheckBox x:Name="FilledShapes" Content="Filled shapes" Margin="18,0,0,0"
          ToolTip="Fill rectangles instead of drawing only their outline" />
```

### Validation

A Release build failed because a running instance held `HexelEditor.exe`, and its title read `Untitled * - Hexel Editor`, meaning unsaved work. Rather than killing it, I built and ran the Debug configuration, which writes to a separate output folder.

To check the tooltip itself I scripted the hover: UI Automation finds the element named "Filled shapes" in the running window, the cursor is placed on it from a neighboring point so the move registers, and after the tooltip delay the NE18NZ2 screen is captured.

![The Filled shapes tooltip showing in the Hexel Editor toolbar on NE18NZ2](Images/2026-10-23_01-38_UTC%20AddTooltipAndRunFromBuildCopy%202.png)

The tooltip appears below the checkbox with the expected text. I then closed the Debug instance and left the user's instance untouched.

## User

change the repo so that i can double-click Run.cmd and use the app without it interfering with your work at all

## Assistant

The interference is a file lock: an open editor holds `bin/Release/net10.0-windows10.0.26100.0/win-x64/HexelEditor.exe`, so `dotnet build` cannot replace the apphost and fails with `MSB3027` after ten retries, which is exactly what happened earlier in this conversation.

`Run.ps1` now copies the build output to `Artifacts/Run/<timestamp>` and starts the editor from the copy, so nothing the editor holds belongs to the build. Details that mattered:

| Concern                     | Handling                                                                                       |
| --------------------------- | ---------------------------------------------------------------------------------------------- |
| Session logs                | The copy stays under `Artifacts/`, inside the repo, so `SessionLog.FindApplicationFolder` still walks up to `HexelEditor.slnx` and logs land in the repo's `LogOutput` |
| Disk use (128 MB per copy)  | Each run first deletes copies left by editors that have exited                                 |
| Deleting a copy in use      | Cleanup skips any folder a running `HexelEditor` process was started from, by checking process paths |
| A launch during a build     | `Copy-Item` failures retry up to three times rather than starting a half-copied editor         |

`Artifacts/` was already gitignored, so the copies are not tracked.

### Validation

With the editor launched through `Run.cmd` (two instances, running from `Artifacts\Run\2026-10-22_18-44-09` and `...18-45-38`), `Scripts/Verify.ps1` ran clean: build with 0 warnings and 0 errors, 51 tests passing, and the headless desktop verification passing including PNG, SVG, and session logging. A forced relink of the desktop project also produced no `MSB3026` copy warnings while an editor was open. Launching a second instance kept the first instance's folder and removed only the stale one.

The editor itself works from the copy. This is the Release build started by `Run.cmd` on NE18NZ2:

![Hexel Editor running from Artifacts/Run on NE18NZ2, showing the Neon tide example and the new tooltip](Images/2026-10-23_01-38_UTC%20AddTooltipAndRunFromBuildCopy%203.png)

I could not test a launch that collides with a build mid-copy; the retry path is written but unexercised.
