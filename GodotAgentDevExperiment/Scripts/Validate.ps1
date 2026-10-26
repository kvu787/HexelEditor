#Requires -Version 7.0
param(
    # The one display set aside for validation. Everything else on the machine belongs to the user.
    [string] $Display = 'NE18NZ2',
    # Omits the pass that puts the real window on that display. Everything that needs no display still runs.
    [switch] $SkipDisplayPass,
    # Runs only that pass, on the build that is already there, for looking into it on its own.
    [switch] $OnlyDisplayPass,
    # The rendering drivers to run the checks under on a desktop of their own. Forward+ for d3d12 and vulkan,
    # the Compatibility renderer for opengl3.
    [string[]] $RenderingDrivers = @('d3d12', 'vulkan', 'opengl3'),
    # Frames of the timing measurement in each run.
    [int] $Frames = 240,
    # The grid size for the timing runs on a desktop of their own, to measure what painting costs at the
    # size a real map has. The display pass keeps the default grid.
    [int] $MeasureColumns = 48,
    [int] $MeasureRows = 40
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
. (Join-Path $SharedScripts 'IsolatedDesktop.ps1')
. (Join-Path $SharedScripts 'DisruptionGuard.ps1')

# The entry point an agent uses to check its own work on this application while someone may be using the
# machine. Build, the engine-free tests, the in-engine checks headless and on a desktop of their own under
# each renderer, then the real window on the validation display: the whole run is watched, and a run that
# disturbed the machine fails whatever else passed.

$steps = @()
$failures = @()

function Add-Step {
    param([string] $Name, [string] $Outcome, [string] $Detail = '')
    $script:steps += [PSCustomObject]@{ Name = $Name; Outcome = $Outcome; Detail = $Detail }
    $marker = switch ($Outcome) { 'passed' { '  ok  ' } 'skipped' { ' skip ' } default { ' FAIL ' } }
    Write-Output "[$marker] $Name$(if ($Detail) { " - $Detail" })"
    if ($Outcome -eq 'failed') { $script:failures += "$Name - $Detail" }
}

# Every check the application runs has to have reported, so an application that exits early cannot pass
# by silence. Skipped is a report too: it says why.
$expectedChecks = @(
    'Environment:', 'Button click:', 'Mouse position sources:', 'Click paints the cell under the pointer:',
    'Painted cell is drawn:', 'Drag paints a stroke:', 'Ctrl+Z undoes the last cell:', 'Tool shortcut keys:',
    'Menu, item and dialog:', 'Escape closes the menu:', 'File dialog opens embedded and closes:',
    'glTF piece under SimplePaint:', 'T turns the camera top-down:', 'Viewport capture:', 'Frame timing:'
)

function Read-ChecksReport {
    param([Parameter(Mandatory)][string] $Folder, [Parameter(Mandatory)][int] $ExitCode)
    $report = Join-Path $Folder 'Checks.txt'
    if (-not (Test-Path -LiteralPath $report)) { throw "No Checks.txt was written (exit code $ExitCode). See $(Join-Path $Folder 'Godot.log')." }
    $lines = @(Get-Content -LiteralPath $report)
    $missing = @($expectedChecks | Where-Object { $name = $_; -not ($lines | Where-Object { $_.StartsWith($name) }) })
    if ($missing.Count -gt 0) { throw "The checks report is missing: $($missing -join ' ')" }
    $failed = @($lines | Where-Object { $_ -match '^[^:]+: failed' })
    if ($ExitCode -ne 0 -or $failed.Count -gt 0) { throw "Checks failed (exit code $ExitCode): $($failed -join ' | ')" }
    $passed = @($lines | Where-Object { $_ -match '^[^:]+: passed' }).Count
    $skipped = @($lines | Where-Object { $_ -match '^[^:]+: skipped' })
    $timing = ($lines | Where-Object { $_.StartsWith('Frame timing:') }) -replace '^Frame timing: passed - ', ''
    "$passed passed, $($skipped.Count) skipped$(if ($skipped.Count) { ' (' + (($skipped | ForEach-Object { ($_ -split ': ')[0] }) -join ', ') + ')' }); $timing"
}

$validationPath = New-LogFolder -Prefix 'Validation_'
$locked = Test-SessionLocked
Write-Output "Validation display: $Display. Session locked: $locked (input desktop '$(Get-InputDesktopName)')."
Write-Output "Artifacts: $validationPath"
Write-Output ''

# Watched from the start. The engine's windowed and console executables, and the two processes Windows puts
# a window up for when a program dies badly.
$watch = Start-DisruptionWatch -AllowedMonitor $Display -WatchNames @('Godot_v4.7.2-stable_mono_win64', 'Godot_v4.7.2-stable_mono_win64_console', 'WerFault', 'vsjitdebugger')
try {
    try {
        $ruleOutput = & (Join-Path $SharedScripts 'TestDisruptionGuard.ps1')
        Add-Step -Name 'Disruption guard rules' -Outcome 'passed' -Detail ($ruleOutput -join ' ')
    }
    catch { Add-Step -Name 'Disruption guard rules' -Outcome 'failed' -Detail "$_" }

    if ($OnlyDisplayPass) {
        Add-Step -Name 'Import, build, tests and the passes that need no display' -Outcome 'skipped' -Detail 'asked for with -OnlyDisplayPass'
        $RenderingDrivers = @()
    }
    elseif ($true) {
    try {
        & (Join-Path $ExperimentRoot 'Build.ps1') 2>&1 | Set-Content -LiteralPath (Join-Path $validationPath 'Build.txt')
        Add-Step -Name 'Import and build' -Outcome 'passed' -Detail 'see Build.txt'
    }
    catch { Add-Step -Name 'Import and build' -Outcome 'failed' -Detail "$_" }

    try {
        $testOutput = & (Join-Path $PSScriptRoot 'Test.ps1') 2>&1
        $testOutput | Set-Content -LiteralPath (Join-Path $validationPath 'Tests.txt')
        $summary = ($testOutput | Where-Object { "$_" -like '*Total:*' } | Select-Object -Last 1) -replace '^\s+', ''
        Add-Step -Name 'Core unit tests' -Outcome 'passed' -Detail "$summary"
    }
    catch { Add-Step -Name 'Core unit tests' -Outcome 'failed' -Detail "$_" }

    # No display server at all: the interface is built and driven, nothing is drawn.
    try {
        $folder = Join-Path $validationPath 'Headless'
        New-Item -ItemType Directory -Path $folder -Force | Out-Null
        $arguments = Get-RunArguments -LogFolder $folder -Headless -ApplicationArguments @('--checks', '--frames', "$Frames")
        $process = Start-Process -FilePath (Get-GodotExecutable -Console) -ArgumentList ($arguments -join ' ') -WorkingDirectory $ApplicationRoot -PassThru -WindowStyle Hidden
        if (-not $process.WaitForExit(180000)) { $process.Kill(); throw 'The headless run did not exit within 180 s.' }
        Add-Step -Name 'Headless checks' -Outcome 'passed' -Detail (Read-ChecksReport -Folder $folder -ExitCode $process.ExitCode)
    }
    catch { Add-Step -Name 'Headless checks' -Outcome 'failed' -Detail "$_" }
    }

    # A desktop of its own: real rendering, real GPU, and no display, foreground, taskbar or pointer to
    # reach. One run per renderer, each measured on the larger grid.
    foreach ($driver in $RenderingDrivers) {
        try {
            $folder = Join-Path $validationPath "OwnDesktop_$driver"
            New-Item -ItemType Directory -Path $folder -Force | Out-Null
            $arguments = Get-RunArguments -LogFolder $folder -RenderingDriver $driver -ApplicationArguments @('--checks', '--frames', "$Frames", '--columns', "$MeasureColumns", '--rows', "$MeasureRows")
            $desktop = New-IsolatedDesktop -Name 'GodotValidation'
            try {
                $process = Start-ProcessOnDesktop -Desktop $desktop -FilePath (Get-GodotExecutable) -ArgumentList $arguments -WorkingDirectory $ApplicationRoot
                $exitCode = Wait-ProcessOnDesktop -Process $process -TimeoutMilliseconds 180000
            }
            finally { Remove-IsolatedDesktop -Desktop $desktop }
            Add-Step -Name "Checks on a desktop of their own ($driver)" -Outcome 'passed' -Detail (Read-ChecksReport -Folder $folder -ExitCode $exitCode)
        }
        catch { Add-Step -Name "Checks on a desktop of their own ($driver)" -Outcome 'failed' -Detail "$_" }
    }

    # The real window on the validation display, at that display's real pixel density, driven through the
    # same checks and photographed from outside as well as from inside.
    if ($SkipDisplayPass) {
        Add-Step -Name "Display pass on $Display" -Outcome 'skipped' -Detail 'asked for with -SkipDisplayPass'
    }
    else {
        $application = $null
        try {
            $folder = Join-Path $validationPath 'Display'
            New-Item -ItemType Directory -Path $folder -Force | Out-Null
            # The checks take a few seconds; --hold keeps the window up afterwards so it can be photographed
            # from outside and its styles and taskbar button looked at.
            $application = & (Join-Path $ExperimentRoot 'Run.ps1') -Display $Display -PassThru -LogFolder $folder -ApplicationArguments @('--checks', '--frames', "$Frames", '--hold', '12')
            Register-WatchedProcess -Watch $watch -ProcessId $application.Id
            $handle = Get-WindowHandle -ProcessId $application.Id -TimeoutMilliseconds 30000
            # Photographed once the checks have run, so the picture shows the interface they drove rather than
            # the engine's boot splash, which is all a window shows in its first moments.
            $deadline = [DateTime]::UtcNow.AddSeconds(120)
            while (-not (Test-Path -LiteralPath (Join-Path $folder 'Checks.txt')) -and [DateTime]::UtcNow -lt $deadline -and -not $application.HasExited) { Start-Sleep -Milliseconds 200 }
            $capture = Join-Path $folder 'Window.png'
            & (Join-Path $SharedScripts 'CaptureWindow.ps1') -OutputPath $capture -ProcessId $application.Id -TimeoutMilliseconds 20000 | Out-Null
            $image = Test-ImageHasContent -Path $capture
            $styles = Get-WindowStyles -Handle $handle
            # The engine gives its main window WS_EX_APPWINDOW, so the taskbar shows a button for it while it is
            # open. Recorded rather than failed: whether that counts as a disturbance is a judgement the report
            # leaves to its reader, and the findings discuss it.
            $styles += "; $(Test-TaskbarButton -Title 'Godot')"
            if (-not $application.WaitForExit(180000)) { throw 'The display run did not exit within 180 s.' }
            $detail = Read-ChecksReport -Folder $folder -ExitCode $application.ExitCode
            $detail += "; window $styles; PrintWindow capture $($image.Width) x $($image.Height) with $($image.DistinctColors) distinct colors"
            if (-not $image.HasContent -and -not $locked) { throw "the capture from outside came back blank; $detail" }
            Add-Step -Name "Display pass on $Display" -Outcome 'passed' -Detail $detail
        }
        catch { Add-Step -Name "Display pass on $Display" -Outcome 'failed' -Detail "$_" }
        finally {
            if ($application -and -not $application.HasExited) {
                $null = Request-WindowClose -ProcessId $application.Id
                if (-not $application.WaitForExit(10000)) { $application.Kill() }
            }
        }
    }
}
finally {
    $guard = Stop-DisruptionWatch -Watch $watch
}

if ($guard.Passed) {
    $clips = if ($guard.CursorClipChanges -gt 0) { ", and $($guard.CursorClipChanges) cursor clip changes that belonged to someone else" } else { '' }
    Add-Step -Name 'Did not disturb the machine' -Outcome 'passed' -Detail "$($guard.Observations.Count) window events seen over $($guard.Seconds) s, none on a user display and none taking focus$clips"
}
else {
    Add-Step -Name 'Did not disturb the machine' -Outcome 'failed' -Detail ($guard.Violations -join ' | ')
}

$summary = [PSCustomObject]@{
    Passed = $failures.Count -eq 0
    StartedUtc = $watch.StartedAt.ToString('o')
    Seconds = $guard.Seconds
    SessionLocked = $locked
    ValidationDisplay = $Display
    ValidationDisplayBounds = $guard.AllowedBounds
    ForegroundAtStart = $guard.BaselineForegroundTitle
    ForegroundChanges = $guard.ForegroundChanges
    CursorClipChanges = $guard.CursorClipChanges
    Steps = $steps
    Violations = $guard.Violations
    Observations = $guard.Observations
}
$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $validationPath 'Validation.json')
$steps | Format-Table -AutoSize -Wrap | Out-String -Width 400 | Set-Content -LiteralPath (Join-Path $validationPath 'Validation.txt')

Write-Output ''
if ($summary.Passed) {
    Write-Output "Validation passed. Nothing of the run appeared outside $Display, took the foreground, or confined the cursor."
    Write-Output "Artifacts: $validationPath"
}
else {
    Write-Output "Artifacts: $validationPath"
    throw "Validation failed:`n  - " + ($failures -join "`n  - ")
}
