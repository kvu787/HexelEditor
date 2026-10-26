#Requires -Version 7.0

# Saves a picture of the running editor without activating it, and fails if the foreground moved while it
# worked. Pair it with Run.ps1 -Display to look at the real interface while someone else uses the machine.

param(
    [Parameter(Mandatory)][string] $OutputPath,
    [string] $ProcessName = 'HexelEditor',
    [int] $ProcessId = 0,
    # How long to wait for the window to appear and then to draw, together.
    [int] $TimeoutMilliseconds = 0
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Windowing.ps1')

$deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
$guard = Get-ForegroundGuard
$handle = Get-WindowHandle -ProcessName $ProcessName -ProcessId $ProcessId -TimeoutMilliseconds $TimeoutMilliseconds
if ($handle -eq $guard.Handle) {
    Write-Warning "$ProcessName already holds the foreground. Something activated it, which is what this harness exists to avoid."
}

$outputFolder = Split-Path -Parent $OutputPath
if ($outputFolder -and -not (Test-Path -LiteralPath $outputFolder)) {
    New-Item -ItemType Directory -Path $outputFolder -Force | Out-Null
}

# A window that has only just been shown may not have drawn its first frame, and PrintWindow then returns the
# frame with nothing inside it. On a machine busy with something else that gap is long enough to land in: a
# capture taken under a running game held the title bar and a blank client area, and read as a window that
# could not draw. So the picture is taken again until it holds something, within the same deadline, and a
# window that really draws nothing still comes out blank once the deadline passes.
$attempts = 0
do {
    $image = Save-WindowImage -Handle $handle -Path $OutputPath
    $attempts++
    $content = Test-ImageHasContent -Path $OutputPath
    if ($content.HasContent -or [DateTime]::UtcNow -ge $deadline) { break }
    Start-Sleep -Milliseconds 250
} while ($true)
Assert-ForegroundUnchanged -Guard $guard
Write-Output "Captured $($image.Width) x $($image.Height) to $($image.Path) on attempt $attempts; focus stayed on '$($guard.Title)'."
