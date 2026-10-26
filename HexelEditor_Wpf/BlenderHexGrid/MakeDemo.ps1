#Requires -Version 7
<#
.SYNOPSIS
    Builds a hex grid with GenerateHexGridSingleMesh.py and saves it as a .blend.

.DESCRIPTION
    The default is the 2000 x 1000 grid: two million cells, eight million triangles, one object.
    Only the single-mesh generator is offered, because two million separate objects weigh several
    gigabytes on disk and open slowly enough to be unpleasant.

    The result goes under Output, which is gitignored: the file is far too large to belong in the
    repository, and it can be rebuilt from the script in seconds.

.PARAMETER Width
    Cells across.

.PARAMETER Height
    Cells down.

.PARAMETER Colors
    Palette size. 3 is the smallest that can work.

.PARAMETER OutputPath
    Where to write the .blend. Defaults to Output\HexGrid<Width>x<Height>.blend.

.PARAMETER NoCompress
    Save uncompressed. Faster to write and to open, several times larger on disk.

.PARAMETER BlenderPath
    Blender 4.5 or newer. Found automatically when it sits in the usual places.
#>
[CmdletBinding()]
param(
    [int] $Width = 2000,
    [int] $Height = 1000,
    [int] $Colors = 7,
    [string] $OutputPath,
    [switch] $NoCompress,
    [string] $BlenderPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptFolder = Split-Path -Parent $PSCommandPath
. (Join-Path $scriptFolder 'BlenderTools.ps1')

$blender = Find-Blender -Preferred $BlenderPath
$logFolderPath = New-LogFolder -AppFolder $scriptFolder
if (-not $OutputPath) {
    $OutputPath = Join-Path $scriptFolder ('Output\HexGrid{0}x{1}.blend' -f $Width, $Height)
}

Write-Host "Blender: $blender"
Write-Host "Logs:    $logFolderPath"
Write-Host ("Building {0} x {1} = {2:N0} cells" -f $Width, $Height, ($Width * [long]$Height))
Write-Host ''

$env:HEXGRID_WIDTH = $Width
$env:HEXGRID_HEIGHT = $Height
$env:HEXGRID_COLORS = $Colors
$env:HEXGRID_OUTPUT = $OutputPath
$env:HEXGRID_COMPRESS = if ($NoCompress) { '0' } else { '1' }
try {
    $log = Join-Path $logFolderPath 'MakeDemo.log'
    & $blender --background --factory-startup --python (Join-Path $scriptFolder 'MakeDemoFile.py') 2>&1 |
        Tee-Object -FilePath $log |
        Where-Object { $_ -match 'Hex grid|Mesh |Done:|DEMO_FILE|Error' }
    $exitCode = $LASTEXITCODE
} finally {
    Remove-Item Env:\HEXGRID_WIDTH, Env:\HEXGRID_HEIGHT, Env:\HEXGRID_COLORS,
                Env:\HEXGRID_OUTPUT, Env:\HEXGRID_COMPRESS -ErrorAction Ignore
}

Write-Host ''
if ($exitCode -ne 0 -or -not (Test-Path -LiteralPath $OutputPath)) {
    Write-Host "Failed. Full output: $log"
    exit 1
}
Write-Host ("Wrote {0} ({1:N1} MB)" -f $OutputPath, ((Get-Item -LiteralPath $OutputPath).Length / 1MB))

# Saving and re-opening is where a mesh filled through foreach_set would show any damage, so the
# file is read back and checked rather than trusted.
Write-Host ''
Write-Host 'Verifying the saved file by opening it again.'
$env:HEXGRID_WIDTH = $Width
$env:HEXGRID_HEIGHT = $Height
$env:HEXGRID_COLORS = $Colors
$env:HEXGRID_OUTPUT = $OutputPath
try {
    $verifyLog = Join-Path $logFolderPath 'VerifyDemo.log'
    & $blender --background --factory-startup --python (Join-Path $scriptFolder 'VerifyDemoFile.py') 2>&1 |
        Tee-Object -FilePath $verifyLog |
        Where-Object { $_ -match 'PASS|FAIL|Opened|DEMO_VERIFY' }
    $verifyExit = $LASTEXITCODE
} finally {
    Remove-Item Env:\HEXGRID_WIDTH, Env:\HEXGRID_HEIGHT, Env:\HEXGRID_COLORS,
                Env:\HEXGRID_OUTPUT -ErrorAction Ignore
}

Write-Host ''
if ($verifyExit -ne 0) {
    Write-Host "The saved file did not verify. Full output: $verifyLog"
    exit 1
}
Write-Host 'The saved file verified.'
exit 0
