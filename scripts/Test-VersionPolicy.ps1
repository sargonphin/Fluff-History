$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
foreach ($valid in @('0.24.1.1','1.2.3.4','65535.65535.65535.65535')) {
    if ((& "$PSScriptRoot\Get-ProductVersion.ps1" -ProductVersion $valid) -cne $valid) { throw 'Valid version was altered.' }
}
foreach ($invalid in @('0.0.0.0','1.2.3','1.2.3.65536','-1.2.3.4','1.02.3.4','1.2.3.4+source','1.2.3.4.5')) {
    $rejected = $false
    try { $null = & "$PSScriptRoot\Get-ProductVersion.ps1" -ProductVersion $invalid } catch { $rejected = $true }
    if (!$rejected) { throw "Invalid Windows product version accepted: $invalid" }
}
foreach ($release in @('1.10.0','v1.10.0','1.10.0-beta.2')) {
    $canonical = & "$PSScriptRoot\Get-ReleaseVersion.ps1" -ReleaseVersion $release -ProductVersion '1.10.0.248'
    if ($canonical -cne $release.TrimStart('v')) { throw 'Public release identity changed.' }
}
foreach ($release in @('V1.10.0','1.10.0-beta','1.10.0-beta.01','1.10.0-rc.1','1.10.0.248','1.010.0','1.10.0 ','2147483648.0.0')) {
    $rejected = $false
    try { $null = & "$PSScriptRoot\Get-ReleaseVersion.ps1" -ReleaseVersion $release } catch { $rejected = $true }
    if (!$rejected) { throw 'Invalid release identity accepted.' }
}
$rejected = $false
try { $null = & "$PSScriptRoot\Get-ReleaseVersion.ps1" -ReleaseVersion '1.10.0-beta.2' -ProductVersion '1.9.0.248' } catch { $rejected = $true }
if (!$rejected) { throw 'Mismatched release/build core accepted.' }
$setup = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\installer\FluffHistory.iss') -Raw
if ($setup -match '(?im)^\s*SetupIconFile\s*=') { throw 'Setup must use the default compiler icon.' }
foreach ($directive in @('AppVersion','VersionInfoVersion','VersionInfoProductVersion','VersionInfoTextVersion','VersionInfoProductTextVersion')) {
    if ($setup -notmatch ('(?m)^' + $directive + '=\{#ProductVersion\}\s*$')) { throw "Unbound Setup version: $directive" }
}
Write-Output 'Version policy passed: valid boundary, malformed/zero/overflow rejection, Setup propagation and default icon.'
