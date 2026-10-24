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

## User

what does it mean to have "a second win32 desktop"

## Assistant

A **desktop** is a kernel object in Windows, not a metaphor. It is a container that owns a set of windows, their menus and their hooks, plus the input state that goes with them: which window is foreground, which has keyboard focus, which holds mouse capture, and the current key states. The hierarchy is:

```
Session 1                      your interactive logon session
  WinSta0                      the one window station wired to the physical keyboard, mouse and display
    Default                    the desktop you are looking at right now
    Winlogon                   the one UAC and Ctrl+Alt+Del switch to
    Screen-saver               what a screen saver runs on
    HexelValidation            one you create
```

Creating a second desktop means calling `CreateDesktop` to add a sibling to `Default` inside the same window station, and launching the app into it.

### Why that isolates anything

Only one desktop per window station is the **input desktop** at a time. That one is displayed and receives physical input; every other desktop is running but invisible and unreachable by the keyboard and mouse. Windows on it still exist, still pump messages, still lay out and still render -- there is simply no screen showing them and no hardware input arriving.

You have seen this: when a UAC prompt dims everything and your windows vanish, Windows switched the input desktop to `Winlogon`. Your programs did not minimize or stop; they are on a desktop that is no longer being shown. The classic "a service popped up a `MessageBox` and now it is hung forever with no dialog anywhere" bug is the same thing from the other side.

The part that matters for a validation harness is that input injection is **per-desktop**. `SendInput` posts into the input queue of the desktop that the calling thread is attached to. So a driver process attached to `HexelValidation` can inject clicks, drags and keystrokes, call `SetForegroundWindow`, and let the app grab mouse capture and open modal dialogs -- and none of it can reach your keyboard, because your keyboard belongs to `Default`. The isolation is structural rather than a matter of being careful, which is a different kind of guarantee from `WS_EX_NOACTIVATE` and a foreground guard.

### How a process gets onto it

Two ways, and neither is automatic:

- `CreateProcess` with `STARTUPINFO.lpDesktop` set to `"WinSta0\HexelValidation"`. The child and all its descendants start on that desktop.
- `SetThreadDesktop` on a thread that has not yet created a window, message queue or hook. Practically that means doing it first thing on a fresh thread.

The gotcha for this repo: `Process.Start` and PowerShell's `Start-Process` cannot set `lpDesktop`, so launching the editor there needs a `CreateProcessW` P/Invoke rather than a one-line change. Capture then has to happen from a process on that same desktop too, since `GetWindowRect` and window enumeration do not reach across. `PrintWindow` with `PW_RENDERFULLCONTENT` works there, which `Save-WindowImage` already passes.

No administrator rights are needed to create a desktop in your own window station. Keep the `HDESK` handle open in the launcher, because a desktop is destroyed once nothing holds a handle and no threads remain on it.

### What you give up

- You cannot watch it. Everything comes back as files. `SwitchDesktop` would flip your screen to it, which is exactly the disruption to avoid.
- Shell integration is absent. There is no `explorer.exe` on that desktop, so no taskbar, no tray, no shell drag and drop.
- UI Automation does not cross desktops; the automation client has to live on the same desktop as the app.
- Hardware-accelerated rendering on a non-input desktop is a known soft spot and can capture black. It does not bite here, because the canvas is Skia software rendering.

### One correction to what I said earlier

I listed the clipboard as per-desktop. It is not -- the clipboard and the atom table belong to the **window station**, so a second desktop inside `WinSta0` shares your clipboard. Separate input queue and foreground, shared clipboard. The clipboard still belongs on the audit list, and an app on that desktop calling `Clipboard.SetText` would trample yours. Only a separate window station or a separate session gets you a separate clipboard.

### Whether it is worth it for this app

Probably not yet. A second desktop earns its complexity when you need a real foreground: blocking modal dialogs, activation and focus logic, genuine `SendInput` gestures, drag and drop. The offscreen `HwndSource` already beats it for everything else, because it runs in-process where assertions can touch `session` and the real controls directly instead of inferring from pixels. Driving real pointer input through the existing host closes a much larger hole for much less work. Keep the second desktop in reserve for the day a check genuinely needs to be the foreground window.

## User

okay, this is all interesting, but i don't have time to dive into it now. what i basically need is for you or any agent to be able to develop this app without disrupting me, with these points:

