# A validation harness that does not steal focus

## Prompt

> while you were working, focus kept being stolen away from me when you were launching windows, which was very disruptive.
> this would result in keypresses and mouse input being sent to the windows you were launching.
>
> this is unacceptable. i have updated agents.md to hopefully guide agents away from this behavior.
>
> given this, how can you update your workflow to still test things without disrupting me?

Followed by:

> Yes, build the non-disruptive validation harness: the non-activating startup flag, the Scripts helper with PrintWindow and the focus guard, and move the palette interaction checks into VerifyEditor.

## What went wrong

While validating the 16 by 16 palette, a throwaway capture script called `SetForegroundWindow` and
`ShowWindow(SW_MAXIMIZE)` on the editor before each of five screenshots. It captured with
`Graphics.CopyFromScreen`, which reads the desktop and so needs the window frontmost; the wrong capture tool
forced the focus stealing. The editor also opens `WindowStartupLocation="CenterScreen"`, so every launch
appeared on the primary display before being dragged across.

It corrupted its own test, too. The color input ended up holding `s #7F00FF` and then `d #7F00FF`; those
leading characters were Kevin's keystrokes landing in the window that had been pulled in front of them. At the
time this looked like flaky automation and was worked around with a retry loop.

The repository already had a path with none of this. `Scripts/Verify.ps1` runs the editor with
`--verify-output`, and `App.OnStartup` parks the real window tree in an `HwndSource` at `(-30000, -30000)` and
renders it to a `RenderTargetBitmap`. No window is ever shown. That render is where `EditorPreview.png` came
from, and it already showed the finished palette grid.

## The harness

### Launching without taking focus

`Run.ps1 -Display <monitor>` resolves the monitor to physical pixel bounds and passes
`--show-without-focus x,y,width,height`. The window is placed from `SourceInitialized`, so it reaches the
display before its first frame instead of appearing elsewhere and moving, and `ShowActivated` is off.

`ShowActivated` alone did not work. The first test showed focus moving to the editor for about 2.6 seconds:
the editor focuses its canvas as it loads, and focusing anything inside a window activates that window. The
window now also carries `WS_EX_NOACTIVATE`, and the canvas focus calls go through a helper that does nothing
while the option is in effect. Without `-Display` the editor opens normally, in front.

### Capturing and guarding

`Scripts/Windowing.ps1` holds the display lookup, a foreground guard, and a `PrintWindow` capture with
`PW_RENDERFULLCONTENT`, which asks the window to draw itself and so needs neither the foreground nor a clear
view. `Scripts/CaptureWindow.ps1` saves a picture and fails if the foreground moved while it worked.

### Interaction moved into the hidden host

`VerifyPalette` asserts the column count, that every color reaches the grid, and that the swatches are square,
then chooses a color through a real swatch and adds one through the Add current color button.

Two things had to be got right. It runs last, after the preview is rendered, because it leaves a color chosen.
And it undoes the added color before returning: adding one is an edit like any other, and an unsaved session
makes `OnClosing` cancel the close, which stops `Application.Shutdown` and hangs the host. That took a while
to find. The host logged "Desktop verification passed" and then sat at zero CPU until the 60 second timeout,
and the cause only became clear after logging `Windows.Count` and `ShutdownMode` around the shutdown.

## What the measurements showed

A probe launched the editor while sampling the foreground window every 100 ms:

```
Foreground before launch: 'Claude' (1967392)
  Started on NE18NZ2 (3840 x 2400 at 5120,-958) without taking focus.
Foreground after launch:  'Claude' (1967392)
Focus never moved during the launch.
Editor window at 5120,-958 size 3840 x 2400
NE18NZ2 is at 5120,-958 size 3840 x 2400
```

One earlier belief turned out to be wrong and is worth recording. UI Automation is not uniformly safe.
Attaching to the automation tree and running `FindFirst` leave the foreground alone, but
`InvokePattern.Invoke()` activates the target window even with `WS_EX_NOACTIVATE` set, and it does so whatever
the button's handler does. Invoking the Fit button, whose handler only calls `EditorCanvas.Fit()`, moved focus
just as a palette swatch did. The foreground guard caught it rather than letting it pass quietly.

So the live display pass is look-only: launch, capture, read. Every interaction check belongs in the hidden
host, where a click is a `RaiseEvent` on a real control and no window exists.

## Validation

`Scripts/Verify.ps1`: a build with no warnings, 54 unit tests, and the desktop verification run all pass,
including the new line:

```
Palette grid: 256 swatches in 16 columns at 21 pixels square, choosing and adding a color verified
```

On NE18NZ2, the editor was launched and photographed with the new scripts. Focus never moved, and
`PrintWindow` captured the whole window including the Skia canvas:

![The editor filling NE18NZ2, captured without activating it](Images/2026-10-23_03-33_UTC%20NonDisruptiveValidationHarness%201.png)

The palette grid at the display's real pixel density:

![The 16 by 16 palette grid on NE18NZ2](Images/2026-10-23_03-33_UTC%20NonDisruptiveValidationHarness%202.png)

Not verified: how the window behaves if it is dragged between displays of different scaling during a
validation run, and whether `WS_EX_NOACTIVATE` has any effect worth noting on a touch screen.

Four commits: the non-disruptive launch and capture, the palette checks in the hidden host, the
documentation, and this conversation.
