[CmdletBinding()]
param([ValidateSet('Development','UnsignedPublic')][string]$SigningMode = 'Development')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($SigningMode -eq 'UnsignedPublic') {
    [pscustomobject]@{Certificate=$null;PeSigning=$false;Mode=$SigningMode;PeSignerThumbprint=$null}
} else {
    $certificate = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq 'CN=Fluff-History Developer Preview' -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(7) } | Sort-Object NotAfter -Descending | Select-Object -First 1
    if (!$certificate) { $certificate = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=Fluff-History Developer Preview' -CertStoreLocation Cert:\CurrentUser\My -HashAlgorithm SHA256 -KeyAlgorithm RSA -KeyLength 2048 -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddYears(1) }
    [pscustomobject]@{Certificate=$certificate;PeSigning=$true;Mode=$SigningMode;PeSignerThumbprint=$certificate.Thumbprint}
}