[CmdletBinding()]
param([string]$Repository = (Split-Path $PSScriptRoot -Parent), [string]$NuGetRoot)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$policy = Get-Content -LiteralPath (Join-Path $Repository 'installer\DistributionTermsPolicy.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($policy.schema -ne 1 -or $policy.revision -notmatch '^\d{4}-\d{2}-\d{2}\.\d+$') { throw 'Invalid distribution terms policy.' }
$file = Join-Path $Repository 'installer\DistributionTerms.txt'
$text = [IO.File]::ReadAllText($file).Replace("`r`n", "`n").Replace("`r", "`n")
if (!$text.Contains('Agreement version: ' + $policy.revision)) { throw 'Distribution agreement revision mismatch.' }
$sha = [Security.Cryptography.SHA256]::Create()
try { $digest = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($text))).Replace('-','') }
finally { $sha.Dispose() }
if (!$NuGetRoot) { $NuGetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' } }
$lock = Get-Content -LiteralPath (Join-Path $Repository 'src\FluffHistory.Desktop\packages.lock.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$resolvedIds = @(foreach ($framework in $lock.dependencies.PSObject.Properties) {
    foreach ($entry in $framework.Value.PSObject.Properties) { if ($entry.Name -imatch '^microsoft\.windowsappsdk\.') { $entry.Name.ToLowerInvariant() } }
}) | Sort-Object -Unique
$policyIds = @($policy.appSdk | ForEach-Object id | Sort-Object -Unique)
if (@($resolvedIds).Count -ne $policyIds.Count -or @($resolvedIds | Where-Object { $_ -notin $policyIds }).Count) { throw 'Incomplete Microsoft component license policy.' }
foreach ($component in $policy.appSdk) {
    if ($component.id -notmatch '^microsoft\.windowsappsdk\.[a-z]+$' -or $component.version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid component policy coordinate.' }
    $found = $false
    foreach ($framework in $lock.dependencies.PSObject.Properties) {
        foreach ($entry in $framework.Value.PSObject.Properties) {
            if ($entry.Name -ieq $component.id) {
                if ($entry.Value.resolved -cne $component.version) { throw 'Distribution license policy version drift.' }
                $found = $true
            }
        }
    }
    if (!$found) { throw 'Distribution license component missing from source lock.' }
    $license = Join-Path $NuGetRoot ($component.id + '\' + $component.version + '\license.txt')
    if ((Get-FileHash -LiteralPath $license).Hash -cne $component.licenseSha256) { throw 'Microsoft distribution license changed; review policy and downstream terms.' }
}
if ($policy.sdkNet.version -notmatch '^\d+\.\d+\.\d+\.\d+$' -or
    $policy.sdkNet.notice -cne ('WindowsSDK-NETRef-'+$policy.sdkNet.version+'.rtf')) { throw 'Invalid Windows SDK notice coordinate.' }
$sdkNotice = Join-Path $Repository ('installer\ThirdPartyNotices\' + $policy.sdkNet.notice)
if ((Get-FileHash -LiteralPath $sdkNotice).Hash -cne $policy.sdkNet.noticeSha256) { throw 'Windows SDK notice changed; review distribution terms.' }
[pscustomobject]@{ Revision=$policy.revision; Sha256=$digest; File=$file; SdkNetVersion=$policy.sdkNet.version; SdkNotice=$sdkNotice }
