$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$installer = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\installer'))
. (Join-Path $installer 'SparseIdentityTrust.ps1')
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('FluffHistory-SigningPolicy-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
try {
    $valid = GetSparseIdentityPolicy $installer
    try { if ($valid.Certificate.HasPrivateKey) { throw 'Private material in public policy.' } }
    finally { $valid.Certificate.Dispose() }
    Copy-Item -LiteralPath (Join-Path $installer 'SparseIdentity.cer'),(Join-Path $installer 'SparseIdentity.json') -Destination $temporary
    $policyPath = Join-Path $temporary 'SparseIdentity.json'
    $original = [IO.File]::ReadAllText($policyPath); $checks=1
    foreach ($field in @('publisher','certificateSha256','certificateThumbprint','packageName','packageFamilyName','trustStore')) {
        $policy = $original | ConvertFrom-Json
        $policy.$field = 'changed'
        $policy | ConvertTo-Json | Set-Content -LiteralPath $policyPath -Encoding UTF8
        $rejected = $false
        try { $value = GetSparseIdentityPolicy $temporary; $value.Certificate.Dispose() }
        catch { if ($_.Exception.Message -notmatch 'certificate/pin mismatch') { throw }; $rejected=$true }
        if (!$rejected) { throw "Unpinned identity accepted: $field" }; $checks++
    }
    $tokens=$null;$errors=$null
    foreach ($name in @('Build-Setup.ps1','Get-SetupSigning.ps1')) {
        [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $name),[ref]$tokens,[ref]$errors) | Out-Null
        if ($errors) { throw 'Signing/build script syntax regression.' }; $checks++
    }
    # Execute the actual unsigned selection branch with its certificate-store lookup intercepted.
    # It must request only the pinned identity; it must never enumerate/create a preview signer.
    $public = GetSparseIdentityPolicy $installer
    $expectedPath = 'Cert:\CurrentUser\My\' + $public.Policy.certificateThumbprint
    function Get-Item { param([string]$LiteralPath)
        if ($LiteralPath -cne $expectedPath) { throw 'Unexpected signing key requested.' }
        return [pscustomobject]@{HasPrivateKey=$true;RawData=$public.Certificate.RawData;Thumbprint=$public.Certificate.Thumbprint}
    }
    function Get-ChildItem { throw 'Unsigned composition enumerated a preview certificate.' }
    function New-SelfSignedCertificate { throw 'Unsigned composition created a preview certificate.' }
    try {
        $selection = & "$PSScriptRoot\Get-SetupSigning.ps1" -SigningMode UnsignedPublic
        if ($selection.PeSigning -or $selection.PackageName -cne 'Sargonphin.FluffHistory') { throw 'Unsigned composition selected preview trust.' }
        $checks++
    } finally {
        Remove-Item Function:\Get-Item,Function:\Get-ChildItem,Function:\New-SelfSignedCertificate
        $public.Certificate.Dispose()
    }
    "Setup signing policy: $checks checks passed; no trust store was changed."
} finally {
    if (![IO.Path]::GetFullPath($temporary).StartsWith([IO.Path]::GetTempPath(),[StringComparison]::OrdinalIgnoreCase)) { throw 'Signing fixture escaped temporary storage.' }
    Remove-Item -LiteralPath $temporary -Recurse -Force
}
