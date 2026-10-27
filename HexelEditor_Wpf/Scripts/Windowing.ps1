#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Helpers for looking at a window on a physical display without disturbing whoever is using the machine.
# Nothing here may activate a window: the user's keyboard and mouse follow the foreground window, so taking
# it both interrupts them and lets their keystrokes land in the window being validated.

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class NativeWindowing
{
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr handle, out int processId);
    public delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);

    // Process.MainWindowHandle only answers for a visible top-level window that has no owner, and a window
    // kept out of the taskbar is given one, so it reports nothing for the editor during validation. Looking
    // for the largest visible window of the process finds it whether or not it owns a taskbar button.
    public static IntPtr FindProcessWindow(int processId)
    {
        IntPtr best = IntPtr.Zero;
        long bestArea = 0;
        EnumWindows(delegate (IntPtr handle, IntPtr parameter)
        {
            int owner;
            GetWindowThreadProcessId(handle, out owner);
            if (owner != processId || !IsWindowVisible(handle)) return true;
            Rect bounds;
            if (!GetWindowRect(handle, out bounds)) return true;
            long area = (long)(bounds.Right - bounds.Left) * (bounds.Bottom - bounds.Top);
            if (area > bestArea) { bestArea = area; best = handle; }
            return true;
        }, IntPtr.Zero);
        return best;
    }
    // The process's top-level window of the given class, whether or not it is visible yet. For placing a
    // window that its own program will show, in the time between its creation and that show.
    public static IntPtr FindProcessWindowAny(int processId, string className)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate (IntPtr handle, IntPtr parameter)
        {
            int owner;
            GetWindowThreadProcessId(handle, out owner);
            if (owner != processId || ClassOf(handle) != className) return true;
            found = handle;
            return false;
        }, IntPtr.Zero);
        return found;
    }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr handle, out Rect bounds);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out Rect value, int size);

    // The part of a window that the window itself draws, which is what PrintWindow can fill and so what a
    // picture of it should hold.
    //
    // A window rect takes in the invisible resize border Windows leaves around a window: eight pixels at the
    // left, the right and the bottom, and none at the top. Nothing draws there, so a capture made at that size
    // comes back with a black edge on three sides. DWM reports the frame that is on screen instead, and the
    // outermost pixel of that frame is the border DWM draws itself, which the window does not draw either. So
    // the frame is taken in by one more pixel on each side that had an invisible border to begin with.
    public static Rect GetDrawnBounds(IntPtr handle)
    {
        Rect window;
        GetWindowRect(handle, out window);
        Rect frame;
        if (DwmGetWindowAttribute(handle, ExtendedFrameBounds, out frame, Marshal.SizeOf(typeof(Rect))) != 0
            || frame.Right <= frame.Left || frame.Bottom <= frame.Top)
        {
            return window;
        }
        if (frame.Left > window.Left) frame.Left++;
        if (frame.Top > window.Top) frame.Top++;
        if (frame.Right < window.Right) frame.Right--;
        if (frame.Bottom < window.Bottom) frame.Bottom--;
        return frame.Right > frame.Left && frame.Bottom > frame.Top ? frame : window;
    }
    private const int ExtendedFrameBounds = 9;

    // Puts a window at the bottom of the Z-order, behind every other window on the machine, at the given
    // bounds, without activating it. A window pushed down while still hidden stays there when it is shown
    // without activation, which is what lets a run put a window on a display someone is using without ever
    // covering anything of theirs.
    public static bool MoveBehind(IntPtr handle, int x, int y, int width, int height)
    {
        return SetWindowPos(handle, HwndBottom, x, y, width, height, NoActivate | NoOwnerZOrder);
    }

    // Where a window stands among the visible, titled, top-level windows: how many are above it and how many
    // below, with the first few below named. The desktop and the taskbars are left out, since being above
    // the wallpaper is not being in front of anyone.
    public static string DescribeZOrder(IntPtr handle)
    {
        int above = 0, below = 0;
        bool seen = false;
        System.Collections.Generic.List<string> named = new System.Collections.Generic.List<string>();
        for (IntPtr window = GetTopWindow(IntPtr.Zero); window != IntPtr.Zero; window = GetWindow(window, NextWindow))
        {
            if (window == handle) { seen = true; continue; }
            if (!IsUserWindow(window)) continue;
            if (seen) { below++; if (named.Count < 4) named.Add("'" + TitleOf(window) + "'"); } else above++;
        }
        return "behind " + above + " window" + (above == 1 ? "" : "s") + ", in front of " + below
            + (named.Count > 0 ? " (" + string.Join(", ", named.ToArray()) + ")" : "");
    }

    // Visible, uncloaked, not minimized, with a title and some area, and not the shell's own desktop or
    // taskbar windows.
    public static bool IsUserWindow(IntPtr window)
    {
        if (!IsWindowVisible(window) || IsIconic(window) || IsCloaked(window)) return false;
        Rect bounds;
        if (!GetWindowRect(window, out bounds) || bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top) return false;
        if (TitleOf(window).Length == 0) return false;
        string className = ClassOf(window);
        return className != "Progman" && className != "WorkerW" && className != "Shell_TrayWnd" && className != "Shell_SecondaryTrayWnd";
    }

    public static bool IsCloaked(IntPtr window)
    {
        int cloaked;
        return DwmGetWindowAttributeInt(window, Cloaked, out cloaked, sizeof(int)) == 0 && cloaked != 0;
    }

    public static string TitleOf(IntPtr window)
    {
        System.Text.StringBuilder text = new System.Text.StringBuilder(512);
        GetWindowTextW(window, text, text.Capacity);
        return text.ToString();
    }

    public static string ClassOf(IntPtr window)
    {
        System.Text.StringBuilder text = new System.Text.StringBuilder(256);
        GetClassNameW(window, text, text.Capacity);
        return text.ToString();
    }

    private static readonly IntPtr HwndBottom = new IntPtr(1);
    private const uint NoActivate = 0x0010, NoOwnerZOrder = 0x0200, NextWindow = 2;
    private const int Cloaked = 14;
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetTopWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr handle, uint command);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr handle, System.Text.StringBuilder text, int count);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int DwmGetWindowAttributeInt(IntPtr handle, int attribute, out int value, int size);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr handle, IntPtr deviceContext, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool PostMessageW(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr handle, System.Text.StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool EnumDisplayDevicesW(string device, uint index, ref DisplayDevice target, uint flags);
    [DllImport("shcore.dll")] public static extern int SetProcessDpiAwareness(int awareness);
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
    }
}
'@

