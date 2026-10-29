$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
# Unsigned public composition cannot access any signing or trust store.
function Get-Item { throw 'Unsigned build requested a certificate.' }
function Get-ChildItem { throw 'Unsigned build enumerated certificates.' }
function New-SelfSignedCertificate { throw 'Unsigned build created a certificate.' }
try {
    $selection=& "$PSScriptRoot\Get-SetupSigning.ps1" -SigningMode UnsignedPublic
    if ($selection.PeSigning -or $null -ne $selection.Certificate -or $null -ne $selection.PeSignerThumbprint) { throw 'Unsigned public signing policy drift.' }
} finally { Remove-Item Function:\Get-Item,Function:\Get-ChildItem,Function:\New-SelfSignedCertificate }
foreach ($name in @('Build-Setup.ps1','Get-SetupSigning.ps1')) {
    $tokens=$null;$errors=$null
    [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $name),[ref]$tokens,[ref]$errors)|Out-Null
    if ($errors) { throw 'Build/signing script syntax regression.' }
}
'Setup signing passed: unsigned composition needs no key; no trust store was changed.'