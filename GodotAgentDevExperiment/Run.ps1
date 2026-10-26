param(
    # Puts the window on the named display without taking focus, for looking at it while someone else uses
    # the machine. Without it the application opens normally, in front, as it should for its user.
    [string] $Display,
    # Returns the started process rather than describing the launch.
    [switch] $PassThru,
    [switch] $Headless,
    [ValidateSet('', 'd3d12', 'vulkan', 'opengl3')][string] $RenderingDriver = '',
    [string] $LogFolder,
    # Further arguments for the application, after the engine's "--" separator.
    [string[]] $ApplicationArguments = @()
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Scripts/Common.ps1')

if (-not (Test-Path -LiteralPath (Get-ApplicationAssembly) -PathType Leaf)) {
    throw 'The application has not been built. Run Build.cmd first.'
}
if (-not $LogFolder) { $LogFolder = New-LogFolder -Prefix 'Run_' }

$position = ''
if ($Display) {
    . (Join-Path $SharedScripts 'Windowing.ps1')
    $bounds = Get-DisplayBounds -Monitor $Display
    # The engine's screen coordinates start at the top left of the virtual desktop, not at the primary
    # display's origin as Windows' do. A position given in Windows coordinates lands off every screen when a
    # display sits above or left of the primary one, and the engine then clamps it onto the primary display.
    $virtual = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $position = "$($bounds.X + 40 - $virtual.Left),$($bounds.Y + 40 - $virtual.Top)"
    # no_focus shows the window without activating it, and is applied when the window is created, before
    # any script runs, so it has to come from the project settings rather than from code.
    Write-SettingsOverride -Lines @('[display]', 'window/size/no_focus=true') | Out-Null
}

$arguments = Get-RunArguments -LogFolder $LogFolder -Headless:$Headless -RenderingDriver $RenderingDriver -Position $position -ApplicationArguments $ApplicationArguments
$executable = Get-GodotExecutable -Console:$Headless
$process = Start-Process -FilePath $executable -ArgumentList ($arguments -join ' ') -WorkingDirectory $ApplicationRoot -PassThru -WindowStyle Hidden
if ($Display) {
    try {
        # The override is read once, at startup. Waiting for the window before removing it leaves no window
        # between runs that could read a stale one, and no file behind for a user's own run to pick up.
        $null = Get-WindowHandle -ProcessId $process.Id -TimeoutMilliseconds 30000
    }
    finally { Remove-SettingsOverride }
}
if ($PassThru) { $process } else { Write-Output "Started process $($process.Id). Logs: $LogFolder" }
