Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
& (Join-Path $PSScriptRoot 'Build.ps1')
& (Join-Path $PSScriptRoot 'Test.ps1')
$verificationPath = Join-Path $RepositoryRoot ('Artifacts/Verification/' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))
New-Item -ItemType Directory -Path $verificationPath -Force | Out-Null
$executable = Join-Path $RepositoryRoot 'Source/HexelEditor.Desktop/bin/Release/net10.0-windows10.0.26100.0/win-x64/HexelEditor.exe'
$errorReport = Join-Path $verificationPath 'StandardError.txt'
$process = Start-Process -FilePath $executable -ArgumentList ('--verify-output "' + $verificationPath + '"') -WindowStyle Hidden -RedirectStandardError $errorReport -PassThru
if (-not $process.WaitForExit(60000)) { $process.Kill(); throw 'Desktop verification timed out.' }
if ($process.ExitCode -ne 0) {
    Get-Content -LiteralPath $errorReport
    $failureReport = Join-Path $verificationPath 'Failure.txt'
    if (Test-Path -LiteralPath $failureReport) { Get-Content -LiteralPath $failureReport }
    throw "Desktop verification failed with exit code $($process.ExitCode)."
}
$report = Join-Path $verificationPath 'Verification.txt'
if (-not (Test-Path -LiteralPath $report)) { throw 'Desktop verification did not produce its report.' }
Get-Content -LiteralPath $report
Write-Output "Verification artifacts: $verificationPath"
