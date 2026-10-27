#Requires -Version 7.0
param(
    # The display to put the window on. NE18NZ2 is the one set aside for agents; every other belongs to the user.
    [Parameter(Mandatory)][string] $Display,
    [ValidateSet('', 'd3d12', 'vulkan', 'opengl3')][string] $RenderingDriver = '',
    [string] $LogFolder,
    # Further arguments for the application, after the engine's "--" separator.
    [string[]] $ApplicationArguments = @()
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
. (Join-Path $SharedScripts 'Windowing.ps1')

# Starts the application for an agent rather than a person: on the given display, without taking focus, while
# someone else uses the machine. Returns the started process. Run.ps1 is the launch for a person. Watching the
# run for disturbances is the caller's job, as Validate.ps1 and Measure.ps1 do it.

if (-not (Test-Path -LiteralPath (Get-ApplicationAssembly) -PathType Leaf)) {
    throw 'The application has not been built. Run Build.ps1 first.'
}
if (-not $LogFolder) { $LogFolder = New-LogFolder -Prefix 'Display_' }

$bounds = Get-DisplayBounds -Monitor $Display
# The engine's screen coordinates start at the top left of the virtual desktop, not at the primary display's
# origin as Windows' do. A position given in Windows coordinates lands off every screen when a display sits
# above or left of the primary one, and the engine then clamps it onto the primary display.
$virtual = [System.Windows.Forms.SystemInformation]::VirtualScreen
$position = "$($bounds.X + 40 - $virtual.Left),$($bounds.Y + 40 - $virtual.Top)"
$arguments = Get-RunArguments -LogFolder $LogFolder -RenderingDriver $RenderingDriver -Position $position -ApplicationArguments $ApplicationArguments

# no_focus shows the window without activating it, and is applied when the window is created, before any
# script runs, so it has to come from the project settings rather than from code.
Write-SettingsOverride -Lines @('[display]', 'window/size/no_focus=true') | Out-Null
try {
    $process = Start-Process -FilePath (Get-GodotExecutable) -ArgumentList ($arguments -join ' ') -WorkingDirectory $ApplicationRoot -PassThru
    # The override is read once, at startup. Waiting for the window before removing it leaves no window
    # between runs that could read a stale one, and no file behind for a person's own run to pick up.
    $null = Get-WindowHandle -ProcessId $process.Id -TimeoutMilliseconds 30000
}
finally { Remove-SettingsOverride }
$process
