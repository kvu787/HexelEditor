param([ValidateSet('Debug', 'Release')][string] $Configuration = 'Release', [string] $Project)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Scripts/Common.ps1')

$executable = Get-EditorExecutable -Configuration $Configuration
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "The $Configuration application has not been built. Run Build.cmd first (or Build.ps1 -Configuration $Configuration)."
}
$startParameters = @{ FilePath = $executable; WorkingDirectory = $RepositoryRoot }
if ($Project) {
    $projectFile = (Resolve-Path -LiteralPath $Project).Path
    $startParameters.ArgumentList = '"' + $projectFile + '"'
}
Start-Process @startParameters
