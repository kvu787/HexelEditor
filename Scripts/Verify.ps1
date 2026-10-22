Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
& (Join-Path $RepositoryRoot 'Build.ps1')
& (Join-Path $PSScriptRoot 'Test.ps1')
do {
    $verificationPath = Join-Path $RepositoryRoot ('LogOutput/' + (Get-Date -Format 'yyyy-MM-dd_HH-mm-ss'))
    if (-not (Test-Path -LiteralPath $verificationPath)) { break }
    Start-Sleep -Milliseconds 50
} while ($true)
New-Item -ItemType Directory -Path $verificationPath | Out-Null
$executable = Get-EditorExecutable
$errorReport = Join-Path $verificationPath 'StandardError.txt'
$process = Start-Process -FilePath $executable -ArgumentList ('--verify-output "' + $verificationPath + '"') -WindowStyle Hidden -RedirectStandardError $errorReport -RedirectStandardOutput (Join-Path $verificationPath 'StandardOutput.txt') -PassThru
if (-not $process.WaitForExit(60000)) { $process.Kill(); throw 'Desktop verification timed out.' }
if ($process.ExitCode -ne 0) {
    Get-Content -LiteralPath $errorReport
    $sessionReport = Join-Path $verificationPath 'Session.log'
    if (Test-Path -LiteralPath $sessionReport) { Get-Content -LiteralPath $sessionReport }
    throw "Desktop verification failed with exit code $($process.ExitCode)."
}
$report = Join-Path $verificationPath 'Verification.txt'
if (-not (Test-Path -LiteralPath $report)) { throw 'Desktop verification did not produce its report.' }
Get-Content -LiteralPath $report
$sessionLog = Get-Content -LiteralPath (Join-Path $verificationPath 'Session.log') -Raw
if ($sessionLog -notmatch 'Application started\.' -or $sessionLog -notmatch 'Desktop verification passed\.' -or $sessionLog -notmatch 'Application exited\. Exit code: 0\.') {
    throw 'Desktop verification did not record the complete application session.'
}
Write-Output 'Application session logging: passed'
Write-Output "Verification artifacts: $verificationPath"