# Without this the display bounds come back in scaled units, and a window placed from them lands in the wrong
# place on any display that is not at 100 percent. Already-aware processes answer with a failure, which is fine.
try { [void] [NativeWindowing]::SetProcessDpiAwareness(2) } catch { }

# Puts a window behind every other window, at the given bounds, activating nothing. Where a run puts a window
# it wants to look at while the person at the machine keeps every one of theirs in front.
function Move-WindowBehind {
    param(
        [Parameter(Mandatory)][IntPtr] $Handle,
        [Parameter(Mandatory)][int] $X, [Parameter(Mandatory)][int] $Y,
        [Parameter(Mandatory)][int] $Width, [Parameter(Mandatory)][int] $Height
    )
    if (-not [NativeWindowing]::MoveBehind($Handle, $X, $Y, $Width, $Height)) { throw 'SetWindowPos refused to move the window behind the others.' }
}

function Get-WindowZOrderReport {
    param([Parameter(Mandatory)][IntPtr] $Handle)
    [NativeWindowing]::DescribeZOrder($Handle)
}

# Waits, a millisecond at a time, for a process to create its top-level window of the given class, and
# returns the handle while the window may still be hidden. A program that shows its own window creates it
# some time before showing it, and that time is when a launcher can put it where it wants it.
function Wait-WindowCreated {
    param([Parameter(Mandatory)][int] $ProcessId, [Parameter(Mandatory)][string] $ClassName, [int] $TimeoutMilliseconds = 30000)
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    do {
        $handle = [NativeWindowing]::FindProcessWindowAny($ProcessId, $ClassName)
        if ($handle -ne [IntPtr]::Zero) { return $handle }
        if ([DateTime]::UtcNow -ge $deadline) { throw "Process $ProcessId created no '$ClassName' window within $TimeoutMilliseconds ms." }
        Start-Sleep -Milliseconds 1
    } while ($true)
}

