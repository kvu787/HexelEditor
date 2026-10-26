param([ValidateSet('Debug', 'Release')][string] $Configuration = 'Release')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Scripts/Common.ps1')
Invoke-DotNet build (Join-Path $RepositoryRoot 'HexelEditor.slnx') --configuration $Configuration --nologo --disable-build-servers '-m:1'
New-Item -ItemType Directory -Path (Join-Path $RepositoryRoot 'LogOutput') -Force | Out-Null
Write-Output "Ready: $(Get-EditorExecutable -Configuration $Configuration)"