* the machine will always be awake
* windows may be unlocked or locked
* the NE18NZ2 display can output anything. i don't use it at all so i don't care what's being shown on it. however, i don't want any disruptions to what the non-NE18NZ2 displays are outputting.
* displaying something, even briefly, on non-NE18NZ2 displays is unacceptable because even having something shown for a split second is like a distracting flashbang for me, and it's possible that i might click into whatever you launched and then that steals focus from what i was using.
* the main disruption i'm concerned about is regarding input and window focus. when i'm in the midst of typing or any kind of input (mouse, gamepad, etc), and focus is stolen away, that is an unacceptable disruption. whatever the agent does, it must ensure that this doesn't happen


i'm going to lock the machine without putting it to sleep and step away for a couple of hours. for this prompt only, you may use non-NE18NZ2 displays. your job is to ensure the agent development workflow meets all my requirements so that when i return, i can simultaneously use the machine while having agents develop this app without having to worry that the agent dev will disrupt my usage.

## Assistant

The workflow is now one command, `pwsh -File ./Scripts/Validate.ps1`, and it is enforced rather than merely intended.

### What was found

The harness that existed was not actually safe to run on a machine in use, and the only reason to believe otherwise was that nobody had checked. Watching a run with `SetWinEventHook` showed three things reaching the user, all from the hidden rendering host that was assumed harmless because its window sits offscreen at -30000,-30000:

| What happened                              | When      | Why                                                                        |
| ------------------------------------------ | --------- | -------------------------------------------------------------------------- |
| The hidden host took the foreground        | 2899 ms   | A WPF window can still be activated while placed offscreen                 |
| It captured the real mouse                 | 3136 ms   | A WPF menu holds mouse capture so it can tell when to close itself          |
| A popup became visible at 0,0 on primary   | 3266 ms   | A submenu is its own window, and it opens where the menu is, not offscreen  |

The third one is exactly the split-second flash that was called unacceptable, and it happened on every verification run.

### What it rests on now

The hidden host runs on a Win32 desktop of its own. A desktop owns its windows and its input state, and only one desktop per window station is displayed and fed by the keyboard at a time; one created for a run is never that one. So its windows cannot appear on a display, cannot take the foreground, and cannot reach the keyboard, whatever the program does. That is structural rather than careful.

Then the whole run is watched and fails if it disturbed anything: a window becoming visible off NE18NZ2 even for one frame, moving onto a user display, taking the foreground, capturing the mouse, or confining the cursor. `SetWinEventHook` is used rather than polling because it records the moment a window gains WS_VISIBLE together with the bounds it had then; a window shown and moved between two samples would slip past a sampler. The watch is also what caught the three problems above, and a negative control confirmed it objects when the editor is launched the ordinary way, so a clean report means something.

Three smaller holes were closed in the application: Windows Error Reporting is suppressed for these runs, so a crash cannot put a dialog on a user display; the validation window is kept out of the taskbar, so there is no button to appear or flash there, which is how Windows answers a refused activation; and the menu check no longer depends on mouse capture.

### Locked and unlocked

Both work, and nothing is skipped in either. Verified by locking the machine and running the same validation: build, tests, hidden-host checks and the NE18NZ2 capture all passed, and the capture came back as real content at 3840 x 2400 rather than black. The watch was confirmed to still detect violations while locked, so a regression cannot pass by hiding behind the lock screen.

One assumption of mine was wrong and is worth recording. A locked Windows 11 session draws its lock screen on the `Default` desktop and only switches to `Winlogon` for the credential prompt, so the input desktop still reads as `Default` while the machine is locked. `LockWorkStation` followed by a read of the input desktop proved it: the name never changed. The lock state comes from the session flags instead, and "is the session locked" and "is this desktop the one being displayed" are now answered separately, because they decide different things.

### What this costs

One check is weaker on a desktop of its own: with no foreground window there is no mouse capture, so a WPF menu will not stay open through its menu item, and the submenu is opened directly and measured instead. The report says which path was taken, so the fallback is visible rather than silent, and `Verify.ps1` fails if the weak path is used where the strong one should have worked. `-SameDesktop` keeps the stronger check for a machine nobody is using. This is the right trade: the fidelity being given up is exactly what caused the disruption.

### Still open

The pointer pipeline is still untested. `VerifyEditor` calls `session.Paint` directly, so `HexCanvas`'s pointer handling, the screen-point-to-hex mapping and mouse capture are never exercised, and painting is what this application is. A desktop of its own is the place to drive real input, since capture and activation there cannot reach anyone. Screenshots are also still not compared against committed baselines.

