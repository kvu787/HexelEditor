param(
    [ValidateSet('Debug', 'Release')][string] $Configuration = 'Release',
    [string] $Project,
    [string] $Display,
    # Returns the started process instead of describing the launch, so that a validation run can follow and
    # capture exactly the editor it started rather than whichever one happens to be open.
    [switch] $PassThru,
    # Further arguments for the editor, passed through as they are. For the switches a run that studies the
    # editor wants, such as --log-frames, which writes how many frames a second the window really draws.
    [string[]] $EditorArguments = @()
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Scripts/Common.ps1')

$executable = Get-EditorExecutable -Configuration $Configuration
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "The $Configuration application has not been built. Run Build.cmd first (or Build.ps1 -Configuration $Configuration)."
}
$runFolder = New-EditorRunCopy -BuildFolder (Split-Path -Parent $executable)
$startParameters = @{ FilePath = Join-Path $runFolder (Split-Path -Leaf $executable); WorkingDirectory = $RepositoryRoot }
$arguments = @()
if ($Display) {
    # Validation fills the named display and takes no focus, so the editor can be looked at while someone
    # else is working on this machine. Without -Display the editor opens normally, in front, as it should.
    . (Join-Path $PSScriptRoot 'Scripts/Windowing.ps1')
    $bounds = Get-DisplayBounds -Monitor $Display
    $arguments += '--show-without-focus'
    $arguments += "$($bounds.X),$($bounds.Y),$($bounds.Width),$($bounds.Height)"
}
if ($Project) {
    $projectFile = (Resolve-Path -LiteralPath $Project).Path
    $arguments += '"' + $projectFile + '"'
}
$arguments += $EditorArguments
if ($arguments.Count -gt 0) { $startParameters.ArgumentList = $arguments -join ' ' }
$process = Start-Process @startParameters -PassThru
if ($PassThru) { return $process }
if ($Display) { Write-Output "Started on $Display ($($bounds.Width) x $($bounds.Height) at $($bounds.X),$($bounds.Y)) without taking focus." }
Write-Output "Started: $($startParameters.FilePath)"
