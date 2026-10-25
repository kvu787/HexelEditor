# Remove Menu Bar Save Button

## User

"C:\Users\k\Pictures\Screenshots\Screenshot 2026-10-24 213912.png"

remove this

![Screenshot of the editor with the menu bar's Save button circled in red](Images/2026-10-25_04-41_UTC%20RemoveMenuBarSaveButton%201.png)

## Assistant

Gone, in one commit (`8061a40`).

The circle was around the accent Save button at the right of the menu bar, `MainWindow.xaml:27`, inside a `DockPanel.Dock="Right"` `StackPanel` that held nothing else, so the panel went with it. `SaveClicked` stays, because `File > Save` and `Ctrl+S` both call it, and the `AccentButton` style stays, because the Apply and Create canvas dialog buttons use it. Nothing else in the project referenced the button.

`Documentation/EditorPreview.png`, which the README shows, was a capture of the menu bar with that button in it, so it is now the verification render from this run - the same 1440 x 960 offscreen render of the real interface the old one came from, at the same 106% fitted zoom.

`pwsh -File ./Scripts/Validate.ps1` passed: the ten disruption guard rules, the build, the unit tests and the hidden-host verification, the display pass on NE18NZ2 at 3824 x 2392 with 57 distinct colours, and nothing of the run on a user display, in the foreground, or confining the cursor. I read the 1440 x 960 render myself and the menu bar now ends after Help, with nothing at its right.
