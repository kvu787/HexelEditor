Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Scripts/Common.ps1')

$logFolder = New-LogFolder -Prefix 'Build_'
Write-Output "Godot $(Assert-GodotVersion)"

# The import pass creates the .godot folder, the resource cache and the script uid files, all without a
# window: --headless uses no display server at all. It runs the editor, which is why it needs --editor.
$importLog = Join-Path $logFolder 'Import.log'
& (Get-GodotExecutable -Console) --headless --editor --path $ApplicationRoot --import --log-file $importLog
if ($LASTEXITCODE -ne 0) { throw "Godot's import pass failed with exit code $LASTEXITCODE. See $importLog." }
if (Select-String -LiteralPath $importLog -Pattern '^(?:SCRIPT )?ERROR:' -Quiet) { throw "Godot reported errors during import. See $importLog." }

Invoke-DotNet build (Join-Path $ApplicationRoot 'GodotAgentDevExperiment.slnx') --configuration Debug --nologo --disable-build-servers '-m:1' '-p:NuGetAudit=false'
# The hook that lets an agent's run put the engine's window behind every window of the user's.
& (Join-Path $ExperimentRoot 'BehindHook/Build.ps1') -Output (Join-Path $ExperimentRoot 'Artifacts/BehindHook/BehindHook.dll') | Where-Object { $_ -like 'Ready:*' }
Write-Output "Ready: $(Get-ApplicationAssembly)"
