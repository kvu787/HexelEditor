#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Proves that an agent-driven run did not disturb whoever is using the machine, rather than asking the agent
# to be careful. A run is disruptive if any of these happen:
#   - a window of the run becomes visible outside the display set aside for validation, even for one frame
#   - a window of the run moves onto one of the user's displays after being shown
#   - a window of the run takes the foreground, which takes the keyboard with it
#   - a window of the run opens a dialog on a user display, or grabs the mouse
#   - the cursor is confined, which steers the user's pointer
# Visibility and activation are observed through SetWinEventHook, so the moment a window gains WS_VISIBLE is
# recorded with the bounds it had at that moment. Polling cannot promise that: a window shown and then moved
# between two samples would slip through, which is exactly the split-second flash this has to rule out.

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
                    observations.Enqueue(new DisruptionObservation
                    {
                        Kind = "CursorClipChanged",
                        Milliseconds = clock.ElapsedMilliseconds,
                        Left = clip.Left, Top = clip.Top, Right = clip.Right, Bottom = clip.Bottom,
                        Watched = true
                    });
                }
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
            Watched = IsWatched(processId)
        });
    }

    private static string NameOf(int processId)
    {
        try { return Process.GetProcessById(processId).ProcessName; } catch { return "<gone>"; }
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
    [DllImport("user32.dll")] private static extern bool GetClipCursor(out Rect bounds);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr window, StringBuilder text, int count);
}

// A locked session moves the input desktop to Winlogon. Nothing that needs a window on screen can work
// there, so a run has to know which state it is in rather than reporting a black picture as a success.
public static class SessionState
{
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

    public static bool IsLocked()
    {
        return !string.Equals(InputDesktopName(), "Default", StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetUserObjectInformationW(IntPtr handle, int index, [Out] byte[] info, int bytes, out int needed);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr handle);
}
'@
}

function Test-SessionLocked { [SessionState]::IsLocked() }

function Get-InputDesktopName { [SessionState]::InputDesktopName() }

function Start-DisruptionWatch {
    param(
        [Parameter(Mandatory)][string] $AllowedMonitor,
        # The editor itself, plus the two processes Windows puts a window up for when a program dies badly.
        # A crash dialog on a user display is as disruptive as the editor appearing there.
        [string[]] $WatchNames = @('HexelEditor', 'WerFault', 'vsjitdebugger')
    )
    $allowed = Get-DisplayBounds -Monitor $AllowedMonitor
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

function Stop-DisruptionWatch {
    param([Parameter(Mandatory)][PSCustomObject] $Watch)
    $watcher = $Watch.Watcher
    $observations = $watcher.Stop()
    $violations = @()
    foreach ($observation in $observations) {
        if ($observation.Kind -eq 'CursorClipChanged') {
            $violations += "The cursor was confined to $($observation.Left),$($observation.Top) $($observation.Right),$($observation.Bottom), which steers the user's pointer."
            continue
        }
        if (-not $observation.Watched) { continue }
        $where = "$($observation.ProcessName) ($($observation.ProcessId)) window '$($observation.Title)' [$($observation.Class)] at $($observation.Left),$($observation.Top) $($observation.Right),$($observation.Bottom) after $($observation.Milliseconds) ms"
        $inside = Test-WithinAllowedDisplay -Allowed $Watch.Allowed -Observation $observation
        switch ($observation.Kind) {
            'WindowShown' { if (-not $inside) { $violations += "A window became visible outside $($Watch.AllowedMonitor): $where" } }
            'WindowMoved' { if (-not $inside) { $violations += "A window moved outside $($Watch.AllowedMonitor): $where" } }
            'DialogShown' { if (-not $inside) { $violations += "A dialog opened outside $($Watch.AllowedMonitor): $where" } }
            'Foreground' { $violations += "A window of the run took the foreground: $where" }
            'ForegroundSample' { $violations += "A window of the run took the foreground: $where" }
            'MouseCaptured' { $violations += "A window of the run captured the mouse: $where" }
        }
    }
    [PSCustomObject]@{
        Passed = $violations.Count -eq 0
        Violations = $violations
        AllowedMonitor = $Watch.AllowedMonitor
        AllowedBounds = "$($Watch.Allowed.X),$($Watch.Allowed.Y) $($Watch.Allowed.Width) x $($Watch.Allowed.Height)"
        BaselineForegroundTitle = $watcher.BaselineForegroundTitle
        ForegroundChanges = $watcher.ForegroundChanges
        CursorClipChanges = $watcher.CursorClipChanges
        Observations = $observations
        Seconds = [Math]::Round(([DateTime]::UtcNow - $Watch.StartedAt).TotalSeconds, 1)
    }
}
