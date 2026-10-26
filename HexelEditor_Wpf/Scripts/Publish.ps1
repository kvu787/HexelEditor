Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
Invoke-DotNet publish (Join-Path $RepositoryRoot 'Source/HexelEditor.Desktop/HexelEditor.Desktop.csproj') --configuration Release --runtime win-x64 --self-contained true --artifacts-path (Join-Path $RepositoryRoot 'Artifacts/Publish') --output (Join-Path $RepositoryRoot 'Artifacts/Release') --nologo --disable-build-servers '-m:1'
Write-Output "Ready: $(Join-Path $RepositoryRoot 'Artifacts/Release/HexelEditor.exe')"