# The bounds a run's window takes on the primary display when it is to sit behind every other window: inside
# the working area, away from its edges, at a size a real editor window has.
function Get-BehindBounds {
    param([int] $Width = 1600, [int] $Height = 1000)
    $work = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    [PSCustomObject]@{
        X = $work.X + 40
        Y = $work.Y + 40
        Width = [Math]::Min($Width, $work.Width - 80)
        Height = [Math]::Min($Height, $work.Height - 80)
    }
}

function Get-DisplayBounds {
    param([Parameter(Mandatory)][string] $Monitor)
    $hardwareId = Get-CimInstance -Namespace root\wmi -ClassName WmiMonitorID |
        Where-Object { (-join ($_.UserFriendlyName | Where-Object { $_ -gt 0 } | ForEach-Object { [char] $_ })) -eq $Monitor } |
        ForEach-Object { ($_.InstanceName -split '\\')[1] } | Select-Object -First 1
    if (-not $hardwareId) { throw "No monitor named $Monitor is connected." }
    foreach ($screen in [System.Windows.Forms.Screen]::AllScreens) {
        $device = New-Object NativeWindowing+DisplayDevice
        $device.Size = [Runtime.InteropServices.Marshal]::SizeOf($device)
        if ([NativeWindowing]::EnumDisplayDevicesW($screen.DeviceName, 0, [ref] $device, 0) -and $device.Id -match $hardwareId) {
            return [PSCustomObject]@{
                Monitor = $Monitor
                DeviceName = $screen.DeviceName
                X = $screen.Bounds.X
                Y = $screen.Bounds.Y
                Width = $screen.Bounds.Width
                Height = $screen.Bounds.Height
            }
        }
    }
    throw "$Monitor is connected but Windows does not report a display for it."
}

function Get-ForegroundWindowDescription {
    $handle = [NativeWindowing]::GetForegroundWindow()
    $text = New-Object System.Text.StringBuilder 512
    [void] [NativeWindowing]::GetWindowTextW($handle, $text, $text.Capacity)
    [PSCustomObject]@{ Handle = $handle; Title = $text.ToString() }
}

# Call before touching a window, then pass the result to Assert-ForegroundUnchanged afterwards. A validation
# step that moves the foreground is a defect in the step, so it should fail loudly rather than pass quietly.
function Get-ForegroundGuard { Get-ForegroundWindowDescription }

function Assert-ForegroundUnchanged {
    param([Parameter(Mandatory)][PSCustomObject] $Guard)
    $now = Get-ForegroundWindowDescription
    if ($now.Handle -ne $Guard.Handle) {
        throw "Focus moved from '$($Guard.Title)' to '$($now.Title)'. A validation step must never take the foreground."
    }
}

