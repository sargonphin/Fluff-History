[CmdletBinding()]
param([string]$ArtifactDirectory = $PSScriptRoot, [switch]$RemoveDevelopmentCertificate)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$shellCleanup = Join-Path $ArtifactDirectory 'Uninstall-ShellIntegration.ps1'
if (Test-Path -LiteralPath $shellCleanup) { & $shellCleanup }
Get-AppxPackage -Name 'Sargonphin.FluffHistory.DeveloperPreview' | ForEach-Object { Remove-AppxPackage -Package $_.PackageFullName }
if ($RemoveDevelopmentCertificate) {
    $info = Get-Content -LiteralPath (Join-Path $ArtifactDirectory 'build-info.json') -Raw | ConvertFrom-Json
    if ($info.packageIdentity -ne 'Sargonphin.FluffHistory.DeveloperPreview' -or $info.certificateThumbprint -notmatch '^[0-9A-Fa-f]{40}$') { throw 'Invalid exact certificate metadata.' }
    $path = 'Cert:\LocalMachine\TrustedPeople\' + $info.certificateThumbprint
    if (Test-Path -LiteralPath $path) {
        $certificate = Get-Item -LiteralPath $path
        if ($certificate.Subject -ne 'CN=Fluff-History Developer Preview' -or $certificate.HasPrivateKey) { throw 'Refusing to remove unexpected certificate.' }
        Remove-Item -LiteralPath $path
    }
}
Write-Output 'Developer Preview removed for this user. Repositories and source folders were not deleted.'