## User

Yes, drive real pointer input through the WPF input pipeline on the isolated desktop, so hit testing, capture, and drag strokes are covered.

## Assistant

Done, and it passes while the machine is in use. The canvas is now checked by driving the pointer rather than by calling into it.

### Why window messages are the only way

Two easier routes both fail, and the reasons matter:

| Route                            | Why it cannot stand in for input                                                                              |
| -------------------------------- | ------------------------------------------------------------------------------------------------------------- |
| Call the handler, or raise `MouseDown` | Skips hit testing, so it passes on a canvas that something covered, that has no size, or that is not hit testable |
| Build a `MouseEventArgs`         | Cannot carry a position: `GetPosition` asks the mouse device, which answers with wherever the physical pointer is |

So `PointerInput` sends the window messages a mouse sends. The position a message carries is the one WPF then reports, and it arrives through the same input provider a real mouse arrives through, so hit testing, mouse capture and the order of enter, move, down and up are all the real ones.

### The one thing that had to change

The first attempt failed with the pointer landing on nothing. The diagnostic was decisive: WPF reported the pointer at 149.0,416.0, exactly the point aimed at, but `DirectlyOver` was nothing and the foreground window was 0. WPF hit tests pointer input by asking the window manager which window lies at a point, and the window manager only answers with **visible** windows. The hidden host had no `WS_VISIBLE`, so the position arrived correctly and hit nothing.

Marking the host visible fixed it, and costs nothing here: it sits at -30000,-30000, which no monitor covers, and it runs on a desktop that is not displayed. `WS_EX_TOOLWINDOW` keeps it out of the taskbar either way. The watch was taught that a window on no display shows nothing, and that exemption was then checked against the rectangles real runs had recorded: the host off every monitor and the editor on NE18NZ2 come back clear, while the submenu popup at the top left of the primary display, an ordinary launch, and a window overlapping a user display by two pixels are all still violations.

### What is covered

Hit testing from the root, hover tracking, capture taken for a stroke and given back after it, a nine-cell drag that only joins up if the cells between two reported positions are filled in as well, one gesture being one undo however many moves it was made of, right-button erase, and a middle-button pan that moves the canvas without painting.

These run on a blank canvas rather than the example. The first attempt at the erase check asserted the composite was transparent and failed, because the example has three layers and clearing the active one leaves the others showing: that made "did the erase work" a question about compositing instead of about the gesture.

### A hang worth fixing

A check that failed after driving the pointer did not exit. Shutting WPF down from mid-gesture hung until the 120 second timeout, which turned a failed check into a timeout and buried the reason. A failing verification now ends the process outright, with the log already flushed, and reports in about five seconds. Measured with a deliberate failure, then reverted.

### Evidence it did not disturb anything

The final runs happened while the machine was unlocked and in use. The watch recorded 36 window events in the last one, including two `MouseCaptured` events from `msedge`, which is the browser being used at the time. None of them belonged to the run: nothing of the run's became visible off NE18NZ2, nothing took the foreground, and the cursor was never confined.

`-SameDesktop` was deliberately not run this time. Permission to use the other displays was for the previous prompt only, and that path is the one that shows a popup on the primary display.

### Still open

Screenshots are still not compared against committed baselines, so a visual regression would need someone to notice it. Keyboard input is still unchecked: the shortcut table is large and `OnKeyDown` is never exercised, and the same message-driven approach would now cover it.

## User

Yes, cover keyboard input the same way, so the shortcut table and OnKeyDown are exercised.

## Assistant

Done. The shortcut table is struck rather than called, with the modifiers genuinely held down.

### A correction to what I said earlier

I had said that a driver process on a second desktop can inject input there with `SendInput`. That is wrong, and trying it is how I found out: `SendInput` answers `ERROR_ACCESS_DENIED`. It only ever feeds the desktop currently being displayed. So on a desktop of its own it does nothing, and on the displayed one it would type into whatever the person at the machine is doing. Injection is not the tool for this, on either desktop.

### What works instead

A shortcut is read through `Keyboard.Modifiers`, which WPF answers by asking the operating system whether Control is down. A key message does not change that state, so posting `WM_KEYDOWN` for Control leaves it unheld and every shortcut needing it unreachable. `SetKeyboardState` is the way through: it changes the **calling thread's** key state only, which is the same state `GetKeyState` reports and therefore the state WPF reads. Nothing outside the process can see it, and the key messages go to one window handle this process owns, so this is safe on any desktop. It cannot reach another window, another process, or the user.

