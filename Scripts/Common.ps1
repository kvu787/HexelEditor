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
