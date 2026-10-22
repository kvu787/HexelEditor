# Redesign Interface On Light Theme

## User

this application was originally implemented by a different model that made some less-than-ideal design choices imo:
1. all of the UI text is unnecessarily small.
2. the "accent color" seems to be teal. this is garish as a default. either avoid an accent color entirely or use a reasonable default like the blue used in windows UIs or vscode.
3. the ui seems to use a dark bluish gray/black. by default, UIs should avoid introducing a "color" and should stick to grayscale.
4. the ui should be a "light mode" ui by default, not "dark mode".

redo the gui from-scratch as you see fit. use vscode's gui and vscode's default light theme as an example of good gui.

## Assistant

Rewrote the interface against Visual Studio Code's Default Light Modern palette.

Colors, all four points:

| Role                          | Before                      | After                   |
| ----------------------------- | --------------------------- | ----------------------- |
| Window and panels             | `#101319`, `#1B2029` (dark) | `#FFFFFF`, `#F8F8F8`    |
| Dividers and control outlines | `#303846`                   | `#E5E5E5`, `#CECECE`    |
| Text                          | `#E4EAF2`, `#94A1B4`        | `#3B3B3B`, `#1F1F1F`, `#616161` |
| Accent                        | `#5CE4C2` (teal)            | `#005FB8` (VS Code blue) |
| Base text size                | 13, with 9 to 11 captions   | 14, with 13 as the smallest |

Every surface is now a pure gray; the accent appears only on the active tool bar, the active document tab and frame card, checked check boxes, the opacity slider, the selected palette swatch, selections on the canvas, and the single Export PNG button.

`App.xaml` holds the whole theme, including control templates for buttons, text boxes, check boxes, radio buttons, the slider, thin scroll bars, menus, tooltips, palette swatches, frame cards, list rows, and collapsible side bar sections.

Layout, taking VS Code's shell as the model:

| Region         | Change                                                                             |
| -------------- | ---------------------------------------------------------------------------------- |
| Menu bar       | A real WPF menu bar with File, Edit, View, and Help, replacing buttons that opened context menus. Items show their shortcuts and enable or disable with the document |
| Command toolbar| Undo and redo, brush size, filled shapes, grid, zoom, and fit                       |
| Tool rail      | Icon-only rail like the activity bar, with an accent bar and darker icon on the active tool |
| Document tab   | A tab with the project name and an unsaved dot, with the canvas size beside it      |
| Side bar       | Collapsible Color, Palette, Layers, and Frame sections with chevrons                |
| Timeline       | A bottom panel with larger thumbnails; the active frame is marked like an open tab  |
| Status bar     | Light, with the tool and cell coordinate at the left, selection and frame at the right |

The canvas was repainted too: a light backdrop with a faint dot pattern, a white and light gray transparency checkerboard, a dark low-alpha cell grid, blue selections, and a white halo under the hover ring so the cursor stays visible over dark artwork. The default drawing color is black rather than teal. The dialogs gained headings, button footers, a Cancel button, and a two-column shortcut table.

The fluff text removed earlier did not come back, and the artwork palette itself is untouched: it is content, not chrome.

`Scripts/Verify.ps1` passed: a build with no warnings, 53 unit tests, and the hidden-host desktop run. Verification now also opens the File menu and lays out both dialogs, so the new templates are exercised. `Documentation/EditorPreview.png` is the screenshot from that run.

Commits:

- `Repaint the canvas and thumbnails for a light interface`
- `Rebuild the interface on a light grayscale theme`
- `Update the preview image and interface notes`
