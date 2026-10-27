#Requires -Version 7.0
param(
    # A display set aside for the window. Without one it sits on the primary display behind every window of
    # the user's. Either way the frames reach a real display, which is what PresentMon times.
    [string] $Display = '',
    [int] $Frames = 900,
    # The grid size, to measure what a brush stroke costs on a map the size a real one has.
    [int] $Columns = 1024,
    [int] $Rows = 1024,
    [ValidateSet('', 'd3d12', 'vulkan', 'opengl3')][string] $RenderingDriver = '',
    # Skips PresentMon, which otherwise records every frame's path from present to display.
    [switch] $NoPresentMon,
    # Summarizes an earlier run's folder again instead of measuring.
    [string] $ParseOnly = ''
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
. (Join-Path $SharedScripts 'DisruptionGuard.ps1')

# Measures the application where the numbers mean something: on a real display, presenting real frames,
# with the engine timing its own work from inside and PresentMon timing the frames from outside, from the
# present call to the moment the display showed them. Watched like any other run, since it puts a window
# on a display while someone may be using the machine.

function Get-Median { param([double[]] $Values) if ($Values.Count -eq 0) { return 0.0 }; $sorted = @($Values | Sort-Object); $sorted[[int][Math]::Floor($sorted.Count / 2)] }
function Get-Percentile95 { param([double[]] $Values) if ($Values.Count -eq 0) { return 0.0 }; $sorted = @($Values | Sort-Object); $sorted[[Math]::Min($sorted.Count - 1, [int]($sorted.Count * 0.95))] }

function Get-MeasurementSummary {
    param([Parameter(Mandatory)][string] $Folder, [switch] $WithPresentMon)
    $lines = @()
    $measurement = Get-Content -LiteralPath (Join-Path $Folder 'Measurement.json') -Raw | ConvertFrom-Json
    $lines += "Inside the engine, $($measurement.Frames) frames on a $($measurement.Grid.Columns) x $($measurement.Grid.Rows) grid, $($measurement.Renderer.Method) on $($measurement.Renderer.Driver) ($($measurement.Renderer.Adapter)):"
    $lines += ('  {0,-44} median {1,8:0.000} ms   p95 {2,8:0.000} ms' -f 'frame interval', $measurement.FrameIntervalMs.Median, $measurement.FrameIntervalMs.Percentile95)
    $lines += ('  {0,-44} median {1,8:0.000} ms   p95 {2,8:0.000} ms' -f 'render, CPU side', $measurement.RenderCpuMs.Median, $measurement.RenderCpuMs.Percentile95)
    $lines += ('  {0,-44} median {1,8:0.000} ms   p95 {2,8:0.000} ms' -f 'render, GPU', $measurement.RenderGpuMs.Median, $measurement.RenderGpuMs.Percentile95)
    $lines += ('  {0,-44} median {1,8:0.000} ms   p95 {2,8:0.000} ms   ({3} uploads)' -f 'texture upload of the whole grid', $measurement.UploadMs.Median, $measurement.UploadMs.Percentile95, $measurement.TextureUploads)

    if ($WithPresentMon) {
        $csvPath = Join-Path $Folder 'PresentMon.csv'
        if (-not (Test-Path -LiteralPath $csvPath)) { return $lines + 'PresentMon wrote no CSV. See PresentMon.error.log.' }
        $rows = @(Import-Csv -LiteralPath $csvPath)
        if ($rows.Count -eq 0) { return $lines + 'PresentMon recorded no frames.' }
        $columns = @($rows[0].PSObject.Properties.Name)
        # The first full row, since the last one can be cut short when the trace is stopped.
        $first = $rows[[Math]::Min(1, $rows.Count - 1)]
        $lines += "PresentMon, $($rows.Count) presented frames, present mode '$($first.PresentMode)', runtime '$($first.PresentRuntime)', sync interval $($first.SyncInterval):"
        # PresentMon 2.x column names, then the 1.x names they replaced, so either version's CSV reads.
        foreach ($metric in @(
            @{ Name = 'between presents'; Columns = @('MsBetweenPresents', 'FrameTime') },
            @{ Name = 'between display changes'; Columns = @('MsBetweenDisplayChange', 'DisplayedTime') },
            @{ Name = 'CPU busy'; Columns = @('MsCPUBusy', 'CPUBusy') },
            @{ Name = 'CPU wait (in present)'; Columns = @('MsCPUWait', 'CPUWait') },
            @{ Name = 'GPU time'; Columns = @('MsGPUTime', 'GPUTime') },
            @{ Name = 'GPU latency (CPU start to GPU start)'; Columns = @('MsGPULatency', 'GPULatency') },
            @{ Name = 'render to present latency'; Columns = @('MsRenderPresentLatency') },
            @{ Name = 'until displayed (present to display)'; Columns = @('MsUntilDisplayed', 'DisplayLatency') },
            @{ Name = 'all input to photon'; Columns = @('MsAllInputToPhotonLatency', 'AllInputToPhotonLatency') },
            @{ Name = 'click to photon'; Columns = @('MsClickToPhotonLatency', 'ClickToPhotonLatency') }
        )) {
            $column = @($metric.Columns | Where-Object { $columns -contains $_ }) | Select-Object -First 1
            if (-not $column) { continue }
            $values = [double[]] @(foreach ($row in $rows) { $value = $row.$column; if ($value -and $value -ne 'NA') { [double] $value } })
            if ($values.Count -eq 0) { $lines += ('  {0,-44} no values (the run drove no real input)' -f $metric.Name); continue }
            $lines += ('  {0,-44} median {1,8:0.000} ms   p95 {2,8:0.000} ms   ({3} frames)' -f $metric.Name, (Get-Median $values), (Get-Percentile95 $values), $values.Count)
        }
    }
    $lines
}

if ($ParseOnly) {
    $summary = Get-MeasurementSummary -Folder $ParseOnly -WithPresentMon:(Test-Path -LiteralPath (Join-Path $ParseOnly 'PresentMon.csv'))
    $summary | Set-Content -LiteralPath (Join-Path $ParseOnly 'Measurement.txt')
    $summary | Write-Output
    return
}

if (-not (Test-Path -LiteralPath (Get-ApplicationAssembly) -PathType Leaf)) { throw 'The application has not been built. Run Build.cmd first.' }
$folder = New-LogFolder -Prefix 'Measurement_'
Write-Output "Artifacts: $folder"

$presentMon = Join-Path $env:UserProfile 'Program/PresentMon-2.6.0-x64.exe'
$presentMonProcess = $null
$usePresentMon = -not $NoPresentMon
if ($usePresentMon) {
    if (-not (Test-Path -LiteralPath $presentMon -PathType Leaf)) { Write-Warning "PresentMon was not found at $presentMon; frames will be timed from inside only."; $usePresentMon = $false }
    # ETW tracing needs membership of Performance Log Users, or elevation. Membership is what this account has.
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if ($usePresentMon -and -not $principal.IsInRole([Security.Principal.SecurityIdentifier]::new('S-1-5-32-559'))) {
        Write-Warning 'This account is not in Performance Log Users, so PresentMon cannot trace; frames will be timed from inside only.'
        $usePresentMon = $false
    }
}

$watch = Start-DisruptionWatch -AllowedMonitor $Display -WatchNames @('Godot_v4.7.2-stable_mono_win64', 'WerFault', 'vsjitdebugger')
$application = $null
try {
    if ($usePresentMon) {
        $csv = Join-Path $folder 'PresentMon.csv'
        # Without elevation PresentMon cannot see a process start, so --terminate_on_proc_exit would never
        # fire; it runs for a fixed time instead and is stopped once the application has exited.
        $arguments = "--process_name Godot_v4.7.2-stable_mono_win64.exe --output_file `"$csv`" --timed 120 --terminate_after_timed --no_console_stats --session_name GodotExperimentMeasure --stop_existing_session"
        $presentMonProcess = Start-Process -FilePath $presentMon -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $folder 'PresentMon.log') -RedirectStandardError (Join-Path $folder 'PresentMon.error.log')
        Start-Sleep -Seconds 2
        if ($presentMonProcess.HasExited) { throw "PresentMon exited at once with code $($presentMonProcess.ExitCode). See PresentMon.error.log." }
    }

    $applicationArguments = @('--measure', '--frames', "$Frames", '--columns', "$Columns", '--rows', "$Rows")
    $application = & (Join-Path $PSScriptRoot 'StartForAgent.ps1') -Display $Display -LogFolder $folder -RenderingDriver $RenderingDriver -ApplicationArguments $applicationArguments
    Register-WatchedProcess -Watch $watch -ProcessId $application.Id
    if (-not $application.WaitForExit(300000)) { $application.Kill(); throw 'The measurement run did not exit within 300 s.' }
    if ($application.ExitCode -ne 0) { throw "The measurement run failed with exit code $($application.ExitCode). See Godot.log." }
}
finally {
    if ($application -and -not $application.HasExited) { $application.Kill() }
    if ($presentMonProcess -and -not $presentMonProcess.HasExited) {
        # PresentMon flushes its CSV on exit; a moment for the last frames to be written, then it is stopped.
        Start-Sleep -Seconds 2
        Stop-Process -Id $presentMonProcess.Id -Force -ErrorAction SilentlyContinue
        $null = $presentMonProcess.WaitForExit(5000)
    }
    $guard = Stop-DisruptionWatch -Watch $watch
}

$lines = Get-MeasurementSummary -Folder $folder -WithPresentMon:$usePresentMon
$lines += ''
$lines += if ($guard.Passed) { "Did not disturb the machine: $($guard.Observations.Count) window events over $($guard.Seconds) s, none on a user display and none taking focus." } else { "DISTURBED THE MACHINE: $($guard.Violations -join ' | ')" }
$lines | Set-Content -LiteralPath (Join-Path $folder 'Measurement.txt')
$lines | Write-Output
Write-Output "Artifacts: $folder"
if (-not $guard.Passed) { throw 'The measurement run disturbed the machine.' }
