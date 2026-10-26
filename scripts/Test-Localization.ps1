[CmdletBinding()]
param([string]$DotNet = 'dotnet', [string]$ArtifactsPath, [switch]$LocalizationCheckpoint)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path $PSScriptRoot -Parent
if (!$ArtifactsPath) { $ArtifactsPath = Join-Path ([IO.Path]::GetTempPath()) 'FluffHistory-Localization-Tool' }
$ArtifactsPath = [IO.Path]::GetFullPath($ArtifactsPath)
$project = Join-Path $repo 'tools\FluffHistory.Localization\FluffHistory.Localization.csproj'
& $DotNet restore $project --locked-mode "-p:ArtifactsPath=$ArtifactsPath"
if ($LASTEXITCODE) { throw 'Localization tool locked restore failed.' }
& $DotNet build $project -c Release --no-restore "-p:ArtifactsPath=$ArtifactsPath"
if ($LASTEXITCODE) { throw 'Localization tool build failed.' }
foreach ($operation in @('guard','test-guards','validate','generate','generate-native','generate-installer','generate-manifests')) {
    if ($LocalizationCheckpoint -and $operation -eq 'validate') {
        Write-Output 'INCOMPLETE LOCALIZATION CHECKPOINT: shipping catalog validation was skipped; this is not a complete language bundle.'
        continue
    }
    $arguments = @('run','--no-build','--no-restore','--project',$project,'-c','Release',"-p:ArtifactsPath=$ArtifactsPath",'--',$operation,$repo)
    if ($operation -like 'generate*') { $arguments += '--check' }
    & $DotNet @arguments
    if ($LASTEXITCODE) { throw "Localization validation failed: $operation" }
}
