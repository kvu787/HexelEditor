# Add Tooltip For Filled Shapes Toggle

## User

[Attached image: the Hexel Editor window with the "Filled shapes" checkbox circled in red]

![Hexel Editor with the Filled shapes toggle circled](Images/2026-10-23_01-38_UTC%20AddTooltipForFilledShapesToggle%201.webp)

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

![The Filled shapes tooltip showing in the Hexel Editor toolbar on NE18NZ2](Images/2026-10-23_01-38_UTC%20AddTooltipForFilledShapesToggle%202.png)

The tooltip appears below the checkbox with the expected text. I then closed the Debug instance and left the user's instance untouched.
