# Godot run window stays hidden

## User

how long is "C:\Users\k\Repository\HexelEditor\GodotAgentDevExperiment\Run.cmd" supposed to take? i've been waiting for a minute...

## Assistant

Found it: the app started in about a second, but its window is hidden. I'm checking the engine source to confirm why.

## Assistant

It should take about a second, and then the window opens in front. Yours did start: Godot (process 27532) has been running since 13:45:07, and its log shows the scene loaded. But its window is invisible, so there's nothing to see.

**Why:** `Run.ps1:38` launches Godot with `Start-Process -WindowStyle Hidden`. That puts `SW_HIDE` in the process's startup info, and Windows uses it in place of the first `ShowWindow(SW_SHOW)` a program makes for its main window. The engine shows its window with a plain `SW_SHOW` (`display_server_windows.cpp:1971` in the 4.7.2 source), so the window stays hidden.

**Why validation missed it:** with `-Display`, the engine shows the window with `SW_SHOWNA` instead. Windows doesn't swap that one out, so validation and measurement got a visible window on NE18NZ2. Validation only ever ran the `-Display` path, never the plain one that `Run.cmd` uses.

**The fix:** use `-WindowStyle Hidden` only with `-Headless`, where it hides the console executable's console window. Process 27532 is still running with no window. You can end it in Task Manager, or I can stop it as part of the fix.

## User

okay, double-clicking Run.cmd is supposed to be for human users to use the application. shutdown whatever is running caused by me running Run.cmd, and fix Run.cmd to launch the app for human use. if ai agents other things for non-disruptive development, they can use scripts other than Run.cmd

## Assistant

Only process 27532 is left from your Run.cmd run. The cmd and pwsh it started have already exited. Stopping 27532 now.

## Assistant

The hidden Godot process is stopped. Now I'm splitting Run.ps1: a plain launch for people, with the no-focus NE18NZ2 launch moved to its own script for agents. First I'm checking that pwsh started through its alias stays on a desktop of its own, so validation can safely test the plain launch there.

## Assistant

Both stay on the separate desktop, so validation can run the real Run.ps1 there safely. Writing the changes.

(The rest of this response was stopped by a safety check and is not recorded.)

## Assistant

Part of my last response was stopped by a safety check. I'm dropping the extra validation step I had planned (testing the plain launch on a desktop of its own) and finishing the fix with the existing tools. Next I'm updating the findings' references to the moved launch.

## Assistant

Now running the experiment's own Validate.ps1, the documented check while the machine is in use. It covers the moved NE18NZ2 launch.

## Assistant

While validation runs, I'm saving your guidance about Run.cmd so future sessions follow it.

## Assistant

Memory saved. I'll pick up again when validation finishes.

## Assistant

Validation passed, including the NE18NZ2 window launched through the new StartOnDisplay.ps1. Running Measure.ps1 next, since its launch call changed too.

## Assistant

Measurement is running. I'll commit once it reports.

## Assistant

Both runs passed and nothing was left behind. Committing the fix and the findings separately.

## Assistant

Done. The process your double-click started (Godot, 27532) is stopped. Run.cmd's own cmd and pwsh had already exited.

**What changed**

- `Run.ps1` is now a plain launch for people: it opens the app shown, in front and with focus. It has no parameters and passes no window style.
- The no-focus launch on NE18NZ2 moved to `Scripts/StartOnDisplay.ps1`. `Validate.ps1` and `Measure.ps1` use it now.
- The README says Run.cmd is for people and lists the new script. `Findings.md` records why the window stayed hidden, as finding 15.

**Verified**

- Validate.ps1 passed every step: build, 13 unit tests, headless checks, all 15 checks under D3D12, Vulkan and OpenGL on a desktop of their own, and the display pass on NE18NZ2 through the new script. Nothing appeared outside NE18NZ2 or took focus.
- Measure.ps1 passed on NE18NZ2, also without disturbing anything.

**Not verified:** I didn't open Run.cmd myself, because by design it takes the foreground on your screen. Double-clicking it should now open the window in about a second.

Committed as `4c59591` (the fix) and `51cb398` (the findings), plus this conversation's record. I also saved a memory that Run.cmd is for people and agent launch modes go in other scripts.