# Asks a process's window to close, the way the close button does, and activates nothing. Process.CloseMainWindow
# cannot do it for the editor during validation: MainWindowHandle only answers for a window without an owner,
# and keeping the window out of the taskbar gives it one, so that call found no window, did nothing, and the
# editor was killed at the timeout on every run. Returns whether a window was found to ask.
function Request-WindowClose {
    param([Parameter(Mandatory)][int] $ProcessId)
    $handle = [NativeWindowing]::FindProcessWindow($ProcessId)
    if ($handle -eq [IntPtr]::Zero) { return $false }
    [void] [NativeWindowing]::PostMessageW($handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    return $true
}

# Prefer ProcessId. Looking a window up by process name would find whichever editor happens to be open,
# including one the user started for themselves, and a validation pass must never reach into that.
function Get-WindowHandle {
    param([string] $ProcessName = 'HexelEditor', [int] $ProcessId = 0, [int] $TimeoutMilliseconds = 0)
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    do {
        if ($ProcessId -ne 0) {
            $handle = [NativeWindowing]::FindProcessWindow($ProcessId)
            if ($handle -ne [IntPtr]::Zero) { return $handle }
        }
        else {
            $process = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue |
                Where-Object { [NativeWindowing]::FindProcessWindow($_.Id) -ne [IntPtr]::Zero } | Select-Object -First 1
            if ($process) { return [NativeWindowing]::FindProcessWindow($process.Id) }
        }
        if ([DateTime]::UtcNow -ge $deadline) { break }
        Start-Sleep -Milliseconds 100
    } while ($true)
    $which = if ($ProcessId -ne 0) { "process $ProcessId" } else { $ProcessName }
    throw "No window is open for $which."
}

# A capture taken from a window that cannot draw comes back as one flat color rather than as an error, so a
# run that depends on a picture has to look at whether the picture holds anything.
function Test-ImageHasContent {
    param([Parameter(Mandatory)][string] $Path, [int] $MinimumColors = 8)
    Add-Type -AssemblyName System.Drawing
    $bitmap = [System.Drawing.Bitmap]::FromFile((Resolve-Path -LiteralPath $Path).Path)
    try {
        $colors = @{}
        $step = [Math]::Max(1, [int]([Math]::Min($bitmap.Width, $bitmap.Height) / 40))
        for ($y = 0; $y -lt $bitmap.Height; $y += $step) {
            for ($x = 0; $x -lt $bitmap.Width; $x += $step) { $colors[$bitmap.GetPixel($x, $y).ToArgb()] = 1 }
        }
        [PSCustomObject]@{
            Path = $Path
            Width = $bitmap.Width
            Height = $bitmap.Height
            DistinctColors = $colors.Count
            HasContent = $colors.Count -ge $MinimumColors
        }
    }
    finally { $bitmap.Dispose() }
}

# PrintWindow asks the window to draw itself, so it works while the window sits behind others and unfocused.
# CopyFromScreen reads the desktop instead, which is what tempts a script into bringing the window forward.
function Save-WindowImage {
    param([Parameter(Mandatory)][IntPtr] $Handle, [Parameter(Mandatory)][string] $Path)
    $bounds = New-Object NativeWindowing+Rect
    if (-not [NativeWindowing]::GetWindowRect($Handle, [ref] $bounds)) { throw 'Could not read the window bounds.' }
    $width = $bounds.Right - $bounds.Left
    $height = $bounds.Bottom - $bounds.Top
    if ($width -lt 1 -or $height -lt 1) { throw 'The window has no area to capture.' }
    # PrintWindow draws from the window rect's corner, so the capture is taken at that size and then cropped
    # to the frame the window really occupies. Cropping rather than asking for a smaller bitmap, because the
    # window draws where it draws: a smaller bitmap would cut the right and bottom off instead of the border.
    $drawn = [NativeWindowing]::GetDrawnBounds($Handle)
    $cropX = [Math]::Max(0, $drawn.Left - $bounds.Left)
    $cropY = [Math]::Max(0, $drawn.Top - $bounds.Top)
    $cropWidth = [Math]::Min($drawn.Right - $drawn.Left, $width - $cropX)
    $cropHeight = [Math]::Min($drawn.Bottom - $drawn.Top, $height - $cropY)
    if ($cropWidth -lt 1 -or $cropHeight -lt 1) { throw 'The drawn part of the window has no area to capture.' }
    $bitmap = New-Object System.Drawing.Bitmap $width, $height
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $deviceContext = $graphics.GetHdc()
            try {
                # 2 is PW_RENDERFULLCONTENT, which captures composited content such as the Skia canvas.
                if (-not [NativeWindowing]::PrintWindow($Handle, $deviceContext, 2)) { throw 'PrintWindow refused to draw the window.' }
            }
            finally { $graphics.ReleaseHdc($deviceContext) }
        }
        finally { $graphics.Dispose() }
        $crop = New-Object System.Drawing.Rectangle $cropX, $cropY, $cropWidth, $cropHeight
        $cropped = $bitmap.Clone($crop, $bitmap.PixelFormat)
        try { $cropped.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png) }
        finally { $cropped.Dispose() }
    }
    finally { $bitmap.Dispose() }
    [PSCustomObject]@{ Path = $Path; Width = $cropWidth; Height = $cropHeight }
}
