#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $IsWindows -or -not [Environment]::Is64BitOperatingSystem) {
    throw 'Hexel Editor development requires Windows 11 x64.'
}
if ([Environment]::OSVersion.Version.Build -lt 22000) {
    throw 'Hexel Editor requires Windows 11 (build 22000 or later).'
}
$RepositoryRoot = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $RepositoryRoot 'Artifacts/DotNetHome'
$env:NUGET_PACKAGES = Join-Path $RepositoryRoot 'Artifacts/Packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $RepositoryRoot 'Artifacts/NuGetHttpCache'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'

function Invoke-DotNet {
    param([Parameter(ValueFromRemainingArguments)][string[]] $Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE." }
}

function Get-EditorExecutable {
    param([ValidateSet('Debug', 'Release')][string] $Configuration = 'Release')
    Join-Path $RepositoryRoot "Source/HexelEditor.Desktop/bin/$Configuration/net10.0-windows10.0.26100.0/win-x64/HexelEditor.exe"
}

function Get-RunningEditorPath {
    Get-Process -Name 'HexelEditor' -ErrorAction SilentlyContinue |
        ForEach-Object { try { $_.Path } catch { $null } } |
        Where-Object { $_ }
}

function New-EditorRunCopy {
    param([Parameter(Mandatory)][string] $BuildFolder)
    # The editor runs from a copy so that an open editor never holds a file lock on the
    # build output, which would make the next build fail. The copy stays inside the
    # repository so the application still finds the repository root for its session log.
    $runRoot = Join-Path $RepositoryRoot 'Artifacts/Run'
    New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
    $running = @(Get-RunningEditorPath)
    foreach ($folder in Get-ChildItem -LiteralPath $runRoot -Directory) {
        $path = $folder.FullName
        # Deleting a copy an open editor is running from would break that editor.
        if ($running | Where-Object { $_.StartsWith($path + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) }) { continue }
        try { Remove-Item -LiteralPath $path -Recurse -Force } catch { }
    }
    for ($attempt = 1; ; $attempt++) {
        do {
            $runFolder = Join-Path $runRoot (Get-Date -Format 'yyyy-MM-dd_HH-mm-ss')
            if (-not (Test-Path -LiteralPath $runFolder)) { break }
            Start-Sleep -Milliseconds 50
        } while ($true)
        try {
            Copy-Item -LiteralPath $BuildFolder -Destination $runFolder -Recurse
            break
        }
        catch {
            # A build writing to the output folder holds its files briefly. Start over
            # rather than launching the editor from a half-copied folder.
            Remove-Item -LiteralPath $runFolder -Recurse -Force -ErrorAction SilentlyContinue
            if ($attempt -ge 3) { throw }
            Start-Sleep -Seconds 2
        }
    }
    $runFolder
}
