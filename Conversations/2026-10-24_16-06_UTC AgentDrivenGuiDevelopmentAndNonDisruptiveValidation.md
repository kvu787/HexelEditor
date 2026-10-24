# Agent-driven GUI development and non-disruptive validation

## User

currently, what is the best way to develop gui apps with agents? my current pain point with this is having the agent do its own testing/validation of the gui app without disrupting my own simultaneous usage of the machine.

## Assistant

Short version: the pattern that works is **make the app verify itself in-process, offscreen, and give the agent assertions instead of eyeballs**. You have already built that here, and it is the right shape. What is left is not a different architecture, it is three specific holes in the one you have, plus a bigger isolation boundary if you want the agent to be allowed to do literally anything.

### The isolation ladder

Reach for the cheapest rung that can answer the question:

| Rung | Technique                                                             | Catches                                                              | Disruption | Fidelity |
| ---- | --------------------------------------------------------------------- | -------------------------------------------------------------------- | ---------- | -------- |
| 1    | Logic in a no-UI assembly, plain unit tests                           | model, geometry, history, serialization                              | none       | n/a      |
| 2    | Real controls in an offscreen `HwndSource` + `RenderTargetBitmap`     | layout, styles, templates, data binding, event handlers, rendering   | none       | high     |
| 3    | Same host, driven through the real input pipeline                     | hit testing, capture, drag gestures, shortcuts, z-order, occlusion   | none       | high     |
| 4    | Golden screenshots diffed against committed baselines                 | visual regressions, which no agent reliably eyeballs                 | none       | high     |
| 5    | Second Win32 desktop (`CreateDesktop`) with real `SendInput`          | modal dialogs, true focus behavior, anything needing a foreground    | none       | near-full |
| 6    | Hyper-V VM or a second machine                                        | DPI, multi-monitor, install/first-run, driver-level behavior         | none       | full     |
| 7    | A physical display the user never looks at, `WS_EX_NOACTIVATE`        | real pixel density, real compositing, "does it actually look right"  | low        | full     |

Rung 2 is the workhorse on Windows and WPF, because an `HwndSource` parked at a negative coordinate with no window style is a real presentation source -- so `SKElement`, control templates, measure/arrange, and routed events all behave normally -- while never appearing on a display, never entering the taskbar, and never being eligible for the foreground. That is what `App.OnStartup` does under `--verify-output`, and it is why the editor can be checked with no display at all.

### What the harness here already gets right

- Verification lives **inside the application**, reached by a command-line switch, so checks assert on real objects (`EditorCanvas.Tool`, `PalettePanel.Columns`, `session.IsDirty`) and fail with a specific sentence instead of a blurry picture.
- The offscreen host is a real `HwndSource` at `-30000, -30000` with `WindowStyle = 0`, so there is no window for the compositor or the user to notice.
- The core assembly has no WPF or Skia reference, so most behavior never needs a UI host.
- Animation was removed and the host is a fixed 1440x960 at 96 dpi, which makes renders deterministic and therefore diffable.
- Dispatcher exceptions become `Shutdown(1)` under verification rather than a `MessageBox`, so a defect can never leave a modal dialog sitting on the desk.
- The physical-display pass is placed from `SourceInitialized` with `ShowActivated = false` and `WS_EX_NOACTIVATE`, captured with `PrintWindow`, and guarded by `Assert-ForegroundUnchanged`. Treating a focus change as a failure of the harness, not a nuisance, is the correct stance.
- Copy and paste use an internal cell clipboard, so a validation run cannot trample the system clipboard.

### The three real gaps

**1. The pointer pipeline is never exercised.** `VerifyEditor` calls `session.Paint(session.Document.CoordinateAt(0), ...)` directly, and the tool and palette checks use `RaiseEvent(Button.ClickEvent)`. Both bypass `HexCanvas.PointerDown`, `PointerMoved`, `PointerUp`, the screen-point-to-hex mapping, and `CaptureMouse`. For an app whose entire purpose is drag-to-paint on a hex grid, that is the largest untested surface in the program: a broken pixel-to-axial conversion, an off-by-one hex pick at a zoom level, a stroke that drops cells between mouse moves, or a pan that fights with drawing would all pass the current suite.

