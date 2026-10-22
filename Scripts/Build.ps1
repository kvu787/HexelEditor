param([ValidateSet('Debug', 'Release')][string] $Configuration = 'Release')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
Invoke-DotNet build (Join-Path $RepositoryRoot 'HexelEditor.slnx') --configuration $Configuration --nologo --disable-build-servers '-m:1'
