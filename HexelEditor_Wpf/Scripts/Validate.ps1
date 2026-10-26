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

# With -SameDesktop the hidden host is allowed to disturb the machine: it takes the foreground, captures the
# mouse and opens its submenu's popup on the primary display, which is what that switch accepts in return for
# the stronger menu check. Watching it would only fail the run for what was asked for, so the watch then
# covers the display pass alone, which is held to the same standard either way.
$watch = if ($SameDesktop) { $null } else { Start-DisruptionWatch -AllowedMonitor $Display }
# Filled line by line as Verify.ps1 writes them, so that a run which fails partway still leaves everything it
# said before failing in Verify.txt. Assigned from the pipeline, the lines before the failure were lost with
# the assignment, and the file held only the error.
$verifyOutput = [System.Collections.Generic.List[object]]::new()
try {
    # The rules that decide whether this run disturbed anyone, checked first. Everything below is judged by
    # them, so a run whose verdict cannot be trusted is worth stopping at rather than reporting.
    try {
        $ruleOutput = & (Join-Path $PSScriptRoot 'TestDisruptionGuard.ps1')
        Add-Step -Name 'Disruption guard rules' -Outcome 'passed' -Detail ($ruleOutput -join ' ')
    }
    catch {
        Add-Step -Name 'Disruption guard rules' -Outcome 'failed' -Detail "$_"
    }

    # Build, unit tests, and the real interface driven inside a hidden rendering host. None of this needs a
    # display, so it is the part that works whether the machine is locked or not.
    try {
        & (Join-Path $PSScriptRoot 'Verify.ps1') -SameDesktop:$SameDesktop 2>&1 | ForEach-Object { $verifyOutput.Add($_) }
        $verifyOutput | Set-Content -LiteralPath (Join-Path $validationPath 'Verify.txt')
        Add-Step -Name 'Build, tests and hidden-host verification' -Outcome 'passed' -Detail "see Verify.txt"
    }
    catch {
        $verifyOutput.Add("$_")
        $verifyOutput | Set-Content -LiteralPath (Join-Path $validationPath 'Verify.txt')
        Add-Step -Name 'Build, tests and hidden-host verification' -Outcome 'failed' -Detail "$_"
    }

    if ($null -eq $watch) { $watch = Start-DisruptionWatch -AllowedMonitor $Display }

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
                # WM_CLOSE asks the window to close and activates nothing. Through the window that was found
                # rather than Process.CloseMainWindow, which sees no main window here and did nothing, so the
                # editor never exited on its own and was killed ten seconds later every run.
                $null = Request-WindowClose -ProcessId $editor.Id
                if (-not $editor.WaitForExit(10000)) { $editor.Kill() }
            }
        }
    }
}
finally {
    $guard = if ($null -ne $watch) { Stop-DisruptionWatch -Watch $watch } else { $null }
}
if ($null -eq $guard) { throw 'The disruption watch never started, so this run cannot say whether it disturbed anyone.' }

$watched = if ($SameDesktop) { 'Display pass did not disturb the machine' } else { 'Did not disturb the machine' }
if ($guard.Passed) {
    # Clip changes are worth reporting even when none of them was this run's: they are the machine being used
    # while it ran, which is the condition all of this exists to survive.
    $clips = if ($guard.CursorClipChanges -gt 0) { ", and $($guard.CursorClipChanges) cursor clip changes that belonged to someone else" } else { '' }
    Add-Step -Name $watched -Outcome 'passed' -Detail "$($guard.Observations.Count) window events seen over $($guard.Seconds) s, none on a user display and none taking focus$clips"
}
else {
    Add-Step -Name $watched -Outcome 'failed' -Detail ($guard.Violations -join ' | ')
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
    WatchedFrom = if ($SameDesktop) { 'the display pass' } else { 'the start' }
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
    $covered = if ($SameDesktop) { 'the display pass' } else { 'the run' }
    Write-Output "Validation passed. Nothing of $covered appeared outside $Display, took the foreground, or confined the cursor."
    Write-Output "Artifacts: $validationPath"
}
else {
    Write-Output "Artifacts: $validationPath"
    throw "Validation failed:`n  - " + ($failures -join "`n  - ")
}
