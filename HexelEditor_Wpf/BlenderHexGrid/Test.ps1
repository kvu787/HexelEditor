#Requires -Version 7
<#
.SYNOPSIS
    Runs the tests for both hex grid generators, and optionally their measurements and a real
    Blender window, through Blender.

.DESCRIPTION
    The tests execute the generator scripts themselves with only their settings constants
    rewritten, so what is checked is the file you would paste into Blender's Text Editor.

.PARAMETER Measure
    Also time both generators at increasing grid sizes. This takes several minutes and builds up
    to two million cells, so it is off by default.

.PARAMETER InEditor
    Also run each generator the way a person would, through Blender's Text Editor in a real
    window. The window is placed on a display nobody is using and never takes the foreground.

.PARAMETER MonitorName
    Which display -InEditor should put the window on. NE18NZ2 is the panel this machine's user
    never works on. Falls back to the largest non-primary display, then to the primary one.

.PARAMETER BlenderPath
    Blender 4.5 or newer. Found automatically when it sits in the usual places.
#>
[CmdletBinding()]
param(
    [switch] $Measure,
    [switch] $InEditor,
    [string] $MonitorName = 'NE18NZ2',
    [string] $BlenderPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptFolder = Split-Path -Parent $PSCommandPath
. (Join-Path $scriptFolder 'BlenderTools.ps1')

$blender = Find-Blender -Preferred $BlenderPath
$logFolderPath = New-LogFolder -AppFolder $scriptFolder

Write-Host "Blender: $blender"
Write-Host "Logs:    $logFolderPath"

$suites = @(
    @{ Name = 'One object per cell'; Tests = 'TestHexGrid.py'; Script = 'GenerateHexGrid.py'; Single = '0' }
    @{ Name = 'Single mesh';         Tests = 'TestHexGridSingleMesh.py'; Script = 'GenerateHexGridSingleMesh.py'; Single = '1' }
)

$failed = $false
foreach ($suite in $suites) {
    Write-Host ''
    Write-Host ("== {0} ==" -f $suite.Name)
    $log = Join-Path $logFolderPath ("Tests-{0}.log" -f ($suite.Tests -replace '\.py$', ''))
    & $blender --background --factory-startup --python (Join-Path $scriptFolder $suite.Tests) 2>&1 |
        Tee-Object -FilePath $log |
        Where-Object { $_ -match 'PASS|FAIL|passed,' }
    if ($LASTEXITCODE -ne 0) { $failed = $true }
}

if ($Measure) {
    Write-Host ''
    Write-Host '== Build cost by grid size =='
    $measureLog = Join-Path $logFolderPath 'Measurements.log'
    foreach ($suite in $suites) {
        $env:HEXGRID_SCRIPT = Join-Path $scriptFolder $suite.Script
        foreach ($size in @(@(400, 200), @(1000, 500), @(2000, 1000))) {
            $env:HEXGRID_WIDTH = $size[0]
            $env:HEXGRID_HEIGHT = $size[1]
            & $blender --background --factory-startup --python (Join-Path $scriptFolder 'MeasureHexGrid.py') 2>&1 |
                Tee-Object -FilePath $measureLog -Append |
                Where-Object { $_ -match 'HEXGRID_MEASUREMENT' }
        }
    }
    Remove-Item Env:\HEXGRID_SCRIPT, Env:\HEXGRID_WIDTH, Env:\HEXGRID_HEIGHT -ErrorAction Ignore
}

if ($InEditor) {
    Write-Host ''
    Write-Host '== Through a real Blender window =='
    $bounds = Get-FreeScreenBounds -Name $MonitorName
    # Inset so the window sits wholly on that display and cannot spill onto a neighbouring one.
    $margin = 100
    $geometry = @(
        $bounds.X + $margin
        $bounds.Y + $margin
        [Math]::Min(3400, $bounds.Width - 2 * $margin)
        [Math]::Min(2100, $bounds.Height - 2 * $margin)
    )
    Write-Host ("Window at {0},{1} sized {2}x{3}" -f $geometry[0], $geometry[1], $geometry[2], $geometry[3])
    foreach ($suite in $suites) {
        Write-Host ("-- {0}" -f $suite.Name)
        $editorLog = Join-Path $logFolderPath ("InEditor-{0}.log" -f ($suite.Script -replace '\.py$', ''))
        $env:HEXGRID_SCRIPT = Join-Path $scriptFolder $suite.Script
        $env:HEXGRID_SINGLE_MESH = $suite.Single
        $env:HEXGRID_SHOT = Join-Path $logFolderPath ("Window-{0}.png" -f ($suite.Script -replace '\.py$', ''))
        # --no-window-focus keeps the window from taking the foreground off whoever is at the machine.
        & $blender --window-geometry $geometry[0] $geometry[1] $geometry[2] $geometry[3] `
            --no-window-focus --factory-startup `
            --python (Join-Path $scriptFolder 'ValidateInEditor.py') 2>&1 |
            Tee-Object -FilePath $editorLog |
            Where-Object { $_ -match 'PASS|FAIL|IN_EDITOR_RESULT' }
        if (-not (Select-String -Path $editorLog -Pattern 'IN_EDITOR_RESULT PASSED' -Quiet)) {
            $failed = $true
        }
    }
    Remove-Item Env:\HEXGRID_SCRIPT, Env:\HEXGRID_SINGLE_MESH, Env:\HEXGRID_SHOT -ErrorAction Ignore
}

Write-Host ''
if ($failed) {
    Write-Host "Something failed. Full output is under $logFolderPath"
    exit 1
}
Write-Host "All passed. Full output is under $logFolderPath"
exit 0
