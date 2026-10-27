[CmdletBinding()]
param([ValidateSet('Development','UnsignedPublic')][string]$SigningMode = 'Development')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($SigningMode -eq 'UnsignedPublic') {
    . "$PSScriptRoot\..\installer\SparseIdentityTrust.ps1"
    $identity = GetSparseIdentityPolicy (Join-Path $PSScriptRoot '..\installer')
    $certificate = Get-Item -LiteralPath ('Cert:\CurrentUser\My\' + $identity.Policy.certificateThumbprint)
    if (!$certificate.HasPrivateKey -or [Convert]::ToBase64String($certificate.RawData) -cne [Convert]::ToBase64String($identity.Certificate.RawData)) {
        throw 'Import the exact sparse-identity signing key into CurrentUser My before building this composition.'
    }
    $identity.Certificate.Dispose()
    [pscustomobject]@{ Certificate=$certificate; PeSigning=$false; PackageName=$identity.Policy.packageName;
        Publisher=$identity.Policy.publisher; PackageFamily=$identity.Policy.packageFamilyName; Mode=$SigningMode }
} else {
    $certificate = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq 'CN=Fluff-History Developer Preview' -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(7) } | Sort-Object NotAfter -Descending | Select-Object -First 1
    if (!$certificate) { $certificate = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=Fluff-History Developer Preview' -CertStoreLocation Cert:\CurrentUser\My -HashAlgorithm SHA256 -KeyAlgorithm RSA -KeyLength 2048 -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddYears(1) }
    [pscustomobject]@{ Certificate=$certificate; PeSigning=$true; PackageName='Sargonphin.FluffHistory.DeveloperPreview';
        Publisher='CN=Fluff-History Developer Preview'; PackageFamily='Sargonphin.FluffHistory.DeveloperPreview_c87380kbqs5fr'; Mode=$SigningMode }
}
