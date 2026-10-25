#Requires -Version 7.0
param(
    # Runs the hidden rendering host on this desktop instead of one of its own. Only safe when nobody is
    # using the machine, and it is never what an agent should choose.
    #
    # The host window is placed offscreen and never shown, which once looked like enough. Watching a run
    # proved it is not: on this desktop the host takes the foreground, the menu check captures the real
    # mouse, and the submenu's own popup window becomes visible at the top left of the primary display. All
    # three reach whoever is using the machine. A desktop of its own has no foreground to take, no shared
    # pointer to capture, and no display to appear on, so that is where validation belongs.
    #
    # What it costs: a desktop that is not the input desktop has no foreground window, and mouse capture
    # needs one, so a WPF menu will not stay open through its menu item and the submenu is measured through
    # the popup directly instead. That is the one check this trade weakens, and it is the check whose full
    # fidelity caused the disruption.
    [switch] $SameDesktop
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
. (Join-Path $PSScriptRoot 'IsolatedDesktop.ps1')
. (Join-Path $PSScriptRoot 'DisruptionGuard.ps1')

& (Join-Path $RepositoryRoot 'Build.ps1')
& (Join-Path $PSScriptRoot 'Test.ps1')

do {
    $verificationPath = Join-Path $RepositoryRoot ('LogOutput/' + (Get-Date -Format 'yyyy-MM-dd_HH-mm-ss'))
    if (-not (Test-Path -LiteralPath $verificationPath)) { break }
    Start-Sleep -Milliseconds 50
} while ($true)
New-Item -ItemType Directory -Path $verificationPath | Out-Null

$executable = Get-EditorExecutable
$locked = Test-SessionLocked
# Built outside the array literal on purpose. Inside one, PowerShell reads the + of a string concatenation as
# a unary operator and makes separate elements of the pieces, which sends the path through padded with spaces.
$quotedPath = '"{0}"' -f $verificationPath
$arguments = @('--verify-output', $quotedPath)

if ($SameDesktop) {
    # WindowStyle Hidden keeps a console from appearing even for an instant. The editor's own window is
    # offscreen, but on this desktop it can still take the foreground and open a visible popup, so this is
    # for a machine nobody is using.
    $started = Start-Process -FilePath $executable -ArgumentList ($arguments -join ' ') -WindowStyle Hidden -PassThru
    if (-not $started.WaitForExit(120000)) { $started.Kill(); throw 'Desktop verification timed out.' }
    $exitCode = $started.ExitCode
}
else {
    $desktop = New-IsolatedDesktop -Name 'HexelVerification'
    try {
        $process = Start-ProcessOnDesktop -Desktop $desktop -FilePath $executable -ArgumentList $arguments -WorkingDirectory $RepositoryRoot
        $exitCode = Wait-ProcessOnDesktop -Process $process -TimeoutMilliseconds 120000
    }
    finally { Remove-IsolatedDesktop -Desktop $desktop }
}

$sessionReport = Join-Path $verificationPath 'Session.log'
if ($exitCode -ne 0) {
    if (Test-Path -LiteralPath $sessionReport) { Get-Content -LiteralPath $sessionReport }
    throw "Desktop verification failed with exit code $exitCode."
}

$report = Join-Path $verificationPath 'Verification.txt'
if (-not (Test-Path -LiteralPath $report)) { throw 'Desktop verification did not produce its report.' }
$reportText = Get-Content -LiteralPath $report -Raw
Get-Content -LiteralPath $report

# Every check has to have reported. Without this an application that exited early after passing the first
# few checks would look the same as one that passed them all.
$expected = @(
    'WPF commands and saved-state indicator: passed',
    'Project save/load: passed',
    'Artwork rendering:',
    'Canvas surface:',
    'Canvas frame:',
    'Menu bar and dialogs: passed',
    'Palette grid:',
    'Render scales:',
    'Pointer input:',
    'Keyboard input:'
)
$missing = @($expected | Where-Object { $reportText -notlike "*$_*" })
if ($missing.Count -gt 0) { throw "The verification report is missing: $($missing -join '; ')" }

# A menu needs mouse capture, which needs a foreground window, which only the input desktop has. So the
# weaker path is expected exactly when this run had no input desktop, and is a defect otherwise.
$usedFallback = $reportText -like '*submenu opened directly*'
$withoutInputDesktop = (-not $SameDesktop) -or (-not (Test-HasInputDesktop))
if ($usedFallback -and -not $withoutInputDesktop) {
    throw 'The File menu did not open through its menu item while running on the displayed desktop, which it should.'
}
if (-not $usedFallback -and $withoutInputDesktop) {
    Write-Warning 'The File menu opened through its menu item without an input desktop, which was not expected. The check is stronger than assumed, not weaker.'
}

$sessionLog = Get-Content -LiteralPath $sessionReport -Raw
if ($sessionLog -notmatch 'Application started\.' -or $sessionLog -notmatch 'Desktop verification passed\.' -or $sessionLog -notmatch 'Application exited\. Exit code: 0\.') {
    throw 'Desktop verification did not record the complete application session.'
}
Write-Output 'Application session logging: passed'
Write-Output "Session was locked: $locked. Ran on a desktop of its own: $(-not $SameDesktop)."
Write-Output "Verification artifacts: $verificationPath"
