#Requires -Version 7.0
param(
    # Which display to open the editor on. Empty opens it where a person would get it: centered on the
    # primary display, in front, with the keyboard.
    [string] $Display = '',
    # How long to wait for the window, a dialog or a state change before giving up on it.
    [int] $TimeoutMilliseconds = 15000,
    # Keeps the editor open at the end instead of closing it through the unsaved-changes prompt.
    [switch] $LeaveOpen
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
. (Join-Path $PSScriptRoot 'Windowing.ps1')

# The editor under a real hand, on the desktop being displayed. This is the disruptive counterpart of
# Validate.ps1: it takes the foreground, moves the real pointer, types on the real keyboard, opens the real
# dialogs and answers them. Everything Validate.ps1 is built never to do, this does on purpose, because a
# menu that opens through its own item, a file dialog that saves where it was told, a message box that is
# answered with a keystroke, and a window closing through Alt+F4 cannot be checked any other way.
#
# So it is for a machine nobody is using. It must never be run while someone may be at the keyboard: their
# keystrokes would land in the editor and the editor's would land in their work.
#
# Input goes through SendInput, which only feeds the desktop being displayed, so this cannot run on a desktop
# of its own; state is read back through UI Automation, which is what a screen reader would see; and every
# stage is photographed, so a run leaves a folder under LogOutput that shows what the editor did.

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

if (-not ('InputDriver' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class InputDriver
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public InputUnion Union; }

    private const uint MouseType = 0, KeyboardType = 1;
    private const uint MouseMove = 0x0001, LeftDown = 0x0002, LeftUp = 0x0004, RightDown = 0x0008, RightUp = 0x0010;
    private const uint MiddleDown = 0x0020, MiddleUp = 0x0040, Wheel = 0x0800, Absolute = 0x8000, VirtualDesk = 0x4000;
    private const uint KeyUp = 0x0002, ExtendedKey = 0x0001, Unicode = 0x0004;
    private const int VirtualLeft = 76, VirtualTop = 77, VirtualWidth = 78, VirtualHeight = 79;

    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr handle, out Rect rect);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr handle, ref Point point);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr handle);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point point);
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }

    public delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out int processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr handle, System.Text.StringBuilder text, int count);

    // The visible top-level windows of a process with their titles: the editor's window, and any dialog or
    // message box it has up. A dialog is owned by the window that opened it, which puts it under that window
    // in the automation tree rather than beside it on the desktop, so dialogs are found this way instead.
    public static System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<IntPtr, string>> VisibleWindowsOf(int processId)
    {
        var windows = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<IntPtr, string>>();
        EnumWindows(delegate (IntPtr handle, IntPtr parameter)
        {
            int owner;
            GetWindowThreadProcessId(handle, out owner);
            if (owner != processId || !IsWindowVisible(handle)) return true;
            var title = new System.Text.StringBuilder(512);
            GetWindowTextW(handle, title, title.Capacity);
            windows.Add(new System.Collections.Generic.KeyValuePair<IntPtr, string>(handle, title.ToString()));
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static void Send(Input input)
    {
        if (SendInput(1, new[] { input }, Marshal.SizeOf(typeof(Input))) != 1)
            throw new InvalidOperationException("SendInput was refused: error " + Marshal.GetLastWin32Error() + ". Is this the desktop being displayed?");
    }

    private static Input Mouse(uint flags, int x, int y, uint data)
    {
        Input input = new Input { Type = MouseType };
        input.Union.Mouse = new MouseInput { X = x, Y = y, Flags = flags, Data = data };
        return input;
    }

    // Absolute coordinates are normalised over the whole virtual desktop, so a point on any display lands.
    public static void MoveTo(int x, int y)
    {
        int left = GetSystemMetrics(VirtualLeft), top = GetSystemMetrics(VirtualTop);
        int width = GetSystemMetrics(VirtualWidth), height = GetSystemMetrics(VirtualHeight);
        int nx = (int)Math.Round((x - left) * 65535.0 / (width - 1));
        int ny = (int)Math.Round((y - top) * 65535.0 / (height - 1));
        Send(Mouse(MouseMove | Absolute | VirtualDesk, nx, ny, 0));
    }

    public static void Button(string button, bool down)
    {
        uint flags = button == "Left" ? (down ? LeftDown : LeftUp)
            : button == "Right" ? (down ? RightDown : RightUp)
            : button == "Middle" ? (down ? MiddleDown : MiddleUp)
            : throw new ArgumentException("Unknown button " + button);
        Send(Mouse(flags, 0, 0, 0));
    }

    public static void Scroll(int notches) { Send(Mouse(Wheel, 0, 0, unchecked((uint)(notches * 120)))); }

    public static void Key(ushort virtualKey, bool down, bool extended)
    {
        Input input = new Input { Type = KeyboardType };
        input.Union.Keyboard = new KeyboardInput { Key = virtualKey, Flags = (down ? 0u : KeyUp) | (extended ? ExtendedKey : 0u) };
        Send(input);
    }

    public static void Text(string text)
    {
        foreach (char character in text)
        {
            Input down = new Input { Type = KeyboardType };
            down.Union.Keyboard = new KeyboardInput { Scan = character, Flags = Unicode };
            Send(down);
            Input up = new Input { Type = KeyboardType };
            up.Union.Keyboard = new KeyboardInput { Scan = character, Flags = Unicode | KeyUp };
            Send(up);
        }
    }
}
'@
}

$report = [System.Collections.Generic.List[string]]::new()
$captures = 0
$editor = $null
$startedAt = [DateTime]::UtcNow
do {
    $exercisePath = Join-Path $RepositoryRoot ('LogOutput/Exercise_' + (Get-Date -Format 'yyyy-MM-dd_HH-mm-ss'))
    if (-not (Test-Path -LiteralPath $exercisePath)) { break }
    Start-Sleep -Milliseconds 50
} while ($true)
New-Item -ItemType Directory -Path $exercisePath -Force | Out-Null

function Step {
    param([Parameter(Mandatory)][string] $Name, [string] $Detail = '')
    $script:report.Add("[  ok  ] $Name$(if ($Detail) { " - $Detail" })")
    Write-Output "[  ok  ] $Name$(if ($Detail) { " - $Detail" })"
}

function Fail {
    param([Parameter(Mandatory)][string] $Message)
    throw $Message
}

function Wait-Until {
    param([Parameter(Mandatory)][scriptblock] $Condition, [Parameter(Mandatory)][string] $Expectation, [int] $Milliseconds = $TimeoutMilliseconds)
    $deadline = [DateTime]::UtcNow.AddMilliseconds($Milliseconds)
    do {
        try { $result = & $Condition } catch { $result = $null }
        if ($result) { return $result }
        if ([DateTime]::UtcNow -ge $deadline) { Fail "$Expectation within $Milliseconds ms." }
        Start-Sleep -Milliseconds 50
    } while ($true)
}

# UI Automation. The editor's named controls carry their x:Name as the automation id, its buttons their
# automation names, and the dialogs are windows of the same process.
$root = [System.Windows.Automation.AutomationElement]::RootElement
function Find-Element {
    param([Parameter(Mandatory)] $Parent, [string] $AutomationId, [string] $Name, [string] $ControlType, [switch] $All)
    $conditions = @()
    if ($AutomationId) { $conditions += New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::AutomationIdProperty), $AutomationId }
    if ($Name) { $conditions += New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::NameProperty), $Name }
    if ($ControlType) { $conditions += New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty), ([System.Windows.Automation.ControlType]::$ControlType) }
    $condition = if ($conditions.Count -eq 1) { $conditions[0] } else { New-Object System.Windows.Automation.AndCondition ([System.Windows.Automation.Condition[]] $conditions) }
    if ($All) { return @($Parent.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)) }
    $Parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

