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
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr handle, out Rect bounds);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr handle, IntPtr deviceContext, uint flags);
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

function Get-WindowHandle {
    param([string] $ProcessName = 'HexelEditor')
    $process = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if (-not $process) { throw "No window is open for $ProcessName." }
    $process.MainWindowHandle
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
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $bitmap.Dispose() }
    [PSCustomObject]@{ Path = $Path; Width = $width; Height = $height }
}
