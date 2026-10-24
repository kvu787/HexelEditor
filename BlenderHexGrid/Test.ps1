#Requires -Version 7
<#
.SYNOPSIS
    Runs the hex grid generator's tests, and optionally its scaling measurements, through Blender.

.DESCRIPTION
    The tests execute GenerateHexGrid.py itself with only its settings constants rewritten, so
    what is checked is the file you would paste into Blender's Text Editor.

.PARAMETER Measure
    Also time the generator at increasing grid sizes. This takes several minutes and builds up to
    two million objects, so it is off by default.

.PARAMETER BlenderPath
    Blender 4.5 or newer. Found automatically when it sits in the usual places.
#>
[CmdletBinding()]
param(
    [switch] $Measure,
    [string] $BlenderPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptFolder = Split-Path -Parent $PSCommandPath

function Find-Blender {
    param([string] $Preferred)

    if ($Preferred) {
        if (-not (Test-Path -LiteralPath $Preferred)) { throw "No Blender at '$Preferred'." }
        return $Preferred
    }
    # The surrounding @() has to stay outside the Where-Object: a pipeline that emits one string
    # unrolls to a scalar, and indexing a string hands back its first character rather than a path.
    $candidates = @(
        @(
            (Get-Command 'blender.exe' -ErrorAction Ignore | Select-Object -ExpandProperty Source)
            Get-ChildItem -Path "$env:UserProfile\Program" -Filter 'blender.exe' -Recurse -Depth 2 -ErrorAction Ignore |
                Select-Object -ExpandProperty FullName
            Get-ChildItem -Path "$env:ProgramFiles\Blender Foundation" -Filter 'blender.exe' -Recurse -Depth 2 -ErrorAction Ignore |
                Select-Object -ExpandProperty FullName
        ) | Where-Object { $_ }
    )
    if (-not $candidates) {
        throw 'Could not find blender.exe. Pass -BlenderPath with the full path to it.'
    }
    return $candidates[0]
}

$blender = Find-Blender -Preferred $BlenderPath
$logFolderPath = Join-Path $scriptFolder "LogOutput\$(Get-Date -Format 'yyyy-MM-dd_HH-mm-ss')"
New-Item -ItemType 'Directory' -Path $logFolderPath | Out-Null

Write-Host "Blender: $blender"
Write-Host "Logs:    $logFolderPath"
Write-Host ''

$testLog = Join-Path $logFolderPath 'Tests.log'
& $blender --background --factory-startup --python (Join-Path $scriptFolder 'TestHexGrid.py') 2>&1 |
    Tee-Object -FilePath $testLog |
    Where-Object { $_ -match 'PASS|FAIL|passed,' }
$testExitCode = $LASTEXITCODE

if ($Measure) {
    Write-Host ''
    Write-Host 'Measuring build cost by grid size. This takes several minutes.'
    $measureLog = Join-Path $logFolderPath 'Measurements.log'
    foreach ($size in @(@(200, 100), @(400, 200), @(1000, 500), @(2000, 1000))) {
        $env:HEXGRID_WIDTH = $size[0]
        $env:HEXGRID_HEIGHT = $size[1]
        & $blender --background --factory-startup --python (Join-Path $scriptFolder 'MeasureHexGrid.py') 2>&1 |
            Tee-Object -FilePath $measureLog -Append |
            Where-Object { $_ -match 'HEXGRID_MEASUREMENT' }
    }
    Remove-Item Env:\HEXGRID_WIDTH, Env:\HEXGRID_HEIGHT -ErrorAction Ignore
}

Write-Host ''
if ($testExitCode -ne 0) {
    Write-Host "Tests failed. Full output: $testLog"
} else {
    Write-Host "Tests passed. Full output: $testLog"
}
exit $testExitCode