# A visible top-level window of the process whose title matches, as an automation element. Through the
# window handles rather than the automation tree's own children, because a dialog the editor opens is owned by
# its window and sits under it in the tree, where a search of the desktop's children never looks.
function Get-ProcessWindow {
    param([Parameter(Mandatory)][int] $ProcessId, [string] $Title = '')
    foreach ($window in [InputDriver]::VisibleWindowsOf($ProcessId)) {
        if (-not $Title -or $window.Value -like $Title) { return [System.Windows.Automation.AutomationElement]::FromHandle($window.Key) }
    }
    $null
}

function Get-Text { param([Parameter(Mandatory)] $Element) $Element.Current.Name }

# The session log folder of an editor started after a moment: the editor names it with its own clock, so
# it is found by when it was created rather than by name.
function Get-SessionLog {
    param([Parameter(Mandatory)][DateTime] $Since)
    $folder = Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'LogOutput') -Directory |
        Where-Object { $_.Name -match '^\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}$' -and $_.CreationTimeUtc -ge $Since.AddSeconds(-2) } |
        Sort-Object Name | Select-Object -Last 1
    if ($null -eq $folder) { Fail 'No session log folder was created for this launch.' }
    $folder
}

# A control that has only just been built has no place on screen until the next layout pass, so its
# position is waited for rather than read once.
function Get-Center {
    param([Parameter(Mandatory)] $Element)
    $bounds = Wait-Until { $rectangle = $Element.Current.BoundingRectangle; if (-not $rectangle.IsEmpty) { $rectangle } } `
        "$($Element.Current.Name) [$($Element.Current.ClassName), offscreen $($Element.Current.IsOffscreen), runtime id $($Element.GetRuntimeId() -join '.')] has no place on screen to click" -Milliseconds 3000
    [int][Math]::Round($bounds.X + $bounds.Width / 2), [int][Math]::Round($bounds.Y + $bounds.Height / 2)
}

# A control the editor rebuilds, such as a layer row, is found afresh until the one found has a place on
# screen: a reference taken to a row that a later rebuild replaced reports no position for good.
function Wait-ForElement {
    param([Parameter(Mandatory)][string] $Name, [Parameter(Mandatory)][string] $ControlType, [Parameter(Mandatory)][string] $Expectation)
    Wait-Until {
        $found = Find-Element $main -Name $Name -ControlType $ControlType
        if ($null -ne $found -and -not $found.Current.BoundingRectangle.IsEmpty) { $found }
    } $Expectation
}

# Input. Every action waits a little afterwards, because injected input is queued and the editor handles
# it on its own thread; the checks then wait on the state they expect rather than on time.
function Move-Pointer { param([int] $X, [int] $Y) [InputDriver]::MoveTo($X, $Y); Start-Sleep -Milliseconds 30 }
function Click-At {
    param([int] $X, [int] $Y, [string] $Button = 'Left')
    Move-Pointer $X $Y
    [InputDriver]::Button($Button, $true); Start-Sleep -Milliseconds 40
    [InputDriver]::Button($Button, $false); Start-Sleep -Milliseconds 120
}
# The side bar scrolls, and a control below its fold still reports where it would be, with nothing in the
# automation tree to say that it is clipped, so a click aimed there would land on whatever is under the
# window. Whether the control is really in view is judged against the window's own client area, and the
# wheel over the side bar brings it up when it is not.
# Where the side bar is: the column to the right of the canvas, between the toolbar and the status bar. A
# control there is in view when its centre lies inside that band; anything elsewhere is in view when it
# lies inside the window.
function Get-SideBarBand {
    [PSCustomObject]@{
        Left = $origin.X + $client.Right - [int](396 * $dpi); Top = $origin.Y + [int](84 * $dpi)
        Bottom = $origin.Y + $client.Bottom - [int](28 * $dpi)
    }
}
function Test-InView {
    param([Parameter(Mandatory)] $Element)
    $x, $y = Get-Center $Element
    if ($x -lt $origin.X -or $x -ge $origin.X + $client.Right -or $y -lt $origin.Y -or $y -ge $origin.Y + $client.Bottom) { return $false }
    $band = Get-SideBarBand
    if ($x -lt $band.Left) { return $true }
    $y -ge $band.Top -and $y -lt $band.Bottom
}
function Show-Element {
    param([Parameter(Mandatory)] $Element)
    $band = Get-SideBarBand
    for ($turn = 0; $turn -lt 12 -and -not (Test-InView $Element); $turn++) {
        $x, $y = Get-Center $Element
        Move-Pointer ($band.Left + [int](180 * $dpi)) ([int](($band.Top + $band.Bottom) / 2))
        [InputDriver]::Scroll($(if ($y -lt $band.Top) { 3 } else { -3 })); Start-Sleep -Milliseconds 150
    }
    if (-not (Test-InView $Element)) { Fail "$($Element.Current.Name) could not be scrolled into view." }
}
function Click-Element {
    param([Parameter(Mandatory)] $Element, [string] $Button = 'Left')
    if (-not (Test-InView $Element)) { Show-Element $Element }
    $x, $y = Get-Center $Element; Click-At $x $y $Button
}
function Drag-Pointer {
    param([int] $FromX, [int] $FromY, [int] $ToX, [int] $ToY, [string] $Button = 'Left', [int] $Steps = 24, [int] $StepMilliseconds = 30)
    Move-Pointer $FromX $FromY
    [InputDriver]::Button($Button, $true); Start-Sleep -Milliseconds 40
    for ($step = 1; $step -le $Steps; $step++) {
        [InputDriver]::MoveTo([int]($FromX + ($ToX - $FromX) * $step / $Steps), [int]($FromY + ($ToY - $FromY) * $step / $Steps))
        Start-Sleep -Milliseconds $StepMilliseconds
    }
    [InputDriver]::Button($Button, $false); Start-Sleep -Milliseconds 150
}
$VirtualKeys = @{
    Control = 0x11; Shift = 0x10; Alt = 0x12; Enter = 0x0D; Escape = 0x1B; Space = 0x20; Delete = 0x2E; Back = 0x08
    Left = 0x25; Up = 0x26; Right = 0x27; Down = 0x28; F4 = 0x73; OpenBracket = 0xDB; CloseBracket = 0xDD; Plus = 0xBB; Minus = 0xBD
}
$ExtendedKeys = @('Left', 'Up', 'Right', 'Down', 'Delete')
function Get-VirtualKey { param([string] $Key) if ($VirtualKeys.ContainsKey($Key)) { $VirtualKeys[$Key] } elseif ($Key.Length -eq 1) { [int][char]$Key.ToUpperInvariant() } else { Fail "Unknown key $Key." } }
function Press-Key {
    param([Parameter(Mandatory)][string] $Key, [string[]] $Modifiers = @())
    foreach ($modifier in $Modifiers) { [InputDriver]::Key((Get-VirtualKey $modifier), $true, $false); Start-Sleep -Milliseconds 15 }
    $extended = $ExtendedKeys -contains $Key
    [InputDriver]::Key((Get-VirtualKey $Key), $true, $extended); Start-Sleep -Milliseconds 25
    [InputDriver]::Key((Get-VirtualKey $Key), $false, $extended); Start-Sleep -Milliseconds 15
    foreach ($modifier in $Modifiers) { [InputDriver]::Key((Get-VirtualKey $modifier), $false, $false); Start-Sleep -Milliseconds 15 }
    Start-Sleep -Milliseconds 120
}
function Type-Text { param([string] $Text) [InputDriver]::Text($Text); Start-Sleep -Milliseconds 150 }

# Pictures. The editor's own window is photographed with PrintWindow, which draws it as it is; anything that
# floats over it, a menu or a dialog, is photographed off the screen, which is the only place it is.
function Save-Capture {
    param([Parameter(Mandatory)][string] $Name, [switch] $Screen)
    $script:captures++
    $path = Join-Path $exercisePath ('{0:00}-{1}.png' -f $script:captures, $Name)
    if ($Screen) {
        $bounds = New-Object NativeWindowing+Rect
        [void] [NativeWindowing]::GetWindowRect($mainHandle, [ref] $bounds)
        $bitmap = New-Object System.Drawing.Bitmap ($bounds.Right - $bounds.Left), ($bounds.Bottom - $bounds.Top)
        try {
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            try { $graphics.CopyFromScreen($bounds.Left, $bounds.Top, 0, 0, $bitmap.Size) } finally { $graphics.Dispose() }
            $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        }
        finally { $bitmap.Dispose() }
    }
    else { Save-WindowImage -Handle $mainHandle -Path $path | Out-Null }
    $path
}

try {
    if (Get-Process -Name 'HexelEditor' -ErrorAction SilentlyContinue) { Fail 'An editor is already running. This exercise drives whichever window has the keyboard, so it refuses to start beside one.' }

    $runArguments = @{ PassThru = $true }
    if ($Display) { $runArguments.Display = $Display }
    $editor = & (Join-Path $RepositoryRoot 'Run.ps1') @runArguments
    $main = Wait-Until { Get-ProcessWindow -ProcessId $editor.Id -Title '*Hexel Editor' } 'The editor window did not appear'
    $mainHandle = [IntPtr] $main.Current.NativeWindowHandle
    [void] [InputDriver]::SetForegroundWindow($mainHandle)
    Wait-Until { [InputDriver]::GetForegroundWindow() -eq $mainHandle } 'The editor did not take the foreground' | Out-Null
    # The canvas is a plain element with no automation peer, so it is found from the layout: the tool rail
    # to its left, the menu, toolbar and document tab above it, the side bar to its right, the status bar below.
    $dpi = [InputDriver]::GetDpiForWindow($mainHandle) / 96.0
    $client = New-Object InputDriver+Rect
    [void] [InputDriver]::GetClientRect($mainHandle, [ref] $client)
    $origin = New-Object InputDriver+Point
    [void] [InputDriver]::ClientToScreen($mainHandle, [ref] $origin)
    $canvas = [PSCustomObject]@{
        Left = $origin.X + [int](54 * $dpi); Top = $origin.Y + [int](120 * $dpi)
        Right = $origin.X + $client.Right - [int](396 * $dpi); Bottom = $origin.Y + $client.Bottom - [int](28 * $dpi)
    }
    $centerX = [int](($canvas.Left + $canvas.Right) / 2); $centerY = [int](($canvas.Top + $canvas.Bottom) / 2)
    if ($canvas.Right - $canvas.Left -lt 400 -or $canvas.Bottom - $canvas.Top -lt 300) { Fail "The canvas area is too small to drive: $($canvas.Right - $canvas.Left) x $($canvas.Bottom - $canvas.Top)." }
    Step 'Editor started' "window '$(Get-Text $main)' at $dpi scale, canvas $($canvas.Right - $canvas.Left) x $($canvas.Bottom - $canvas.Top) px"
    $status = Find-Element $main -AutomationId 'StatusLabel'
    $title = Find-Element $main -AutomationId 'DocumentTitle'
    $details = Find-Element $main -AutomationId 'DocumentDetails'
    $undo = Find-Element $main -AutomationId 'UndoButton'
    $redo = Find-Element $main -AutomationId 'RedoButton'
    $zoom = Find-Element $main -AutomationId 'ZoomLabel'
    $colorLabel = Find-Element $main -AutomationId 'CurrentColorLabel'
    $toolLabel = Find-Element $main -AutomationId 'ToolLabel'
    $selectionStatus = Find-Element $main -AutomationId 'SelectionStatus'
    foreach ($element in @($status, $title, $details, $undo, $redo, $zoom, $colorLabel, $toolLabel, $selectionStatus)) { if ($null -eq $element) { Fail 'A named control of the window is missing from its automation tree.' } }
    if ((Get-Text $title) -ne 'Neon tide') { Fail "The document tab reads '$(Get-Text $title)' rather than the example's name." }
    Save-Capture 'Started' | Out-Null

    # Hovering names the cell under the pointer. This is the pointer-to-cell mapping under the real pointer,
    # at whatever scale the display runs at.
    Move-Pointer $centerX $centerY
    $hovered = Wait-Until { $text = Get-Text $status; if ($text -match '^Cell (\d+), (\d+)') { $text } } 'Hovering the canvas did not name a cell'
    Move-Pointer ($canvas.Left + 10) ($canvas.Top + 10)
    Wait-Until { (Get-Text $status) -like 'Outside canvas*' } 'Hovering beside the canvas did not say so' | Out-Null
    Step 'Pointer mapping' "the centre of the canvas is '$hovered', its corner is outside"

    # Menus, through their own items. The File menu opens on a click and shows its items; Escape closes it.
    $fileMenu = Find-Element $main -Name 'File' -ControlType 'MenuItem'
    if ($null -eq $fileMenu) { Fail 'The File menu item is not in the automation tree.' }
    Click-Element $fileMenu
    $newItem = Wait-Until { Find-Element $root -Name 'New canvas...' -ControlType 'MenuItem' } 'The File menu did not open its items'
    Save-Capture 'FileMenu' -Screen | Out-Null
    # The keys belong to the menu while it is open: Down walks onto its first item and Escape closes it. Both
    # are editor shortcuts too, and both used to go to the editor instead.
    Press-Key 'Down'
    Wait-Until { (Find-Element $root -Name 'New canvas...' -ControlType 'MenuItem').Current.HasKeyboardFocus } 'Down did not move the highlight onto the first File menu item' | Out-Null
    Press-Key 'Escape'
    Wait-Until { $null -eq (Find-Element $root -Name 'New canvas...' -ControlType 'MenuItem') } 'Escape did not close the File menu' | Out-Null
    # And the View menu through the keyboard: Alt+V, then its first item by Enter fits the canvas, which is
    # what the zoom label shows.
    Press-Key 'V' -Modifiers 'Alt'
    Wait-Until { Find-Element $root -Name 'Fit canvas' -ControlType 'MenuItem' } 'Alt+V did not open the View menu' | Out-Null
    Press-Key 'Escape'
    Step 'Menus' 'the File menu opened on a click, Down walked onto its first item, Escape closed it; the View menu opened on Alt+V'

    # Painting. A stroke dragged across the middle of the canvas marks the document unsaved and leaves one
    # undo; Ctrl+Z takes it out and leaves one redo; Ctrl+Y puts it back.
    $before = Save-Capture 'BeforeStroke'
    Drag-Pointer ($centerX - 220) $centerY ($centerX + 220) $centerY
    Wait-Until { $undo.Current.IsEnabled -and (Get-Text $main) -like '*`* - Hexel Editor' } 'A stroke did not mark the document unsaved and undoable' | Out-Null
    $after = Save-Capture 'AfterStroke'
    $beforeImage = [System.Drawing.Bitmap]::FromFile($before); $afterImage = [System.Drawing.Bitmap]::FromFile($after)
    try {
        $changed = 0
        for ($x = 0; $x -lt $beforeImage.Width; $x += 4) { for ($y = 0; $y -lt $beforeImage.Height; $y += 4) { if ($beforeImage.GetPixel($x, $y) -ne $afterImage.GetPixel($x, $y)) { $changed++ } } }
    }
    finally { $beforeImage.Dispose(); $afterImage.Dispose() }
    if ($changed -lt 50) { Fail "The stroke changed only $changed sampled pixels of the window, so it did not draw." }
    Press-Key 'Z' -Modifiers 'Control'
    Wait-Until { -not $undo.Current.IsEnabled -and $redo.Current.IsEnabled -and (Get-Text $main) -eq 'Neon tide - Hexel Editor' } 'Ctrl+Z did not undo the stroke' | Out-Null
    Press-Key 'Y' -Modifiers 'Control'
    Wait-Until { $undo.Current.IsEnabled -and -not $redo.Current.IsEnabled } 'Ctrl+Y did not redo the stroke' | Out-Null
    Step 'Pencil stroke, undo and redo' "$changed sampled pixels changed under the stroke; Ctrl+Z and Ctrl+Y moved the history both ways"

    # The eraser at a wider brush, through its keys, over the stroke just made; then the right button
    # erasing with the pencil. Both leave their own undo entries.
    Press-Key 'E'
    Wait-Until { (Get-Text $toolLabel) -eq 'Eraser' } 'E did not select the eraser' | Out-Null
    Press-Key 'CloseBracket'; Press-Key 'CloseBracket'
    Drag-Pointer ($centerX - 100) $centerY ($centerX + 100) $centerY
    Wait-Until { $undo.Current.HelpText -like 'Undo Erase*' } 'The eraser stroke was not recorded as an erase' | Out-Null
    Press-Key 'OpenBracket'; Press-Key 'OpenBracket'
    Press-Key 'B'
    Wait-Until { (Get-Text $toolLabel) -eq 'Pencil' } 'B did not select the pencil' | Out-Null
    Drag-Pointer ($centerX - 220) ($centerY + 60) ($centerX + 220) ($centerY + 60) -Button 'Right'
    Wait-Until { $undo.Current.HelpText -like 'Undo Erase*' } 'A right-button drag was not recorded as an erase' | Out-Null
    Save-Capture 'Erased' | Out-Null
    Step 'Eraser and right-button erase' 'both recorded as Erase'

    # View: the wheel zooms about the pointer, a middle drag pans, F fits, H hides and shows the grid.
    $fitted = Get-Text $zoom
    Move-Pointer $centerX $centerY
    [InputDriver]::Scroll(3); Start-Sleep -Milliseconds 200
    Wait-Until { (Get-Text $zoom) -ne $fitted } 'The wheel did not zoom' | Out-Null
    $zoomed = Get-Text $zoom
    Move-Pointer $centerX $centerY
    $cellBefore = Get-Text $status
    Drag-Pointer $centerX $centerY ($centerX + 160) ($centerY + 90) -Button 'Middle'
    Move-Pointer $centerX $centerY
    Wait-Until { (Get-Text $status) -ne $cellBefore } 'A middle drag did not pan the canvas' | Out-Null
    Save-Capture 'ZoomedAndPanned' | Out-Null
    Press-Key 'F'
    Wait-Until { (Get-Text $zoom) -eq $fitted } 'F did not fit the canvas back' | Out-Null
    Press-Key 'H'
    Start-Sleep -Milliseconds 300
    $gridOff = Save-Capture 'GridHidden'
    Press-Key 'H'
    Start-Sleep -Milliseconds 300
    $gridOn = Save-Capture 'GridShown'
    $offImage = [System.Drawing.Bitmap]::FromFile($gridOff); $onImage = [System.Drawing.Bitmap]::FromFile($gridOn)
    try {
        $gridChanged = 0
        for ($x = 0; $x -lt $offImage.Width; $x += 4) { for ($y = 0; $y -lt $offImage.Height; $y += 4) { if ($offImage.GetPixel($x, $y) -ne $onImage.GetPixel($x, $y)) { $gridChanged++ } } }
    }
    finally { $offImage.Dispose(); $onImage.Dispose() }
    if ($gridChanged -lt 50) { Fail "Toggling the grid changed only $gridChanged sampled pixels." }
    Step 'Zoom, pan, fit and grid' "wheel took $fitted to $zoomed, F brought it back, H changed $gridChanged sampled pixels"

    # Shapes and selection. A rectangle outline, then a selection that is moved with the arrows, rotated with
    # Ctrl+R, copied, pasted at the pointer and cleared with Delete, each through its own keys.
    Press-Key 'U'
    Wait-Until { (Get-Text $toolLabel) -eq 'Rectangle' } 'U did not select the rectangle tool' | Out-Null
    Drag-Pointer ($centerX - 150) ($centerY - 120) ($centerX + 150) ($centerY - 20)
    Wait-Until { $undo.Current.HelpText -like 'Undo Rectangle*' } 'The rectangle drag was not recorded' | Out-Null
    Press-Key 'L'
    Wait-Until { (Get-Text $toolLabel) -eq 'Line' } 'L did not select the line tool' | Out-Null
    Drag-Pointer ($centerX - 150) ($centerY + 100) ($centerX + 150) ($centerY + 160)
    Wait-Until { $undo.Current.HelpText -like 'Undo Line*' } 'The line drag was not recorded' | Out-Null
    Press-Key 'M'
    Wait-Until { (Get-Text $toolLabel) -like 'Selection*' } 'M did not select the selection tool' | Out-Null
    Drag-Pointer ($centerX - 160) ($centerY - 130) ($centerX + 160) ($centerY - 10)
    $selected = Wait-Until { $text = Get-Text $selectionStatus; if ($text -match '^(\d+) selected') { [int]$Matches[1] } } 'The selection drag selected nothing'
    Save-Capture 'Selected' | Out-Null
    Press-Key 'Right'
    Wait-Until { $undo.Current.HelpText -like 'Undo Move selection*' } 'The arrow key did not move the selection' | Out-Null
    Press-Key 'R' -Modifiers 'Control'
    Wait-Until { $undo.Current.HelpText -like 'Undo Rotate selection*' } 'Ctrl+R did not rotate the selection' | Out-Null
    Press-Key 'C' -Modifiers 'Control'
    Wait-Until { (Get-Text $status) -like 'Copied cells*' } 'Ctrl+C did not copy' | Out-Null
    Move-Pointer ($centerX + 40) ($centerY + 40)
    Press-Key 'V' -Modifiers 'Control'
    Wait-Until { $undo.Current.HelpText -like 'Undo Paste*' } 'Ctrl+V did not paste' | Out-Null
    Press-Key 'Delete'
    Wait-Until { $undo.Current.HelpText -like 'Undo Clear cells*' } 'Delete did not clear the selected cells' | Out-Null
    Press-Key 'D' -Modifiers 'Control'
    Wait-Until { (Get-Text $selectionStatus) -notlike '*selected*' } 'Ctrl+D did not deselect' | Out-Null
    Press-Key 'A' -Modifiers 'Control'
    Wait-Until { (Get-Text $selectionStatus) -like '1280 selected*' } 'Ctrl+A did not select every cell' | Out-Null
    Press-Key 'Escape'
    Wait-Until { (Get-Text $selectionStatus) -notlike '*selected*' } 'Escape did not deselect' | Out-Null
    Save-Capture 'AfterSelectionWork' | Out-Null
    Step 'Shapes, selection and clipboard' "rectangle, line, $selected cells selected, moved, rotated, copied, pasted, cleared, Ctrl+A and Ctrl+D"

    # Layers: Add, the visibility box, the lock, Rename through its dialog, and a locked layer refusing paint.
    $addLayer = Find-Element $main -Name 'Add' -ControlType 'Button'
    Click-Element $addLayer
    $showNew = Wait-ForElement -Name 'Show Layer 4' -ControlType 'CheckBox' -Expectation 'Add did not add a fourth layer'
    Click-Element $showNew
    Wait-Until { $undo.Current.HelpText -like 'Undo Layer visibility*' } 'The visibility box did not hide the layer' | Out-Null
    # Every change rebuilds the layer rows, so the box is found again rather than clicked through the old row.
    Click-Element (Wait-ForElement -Name 'Show Layer 4' -ControlType 'CheckBox' -Expectation 'The layer row did not come back after hiding it')
    $rename = Find-Element $main -Name 'Rename' -ControlType 'Button'
    Click-Element $rename
    $prompt = Wait-Until { Get-ProcessWindow -ProcessId $editor.Id -Title 'Rename layer' } 'Rename did not open its dialog'
    Save-Capture 'RenameDialog' -Screen | Out-Null
    Press-Key 'A' -Modifiers 'Control'
    Type-Text 'Clouds'
    Press-Key 'Enter'
    Wait-ForElement -Name 'Show Clouds' -ControlType 'CheckBox' -Expectation 'The rename did not reach the layer list' | Out-Null
    Click-Element (Wait-ForElement -Name 'Lock Clouds' -ControlType 'Button' -Expectation 'The renamed layer has no lock button')
    Wait-Until { (Get-Text $selectionStatus) -like '*Layer locked*' } 'Locking the layer did not show in the status bar' | Out-Null
    $lockedUndo = $undo.Current.HelpText
    Press-Key 'B'
    Drag-Pointer ($centerX - 100) ($centerY - 60) ($centerX + 100) ($centerY - 60)
    Start-Sleep -Milliseconds 300
    if ($undo.Current.HelpText -ne $lockedUndo) { Fail "Painting on a locked layer was recorded: '$($undo.Current.HelpText)'." }
    Click-Element (Wait-ForElement -Name 'Unlock Clouds' -ControlType 'Button' -Expectation 'The locked layer has no unlock button')
    Wait-Until { (Get-Text $selectionStatus) -notlike '*Layer locked*' } 'Unlocking the layer did not clear the status' | Out-Null
    Save-Capture 'Layers' | Out-Null
    Step 'Layers' 'added, hidden and shown, renamed through the dialog, locked against painting, unlocked'

    # Colour: a palette swatch under the pointer, and the text box with Enter.
    $swatch = Find-Element $main -Name 'Color #DC1B1E' -ControlType 'Button'
    if ($null -eq $swatch) { Fail 'The palette swatch for #DC1B1E is not in the automation tree.' }
    Click-Element $swatch
    Wait-Until { (Get-Text $colorLabel) -eq '#DC1B1E' } 'Clicking a swatch did not set the foreground colour' | Out-Null
    $colorInput = Find-Element $main -AutomationId 'ColorInput'
    Click-Element $colorInput
    Press-Key 'A' -Modifiers 'Control'
    Type-Text '#2FC866'
    Press-Key 'Enter'
    Wait-Until { (Get-Text $colorLabel) -eq '#2FC866' } 'Typing a colour and Enter did not set the foreground colour' | Out-Null
    $backgroundInput = Find-Element $main -AutomationId 'BackgroundColorInput'
    Click-Element $backgroundInput
    Press-Key 'A' -Modifiers 'Control'
    Type-Text '#203040'
    Press-Key 'Enter'
    Wait-Until { $undo.Current.HelpText -like 'Undo Background color*' } 'Typing a background colour and Enter did not change the background' | Out-Null
    Save-Capture 'Colours' | Out-Null
    Step 'Colours' 'swatch click, the foreground box and the background box'

    # The New canvas dialog: the unsaved prompt first, then a size it refuses, then one it makes.
    Press-Key 'N' -Modifiers 'Control'
    $unsaved = Wait-Until { Get-ProcessWindow -ProcessId $editor.Id -Title 'Unsaved artwork' } 'Ctrl+N on an unsaved document did not ask about saving'
    Save-Capture 'UnsavedPrompt' -Screen | Out-Null
    Press-Key 'N'
    $newCanvas = Wait-Until { Get-ProcessWindow -ProcessId $editor.Id -Title 'New hexel canvas' } 'Declining to save did not open the New canvas dialog'
    $fields = @(Find-Element $newCanvas -ControlType 'Edit' -All)
    if ($fields.Count -ne 3) { Fail "The New canvas dialog shows $($fields.Count) text boxes rather than 3." }
    foreach ($pair in @(@($fields[0], 'Exercise'), @($fields[1], '3000'), @($fields[2], '3000'))) {
        Click-Element $pair[0]; Press-Key 'A' -Modifiers 'Control'; Type-Text $pair[1]
    }
    $flatTop = Find-Element $newCanvas -Name 'Flat top' -ControlType 'RadioButton'
    Click-Element $flatTop
    Save-Capture 'NewCanvasDialog' -Screen | Out-Null
    Press-Key 'Enter'
    $refused = Wait-Until { Get-ProcessWindow -ProcessId $editor.Id -Title 'Canvas size' } 'A 3000 by 3000 canvas was not refused'
    Press-Key 'Enter'
    Wait-Until { $null -eq (Get-ProcessWindow -ProcessId $editor.Id -Title 'Canvas size') } 'The refusal did not close' | Out-Null
    foreach ($pair in @(@($fields[1], '60'), @($fields[2], '45'))) { Click-Element $pair[0]; Press-Key 'A' -Modifiers 'Control'; Type-Text $pair[1] }
    Press-Key 'Enter'
    Wait-Until { (Get-Text $details) -eq '60 x 45 hexels  /  Flat top' -and (Get-Text $title) -eq 'Exercise' } 'The new canvas did not replace the document' | Out-Null
    Save-Capture 'NewCanvas' | Out-Null
    Step 'New canvas' 'unsaved prompt declined, 3000 x 3000 refused, 60 x 45 flat top created'

    # Saving through the file dialog, then Save as, then the saved file read back through the editor's own core.
    Press-Key 'B'
    Drag-Pointer ($centerX - 200) $centerY ($centerX + 200) ($centerY + 80)
    Wait-Until { $undo.Current.IsEnabled } 'The stroke on the new canvas was not recorded' | Out-Null
    $savedPath = Join-Path $exercisePath 'Exercise.hexel'
    Press-Key 'S' -Modifiers 'Control'
    $saveDialog = Wait-Until { Get-ProcessWindow -ProcessId $editor.Id -Title 'Save As' } 'Ctrl+S on a new document did not open the Save dialog'
    Save-Capture 'SaveDialog' -Screen | Out-Null
    Type-Text $savedPath
    Press-Key 'Enter'
    Wait-Until { (Test-Path -LiteralPath $savedPath) -and (Get-Text $main) -eq 'Exercise - Hexel Editor' } 'The project was not saved where it was told to' | Out-Null
    $savedAsPath = Join-Path $exercisePath 'ExerciseCopy.hexel'
    Press-Key 'S' -Modifiers 'Control', 'Shift'
    Wait-Until { Get-ProcessWindow -ProcessId $editor.Id -Title 'Save As' } 'Ctrl+Shift+S did not open the Save dialog' | Out-Null
    Type-Text $savedAsPath
    Press-Key 'Enter'
    Wait-Until { (Test-Path -LiteralPath $savedAsPath) -and (Get-Text $main) -eq 'ExerciseCopy - Hexel Editor' } 'Save as did not save a copy and take its name' | Out-Null
    $core = Join-Path (Split-Path -Parent (Get-EditorExecutable)) 'HexelEditor.Core.dll'
    $loaded = & {
        Add-Type -Path $core
        $document = [HexelEditor.Core.ProjectFile]::Load($savedAsPath)
        $painted = 0
        foreach ($cell in $document.Layers[0].Cells) { if ($cell -ne 0) { $painted++ } }
        [PSCustomObject]@{ Width = $document.Width; Height = $document.Height; Orientation = "$($document.Orientation)"; Painted = $painted; Name = $document.Name }
    }
    if ($loaded.Width -ne 60 -or $loaded.Height -ne 45 -or $loaded.Orientation -ne 'FlatTop' -or $loaded.Painted -lt 10) { Fail "The saved project reads back as $($loaded.Width) x $($loaded.Height) $($loaded.Orientation) with $($loaded.Painted) painted cells." }
    Step 'Save and Save as' "saved through the dialog; the copy reads back as $($loaded.Width) x $($loaded.Height) $($loaded.Orientation) with $($loaded.Painted) painted cells"

    # Help, and the View menu's background prompt, both answered from the keyboard.
    $helpMenu = Find-Element $main -Name 'Help' -ControlType 'MenuItem'
    Click-Element $helpMenu
    $shortcuts = Wait-Until { Find-Element $root -Name 'Shortcuts' -ControlType 'MenuItem' } 'The Help menu did not open'
    Click-Element $shortcuts
    Wait-Until { Get-ProcessWindow -ProcessId $editor.Id -Title 'Hexel Editor - Shortcuts' } 'The Shortcuts dialog did not open' | Out-Null
    Save-Capture 'Shortcuts' -Screen | Out-Null
    Press-Key 'Escape'
    Wait-Until { $null -eq (Get-ProcessWindow -ProcessId $editor.Id -Title 'Hexel Editor - Shortcuts') } 'Escape did not close the Shortcuts dialog' | Out-Null
    Step 'Help' 'the Shortcuts dialog opened from the menu and closed on Escape'

    # Closing. Alt+F4 on an unsaved document asks; Cancel keeps it open, No lets it close, and the session log
    # records the exit.
    Drag-Pointer ($centerX - 50) ($centerY - 50) ($centerX + 50) ($centerY + 50)
    Wait-Until { (Get-Text $main) -like '*`* - Hexel Editor' } 'The last stroke did not mark the document unsaved' | Out-Null
    if ($LeaveOpen) { Step 'Left open' 'as asked'; $editor = $null }
    else {
        Press-Key 'F4' -Modifiers 'Alt'
        Wait-Until { Get-ProcessWindow -ProcessId $editor.Id -Title 'Unsaved artwork' } 'Alt+F4 on an unsaved document did not ask about saving' | Out-Null
        Press-Key 'Escape'
        Start-Sleep -Milliseconds 400
        if ($editor.HasExited) { Fail 'Cancelling the unsaved prompt closed the editor.' }
        Press-Key 'F4' -Modifiers 'Alt'
        Wait-Until { Get-ProcessWindow -ProcessId $editor.Id -Title 'Unsaved artwork' } 'The second Alt+F4 did not ask about saving' | Out-Null
        Press-Key 'N'
        if (-not $editor.WaitForExit($TimeoutMilliseconds)) { Fail 'The editor did not exit after declining to save.' }
        if ($editor.ExitCode -ne 0) { Fail "The editor exited with code $($editor.ExitCode)." }
        $sessionLog = Get-SessionLog -Since $startedAt
        $log = Get-Content -LiteralPath (Join-Path $sessionLog.FullName 'Session.log') -Raw
        foreach ($line in @('Application started.', 'GPU renderer ready.', "Saved project: $savedPath", "Saved project: $savedAsPath", 'Application exited. Exit code: 0.')) {
            if ($log -notlike "*$line*") { Fail "The session log is missing '$line'." }
        }
        Step 'Close' "Alt+F4 asked, Cancel kept the editor, No closed it with exit code 0; the session log in $($sessionLog.Name) records the saves and the exit"
        $editor = $null

        # A project named on the command line opens at startup. Then a canvas of the size the editor is built
        # for, under a real drag, with --log-frames writing how many frames the window really drew.
        $launchedAt = [DateTime]::UtcNow
        $editor = & (Join-Path $RepositoryRoot 'Run.ps1') @runArguments -Project $savedAsPath -EditorArguments '--log-frames'
        $main = Wait-Until { Get-ProcessWindow -ProcessId $editor.Id -Title 'ExerciseCopy - Hexel Editor' } 'The project named on the command line did not open'
        $mainHandle = [IntPtr] $main.Current.NativeWindowHandle
        [void] [InputDriver]::SetForegroundWindow($mainHandle)
        Wait-Until { [InputDriver]::GetForegroundWindow() -eq $mainHandle } 'The reopened editor did not take the foreground' | Out-Null
        $details = Find-Element $main -AutomationId 'DocumentDetails'
        $title = Find-Element $main -AutomationId 'DocumentTitle'
        $undo = Find-Element $main -AutomationId 'UndoButton'
        if ((Get-Text $details) -ne '60 x 45 hexels  /  Flat top') { Fail "The reopened project reads '$(Get-Text $details)'." }
        Save-Capture 'Reopened' | Out-Null
        Press-Key 'N' -Modifiers 'Control'
        $newCanvas = Wait-Until { Get-ProcessWindow -ProcessId $editor.Id -Title 'New hexel canvas' } 'Ctrl+N on a saved document did not open the New canvas dialog'
        $fields = @(Find-Element $newCanvas -ControlType 'Edit' -All)
        foreach ($pair in @(@($fields[0], 'Large'), @($fields[1], '2000'), @($fields[2], '1000'))) { Click-Element $pair[0]; Press-Key 'A' -Modifiers 'Control'; Type-Text $pair[1] }
        Press-Key 'Enter'
        Wait-Until { (Get-Text $details) -eq '2000 x 1000 hexels  /  Pointy top' -and (Get-Text $title) -eq 'Large' } 'The 2000 x 1000 canvas was not created' | Out-Null
        Press-Key 'B'
        Press-Key 'CloseBracket'; Press-Key 'CloseBracket'; Press-Key 'CloseBracket'
        # Three seconds of dragging, so that the frame log, which reports every two seconds while frames are
        # being drawn, has a whole period to report on. The moves are fed as fast as the driver can send them,
        # because a frame is drawn for every move and a slower hand would be measuring the hand.
        Drag-Pointer ($canvas.Left + 40) ($canvas.Top + 40) ($canvas.Right - 40) ($canvas.Bottom - 40) -Steps 1200 -StepMilliseconds 2
        Wait-Until { $undo.Current.IsEnabled } 'The drag on the large canvas was not recorded' | Out-Null
        Save-Capture 'LargeCanvasStroke' | Out-Null
        Start-Sleep -Milliseconds 2500
        Press-Key 'F4' -Modifiers 'Alt'
        Wait-Until { Get-ProcessWindow -ProcessId $editor.Id -Title 'Unsaved artwork' } 'Alt+F4 on the large canvas did not ask about saving' | Out-Null
        Press-Key 'N'
        if (-not $editor.WaitForExit($TimeoutMilliseconds)) { Fail 'The editor did not exit after declining to save the large canvas.' }
        $sessionLog = Get-SessionLog -Since $launchedAt
        $log = Get-Content -LiteralPath (Join-Path $sessionLog.FullName 'Session.log')
        if (($log -join "`n") -notlike "*Opened project: $savedAsPath*") { Fail 'The session log does not record the project opened from the command line.' }
        $frameLines = @($log | Where-Object { $_ -match 'Frames: (\d+) drawn in ([\d.]+) s \(([\d.]+) a second\); gaps median ([\d.]+) ms, shortest ([\d.]+) ms, longest ([\d.]+) ms, (\d+) over twice the median' })
        if ($frameLines.Count -eq 0) { Fail 'The session log holds no frame cadence lines, so --log-frames did not log.' }
        $busiest = $frameLines | ForEach-Object { if ($_ -match 'Frames: (\d+) drawn in ([\d.]+) s \(([\d.]+) a second\); gaps median ([\d.]+) ms, shortest ([\d.]+) ms, longest ([\d.]+) ms, (\d+) over twice the median') { [PSCustomObject]@{ Frames = [int]$Matches[1]; PerSecond = [double]$Matches[3]; Median = [double]$Matches[4]; Longest = [double]$Matches[6]; Late = [int]$Matches[7] } } } | Sort-Object Frames | Select-Object -Last 1
        Step 'Command line project and frame cadence' "the saved project opened at startup; a drag over a 2000 x 1000 canvas with the pointer reported every few milliseconds drew $($busiest.Frames) frames in its busiest two seconds ($($busiest.PerSecond) a second, median gap $($busiest.Median) ms, longest $($busiest.Longest) ms, $($busiest.Late) late)"
        $editor = $null

        # A project that cannot be read is refused with a message, and the editor stays up with the example.
        $corrupt = Join-Path $exercisePath 'Corrupt.hexel'
        [System.IO.File]::WriteAllBytes($corrupt, [byte[]] (1..64))
        $launchedAt = [DateTime]::UtcNow
        $editor = & (Join-Path $RepositoryRoot 'Run.ps1') @runArguments -Project $corrupt
        Wait-Until { Get-ProcessWindow -ProcessId $editor.Id -Title 'Hexel Editor' } 'Opening a corrupt project did not show an error box' | Out-Null
        $main = Get-ProcessWindow -ProcessId $editor.Id -Title 'Neon tide - Hexel Editor'
        if ($null -eq $main) { Fail 'The editor did not stay up with the example behind the error box.' }
        $mainHandle = [IntPtr] $main.Current.NativeWindowHandle
        Save-Capture 'CorruptProject' -Screen | Out-Null
        Press-Key 'Enter'
        Wait-Until { $null -eq (Get-ProcessWindow -ProcessId $editor.Id -Title 'Hexel Editor') } 'Enter did not dismiss the error box' | Out-Null
        if ($editor.HasExited) { Fail 'The editor exited after refusing a corrupt project.' }
        [void] [InputDriver]::SetForegroundWindow($mainHandle)
        Wait-Until { [InputDriver]::GetForegroundWindow() -eq $mainHandle } 'The editor did not take the foreground after the error box' | Out-Null
        Press-Key 'F4' -Modifiers 'Alt'
        if (-not $editor.WaitForExit($TimeoutMilliseconds)) { Fail 'The editor did not exit on Alt+F4 with nothing unsaved.' }
        $log = Get-Content -LiteralPath (Join-Path (Get-SessionLog -Since $launchedAt).FullName 'Session.log') -Raw
        if ($log -notlike '*Editor error: Could not open the project.*') { Fail 'The session log does not record the refused project.' }
        Step 'Corrupt project' 'refused with an error box, logged, and the editor stayed up and closed cleanly'
        $editor = $null
    }
}
catch {
    $report.Add("[ FAIL ] $_")
    Write-Output "[ FAIL ] $_"
    try { Save-Capture 'Failure' -Screen | Out-Null } catch { }
    throw
}
finally {
    if ($editor -and -not $editor.HasExited) { $editor.Kill() }
    $report | Set-Content -LiteralPath (Join-Path $exercisePath 'Exercise.txt')
    Write-Output ''
    Write-Output "Artifacts: $exercisePath"
}
