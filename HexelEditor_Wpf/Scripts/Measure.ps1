#Requires -Version 7.0
param(
    [ValidateSet('Debug', 'Release')][string] $Configuration = 'Release',
    # Nothing is meant to appear on any display during a measurement run. This is the one display where it
    # would be allowed if it did, so that the watch has the same standard to judge by as Validate.ps1.
    [string] $Display = 'NE18NZ2',
    # Skips the build, for measuring a build that is already there.
    [switch] $NoBuild,
    # Measures only these sections, by name, rather than all of them: Renderer frames, Compositing,
    # Canvas paints, Interactions, Interface, Editing, Documents.
    [string[]] $Sections = @(),
    # Measurement walks whole canvases of two million cells, several times each, so it is slower than
    # verification by a wide margin. Raise this rather than guessing why a run was cut short.
    [int] $TimeoutMilliseconds = 900000
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
. (Join-Path $PSScriptRoot 'IsolatedDesktop.ps1')
. (Join-Path $PSScriptRoot 'DisruptionGuard.ps1')

# Times the editor's own work, and nothing else: no window is shown, nothing is activated, and the whole run
# happens on a Win32 desktop of its own, exactly as Verify.ps1 does. The rule this follows is the one in
# AGENTS.md -- the machine is in use, so a measurement that needed a display or the foreground would be
# measuring at someone's expense. What that costs is the compositor and the display: this measures what the
# editor spends per frame, not when a frame reaches a monitor. Those parts are reasoned about, not timed.
#
# The figures land in LogOutput/Measurement_<timestamp>/Measurement.txt, with the raw samples beside them in
# Measurement.json and the run's own account in Session.log.

if (-not $NoBuild) { & (Join-Path $RepositoryRoot 'Build.ps1') -Configuration $Configuration }

$executable = Get-EditorExecutable -Configuration $Configuration
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "The $Configuration application has not been built."
}

do {
    $measurementPath = Join-Path $RepositoryRoot ('LogOutput/Measurement_' + (Get-Date -Format 'yyyy-MM-dd_HH-mm-ss'))
    if (-not (Test-Path -LiteralPath $measurementPath)) { break }
    Start-Sleep -Milliseconds 50
} while ($true)
New-Item -ItemType Directory -Path $measurementPath | Out-Null

Write-Output "Measuring $Configuration. Session locked: $(Test-SessionLocked)."
Write-Output "Artifacts: $measurementPath"

$env:HEXEL_MEASURE_SECTIONS = $Sections -join ','
$watch = Start-DisruptionWatch -AllowedMonitor $Display
try {
    $desktop = New-IsolatedDesktop -Name 'HexelMeasurement'
    try {
        $process = Start-ProcessOnDesktop -Desktop $desktop -FilePath $executable `
            -ArgumentList @('--measure-output', ('"{0}"' -f $measurementPath)) -WorkingDirectory $RepositoryRoot
        Register-WatchedProcess -Watch $watch -ProcessId $process.ProcessId
        $exitCode = Wait-ProcessOnDesktop -Process $process -TimeoutMilliseconds $TimeoutMilliseconds
    }
    finally { Remove-IsolatedDesktop -Desktop $desktop }
}
finally { $guard = Stop-DisruptionWatch -Watch $watch }

$sessionLog = Join-Path $measurementPath 'Session.log'
if ($exitCode -ne 0) {
    if (Test-Path -LiteralPath $sessionLog) { Get-Content -LiteralPath $sessionLog }
    throw "Measurement failed with exit code $exitCode."
}

$report = Join-Path $measurementPath 'Measurement.txt'
if (-not (Test-Path -LiteralPath $report)) { throw 'The measurement run did not produce its report.' }
Get-Content -LiteralPath $report

if (-not $guard.Passed) {
    throw "The measurement run disturbed the machine: $($guard.Violations -join ' | ')"
}
Write-Output "Did not disturb the machine: $($guard.Observations.Count) window events over $($guard.Seconds) s, none on a display and none taking focus."
Write-Output "Artifacts: $measurementPath"
