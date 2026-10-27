#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Proves that an agent-driven run did not disturb whoever is using the machine, rather than asking the agent
# to be careful. A run is disruptive if any of these happen:
#   - a window of the run is in front of a window of the user's that it overlaps, even for one frame
#   - with a display set aside for validation: a window of the run becomes visible outside it, or moves
#     outside it after being shown
#   - a window of the run takes the foreground, which takes the keyboard with it
#   - a window of the run opens a dialog on a user display, or grabs the mouse
#   - a window of the run confines the cursor, which steers the user's pointer
# Visibility and activation are observed through SetWinEventHook, so the moment a window gains WS_VISIBLE is
# recorded with the bounds it had at that moment, and judged against the Z-order at that moment. Polling
# cannot promise that: a window shown and then moved between two samples would slip through, which is exactly
# the split-second flash this has to rule out. The Z-order is sampled as well, every few milliseconds, for a
# window that is raised after being shown.

. (Join-Path $PSScriptRoot 'Windowing.ps1')

if (-not ('DisruptionWatcher' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public sealed class DisruptionObservation
{
    public string Kind { get; set; } = "";
    public long Milliseconds { get; set; }
    public int ProcessId { get; set; }
    public string ProcessName { get; set; } = "";
    public long Window { get; set; }
    public string Title { get; set; } = "";
    public string Class { get; set; } = "";
    public int Left { get; set; }
    public int Top { get; set; }
    public int Right { get; set; }
    public int Bottom { get; set; }
    public bool Watched { get; set; }
    // For a window of the run found in front of a user's window: that window.
    public string Covered { get; set; } = "";
}

// One watcher per run. Hooks are set on a dedicated thread that owns a message loop, because out-of-context
// WinEvent callbacks are delivered while that thread dispatches messages.
public sealed class DisruptionWatcher
{
    private const uint EventSystemForeground = 0x0003;
    private const uint EventSystemCaptureStart = 0x0008;
    private const uint EventSystemDialogStart = 0x0010;
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectLocationChange = 0x800B;
    private const int ObjectIdWindow = 0;
    private const uint OutOfContext = 0x0000;
    private const uint QuitMessage = 0x0012;

    private readonly ConcurrentQueue<DisruptionObservation> observations = new ConcurrentQueue<DisruptionObservation>();
    private readonly ConcurrentDictionary<int, byte> watched = new ConcurrentDictionary<int, byte>();
    private readonly ConcurrentDictionary<string, byte> watchedNames = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
    // Process names are asked for on every sample of every window on the machine; the kernel's answer is
    // slow, and a process id keeps its name for the length of a run.
    private readonly ConcurrentDictionary<int, string> names = new ConcurrentDictionary<int, string>();
    private readonly HashSet<long> reportedCoverings = new HashSet<long>();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly WinEventDelegate callback;
    private readonly List<IntPtr> hooks = new List<IntPtr>();
    private Thread pump;
    private uint pumpThread;
    private Thread sampler;
    private volatile bool stopping;

    public long BaselineForeground { get; private set; }
    public string BaselineForegroundTitle { get; private set; }
    public int ForegroundChanges;
    public int CursorClipChanges;

    public DisruptionWatcher()
    {
        callback = OnWinEvent;
        BaselineForegroundTitle = "";
    }

    // Processes whose windows this run is answerable for. Everything else on the machine belongs to the user.
    // Names are watched as well as process ids, because a launcher does not always own the window it causes:
    // a crash reporter is nobody's child, and a stub can hand off to another process entirely.
    public void Watch(int processId) { watched[processId] = 0; }
    public void WatchName(string processName) { watchedNames[processName] = 0; }

    public bool IsWatched(int processId)
    {
        if (watched.ContainsKey(processId)) return true;
        return watchedNames.ContainsKey(NameOf(processId));
    }

    public void Start()
    {
        IntPtr foreground = GetForegroundWindow();
        BaselineForeground = foreground.ToInt64();
        BaselineForegroundTitle = TitleOf(foreground);
        Rect clip;
        GetClipCursor(out clip);
        baselineClip = clip;

        ManualResetEventSlim ready = new ManualResetEventSlim(false);
        pump = new Thread(delegate ()
        {
            pumpThread = GetCurrentThreadId();
            Hook(EventSystemForeground, EventSystemForeground);
            Hook(EventSystemCaptureStart, EventSystemCaptureStart);
            Hook(EventSystemDialogStart, EventSystemDialogStart);
            Hook(EventObjectShow, EventObjectShow);
            Hook(EventObjectLocationChange, EventObjectLocationChange);
            ready.Set();
            Message message;
            while (!stopping && GetMessageW(out message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessageW(ref message);
            }
            foreach (IntPtr hook in hooks) UnhookWinEvent(hook);
            hooks.Clear();
        });
        pump.IsBackground = true;
        pump.Name = "DisruptionWatcherPump";
        pump.Start();
        ready.Wait(5000);

        // The cursor clip has no event of its own, and a missed foreground change would be the worst thing to
        // miss, so both are sampled as well. The hook stays the authority on when a window became visible.
        sampler = new Thread(delegate ()
        {
            long lastForeground = BaselineForeground;
            Rect lastClip = baselineClip;
            while (!stopping)
            {
                IntPtr now = GetForegroundWindow();
                if (now.ToInt64() != lastForeground)
                {
                    lastForeground = now.ToInt64();
                    Interlocked.Increment(ref ForegroundChanges);
                    Record("ForegroundSample", now);
                }
                Rect clip;
                if (GetClipCursor(out clip) && !clip.Matches(lastClip))
                {
                    lastClip = clip;
                    Interlocked.Increment(ref CursorClipChanges);
                    // Nothing says which process confined the cursor, so the window in front when it happened
                    // stands in for one: Windows only honours a clip for the process owning the foreground
                    // window, which is why a game loses the pointer the moment it loses the foreground. That
                    // costs no coverage here, because a window of the run reaching the foreground at all is
                    // already a violation in its own right, so a clip this run could have made is one this
                    // run was going to fail for anyway.
                    //
                    // Without it, the user's own fullscreen game grabbing their pointer on their own screen
                    // reads exactly like the run doing it, and a guard that fails for what it is meant to
                    // tolerate teaches whoever reads it to re-run until it passes.
                    IntPtr front = GetForegroundWindow();
                    int owner;
                    GetWindowThreadProcessId(front, out owner);
                    observations.Enqueue(new DisruptionObservation
                    {
                        Kind = "CursorClipChanged",
                        Milliseconds = clock.ElapsedMilliseconds,
                        ProcessId = owner,
                        ProcessName = NameOf(owner),
                        Window = front.ToInt64(),
                        Title = TitleOf(front),
                        Class = ClassOf(front),
                        // The bounds of a clip observation are the clip, not a window.
                        Left = clip.Left, Top = clip.Top, Right = clip.Right, Bottom = clip.Bottom,
                        Watched = IsWatched(owner)
                    });
                }
                SampleZOrder();
                Thread.Sleep(5);
            }
        });
        sampler.IsBackground = true;
        sampler.Name = "DisruptionWatcherSampler";
        sampler.Start();
    }

    public DisruptionObservation[] Stop()
    {
        stopping = true;
        if (pumpThread != 0) PostThreadMessageW(pumpThread, QuitMessage, IntPtr.Zero, IntPtr.Zero);
        if (pump != null) pump.Join(5000);
        if (sampler != null) sampler.Join(5000);
        List<DisruptionObservation> collected = new List<DisruptionObservation>();
        DisruptionObservation observation;
        while (observations.TryDequeue(out observation)) collected.Add(observation);
        return collected.ToArray();
    }

    private Rect baselineClip;

    private void Hook(uint first, uint last)
    {
        IntPtr hook = SetWinEventHook(first, last, IntPtr.Zero, callback, 0, 0, OutOfContext);
        if (hook != IntPtr.Zero) hooks.Add(hook);
    }

    private void OnWinEvent(IntPtr hook, uint eventId, IntPtr window, int objectId, int childId, uint thread, uint time)
    {
        if (objectId != ObjectIdWindow || window == IntPtr.Zero) return;
        if (eventId == EventSystemForeground) { Record("Foreground", window); return; }
        if (eventId == EventSystemCaptureStart) { Record("MouseCaptured", window); return; }
        if (eventId == EventSystemDialogStart) { Record("DialogShown", window); return; }
        if (eventId == EventObjectShow) { Record("WindowShown", window); return; }
        if (eventId == EventObjectLocationChange)
        {
            // Volume here is high across the whole machine, so only the run's own windows are followed, and
            // only while they are visible. A window is created at its default position and moved onto the
            // validation display before it is ever shown, so moves of an invisible window display nothing.
            if (!IsWindowVisible(window)) return;
            int mover;
            GetWindowThreadProcessId(window, out mover);
            if (IsWatched(mover)) Record("WindowMoved", window);
        }
    }

    private void Record(string kind, IntPtr window)
    {
        int processId;
        GetWindowThreadProcessId(window, out processId);
        Rect bounds;
        GetWindowRect(window, out bounds);
        bool watchedWindow = IsWatched(processId);
        observations.Enqueue(new DisruptionObservation
        {
            Kind = kind,
            Milliseconds = clock.ElapsedMilliseconds,
            ProcessId = processId,
            ProcessName = NameOf(processId),
            Window = window.ToInt64(),
            Title = TitleOf(window),
            Class = ClassOf(window),
            Left = bounds.Left, Top = bounds.Top, Right = bounds.Right, Bottom = bounds.Bottom,
            Watched = watchedWindow
        });
        // The moment a window of the run is shown or moved is the moment its place among the user's windows
        // matters most, so it is judged right here rather than left to the next sample.
        if (watchedWindow && (kind == "WindowShown" || kind == "WindowMoved")) ReportIfCovering(window);
    }

    // Every visible window of the run, checked against the windows below it in the Z-order.
    private void SampleZOrder()
    {
        for (IntPtr window = GetTopWindow(IntPtr.Zero); window != IntPtr.Zero; window = GetWindow(window, NextWindow))
        {
            if (!IsWindowVisible(window)) continue;
            int owner;
            GetWindowThreadProcessId(window, out owner);
            if (IsWatched(owner)) ReportIfCovering(window);
        }
    }

    // Reports a window of the run that is in front of a user's window it overlaps, each time it comes to be
    // so, with the user's window named. The desktop and the taskbars do not count: being above the wallpaper
    // is not being in front of anyone, and the taskbars are always on top of everything anyway. A cloaked
    // window of the run does not count either: the desktop window manager draws nothing of it, wherever it
    // stands, which is why a launcher cloaks a window it cannot place before the program shows it.
    private void ReportIfCovering(IntPtr window)
    {
        Rect mine;
        if (!IsWindowVisible(window) || !GetWindowRect(window, out mine) || mine.Right <= mine.Left || mine.Bottom <= mine.Top) return;
        if (IsCloaked(window)) { lock (reportedCoverings) { reportedCoverings.Remove(window.ToInt64()); } return; }
        for (IntPtr below = GetWindow(window, NextWindow); below != IntPtr.Zero; below = GetWindow(below, NextWindow))
        {
            if (!IsWindowVisible(below) || IsIconic(below) || IsCloaked(below)) continue;
            int owner;
            GetWindowThreadProcessId(below, out owner);
            if (IsWatched(owner)) continue;
            Rect theirs;
            if (!GetWindowRect(below, out theirs) || theirs.Right <= theirs.Left || theirs.Bottom <= theirs.Top) continue;
            if (mine.Left >= theirs.Right || mine.Right <= theirs.Left || mine.Top >= theirs.Bottom || mine.Bottom <= theirs.Top) continue;
            string title = TitleOf(below);
            if (title.Length == 0) continue;
            string className = ClassOf(below);
            if (className == "Progman" || className == "WorkerW" || className == "Shell_TrayWnd" || className == "Shell_SecondaryTrayWnd") continue;
            // Reported when the window comes to be in front, not on every sample while it stays there, and
            // again if it is put behind and then raised once more.
            lock (reportedCoverings) { if (!reportedCoverings.Add(window.ToInt64())) return; }
            int processId;
            GetWindowThreadProcessId(window, out processId);
            observations.Enqueue(new DisruptionObservation
            {
                Kind = "WindowAboveUser",
                Milliseconds = clock.ElapsedMilliseconds,
                ProcessId = processId,
                ProcessName = NameOf(processId),
                Window = window.ToInt64(),
                Title = TitleOf(window),
                Class = ClassOf(window),
                Left = mine.Left, Top = mine.Top, Right = mine.Right, Bottom = mine.Bottom,
                Watched = true,
                Covered = NameOf(owner) + " (" + owner + ") window '" + title + "' [" + className + "] at " + theirs.Left + "," + theirs.Top + " " + theirs.Right + "," + theirs.Bottom
            });
            return;
        }
        // In front of nothing: a later raise is a new event.
        lock (reportedCoverings) { reportedCoverings.Remove(window.ToInt64()); }
    }

    private string NameOf(int processId)
    {
        string name;
        if (names.TryGetValue(processId, out name)) return name;
        try { name = Process.GetProcessById(processId).ProcessName; } catch { name = "<gone>"; }
        names[processId] = name;
        return name;
    }

    private static bool IsCloaked(IntPtr window)
    {
        int cloaked;
        return DwmGetWindowAttribute(window, CloakedAttribute, out cloaked, sizeof(int)) == 0 && cloaked != 0;
    }

    private static string TitleOf(IntPtr window)
    {
        StringBuilder text = new StringBuilder(512);
        GetWindowTextW(window, text, text.Capacity);
        return text.ToString();
    }

    private static string ClassOf(IntPtr window)
    {
        StringBuilder text = new StringBuilder(256);
        GetClassNameW(window, text, text.Capacity);
        return text.ToString();
    }

    public delegate void WinEventDelegate(IntPtr hook, uint eventId, IntPtr window, int objectId, int childId, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left, Top, Right, Bottom;
        public bool Matches(Rect other) { return Left == other.Left && Top == other.Top && Right == other.Right && Bottom == other.Bottom; }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Message { public IntPtr Window; public uint Value; public IntPtr W; public IntPtr L; public uint Time; public int X; public int Y; }

    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint first, uint last, IntPtr module, WinEventDelegate callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern int GetMessageW(out Message message, IntPtr window, uint first, uint last);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref Message message);
    [DllImport("user32.dll")] private static extern bool PostThreadMessageW(uint thread, uint message, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect bounds);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetTopWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
    private const uint NextWindow = 2;
    private const int CloakedAttribute = 14;
    [DllImport("user32.dll")] private static extern bool GetClipCursor(out Rect bounds);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr window, StringBuilder text, int count);
}

// Two different questions, and conflating them gives the wrong answer on Windows 11.
//
// Is the session locked? Windows 11 draws its lock screen on the Default desktop and only switches to
// Winlogon for the credential prompt, so the input desktop still reads as Default while the machine is
// locked. The session flags are what actually say, and they say a locked session is worth knowing about
// because a picture taken of a window may not be worth trusting.
//
// Is this desktop the one being displayed? That is the input desktop, and it is a separate matter. Only a
// window there has a foreground to take or mouse capture to hold, which is what the parts of WPF that need
// capture depend on. A desktop created for a validation run is never the input desktop, whatever the lock
// state is.
public static class SessionState
{
    private const int SessionInfoEx = 25;
    private const int SessionFlagsOffset = 16;
    private const int Locked = 0;

    public static bool IsSessionLocked()
    {
        IntPtr buffer;
        int bytes;
        int session = WTSGetActiveConsoleSessionId();
        if (!WTSQuerySessionInformationW(IntPtr.Zero, session, SessionInfoEx, out buffer, out bytes))
            return !HasInputDesktop();
        try
        {
            // WTSINFOEXW is a level, then a union whose member starts with the session id at offset 8, the
            // connect state, and then the session flags.
            if (bytes < SessionFlagsOffset + 4) return !HasInputDesktop();
            return Marshal.ReadInt32(buffer, SessionFlagsOffset) == Locked;
        }
        finally { WTSFreeMemory(buffer); }
    }

    public static bool HasInputDesktop()
    {
        return string.Equals(InputDesktopName(), "Default", StringComparison.OrdinalIgnoreCase);
    }

    public static string InputDesktopName()
    {
        IntPtr desktop = OpenInputDesktop(0, false, 0x0001);
        if (desktop == IntPtr.Zero) return "Winlogon";
        try
        {
            byte[] buffer = new byte[512];
            int needed;
            if (!GetUserObjectInformationW(desktop, 2, buffer, buffer.Length, out needed)) return "Unknown";
            return Encoding.Unicode.GetString(buffer, 0, Math.Max(0, needed - 2));
        }
        finally { CloseDesktop(desktop); }
    }

    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQuerySessionInformationW(IntPtr server, int session, int infoClass, out IntPtr buffer, out int bytes);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern int WTSGetActiveConsoleSessionId();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetUserObjectInformationW(IntPtr handle, int index, [Out] byte[] info, int bytes, out int needed);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr handle);
}
'@
}

