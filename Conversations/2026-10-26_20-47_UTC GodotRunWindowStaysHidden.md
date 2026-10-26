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
