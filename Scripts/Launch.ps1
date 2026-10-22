param([switch] $NoBuild, [string] $Project)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
if (-not $NoBuild) { Invoke-DotNet build (Join-Path $RepositoryRoot 'Source/HexelEditor.Desktop/HexelEditor.Desktop.csproj') --configuration Release --nologo --disable-build-servers '-m:1' }
$executable = Join-Path $RepositoryRoot 'Source/HexelEditor.Desktop/bin/Release/net10.0-windows10.0.26100.0/win-x64/HexelEditor.exe'
if (-not (Test-Path -LiteralPath $executable)) { throw 'The application has not been built. Run Scripts/Build.ps1 first.' }
if ($Project) {
    $projectFile = (Resolve-Path -LiteralPath $Project).Path
    Start-Process -FilePath $executable -ArgumentList ('"' + $projectFile + '"')
} else {
    Start-Process -FilePath $executable
}