function Test-SessionLocked { [SessionState]::IsSessionLocked() }

# Whether this desktop is the one being displayed and fed by the keyboard. Separate from the lock state.
function Test-HasInputDesktop { [SessionState]::HasInputDesktop() }

function Get-InputDesktopName { [SessionState]::InputDesktopName() }

function Start-DisruptionWatch {
    param(
        # A display set aside for the run, on which its windows may appear in front. Without one, the run's
        # windows may appear on any display, but only behind every window of the user's that they overlap.
        [string] $AllowedMonitor = '',
        # The editor itself, plus the two processes Windows puts a window up for when a program dies badly.
        # A crash dialog on a user display is as disruptive as the editor appearing there.
        [string[]] $WatchNames = @('HexelEditor', 'WerFault', 'vsjitdebugger')
    )
    $allowed = if ($AllowedMonitor) { Get-DisplayBounds -Monitor $AllowedMonitor } else { $null }
    $watcher = New-Object DisruptionWatcher
    foreach ($name in $WatchNames) { $watcher.WatchName($name) }
    $watcher.Start()
    [PSCustomObject]@{
        Watcher = $watcher
        Allowed = $allowed
        AllowedMonitor = $AllowedMonitor
        StartedAt = [DateTime]::UtcNow
    }
}

function Register-WatchedProcess {
    param([Parameter(Mandatory)][PSCustomObject] $Watch, [Parameter(Mandatory)][int] $ProcessId)
    $Watch.Watcher.Watch($ProcessId)
}

