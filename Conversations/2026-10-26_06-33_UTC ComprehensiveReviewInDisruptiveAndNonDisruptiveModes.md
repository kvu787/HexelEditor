# Comprehensive review in disruptive and non-disruptive modes

## User

perform a comprehensive review, testing, and validation of this repo.

do this in two ways:

1. assume this machine is not being used, you can use any display, and you can do your work disruptively
2. assume this machine is being used, you can only use the NE18NZ2 display, and you must do your work non-disruptively


this machine will remain unlocked as you work. make changes as you see fit.

## Assistant

Five commits are on main, and both modes pass on the final build. The non-disruptive validation on NE18NZ2 passes with nothing of the run reaching a user display or the foreground. The same-desktop validation passes with the stronger menu check. A new real-input exercise passes all fifteen of its stages on the primary display. The 73 unit tests pass, the published build runs from a folder outside the repository, and the measurement suite ran clean on a desktop of its own.

**What the disruptive mode found that the non-disruptive mode could not.** All three were invisible to the non-disruptive path, which is the only one that had been run since the pointer checks were added.

- **Escape and the arrow keys never reached an open menu.** The window's shortcut handler runs before the menu sees a key and marked Escape handled, so Escape cleared the selection and left the menu open, and Down moved the selection instead of the highlight. The handler now steps aside while a menu item has the focus or a menu holds the mouse, and lets Space and Enter reach a focused button. The first exercise run found it, and its capture of the menu still open after Escape is under LogOutput.
- **The same-desktop validation had been failing its pointer checks since they were added.** On an input desktop the menu check opens the File menu through its item, which gives the menu mouse capture, and closing the submenu by property never gave the capture back. Every driven pointer move after it landed on the menu. The check now closes the menu with Escape, raised as the two routed events a struck key raises, gives any capture left back, and asserts that nothing holds the mouse. I removed the shortcut fix temporarily and ran the check against it: it fails on exactly that line, so it is not vacuous.
- **The same-desktop mode could never pass at all.** The disruption watch judged the hidden host for taking the foreground and opening its popup, which is what that switch allows. The watch now covers the display pass alone in that mode, and the report says which part it covered.

**What was built.** `Scripts/Exercise.ps1` is the disruptive counterpart of the validation script, for a machine nobody is using. It starts the real editor in front, injects pointer and keyboard input with SendInput, reads the interface back through UI Automation, and photographs each stage. It covers the menus with Down and Escape, a pencil stroke with undo and redo, the eraser and right-button erase, wheel zoom, middle-button pan, fit and the grid, rectangle, line, selection, move, rotate, copy, paste and clear, layers added, hidden, renamed through their dialog, locked against painting and unlocked, the palette and both colour boxes, the New canvas dialog with a refused 3000 by 3000 size, Save and Save as through the file dialog with the copy read back through the editor's own core, the Shortcuts dialog, the unsaved-changes prompt cancelled and then declined, reopening the saved project from the command line, the frame cadence of a drag over a 2000 by 1000 canvas, and a corrupt project refused with an error box. README and AGENTS say it is for an idle machine only, and the keyboard check at the end of the hidden-host run now strikes a tool key at a focused menu item where a menu item can keep the focus.

Three things about driving the window are recorded in the script, each found by a failed run. A control below the side bar's fold reports where it would be with nothing in the automation tree to say it is clipped. A control the editor rebuilds, such as a layer row, has to be found again rather than clicked through the old reference. A dialog owned by the main window is a descendant of it in the automation tree, not a child of the desktop.

**What holds.** I read every source file, the tests, the scripts and the Blender tool. The editing core, the history, the project format, the compositing, the shader and the scripts hold up: limits are checked before anything is allocated, saves are atomic, the structural records can share cells because history is undone in order, and the archive reader bounds every read. Four unit tests were added where behaviour was unpinned: selecting every cell of a canvas that is not a whole number of 64-bit words, a run of cells across a word boundary, the history keeping the most recent two hundred commands, and paste writing every clipboard cell including the empty ones. That last one is a design choice rather than a defect: a moved or pasted block clears what it lands on, for paste, move and rotation alike, and the test holds it so a change to one would be a change to all three.

| Figure                                                    | Result                               |
| --------------------------------------------------------- | ------------------------------------ |
| Drag over 2000 x 1000 cells, radius 3, pointer every 2 ms | 52 frames a second, median gap 16 ms |
| Measured paint, large canvas while painting               | 0.12 ms                              |
| Hide or show a layer, operation and frame                 | 8.6 ms                               |
| Flood fill the whole canvas                               | 37 ms                                |
| Save and load, 2000 x 1000, three layers                  | 35 ms and 20 ms                      |

The median gap of 16 ms under the drag is one refresh of a 60 Hz display, so the window was drawing at the display's rate while painting; the editor's own share of each frame is a fraction of a millisecond.

**Caveats.** A menu item cannot keep the keyboard focus on a desktop of its own, because focusing one enters menu mode, menu mode needs mouse capture, and a window the mouse is not over is refused it, so the focused-menu-item key check runs only in the same-desktop mode and the report says so where it did not run. The exercise finds the canvas from the window's layout rather than through automation, since the canvas has no automation peer. Testyo.hexel at the repository root is still there for you to keep or remove.

**Artifacts.** The final runs are under `LogOutput/Validation_2026-10-25_23-30-40`, `LogOutput/Validation_2026-10-25_23-30-50`, `LogOutput/Exercise_2026-10-25_23-30-56` and `LogOutput/Measurement_2026-10-25_23-32-18`. The conversation is recorded under `Conversations`.
