Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')

# A standalone release build of the application, exported by the editor executable without a window. The
# export builds the C# project itself in its ExportRelease configuration and lays the engine template, the
# resource pack and the assembly out next to each other.
$logFolder = New-LogFolder -Prefix 'Export_'
$template = Join-Path (Get-GodotDirectory) 'editor_data/export_templates/4.7.2.stable.mono/windows_release_x86_64.exe'
if (-not (Test-Path -LiteralPath $template -PathType Leaf)) { throw "The Windows export template is missing: $template" }
$output = Join-Path $ExperimentRoot 'Artifacts/Release'
New-Item -ItemType Directory -Path $output -Force | Out-Null

$exportLog = Join-Path $logFolder 'Export.log'
& (Get-GodotExecutable -Console) --headless --path $ApplicationRoot --export-release 'Windows Desktop' --log-file $exportLog
if ($LASTEXITCODE -ne 0) { throw "The export failed with exit code $LASTEXITCODE. See $exportLog." }
if (Select-String -LiteralPath $exportLog -Pattern '^(?:SCRIPT )?ERROR:' -Quiet) { throw "Godot reported errors during the export. See $exportLog." }
$executable = Join-Path $output 'GodotAgentDevExperiment.exe'
foreach ($file in @($executable, (Join-Path $output 'GodotAgentDevExperiment.pck'))) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "The export is incomplete: $file is missing. See $exportLog." }
}
Write-Output "Ready: $executable"
