# A 16 by 16 color palette

## Prompt

> at some point, i want a better color picker.
> however, for now, a good 256-color palette will suffice.
> change the palette section to a 16x16 dimension 256 SDR color palette that provides a good selection of colors

Attached: the editor with the Palette section circled.

![The editor with the Palette section circled in the side bar](Images/2026-10-23_02-52_UTC%20SixteenBySixteenColorPalette%200%20Prompt.webp)

## Response

### The palette

The old default was 32 colors in four hand-picked rows. The new one is 256 colors laid out as a grid where the
position of a color tells you what it is:

- Row 1 is a neutral grey ramp, `#000000` to `#FFFFFF` in sixteen even steps.
- Rows 2 to 16 are one hue each, shaded dark on the left to light on the right: red, vermilion, orange, yellow,
  lime, green, emerald, teal, cyan, azure, blue, indigo, violet, magenta, rose.

Each hue row is a shading ramp rather than a brightness slider. Lightness runs from 0.14 to 0.88, so the ends stay
usable instead of collapsing into near-black and near-white, saturation peaks in the middle, and the hue drifts
toward violet in the shadows and toward yellow in the highlights the way hand-mixed ramps do. Dark oranges land on
browns, so earth tones come out of the grid rather than needing a row of their own.

The 256 colors are distinct, every one is opaque, and a unit test holds all three facts along with the grid being a
whole number of rows.

![The 256 colors as a flat 16 by 16 grid](Images/2026-10-23_02-52_UTC%20SixteenBySixteenColorPalette%201.png)

### The side bar

The palette was a `WrapPanel` of 28 pixel swatches, which wraps 256 colors into a ragged block that loses the row
structure. It is now a `UniformGrid` of `HexDocument.PaletteColumns` columns whose swatches stretch to the column
width.

A `UniformGrid` takes its row height from the tallest child, and a swatch that stretches has no height of its own,
so the first attempt collapsed all sixteen rows to a few pixels. The swatches are now sized square from the grid
width whenever it changes. The side bar widened from 310 to 396 to leave them a usable size, and the swatch edge
became translucent black so one edge reads the same on a pale swatch and a dark one.

`MaximumPaletteColors` rose from 256 to 512. The defaults now fill the grid exactly, so without that Add current
color could never do anything again; added colors flow into rows underneath the grid.

### Validation

`Scripts/Verify.ps1`: a build with no warnings, 54 unit tests, and the desktop verification run all pass.

On NE18NZ2, the grid renders as sixteen square rows with the active color ringed in the accent color. Clicking the
orange row's eleventh swatch through UI automation set the foreground to `#EFAB54`, updated the input box, and moved
the ring to that cell. All 256 swatches are reachable by name through UI automation:

![The palette grid with the foreground set to an orange from it](Images/2026-10-23_02-52_UTC%20SixteenBySixteenColorPalette%202.png)

Setting `#7F00FF` and pressing Add current color appended a seventeenth row holding that one swatch, so adding colors
still works past the 256 defaults:

![An added color in a row of its own below the grid](Images/2026-10-23_02-52_UTC%20SixteenBySixteenColorPalette%203.png)

`Documentation/EditorPreview.png` was refreshed from the verification run so the README screenshot shows the grid.

Not done: the better color picker. This change is the stopgap that was asked for, not the picker.

Four commits: the palette and its test, the side bar layout, the documentation, and this conversation.
