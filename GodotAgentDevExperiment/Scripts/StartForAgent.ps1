#Requires -Version 7.0
param(
    # A display set aside for agents, which the window goes onto without focus. Without one, every display
    # belongs to the user, and the window goes onto the primary one behind every window they have open.
    [string] $Display = '',
    [ValidateSet('', 'd3d12', 'vulkan', 'opengl3')][string] $RenderingDriver = '',
    [string] $LogFolder,
    # Further arguments for the application, after the engine's "--" separator.
    [string[]] $ApplicationArguments = @()
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
. (Join-Path $PSScriptRoot 'HookedLaunch.ps1')
. (Join-Path $SharedScripts 'Windowing.ps1')

# Starts the application for an agent rather than a person: without taking focus, and either on a display set
# aside for it or behind every window of the person using the machine. Returns the started process and
# writes how the window was placed to Launch.txt in the log folder. Run.ps1 is the launch for a person.
# Watching the run for disturbances is the caller's job, as Validate.ps1 and Measure.ps1 do it.

if (-not (Test-Path -LiteralPath (Get-ApplicationAssembly) -PathType Leaf)) {
    throw 'The application has not been built. Run Build.ps1 first.'
}
if (-not $LogFolder) { $LogFolder = New-LogFolder -Prefix 'Agent_' }
$launchLog = Join-Path $LogFolder 'Launch.txt'

# The engine's screen coordinates start at the top left of the virtual desktop, not at the primary display's
# origin as Windows' do. A position given in Windows coordinates lands off every screen when a display sits
# above or left of the primary one, and the engine then clamps it onto the primary display.
$virtual = [System.Windows.Forms.SystemInformation]::VirtualScreen
$notes = @()
$clock = [System.Diagnostics.Stopwatch]::StartNew()
if ($Display) {
    $bounds = Get-DisplayBounds -Monitor $Display
    $position = "$($bounds.X + 40 - $virtual.Left),$($bounds.Y + 40 - $virtual.Top)"
    $arguments = Get-RunArguments -LogFolder $LogFolder -RenderingDriver $RenderingDriver -Position $position -ApplicationArguments $ApplicationArguments
    # no_focus shows the window without activating it, and is applied when the window is created, before
    # any script runs, so it has to come from the project settings rather than from code. It also makes the
    # window topmost, which on a display set aside for it is no harm.
    Write-SettingsOverride -Lines @('[display]', 'window/size/no_focus=true') | Out-Null
    try {
        $process = Start-Process -FilePath (Get-GodotExecutable) -ArgumentList ($arguments -join ' ') -WorkingDirectory $ApplicationRoot -PassThru
        # The override is read once, at startup. Waiting for the window before removing it leaves no window
        # between runs that could read a stale one, and no file behind for a person's own run to pick up.
        $handle = Get-WindowHandle -ProcessId $process.Id -TimeoutMilliseconds 30000
    }
    finally { Remove-SettingsOverride }
    $notes += "Shown on $Display $([Math]::Round($clock.Elapsed.TotalMilliseconds)) ms after launch without focus."
}
else {
    # The engine shows its own window, and a new window is shown at the top of the Z-order, in front of
    # whatever the user has there. Two things the engine does make that hard to prevent from outside: the
    # setting that keeps it from taking focus also creates the window topmost, and Windows brings a window
    # that was created topmost to the front the moment it is shown, whatever was done to its order while it
    # was hidden; and a window's order cannot be changed from another process until the engine's thread
    # reads its messages, which it first does as it shows the window.
    #
    # So the placing is done from inside the engine's own thread, by BehindHook, a CBT hook installed on
    # that thread as the process starts: every top-level window it creates is created at the bottom of the
    # Z-order and marked not to activate, and every activation and focus change is refused. With activation
    # refused, the engine runs without no_focus, so the window is never topmost and stays where it was
    # created: behind everything.
    $final = Get-BehindBounds -Width 1280 -Height 800
    $position = "$($final.X - $virtual.Left),$($final.Y - $virtual.Top)"
    $arguments = Get-RunArguments -LogFolder $LogFolder -RenderingDriver $RenderingDriver -Position $position -Resolution "$($final.Width)x$($final.Height)" -ApplicationArguments $ApplicationArguments
    $hooked = Start-HookedProcess -FilePath (Get-GodotExecutable) -ArgumentList $arguments -WorkingDirectory $ApplicationRoot -HookLibrary (Get-BehindHookLibrary)
    $process = Get-Process -Id $hooked.ProcessId
    $notes += "Hooked $($hooked.HookedAfterMilliseconds) ms after launch."
    $handle = Wait-WindowCreated -ProcessId $process.Id -ClassName 'Engine' -TimeoutMilliseconds 30000
    $notes += "Window created $([Math]::Round($clock.Elapsed.TotalMilliseconds)) ms after launch, $(Get-WindowStyles -Handle $handle); $(Get-WindowZOrderReport -Handle $handle)."
    $handle = Get-WindowHandle -ProcessId $process.Id -TimeoutMilliseconds 30000
    $notes += "Shown $([Math]::Round($clock.Elapsed.TotalMilliseconds)) ms after launch; $(Get-WindowZOrderReport -Handle $handle)."
    # The hook stays on the thread for the life of this PowerShell session, which installed it; nothing of
    # the engine's later behavior may activate the window either.
}
$notes | Set-Content -LiteralPath $launchLog
$process
