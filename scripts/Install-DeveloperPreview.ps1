[CmdletBinding()]
param([string]$ArtifactDirectory = $PSScriptRoot, [switch]$TrustCertificate)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$info = Get-Content -LiteralPath (Join-Path $ArtifactDirectory 'build-info.json') -Raw | ConvertFrom-Json
if ($info.packageIdentity -ne 'Sargonphin.FluffHistory.DeveloperPreview') { throw 'Unexpected package identity.' }
$cerPath = Join-Path $ArtifactDirectory 'Fluff-History-Developer-Preview.cer'
$certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($cerPath)
if ($certificate.Thumbprint -ne $info.certificateThumbprint -or $certificate.Subject -ne 'CN=Fluff-History Developer Preview' -or $certificate.HasPrivateKey) {
    throw 'Public development certificate does not match build metadata.'
}
$trusted = Test-Path -LiteralPath ("Cert:\LocalMachine\TrustedPeople\" + $certificate.Thumbprint)
if (!$trusted) {
    if (!$TrustCertificate) { throw 'Review this test certificate, then rerun with -TrustCertificate from elevated PowerShell to trust its exact public key.' }
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Certificate trust requires elevated PowerShell. Rerun with elevation and -TrustCertificate; package install is per-user.' }
    Import-Certificate -FilePath $cerPath -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
}
$packages = @(Get-ChildItem -LiteralPath $ArtifactDirectory -Filter '*.msix' -File)
if ($packages.Count -ne 1) { throw 'Expected exactly one Developer Preview MSIX in artifact folder.' }
if ((Get-FileHash -LiteralPath $packages[0].FullName -Algorithm SHA256).Hash -ne $info.msixSha256) { throw 'Package hash differs from build metadata.' }
try { Add-AppxPackage -Path $packages[0].FullName -ErrorAction Stop }
catch { throw 'Developer Preview install/update failed. Close the app; check certificate trust and version. For a downgrade, uninstall this preview first. Windows signature validation remains enabled.' }
Get-AppxPackage -Name 'Sargonphin.FluffHistory.DeveloperPreview' | Select-Object Name, Version, PackageFullName