Two fixes, both free of a display:

- Assert the hit test before raising the event: `root.InputHitTest(point)` from the coordinate you believe the control occupies, and check the result is the control you meant. A `RaiseEvent` passes on a control that is zero-sized, covered by another element, or `IsHitTestVisible = false`; a hit test does not.
- Post real messages to the host's handle (`WM_MOUSEMOVE`, `WM_LBUTTONDOWN`, several moves, `WM_LBUTTONUP`) so input enters through WPF's own pipeline, hit testing and capture included. Keyboard goes the same way once focus is set within the host, which also gets the whole shortcut table under test -- `OnKeyDown` is currently unverified.

**2. Nothing compares the screenshot to anything.** `Editor.png` lands in gitignored `LogOutput` and is never diffed. An agent asked "does this look right" from a single image is guessing; an agent handed "47 pixels differ from the baseline, here is the bounding box" is not. Commit baselines for the editor, the palette, and a flat-top canvas, fail the run on a perceptual difference past a threshold, and require an explicit approved step to replace a baseline. This is the single highest-value addition, because it converts the judgment the agent is worst at into arithmetic.

**3. The report is prose.** `Verification.txt` is for a human reader. A JSON sibling with one object per check, plus a meaningful exit code, removes the greps.

### Disruption is not only about focus

Worth auditing anything a validation run can touch outside its own process: the system clipboard, `ClipCursor` and `SetCapture` outside a real gesture, global hotkeys, single-instance mutexes that would make the agent's launch steal the editor you already have open, taskbar flashing, toast notifications, audio, jump lists and recent-document entries, file associations, and writes anywhere under the real Documents folder. Also `Start-Process` without `-WindowStyle Hidden`, which flashes a console window. `Run.ps1` already launches from a fresh copy under `Artifacts/Run`, which is the same instinct applied to file locks.

### If you want the agent fully unleashed

Rungs 5 and 6 are the answer to "the agent should be able to do anything, including things that need a real foreground".

- **A second Win32 desktop.** `CreateDesktop`, `SetThreadDesktop`, launch the app there via `STARTUPINFO.lpDesktop`. Desktops have separate input queues, foreground windows, and clipboards, so `SendInput` and activation on that desktop cannot reach your keyboard at all. Capture with `PrintWindow` from a process on the same desktop. This is how interactive UI suites run on build agents. Costs: you cannot watch it, and some DWM and shell behavior differs.
- **A Hyper-V VM.** Full fidelity including DPI and multi-monitor, driven over PowerShell Direct so it needs no networking. Normally the objection is GPU rendering, which does not apply here: the canvas already goes through Skia's software rendering, so a VM renders what your desktop renders.

One trap to avoid: a loopback RDP session on Windows 11 Pro does not give you a second desk. Pro allows one interactive session, so connecting locks the console session -- exactly the disruption you are trying to prevent. Use a separate desktop or a VM, not RDP to yourself.

### The general recipe, independent of this app

1. Keep the UI layer thin; everything testable lives in an assembly with no UI reference.
2. Build verification into the app behind a switch, hosted offscreen, asserting on real objects.
3. Make rendering deterministic: fixed size, fixed DPI, fixed fonts, no animation, seeded randomness.
4. Drive the real input pipeline, not the event handlers, so hit testing and capture are covered.
5. Diff against committed baselines rather than asking the agent for an opinion.
6. Emit a machine-readable report and a real exit code.
7. Keep one low-disruption physical-display pass for the question renders cannot answer, and make a focus change a hard failure.
8. Reserve a separate desktop or a VM for the cases that genuinely need a foreground.

The ordering matters more than any single technique: every rung you can answer a question on without a display is a question the agent can answer while you keep working.
