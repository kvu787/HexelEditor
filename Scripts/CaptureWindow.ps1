#Requires -Version 7.0

# Saves a picture of the running editor without activating it, and fails if the foreground moved while it
# worked. Pair it with Run.ps1 -Display to look at the real interface while someone else uses the machine.

param(
    [Parameter(Mandatory)][string] $OutputPath,
    [string] $ProcessName = 'HexelEditor',
    [int] $ProcessId = 0,
    [int] $TimeoutMilliseconds = 0
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Windowing.ps1')

$guard = Get-ForegroundGuard
$handle = Get-WindowHandle -ProcessName $ProcessName -ProcessId $ProcessId -TimeoutMilliseconds $TimeoutMilliseconds
if ($handle -eq $guard.Handle) {
    Write-Warning "$ProcessName already holds the foreground. Something activated it, which is what this harness exists to avoid."
}

$outputFolder = Split-Path -Parent $OutputPath
if ($outputFolder -and -not (Test-Path -LiteralPath $outputFolder)) {
    New-Item -ItemType Directory -Path $outputFolder -Force | Out-Null
}

$image = Save-WindowImage -Handle $handle -Path $OutputPath
Assert-ForegroundUnchanged -Guard $guard
Write-Output "Captured $($image.Width) x $($image.Height) to $($image.Path); focus stayed on '$($guard.Title)'."
