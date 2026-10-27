#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $IsWindows -or -not [Environment]::Is64BitOperatingSystem) {
    throw 'This experiment requires Windows 11 x64.'
}

$ExperimentRoot = Split-Path -Parent $PSScriptRoot
$ApplicationRoot = Join-Path $ExperimentRoot 'Application'
# The desktop, windowing and disruption-guard helpers are engine-independent, so the WPF editor's are shared
# rather than copied. They prove the same thing about any process: that a run disturbed nobody.
$SharedScripts = Join-Path (Split-Path -Parent $ExperimentRoot) 'HexelEditor_Wpf/Scripts'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'

function Get-GodotDirectory { Join-Path $env:UserProfile 'Program/Godot_v4.7.2-stable_mono_win64' }

# The console build attaches to the console it was started from and is what a script reads output from. The
# other one is a windowed application, for runs where no console may appear anywhere.
function Get-GodotExecutable {
    param([switch] $Console)
    $name = if ($Console) { 'Godot_v4.7.2-stable_mono_win64_console.exe' } else { 'Godot_v4.7.2-stable_mono_win64.exe' }
    $path = Join-Path (Get-GodotDirectory) $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Godot 4.7.2 .NET was not found at $path." }
    $path
}

function Assert-GodotVersion {
    $version = & (Get-GodotExecutable -Console) --version
    if ($LASTEXITCODE -ne 0 -or $version -notlike '4.7.2.stable.mono.*') { throw "Expected Godot 4.7.2 .NET; found '$version'." }
    $version
}

function Invoke-DotNet {
    param([Parameter(ValueFromRemainingArguments)][string[]] $Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE." }
}

function Get-ApplicationAssembly {
    Join-Path $ApplicationRoot '.godot/mono/temp/bin/Debug/GodotAgentDevExperiment.dll'
}

# The hook that puts the engine's windows behind everything as they are created, built by Build.ps1 and by
# this on demand. See BehindHook/BehindHook.c.
function Get-BehindHookLibrary {
    $library = Join-Path $ExperimentRoot 'Artifacts/BehindHook/BehindHook.dll'
    if (-not (Test-Path -LiteralPath $library -PathType Leaf)) {
        & (Join-Path $ExperimentRoot 'BehindHook/Build.ps1') -Output $library | Out-Null
    }
    $library
}

# One folder per run under LogOutput, named by the time it started, never reused.
function New-LogFolder {
    param([string] $Prefix = '')
    do {
        $folder = Join-Path $ExperimentRoot ('LogOutput/' + $Prefix + (Get-Date -Format 'yyyy-MM-dd_HH-mm-ss'))
        if (-not (Test-Path -LiteralPath $folder)) { break }
        Start-Sleep -Milliseconds 50
    } while ($true)
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    $folder
}

# The engine's own command line for one run of the application, with the application's arguments after
# the "--" separator. --log-file keeps the engine's output when the windowed executable has no console.
function Get-RunArguments {
    param(
        [Parameter(Mandatory)][string] $LogFolder,
        [switch] $Headless,
        [string] $RenderingDriver = '',
        [string] $Position = '',
        [string] $Resolution = '',
        [string[]] $ApplicationArguments = @()
    )
    $arguments = @('--path', ('"{0}"' -f $ApplicationRoot), '--log-file', ('"{0}"' -f (Join-Path $LogFolder 'Godot.log')))
    if ($Headless) { $arguments += '--headless' }
    if ($RenderingDriver) {
        $arguments += @('--rendering-driver', $RenderingDriver)
        if ($RenderingDriver -eq 'opengl3') { $arguments += @('--rendering-method', 'gl_compatibility') }
    }
    if ($Position) { $arguments += @('--position', $Position) }
    if ($Resolution) { $arguments += @('--resolution', $Resolution) }
    $arguments += @('--', '--log-folder', ('"{0}"' -f $LogFolder)) + $ApplicationArguments
    $arguments
}

# Settings a run needs that the engine applies before any script runs, so they cannot come from code. The
# file is read once at startup and removed again by Remove-SettingsOverride once the window exists.
function Write-SettingsOverride {
    param([Parameter(Mandatory)][string[]] $Lines)
    $path = Join-Path $ApplicationRoot 'override.cfg'
    Set-Content -LiteralPath $path -Value $Lines
    $path
}

function Remove-SettingsOverride {
    Remove-Item -LiteralPath (Join-Path $ApplicationRoot 'override.cfg') -Force -ErrorAction SilentlyContinue
}

if (-not ('WindowStyles' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class WindowStyles
{
    [DllImport("user32.dll")] public static extern IntPtr GetWindowLongPtrW(IntPtr handle, int index);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr handle, uint command);
}
'@
}

# What the window would mean to the taskbar and the foreground: the styles the engine gave it and whether it
# has an owner. A visible top-level window with WS_EX_APPWINDOW gets a taskbar button; WS_EX_NOACTIVATE keeps
# it from being activated by a click, and WS_EX_TOPMOST keeps it above other windows.
function Get-WindowStyles {
    param([Parameter(Mandatory)][IntPtr] $Handle)
    $style = [WindowStyles]::GetWindowLongPtrW($Handle, -16).ToInt64()
    $extended = [WindowStyles]::GetWindowLongPtrW($Handle, -20).ToInt64()
    $owner = [WindowStyles]::GetWindow($Handle, 4)
    $flags = @()
    if ($extended -band 0x00040000) { $flags += 'WS_EX_APPWINDOW' }
    if ($extended -band 0x00000080) { $flags += 'WS_EX_TOOLWINDOW' }
    if ($extended -band 0x00000008) { $flags += 'WS_EX_TOPMOST' }
    if ($extended -band 0x08000000) { $flags += 'WS_EX_NOACTIVATE' }
    if ($style -band 0x80000000) { $flags += 'WS_POPUP' }
    if ($style -band 0x00C00000) { $flags += 'WS_CAPTION' }
    "styles $($flags -join '|'), owner $(if ($owner -eq [IntPtr]::Zero) { 'none' } else { 'yes' })"
}

# Whether the taskbar shows a button for an application, read through UI Automation, which sees the
# taskbar's buttons the way a screen reader does and touches nothing. Buttons are named after the application
# ("Godot Engine - 1 running window"), not after the window.
function Test-TaskbarButton {
    param([Parameter(Mandatory)][string] $Title)
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $buttonCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    $names = @()
    # The primary display's taskbar and the ones on the other displays.
    foreach ($class in @('Shell_TrayWnd', 'Shell_SecondaryTrayWnd')) {
        $trayCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ClassNameProperty, $class)
        foreach ($tray in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $trayCondition)) {
            foreach ($button in $tray.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonCondition)) {
                if ($button.Current.Name -like "*$Title*") { $names += "'$($button.Current.Name)' on $class" }
            }
        }
    }
    if ($names.Count -eq 0) { 'no taskbar button' } else { "taskbar buttons: $($names -join ', ')" }
}
