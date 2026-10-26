#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The rules that decide whether a run disturbed the machine, checked against observations written by hand.
#
# A real disruption is rare and cannot be produced on demand without doing the very thing the guard exists to
# prevent: confining the user's cursor, or putting a window on their screen to see whether it is caught. So
# the collecting and the judging are separate, and this exercises the judging. It is the piece that decides
# whether everything else passed, and it has been wrong: a clip covering the whole virtual desktop, which is
# the cursor free rather than held, was reported as a confinement, and a clip made by anything at all on the
# machine was blamed on the run.

. (Join-Path $PSScriptRoot 'DisruptionGuard.ps1')

$failures = @()
$checked = 0

function Test-Rule {
    param(
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][PSCustomObject] $Allowed,
        [Parameter(Mandatory)] $Observation,
        [Parameter(Mandatory)][int] $ExpectedViolations
    )
    $script:checked++
    $violations = Get-DisruptionViolations -Allowed $Allowed -AllowedMonitor 'TestDisplay' -Observations @($Observation)
    if ($violations.Count -ne $ExpectedViolations) {
        $script:failures += "$Name expected $ExpectedViolations violations and got $($violations.Count): $($violations -join ' | ')"
    }
}

function New-Observation {
    param(
        [Parameter(Mandatory)][string] $Kind,
        [Parameter(Mandatory)][bool] $Watched,
        [Parameter(Mandatory)][int] $Left,
        [Parameter(Mandatory)][int] $Top,
        [Parameter(Mandatory)][int] $Right,
        [Parameter(Mandatory)][int] $Bottom
    )
    $observation = New-Object DisruptionObservation
    $observation.Kind = $Kind
    $observation.Watched = $Watched
    $observation.ProcessId = 1234
    $observation.ProcessName = 'HexelEditor'
    $observation.Title = 'Hexel Editor'
    $observation.Class = 'HwndWrapper'
    $observation.Milliseconds = 1000
    $observation.Left = $Left
    $observation.Top = $Top
    $observation.Right = $Right
    $observation.Bottom = $Bottom
    $observation
}

# A real display, so that "is this on any screen at all" has something true to answer against.
$screen = [System.Windows.Forms.Screen]::AllScreens[0].Bounds
$allowed = [PSCustomObject]@{ X = $screen.X; Y = $screen.Y; Width = $screen.Width; Height = $screen.Height }
$virtual = [System.Windows.Forms.SystemInformation]::VirtualScreen

# The cursor. A clip is attributed to whichever window was in front when it changed, because nothing else says
# who made it, so one that belonged to the user's own program is not this run's to answer for.
Test-Rule -Name 'A clip over the whole virtual desktop is the cursor released' -Allowed $allowed -ExpectedViolations 0 `
    -Observation (New-Observation -Kind 'CursorClipChanged' -Watched $true `
        -Left $virtual.Left -Top $virtual.Top -Right $virtual.Right -Bottom $virtual.Bottom)

Test-Rule -Name 'A clip wider than the virtual desktop is the cursor released' -Allowed $allowed -ExpectedViolations 0 `
    -Observation (New-Observation -Kind 'CursorClipChanged' -Watched $true `
        -Left ($virtual.Left - 1) -Top ($virtual.Top - 1) -Right ($virtual.Right + 1) -Bottom ($virtual.Bottom + 1))

Test-Rule -Name "A clip made while someone else's window was in front is not the run's" -Allowed $allowed -ExpectedViolations 0 `
    -Observation (New-Observation -Kind 'CursorClipChanged' -Watched $false `
        -Left ($virtual.Left + 1) -Top ($virtual.Top + 1) -Right ($virtual.Right - 1) -Bottom ($virtual.Bottom - 1))

Test-Rule -Name "A clip made while the run's own window was in front is a violation" -Allowed $allowed -ExpectedViolations 1 `
    -Observation (New-Observation -Kind 'CursorClipChanged' -Watched $true `
        -Left ($virtual.Left + 1) -Top ($virtual.Top + 1) -Right ($virtual.Right - 1) -Bottom ($virtual.Bottom - 1))

# Windows. Unchanged rules, checked here so that the clip work cannot quietly loosen them.
Test-Rule -Name 'A window of the run inside the validation display is allowed' -Allowed $allowed -ExpectedViolations 0 `
    -Observation (New-Observation -Kind 'WindowShown' -Watched $true `
        -Left $screen.Left -Top $screen.Top -Right $screen.Right -Bottom $screen.Bottom)

Test-Rule -Name 'A window of the run reaching past the validation display is a violation' -Allowed $allowed -ExpectedViolations 1 `
    -Observation (New-Observation -Kind 'WindowShown' -Watched $true `
        -Left ($screen.Left - 10) -Top $screen.Top -Right ($screen.Right - 10) -Bottom $screen.Bottom)

Test-Rule -Name 'A window of the run on no display at all shows nothing to anyone' -Allowed $allowed -ExpectedViolations 0 `
    -Observation (New-Observation -Kind 'WindowShown' -Watched $true -Left -30000 -Top -30000 -Right -28560 -Bottom -29040)

Test-Rule -Name "Someone else's window on their own display is not the run's" -Allowed $allowed -ExpectedViolations 0 `
    -Observation (New-Observation -Kind 'WindowShown' -Watched $false `
        -Left ($screen.Left - 10) -Top $screen.Top -Right ($screen.Right - 10) -Bottom $screen.Bottom)

Test-Rule -Name 'A window of the run taking the foreground is a violation' -Allowed $allowed -ExpectedViolations 1 `
    -Observation (New-Observation -Kind 'Foreground' -Watched $true `
        -Left $screen.Left -Top $screen.Top -Right $screen.Right -Bottom $screen.Bottom)

Test-Rule -Name 'A window of the run capturing the mouse is a violation' -Allowed $allowed -ExpectedViolations 1 `
    -Observation (New-Observation -Kind 'MouseCaptured' -Watched $true `
        -Left $screen.Left -Top $screen.Top -Right $screen.Right -Bottom $screen.Bottom)

if ($failures.Count -gt 0) { throw "Disruption guard rules failed:`n  - " + ($failures -join "`n  - ") }
Write-Output "Disruption guard rules: $checked rules verified against hand-written observations"
