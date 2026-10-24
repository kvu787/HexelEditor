#Requires -Version 7.0
param(
    # The one display set aside for validation. Everything else on the machine belongs to the user.
    [string] $Display = 'NE18NZ2',
    # Omits the pass that puts the real window on that display. The checks that need no display still run.
    [switch] $SkipDisplayPass,
    # Runs the hidden rendering host on this desktop rather than one of its own. See Verify.ps1: it lets the
    # host take the foreground and show a popup on the primary display, so it is for an idle machine only.
    [switch] $SameDesktop
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
. (Join-Path $PSScriptRoot 'DisruptionGuard.ps1')

# The entry point an agent uses to check its own work on this application. Two things make it safe to run
# while someone is using this machine, and the second is the one that matters:
#   1. Every step is built to stay out of the way. Nothing is shown except on the validation display, nothing
#      is activated, and the window that the checks actually drive is never shown at all.
#   2. The whole run is watched, and a run that disturbed the machine fails. Care is an intention; the watch
#      is evidence. If a later change to this repository starts showing a window on a user display or taking
#      the foreground, this script fails and says which window did it.
# An agent must not reach for Run.ps1, Verify.ps1 or the editor directly while someone may be at the machine.
# Those are the pieces; this is the thing that proves the pieces behaved.

$steps = @()
$failures = @()

function Add-Step {
    param([string] $Name, [string] $Outcome, [string] $Detail = '')
    $script:steps += [PSCustomObject]@{ Name = $Name; Outcome = $Outcome; Detail = $Detail }
    $marker = switch ($Outcome) { 'passed' { '  ok  ' } 'skipped' { ' skip ' } default { ' FAIL ' } }
    Write-Output "[$marker] $Name$(if ($Detail) { " - $Detail" })"
    if ($Outcome -eq 'failed') { $script:failures += "$Name - $Detail" }
}

do {
    $validationPath = Join-Path $RepositoryRoot ('LogOutput/Validation_' + (Get-Date -Format 'yyyy-MM-dd_HH-mm-ss'))
    if (-not (Test-Path -LiteralPath $validationPath)) { break }
    Start-Sleep -Milliseconds 50
} while ($true)
New-Item -ItemType Directory -Path $validationPath -Force | Out-Null

$locked = Test-SessionLocked
$inputDesktop = Get-InputDesktopName
Write-Output "Validation display: $Display. Session locked: $locked (input desktop '$inputDesktop')."
Write-Output "Artifacts: $validationPath"
Write-Output ''

$watch = Start-DisruptionWatch -AllowedMonitor $Display
$verifyOutput = @()
try {
    # Build, unit tests, and the real interface driven inside a hidden rendering host. None of this needs a
    # display, so it is the part that works whether the machine is locked or not.
    try {
        $verifyOutput = & (Join-Path $PSScriptRoot 'Verify.ps1') -SameDesktop:$SameDesktop 2>&1
        $verifyOutput | Set-Content -LiteralPath (Join-Path $validationPath 'Verify.txt')
        Add-Step -Name 'Build, tests and hidden-host verification' -Outcome 'passed' -Detail "see Verify.txt"
    }
    catch {
        $verifyOutput + @("$_") | Set-Content -LiteralPath (Join-Path $validationPath 'Verify.txt')
        Add-Step -Name 'Build, tests and hidden-host verification' -Outcome 'failed' -Detail "$_"
    }

    # The real window at real pixel density on the validation display. This is the only step that puts
    # anything on a screen, and the only one that cannot work without an input desktop.
    if ($SkipDisplayPass) {
        Add-Step -Name "Display pass on $Display" -Outcome 'skipped' -Detail 'asked for with -SkipDisplayPass'
    }
    elseif (Get-Process -Name 'HexelEditor' -ErrorAction SilentlyContinue) {
        Add-Step -Name "Display pass on $Display" -Outcome 'skipped' -Detail 'an editor is already running and this pass will not touch it'
    }
    else {
        $editor = $null
        try {
            $editor = & (Join-Path $RepositoryRoot 'Run.ps1') -Display $Display -PassThru
            Register-WatchedProcess -Watch $watch -ProcessId $editor.Id
            $capture = Join-Path $validationPath 'Editor.png'
            & (Join-Path $PSScriptRoot 'CaptureWindow.ps1') -OutputPath $capture -ProcessId $editor.Id -TimeoutMilliseconds 20000 | Out-Null
            $image = Test-ImageHasContent -Path $capture
            $detail = "$($image.Width) x $($image.Height), $($image.DistinctColors) distinct colors"
            if ($image.HasContent) {
                Add-Step -Name "Display pass on $Display" -Outcome 'passed' -Detail $detail
            }
            elseif ($locked) {
                # A window on a desktop that is not being displayed has nothing to draw with. That is a fact
                # about the machine's state, not a defect, so it is recorded rather than failed.
                Add-Step -Name "Display pass on $Display" -Outcome 'skipped' -Detail "the session is locked and the window could not draw ($detail)"
            }
            else {
                Add-Step -Name "Display pass on $Display" -Outcome 'failed' -Detail "the capture came back blank ($detail)"
            }
        }
        catch {
            Add-Step -Name "Display pass on $Display" -Outcome 'failed' -Detail "$_"
        }
        finally {
            if ($editor) {
                # WM_CLOSE asks the window to close and activates nothing.
                $null = $editor.CloseMainWindow()
                if (-not $editor.WaitForExit(10000)) { $editor.Kill() }
            }
        }
    }
}
finally {
    $guard = Stop-DisruptionWatch -Watch $watch
}

if ($guard.Passed) {
    Add-Step -Name 'Did not disturb the machine' -Outcome 'passed' -Detail "$($guard.Observations.Count) window events seen over $($guard.Seconds) s, none on a user display and none taking focus"
}
else {
    Add-Step -Name 'Did not disturb the machine' -Outcome 'failed' -Detail ($guard.Violations -join ' | ')
}

$summary = [PSCustomObject]@{
    Passed = $failures.Count -eq 0
    StartedUtc = $watch.StartedAt.ToString('o')
    Seconds = $guard.Seconds
    SessionLocked = $locked
    InputDesktop = $inputDesktop
    ValidationDisplay = $Display
    ValidationDisplayBounds = $guard.AllowedBounds
    HiddenHostOnOwnDesktop = -not $SameDesktop
    ForegroundAtStart = $guard.BaselineForegroundTitle
    ForegroundChanges = $guard.ForegroundChanges
    CursorClipChanges = $guard.CursorClipChanges
    Steps = $steps
    Violations = $guard.Violations
    Observations = $guard.Observations
}
$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $validationPath 'Validation.json')
$steps | Format-Table -AutoSize | Out-String | Set-Content -LiteralPath (Join-Path $validationPath 'Validation.txt')

Write-Output ''
if ($summary.Passed) {
    Write-Output "Validation passed. Nothing appeared outside $Display, the foreground never moved, and the cursor was never confined."
    Write-Output "Artifacts: $validationPath"
}
else {
    Write-Output "Artifacts: $validationPath"
    throw "Validation failed:`n  - " + ($failures -join "`n  - ")
}
