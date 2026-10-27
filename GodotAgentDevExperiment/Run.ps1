Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Scripts/Common.ps1')

# Opens the application for a person to use: shown, in front and with focus, like any other program. Agents
# that must not disturb whoever is at the machine use Scripts/Validate.ps1, or Scripts/StartForAgent.ps1 to
# look at the window behind every window of theirs.

if (-not (Test-Path -LiteralPath (Get-ApplicationAssembly) -PathType Leaf)) {
    throw 'The application has not been built. Run Build.cmd first.'
}
$logFolder = New-LogFolder -Prefix 'Run_'
$arguments = Get-RunArguments -LogFolder $logFolder
# No -WindowStyle: Windows applies a hidden one to the engine's first show of its window, which then stays
# hidden for good.
$process = Start-Process -FilePath (Get-GodotExecutable) -ArgumentList ($arguments -join ' ') -WorkingDirectory $ApplicationRoot -PassThru
Write-Output "Started process $($process.Id). Logs: $logFolder"
