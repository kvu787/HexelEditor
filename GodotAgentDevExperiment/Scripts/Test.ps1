Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
# The engine-free core, tested with xUnit's in-process runner. Nothing here starts Godot.
$testProject = Join-Path $ExperimentRoot 'Tests/Experiment.Core.Tests/Experiment.Core.Tests.csproj'
Invoke-DotNet build $testProject --configuration Debug --nologo --disable-build-servers '-m:1' '-p:NuGetAudit=false'
$testRunner = Join-Path $ExperimentRoot 'Tests/Experiment.Core.Tests/bin/Debug/net10.0/Experiment.Core.Tests.exe'
& $testRunner -parallel none -nocolor
if ($LASTEXITCODE -ne 0) { throw "Core tests failed with exit code $LASTEXITCODE." }