# A window that lies on no display at all shows nothing to anyone, whatever its style says. The hidden
# rendering host is such a window: it is marked visible, because WPF will not hit test pointer input against
# an invisible window, and parked at coordinates no monitor covers.
function Test-OnAnyDisplay {
    param([Parameter(Mandatory)] $Observation)
    foreach ($screen in [System.Windows.Forms.Screen]::AllScreens) {
        $bounds = $screen.Bounds
        if ($Observation.Left -lt $bounds.Right -and $Observation.Right -gt $bounds.Left -and
            $Observation.Top -lt $bounds.Bottom -and $Observation.Bottom -gt $bounds.Top) { return $true }
    }
    return $false
}

# A window is clear only if all of it lies inside the display set aside for validation. Touching a user
# display at all counts, because a sliver at the edge of their screen is still something they would see.
function Test-WithinAllowedDisplay {
    param([Parameter(Mandatory)][PSCustomObject] $Allowed, [Parameter(Mandatory)] $Observation)
    if ($Observation.Right -le $Observation.Left -or $Observation.Bottom -le $Observation.Top) { return $true }
    $Observation.Left -ge $Allowed.X -and
        $Observation.Top -ge $Allowed.Y -and
        $Observation.Right -le ($Allowed.X + $Allowed.Width) -and
        $Observation.Bottom -le ($Allowed.Y + $Allowed.Height)
}