### Two obstacles that were not obvious

**The handlers were unreachable.** `PreviewKeyDown` sat on the window, and validation hosts the window's *content* without the window, so a tunneling shortcut event never passed through them. They are now on the content root as well as the window. The window still receives a key first and marks it handled, so nothing about the editor's behavior changes; the content root matters only where there is no window.

**The keyboard focus breaks the pointer.** Taking the Win32 focus makes WPF work out afresh where the pointer is by asking the system, and on a desktop that is not being displayed there is no cursor to report. It settled on a position outside the window, about 30000 units away, decided the pointer had left, and then ignored every driven move for the rest of the run. Two consequences: WPF's own focus is used and `SetFocus` is deliberately not called, and the pointer checks run before the keyboard ones. The stroke these keys undo is made through the session rather than with the pointer, because what they are here to prove is the shortcut, not the stroke.

### What is covered

All seven tool keys, the brush and grid keys, `Space` held and released so `OnKeyUp` is reached as well, and `Ctrl+A`, `Ctrl+D`, `Ctrl+Z` and `Ctrl+Shift+Z`. The last two matter most, because they are what proves the modifier state is really being read.

Nothing here opens a dialog or touches the clipboard. `Ctrl+N`, `Ctrl+O` and `Ctrl+S` would wait on a modal window nobody can answer, which hangs the run until it times out, and the clipboard belongs to the window station, which is shared with whoever is using the machine. Both are now written down as rules.

### What was checked and what was not

The full validation passes, with the watch reporting nothing of the run's on a user display, no foreground change and no cursor confinement, on a machine that was unlocked and in use at the time.

Not measured: that the shipped editor still answers shortcuts when the window itself holds focus. That one rests on the window's handler being untouched and still running first, which is WPF's tunneling order, rather than on a test, because pressing keys at the real window would mean injecting into the user's desktop.

### Still open

Screenshots are still not compared against committed baselines, which remains the one gap where a regression would need a person to notice it.

## User

does this app use the gpu at all?

## Assistant

Yes, but not for the artwork. Checked by reading what the code references and then by listing the modules the running editor actually loads, which settles it rather than inferring it.

| Layer                                  | Where it runs | Evidence                                                              |
| -------------------------------------- | ------------- | --------------------------------------------------------------------- |
| Drawing the hexagons                   | CPU           | `HexCanvas : SKElement`, Skia's software raster; `libSkiaSharp.dll`    |
| Compositing the window and its chrome  | GPU           | `wpfgfx_cor3.dll` with `d3d9.dll`, `d3d11.dll`, `dxgi.dll`, `dxcore.dll` |
| The display driver actually in use     | GPU           | Intel `igd*`, `igc64.dll`, and NVIDIA `nvd3dumx.dll`, `nvldumdx.dll`  |
| OpenGL or Vulkan                       | neither       | none loaded                                                           |
| PNG and SVG export                     | CPU           | Skia surfaces, no presentation source involved                        |
| `RenderTargetBitmap` during validation | CPU           | WPF's software rasterizer                                             |

So Skia rasterizes every cell into a memory bitmap on the processor, and WPF hands that bitmap to Direct3D as a texture along with the menus, side bar and text. The artwork is drawn on the CPU; everything that puts it on screen is on the GPU. Nothing sets `RenderOptions.ProcessRenderMode`, so WPF's hardware acceleration is simply on by default, which the loaded Direct3D modules confirm.

`SkiaSharp.Views.WPF` does offer a GPU-backed element, `SKGLElement`, and this editor does not use it.

### Two things worth knowing from this

**5 MB of the build is never used.** `SkiaSharp.Views.WPF` drags in OpenTK and GLFW as transitive dependencies, for the `SKGLElement` path. Ten files, 5.0 MB, ship in the output, and the probe confirms not one of them is ever loaded: OpenGL, Vulkan and OpenTK are all absent from the running process. They could be trimmed from the published application.

**It explains why the validation harness works at all.** The checks render through `RenderTargetBitmap` and Skia, both on the CPU, which is why they work on a desktop that is not being displayed and while the machine is locked, and why a virtual machine would be a faithful place to run this. The GPU only matters in the WPF layer, which is also why photographing the window needs `PrintWindow` with `PW_RENDERFULLCONTENT`: the content being captured is composited by Direct3D rather than drawn with GDI.

The probe ran on the validation display under the usual watch, which reported no violations.
