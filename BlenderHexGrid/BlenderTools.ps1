#Requires -Version 7
<#
.SYNOPSIS
    Helpers shared by Test.ps1 and MakeDemo.ps1. Dot-source it; it defines functions only.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Find-Blender {
    <#
        Locates blender.exe, preferring an explicit path. Blender 4.5 or newer is wanted: the
        single-mesh generator relies on polygons.foreach_set('loop_start', ...) being writable.
    #>
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
    Write-Warning 'No spare display found; the window will open on the primary one.'
    return ([System.Windows.Forms.Screen]::PrimaryScreen).Bounds
}

function New-LogFolder {
    param([string] $AppFolder)

    $path = Join-Path $AppFolder "LogOutput\$(Get-Date -Format 'yyyy-MM-dd_HH-mm-ss')"
    New-Item -ItemType 'Directory' -Path $path | Out-Null
    return $path
}
