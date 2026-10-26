# Native Windows PowerShell 5.1 diagnostic only. No account passwords or tokens accepted.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidateSet('Protect','Recover','Cleanup','Inspect')][string]$Mode,
    [string]$Directory,
    # Optional preselected synthetic coordinate lets BOTH machines prove absence before A provisions it.
    [Guid]$SampleId = [Guid]::Empty,
    [switch]$AllowWindowsUI
)
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT' -or $env:FLUFF_HISTORY_M202_ACCOUNT_DIAGNOSTICS -ne '1') {
    throw 'Opt-in native Windows diagnostic: set FLUFF_HISTORY_M202_ACCOUNT_DIAGNOSTICS=1. Use synthetic data only.'
}
Add-Type -Path (Join-Path $PSScriptRoot 'MicrosoftAccountProbeNative.cs')
$identity = [FluffHistory.Tests.Diagnostics.MicrosoftAccountProbeNative]::InspectIdentity()
$metadata = [ordered]@{ identityStatus=$identity.Status; internetIdentity=$identity.InternetIdentity; provider=$identity.Provider;
    hasPrincipalName=$identity.HasPrincipalName; processSession=(Get-Process -Id $PID).SessionId }
function Test-ExactCredential($Vault, [string]$Resource) {
    try { $null = $Vault.Retrieve($Resource,'SyntheticProbe'); return $true }
    catch { if ($_.Exception.GetBaseException().HResult -ne -2147023728) { throw }; return $false }
}
if ($Mode -eq 'Inspect') {
    if ($SampleId -ne [Guid]::Empty) {
        $vault = New-Object 'Windows.Security.Credentials.PasswordVault,Windows.Security.Credentials,ContentType=WindowsRuntime'
        $metadata.ownSyntheticCredentialPresent = Test-ExactCredential $vault ('FluffHistory-M202-' + $SampleId.ToString('N'))
    }
    $metadata | ConvertTo-Json -Compress; return
}
if (!$Directory) { throw 'An explicit synthetic-artifact directory is required.' }
$Directory = [IO.Path]::GetFullPath($Directory)
$path = Join-Path $Directory 'synthetic-protection.json'
$vault = New-Object 'Windows.Security.Credentials.PasswordVault,Windows.Security.Credentials,ContentType=WindowsRuntime'
function Format-Status([int]$Status) { '0x' + $Status.ToString('X8') }
if ($Mode -eq 'Protect') {
    if (Test-Path -LiteralPath $Directory) { throw 'Protect requires a fresh directory; never overwrites another sample.' }
    $null = New-Item -ItemType Directory -Path $Directory
    if ($SampleId -eq [Guid]::Empty) { $SampleId = [Guid]::NewGuid() }
    $resource = 'FluffHistory-M202-' + $SampleId.ToString('N')
    if (Test-ExactCredential $vault $resource) { throw 'Synthetic coordinate already exists; never overwrite a credential.' }
    $user = 'SyntheticProbe'
    $random = New-Object byte[] 32
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($random); $syntheticPassword = [Convert]::ToBase64String($random) }
    finally { $rng.Dispose(); [Array]::Clear($random,0,$random.Length) }
    $credential = New-Object 'Windows.Security.Credentials.PasswordCredential,Windows.Security.Credentials,ContentType=WindowsRuntime' -ArgumentList $resource,$user,$syntheticPassword
    $syntheticPassword = $null
    $cases = New-Object Collections.Generic.List[object]
    $samples = New-Object Collections.Generic.List[object]
    # Only Microsoft's documented single-name and name,resource forms; no descriptor brute force.
    # The PasswordVault mapping is a controlled hypothesis, NOT a supported mapping assertion.
    $descriptors = @('LOCAL=user', ('WEBCREDENTIALS=' + $user), ('WEBCREDENTIALS=' + $user + ',' + $resource))
    $flags = @(64)
    if ($AllowWindowsUI) { $flags += 0 } # Supported interactive permission, never an app-owned login form.
    $ownedCredential = $false
    try {
        foreach ($phase in @('before-vault-add','after-vault-add')) {
            if ($phase -eq 'after-vault-add') { $vault.Add($credential); $ownedCredential = $true }
            foreach ($descriptor in $descriptors) {
                foreach ($flag in $flags) {
                    $sample = [FluffHistory.Tests.Diagnostics.MicrosoftAccountProbeNative]::ProtectSynthetic($descriptor, $flag)
                    $recovery = $null
                    if ($sample.Blob) { $recovery = [FluffHistory.Tests.Diagnostics.MicrosoftAccountProbeNative]::RecoverSynthetic($sample.Blob,$sample.ExpectedHash,$flag) }
                    $label = if ($descriptor -eq 'LOCAL=user') {'LOCAL=user'} elseif ($descriptor.Contains(',')) {'WEBCREDENTIALS=name,resource'} else {'WEBCREDENTIALS=name'}
                    $cases.Add([ordered]@{phase=$phase; descriptor=$label; flags=$flag; descriptorStatus=(Format-Status $sample.DescriptorStatus);
                        protectStatus=$(if($sample.DescriptorStatus -eq 0){Format-Status $sample.ProtectStatus}else{$null});
                        unprotectStatus=$(if($recovery){Format-Status $recovery.Status}else{$null}); matches=$(if($recovery){$recovery.Matches}else{$false})})
                    if ($sample.Blob) { $samples.Add([ordered]@{label=$label;phase=$phase;flags=$flag;expectedHash=$sample.ExpectedHash;blob=[Convert]::ToBase64String($sample.Blob)}) }
                }
            }
        }
        # Synthetic protected blobs/hash only; no account credentials or plaintext random secret exported.
        [ordered]@{schema=1;resource=$resource;user=$user;samples=@($samples.ToArray())} | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 -LiteralPath $path
        [ordered]@{mode=$Mode;identity=$metadata;vaultAdded=$ownedCredential;cases=@($cases.ToArray())} | ConvertTo-Json -Depth 6
    }
    catch {
        if ($ownedCredential) { $vault.Remove($credential) }
        throw
    }
    # Keep only this synthetic credential until the explicit cleanup pass for fresh-process testing.
    return
}
if ((Get-Item -LiteralPath $path).Length -gt 400000) { throw 'Synthetic manifest exceeds diagnostic bound.' }
$manifest = Get-Content -Raw -Encoding UTF8 -LiteralPath $path | ConvertFrom-Json
if ($manifest.schema -ne 1 -or $manifest.resource -notmatch '^FluffHistory-M202-[0-9a-f]{32}$' -or $manifest.user -ne 'SyntheticProbe') {
    throw 'Unexpected synthetic manifest ownership.'
}
if ($Mode -eq 'Cleanup') {
    $credential = $vault.Retrieve($manifest.resource,$manifest.user); $vault.Remove($credential)
    [ordered]@{mode=$Mode;ownSyntheticCredentialRemoved=$true} | ConvertTo-Json -Compress
    return
}
$credentialPresent = $false
try { $null = $vault.Retrieve($manifest.resource,$manifest.user); $credentialPresent = $true }
catch { if ($_.Exception.GetBaseException().HResult -ne -2147023728) { throw } } # ERROR_NOT_FOUND, no secret retrieval.
$results = foreach ($sample in $manifest.samples) {
    $blob = [Convert]::FromBase64String($sample.blob)
    $result = [FluffHistory.Tests.Diagnostics.MicrosoftAccountProbeNative]::RecoverSynthetic($blob,$sample.expectedHash,64)
    [ordered]@{descriptor=$sample.label;phase=$sample.phase;unprotectStatus=(Format-Status $result.Status);matches=$result.Matches}
}
[ordered]@{mode=$Mode;identity=$metadata;ownSyntheticCredentialPresent=$credentialPresent;cases=@($results)} | ConvertTo-Json -Depth 6
