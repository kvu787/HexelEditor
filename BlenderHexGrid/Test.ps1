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

.PARAMETER InEditor
    Also run the script the way a person would, through Blender's Text Editor in a real window.
    The window is placed on a display nobody is using and never takes the foreground.

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

function Get-FreeScreenBounds {
    <#
        Finds the display to put a validation window on. WMI gives the monitor names and
        System.Windows.Forms gives the desktop rectangles, but the two lists are in different
        orders, so they are matched on resolution: the named panel is identified by being the
        only one with its pixel size. When that does not resolve, the largest non-primary
        display is the next best guess at somewhere nobody is looking.
    #>
    param([string] $Name)

    Add-Type -AssemblyName System.Windows.Forms
    $screens = [System.Windows.Forms.Screen]::AllScreens
    $monitors = @(Get-CimInstance -Namespace root\wmi -ClassName WmiMonitorID -ErrorAction Ignore |
        ForEach-Object { -join ($_.UserFriendlyName | Where-Object { $_ -gt 0 } | ForEach-Object { [char]$_ }) })

    if ($Name -and ($monitors -contains $Name)) {
        # Resolutions that appear once belong to exactly one monitor, so a panel whose size is
        # unique can be placed with confidence. A duplicated size is ambiguous, so it is skipped.
        $sizes = @{}
        foreach ($screen in $screens) {
            $key = '{0}x{1}' -f $screen.Bounds.Width, $screen.Bounds.Height
            $sizes[$key] = if ($sizes.ContainsKey($key)) { $null } else { $screen }
        }
        $unique = @($sizes.Values | Where-Object { $_ })
        if ($unique.Count -eq 1 -and $monitors.Count -eq $screens.Count) {
            return $unique[0].Bounds
        }
    }
    $other = @($screens | Where-Object { -not $_.Primary } | Sort-Object { $_.Bounds.Width * $_.Bounds.Height })
    if ($other.Count -gt 0) { return $other[-1].Bounds }
    Write-Warning 'No spare display found; the validation window will open on the primary one.'
    return ([System.Windows.Forms.Screen]::PrimaryScreen).Bounds
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

if ($InEditor) {
    Write-Host ''
    Write-Host 'Running the script through Blender''s Text Editor in a real window.'
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
    $env:HEXGRID_SHOT = Join-Path $logFolderPath 'BlenderWindow.png'
    $editorLog = Join-Path $logFolderPath 'InEditor.log'
    # --no-window-focus keeps the window from taking the foreground off whoever is at the machine.
    & $blender --window-geometry $geometry[0] $geometry[1] $geometry[2] $geometry[3] `
        --no-window-focus --factory-startup `
        --python (Join-Path $scriptFolder 'ValidateInEditor.py') 2>&1 |
        Tee-Object -FilePath $editorLog |
        Where-Object { $_ -match 'PASS|FAIL|IN_EDITOR_RESULT' }
    Remove-Item Env:\HEXGRID_SHOT -ErrorAction Ignore
    if (-not (Select-String -Path $editorLog -Pattern 'IN_EDITOR_RESULT PASSED' -Quiet)) {
        $testExitCode = 1
    }
}

Write-Host ''
if ($testExitCode -ne 0) {
    Write-Host "Tests failed. Full output: $testLog"
} else {
    Write-Host "Tests passed. Full output: $testLog"
}
exit $testExitCode
