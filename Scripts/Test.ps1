param([ValidateSet('Debug', 'Release')][string] $Configuration = 'Release')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
$testProject = Join-Path $RepositoryRoot 'Tests/HexelEditor.Tests/HexelEditor.Tests.csproj'
Invoke-DotNet build $testProject --configuration $Configuration --nologo --disable-build-servers '-m:1'
# xUnit's in-process runner also works in restricted environments without VSTest's IPC service.
$testRunner = Join-Path $RepositoryRoot "Tests/HexelEditor.Tests/bin/$Configuration/net10.0/HexelEditor.Tests.exe"
$testTemporaryPath = Join-Path $RepositoryRoot 'Artifacts/TestTemporary'
New-Item -ItemType Directory -Path $testTemporaryPath -Force | Out-Null
$previousTemporaryPath = $env:TEMP
$previousTemporaryDirectory = $env:TMP
try {
    $env:TEMP = $testTemporaryPath
    $env:TMP = $testTemporaryPath
    & $testRunner -parallel none -nocolor
    if ($LASTEXITCODE -ne 0) { throw "Editor tests failed with exit code $LASTEXITCODE." }
} finally {
    $env:TEMP = $previousTemporaryPath
    $env:TMP = $previousTemporaryDirectory
}