# A clip that covers the whole virtual desktop is the cursor free rather than held: it is what Windows reports
# when nothing is confining the pointer, and what a program that was holding it leaves behind on letting go.
# Reading that as a confinement turns a game releasing the mouse into a failure.
function Test-CursorConfined {
    param([Parameter(Mandatory)] $Observation, $Virtual = [System.Windows.Forms.SystemInformation]::VirtualScreen)
    -not ($Observation.Left -le $Virtual.Left -and $Observation.Top -le $Virtual.Top -and
          $Observation.Right -ge $Virtual.Right -and $Observation.Bottom -ge $Virtual.Bottom)
}

# Separate from collecting the observations so that the judgement can be exercised on its own, with
# observations written by hand. A run that disturbs the machine is rare and not reproducible on demand, which
# is exactly why the rules that decide need to be checkable without one.
function Get-DisruptionViolations {
    param(
        # The display set aside for the run, or null when its windows may go anywhere as long as they stay
        # behind the user's.
        [AllowNull()][PSCustomObject] $Allowed,
        [string] $AllowedMonitor = '',
        [AllowEmptyCollection()][Parameter(Mandatory)] $Observations
    )
    $violations = @()
    foreach ($observation in $Observations) {
        # Everything else on the machine belongs to the user, including a game of theirs that takes the
        # foreground, captures the mouse and confines the pointer while this runs.
        if (-not $observation.Watched) { continue }
        # A clip observation's bounds are the clip rather than a window, so it reports itself rather than
        # borrowing the wording the window rules share.
        if ($observation.Kind -eq 'CursorClipChanged') {
            if (Test-CursorConfined -Observation $observation) {
                $violations += "A window of the run confined the cursor to $($observation.Left),$($observation.Top) $($observation.Right),$($observation.Bottom), which steers the user's pointer: $($observation.ProcessName) ($($observation.ProcessId)) window '$($observation.Title)' was in front after $($observation.Milliseconds) ms"
            }
            continue
        }
        $where = "$($observation.ProcessName) ($($observation.ProcessId)) window '$($observation.Title)' [$($observation.Class)] at $($observation.Left),$($observation.Top) $($observation.Right),$($observation.Bottom) after $($observation.Milliseconds) ms"
        # With no display set aside, a window may be anywhere: the Z-order rule below is what holds it to
        # account, and it applies either way.
        $inside = ($null -eq $Allowed) -or
                  (Test-WithinAllowedDisplay -Allowed $Allowed -Observation $observation) -or
                  -not (Test-OnAnyDisplay -Observation $observation)
        switch ($observation.Kind) {
            'WindowShown' { if (-not $inside) { $violations += "A window became visible outside ${AllowedMonitor}: $where" } }
            'WindowMoved' { if (-not $inside) { $violations += "A window moved outside ${AllowedMonitor}: $where" } }
            'DialogShown' { if (-not $inside) { $violations += "A dialog opened outside ${AllowedMonitor}: $where" } }
            'WindowAboveUser' { $violations += "A window of the run was in front of a user's window it overlaps: $where, covering $($observation.Covered)" }
            'Foreground' { $violations += "A window of the run took the foreground: $where" }
            'ForegroundSample' { $violations += "A window of the run took the foreground: $where" }
            'MouseCaptured' { $violations += "A window of the run captured the mouse: $where" }
        }
    }
    , $violations
}

function Stop-DisruptionWatch {
    param([Parameter(Mandatory)][PSCustomObject] $Watch)
    $watcher = $Watch.Watcher
    $observations = $watcher.Stop()
    $violations = Get-DisruptionViolations -Allowed $Watch.Allowed -AllowedMonitor $Watch.AllowedMonitor -Observations $observations
    [PSCustomObject]@{
        Passed = $violations.Count -eq 0
        Violations = $violations
        AllowedMonitor = $Watch.AllowedMonitor
        AllowedBounds = if ($null -ne $Watch.Allowed) { "$($Watch.Allowed.X),$($Watch.Allowed.Y) $($Watch.Allowed.Width) x $($Watch.Allowed.Height)" } else { 'any display, behind every window of the user' }
        BaselineForegroundTitle = $watcher.BaselineForegroundTitle
        ForegroundChanges = $watcher.ForegroundChanges
        CursorClipChanges = $watcher.CursorClipChanges
        Observations = $observations
        Seconds = [Math]::Round(([DateTime]::UtcNow - $Watch.StartedAt).TotalSeconds, 1)
    }
}
