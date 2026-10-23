# Solid Canvas Background Color

## User

It seems there is a permanent "background layer" that has an "alternating line" pattern. i want to keep this permanent background layer, but instead of an alternating line pattern, i want a solid color. a new canvas must start with #ffffff white for the background layer. there must be a setting somewhere to set the background layer color at anytime after a new canvas is created.

## Assistant

The pattern was in `ArtworkRenderer.Draw`, which built two paths from the cells and filled them with two grays:

```csharp
// Checkered paper marks the cells that hold no color, as a transparency checkerboard does.
if (paper) ((index % document.Width + index / document.Width) % 2 == 0 ? lightPaper : darkPaper).AddPath(cell, (float)x, (float)y);
...
if (paper) { FillCells(lightPaper, 0xFFFFFFFF); FillCells(darkPaper, 0xFFE9E9E9); }
```

In a pointy-top grid, offset row parity alternates along rows, which is why it reads as lines rather than as a checkerboard. The background was also not part of the document at all: it was two constants in the renderer, so there was nothing to set, save, or undo.

### The background as document state

`HexDocument` now carries the color, and the setter drops alpha so the background cannot stop being solid:

```csharp
public const uint DefaultBackgroundColor = 0xFFFFFFFF;

public uint BackgroundColor
{
    get => backgroundColor;
    set => backgroundColor = value | 0xFF000000;
}
```

A new canvas starts on `#FFFFFF` because that is the field's initial value; no dialog field and no call site is needed for it. `Clone` carries it, which is what makes undo and redo work, since `ChangeDocument` snapshots the document around each structural edit.

`EditorSession.SetBackgroundColor` goes through that same path, so the change joins the history as "Background color" and marks the project unsaved:

```csharp
public void SetBackgroundColor(uint color) { var opaque = color | 0xFF000000; if (Document.BackgroundColor != opaque) ChangeDocument("Background color", () => Document.BackgroundColor = opaque); }
```

`ProjectFile` writes it into the manifest. The field is nullable on the way back in, so `Examples/NeonTide.hexel` and any project saved before this change load on white instead of on a transparent-black value that JSON would otherwise supply.

The renderer then collects one path and fills it once:

```csharp
// The background layer lies under every cell, so a cell that holds no color shows it instead.
if (background) backgroundCells.AddPath(cell, (float)x, (float)y);
...
if (background) FillCells(backgroundCells, document.BackgroundColor);
```

The PNG and SVG exports call `Draw` without the background, as they did without the paper, so exported artwork stays transparent where cells hold no color.

### The setting

Two ways in, both going through the session:

- The Color side bar section gained a Canvas background swatch, its value, and a `#RRGGBB` input with a Set button, laid out like the Foreground block above it.
- View gained `Canvas background color...`, which opens the existing prompt dialog pre-filled with the current value.

### Validation

`Scripts/Verify.ps1`: a build with no warnings, 53 unit tests, and the desktop verification run all pass. The new unit tests cover the white default, alpha being dropped, undo and redo, the archive round trip, and a manifest with no background field. Desktop verification now renders a canvas on a background of its own and samples every cell, so it fails if the background ever becomes a pattern again:

```
Canvas background: one solid color under all 20 cells
```

On NE18NZ2, a new canvas from File > New canvas opens on solid white, with the grid outlines being the grid itself rather than the old banding:

![A new Hexel Editor canvas on solid white](Images/2026-10-23_02-31_UTC%20SolidCanvasBackgroundColor%201.png)

Setting `#FFD2B2` and then `#151923` from the side bar repaints the whole canvas in one flat color, with painted cells sitting on top of it:

![The canvas background set to a dark color with black cells painted on it](Images/2026-10-23_02-31_UTC%20SolidCanvasBackgroundColor%202.png)

The View menu item opens the prompt on the current value:

![The Canvas background color prompt pre-filled with the current value](Images/2026-10-23_02-31_UTC%20SolidCanvasBackgroundColor%203.png)

Undo took `#151923` back to `#FFD2B2` with the button reading "Undo Background color", and a value the parser rejects reports in a message box and leaves the background as it was.

`Documentation/EditorPreview.png` was refreshed from the verification run so the README screenshot shows the new control.

Four commits: the document and renderer change, the interface and verification, the documentation, and this conversation.
