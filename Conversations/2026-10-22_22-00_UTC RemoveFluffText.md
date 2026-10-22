# Remove Fluff Text

## User

[Attached image: the Hexel Editor window with "ART STUDIO" and "MAKE EVERY HEX COUNT." circled in red]

![Hexel Editor with the fluff text circled](Images/2026-10-22_22-00_UTC%20RemoveFluffText%201.webp)

i see some unnecessary "fluff text" in the application. remove all instances of fluff text.

## Assistant

I searched the desktop project for decorative strings, starting from the two circled in the screenshot, and found seven in total.

Removed outright:

| Location                             | Text                                                        |
| ------------------------------------ | ----------------------------------------------------------- |
| Header, under the HEXEL wordmark     | `ART STUDIO`                                                 |
| Tool strip, bottom caption           | `HEX GRID`                                                   |
| Shortcut hint box, right panel       | `MAKE EVERY HEX COUNT.`                                      |
| Status bar, startup text             | `Welcome to Hexel. Your canvas is a little different here.`  |

Replaced with plain labels:

| Location                             | Before                                                      | After              |
| ------------------------------------ | ----------------------------------------------------------- | ------------------ |
| Palette heading                      | `STUDIO PALETTE`                                             | `PALETTE`          |
| Shortcuts dialog heading             | `A different kind of pixel.`                                 | `Shortcuts`        |
| Shortcuts dialog button              | `Back to the canvas`                                         | `Close`            |
| New canvas dialog heading            | `Start something hextraordinary.`                            | `New hexel canvas` |
| Status after creating a canvas       | `New canvas. Choose a color and make your first mark.`       | `New canvas`       |

Kept, because they carry information rather than personality: the `B pencil / E eraser / G fill`, pan and zoom lines in the hint box; the `Outside canvas | Space + drag to pan` status; the `Copied cells. Hover the destination and press Ctrl+V to paste.` status; and the section captions `COLOR`, `LAYERS`, `FRAME`, `CANVAS`, `TIMELINE`, `BRUSH`.

Files touched: `Source/HexelEditor.Desktop/MainWindow.xaml`, `Source/HexelEditor.Desktop/MainWindow.xaml.cs`, `Source/HexelEditor.Desktop/EditorDialogs.cs`.

`Scripts/Verify.ps1` passed: build with no warnings, 53 unit tests, and the desktop verification run (save/load, PNG export, sprite sheet, SVG, session logging). The app was not opened on a physical display for a visual check.

Commit: `Remove decorative tagline text from the interface`.

## User

No need to launch it.

## Assistant

Understood, the change is left as committed.
