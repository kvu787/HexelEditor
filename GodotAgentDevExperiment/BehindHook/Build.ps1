param([string] $Output = (Join-Path (Split-Path -Parent $PSScriptRoot) 'Artifacts/BehindHook/BehindHook.dll'))
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# Builds BehindHook.dll with the Visual C++ compiler, found through vswhere. The library is small and plain C,
# and it is built into Artifacts rather than kept in the repository, like every other build output.

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) { throw 'Visual Studio is not installed: vswhere.exe was not found.' }
$installation = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Select-Object -First 1
if (-not $installation) { throw 'No Visual Studio installation with the C++ build tools was found.' }
$environment = Join-Path $installation 'VC/Auxiliary/Build/vcvars64.bat'
if (-not (Test-Path -LiteralPath $environment -PathType Leaf)) { throw "vcvars64.bat was not found under $installation." }

$outputFolder = Split-Path -Parent $Output
New-Item -ItemType Directory -Path $outputFolder -Force | Out-Null
$source = Join-Path $PSScriptRoot 'BehindHook.c'
# cl writes its intermediates into the working directory, which is the output folder; the batch file sets up
# the compiler's environment first.
$command = 'cd /d "{3}" && "{0}" >nul && cl /nologo /O2 /W4 /WX /LD "{1}" /Fe:"{2}" /link user32.lib' -f $environment, $source, $Output, $outputFolder
& cmd.exe /c $command
if ($LASTEXITCODE -ne 0) { throw "The C++ compiler failed with exit code $LASTEXITCODE." }
if (-not (Test-Path -LiteralPath $Output -PathType Leaf)) { throw "BehindHook.dll was not produced at $Output." }
Write-Output "Ready: $Output"
