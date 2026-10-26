[CmdletBinding()]
param([string]$WorkingRoot = [IO.Path]::GetTempPath())
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path $PSScriptRoot -Parent
$guard = Join-Path $PSScriptRoot 'Test-RecoveryDependencies.ps1'
$fixture = Join-Path $WorkingRoot ('FH-RecoveryApproval-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$approval = Get-Content -LiteralPath (Join-Path $repo 'installer/RecoveryDependencyApproval.json') -Raw | ConvertFrom-Json
$files = @('Directory.Packages.props', 'installer/RecoveryDependencyApproval.json') + @($approval.lockFiles) + @($approval.packages.notice | Select-Object -Unique)
$original = @{}
foreach ($relative in $files) {
    $target = Join-Path $fixture $relative
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    $original[$relative] = [IO.File]::ReadAllText((Join-Path $repo $relative))
    [IO.File]::WriteAllText($target, $original[$relative])
}
New-Item -ItemType Directory -Path (Join-Path $fixture 'src/FluffHistory.Agent') -Force | Out-Null
function Reset-Fixture { foreach ($relative in $original.Keys) { [IO.File]::WriteAllText((Join-Path $fixture $relative), $original[$relative]) } }
$script:checks = 0
function Reject([string]$Expected, [string]$Assets = '') {
    $rejected = $false
    try { & $guard -Repository $fixture -AssetsPath $Assets | Out-Null }
    catch { if ($_.Exception.Message -notlike "*$Expected*") { throw }; $rejected = $true }
    if (!$rejected) { throw "Recovery guard accepted an invalid fixture: $Expected" }
    $script:checks++
    Reset-Fixture
}
& $guard -Repository $fixture | Out-Null
$script:checks++
$central = Join-Path $fixture 'Directory.Packages.props'
$direct = @($approval.packages | Where-Object direct)[0]
[IO.File]::WriteAllText($central, $original['Directory.Packages.props'].Replace(('Version="['+$direct.version+']"'), ('Version="['+$direct.version+'-unreviewed]"')))
Reject 'reviewed exact pin'
$lock = 'tools/FluffHistory.OneDriveSmoke/packages.lock.json'
$changedLock = $original[$lock] | ConvertFrom-Json
$native = @($approval.packages | Where-Object id -eq 'Microsoft.Identity.Client.NativeInterop')[0]
foreach ($framework in $changedLock.dependencies.PSObject.Properties) { $framework.Value.($native.id).resolved = $native.version+'-unreviewed' }
[IO.File]::WriteAllText((Join-Path $fixture $lock), ($changedLock|ConvertTo-Json -Depth 12))
Reject 'lock/version/hash drift'
$first = $approval.packages[0]
[IO.File]::WriteAllText((Join-Path $fixture $lock), $original[$lock].Replace($first.contentHash, 'changed-content-hash'))
Reject 'lock/version/hash drift'
$notice = $first.notice
[IO.File]::WriteAllText((Join-Path $fixture $notice), 'You may not distribute this software.')
Reject 'MIT notice changed'
$policyPath = Join-Path $fixture 'installer/RecoveryDependencyApproval.json'
$changedPolicy = $original['installer/RecoveryDependencyApproval.json'] | ConvertFrom-Json
$changedPolicy.packages[0].license = 'Proprietary'
[IO.File]::WriteAllText($policyPath, ($changedPolicy|ConvertTo-Json -Depth 8))
Reject 'unreviewed recovery license'
$agentLock = Join-Path $fixture 'src/FluffHistory.Agent/packages.lock.json'
[IO.File]::WriteAllText($agentLock, $original[$lock])
Reject 'Unapproved recovery dependency/project'
# Leave an empty valid Agent lock; the production graph must remain free of auth dependencies.
[IO.File]::WriteAllText($agentLock, '{"version":2,"dependencies":{"net10.0":{}}}')
$libraries = [ordered]@{}
foreach ($package in $approval.packages) {
    $libraries["$($package.id)/$($package.version)"] = @{sha512=$package.contentHash;path=($package.id.ToLowerInvariant()+'/'+$package.version)}
}
$cache = Join-Path $fixture 'cache'
$firstDir = Join-Path $cache ($first.id.ToLowerInvariant()+'/'+$first.version)
New-Item -ItemType Directory -Path $firstDir -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $firstDir ($first.id.ToLowerInvariant()+'.nuspec')), '<package><metadata><license type="expression">Proprietary</license></metadata></package>')
$folders = [ordered]@{}; $folders[$cache] = @{}
$assets = Join-Path $fixture 'project.assets.json'
[IO.File]::WriteAllText($assets, (@{libraries=$libraries;packageFolders=$folders}|ConvertTo-Json -Depth 6))
Reject 'package/license differs from reviewed artifact' $assets
$libraries["$($first.id)/$($first.version)"].sha512 = 'changed-restored-hash'
[IO.File]::WriteAllText($assets, (@{libraries=$libraries;packageFolders=$folders}|ConvertTo-Json -Depth 6))
Reject 'Restored recovery dependency/hash missing or changed' $assets
# Exercise the complete positive restored-artifact path, not only early rejections.
# Nested Windows PowerShell launched from pwsh/MSBuild can lack Get-FileHash.
$artifactApproval = $original['installer/RecoveryDependencyApproval.json'] | ConvertFrom-Json
foreach ($package in $artifactApproval.packages) {
    $directory = Join-Path $cache ($package.id.ToLowerInvariant()+'/'+$package.version)
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $directory ($package.id.ToLowerInvariant()+'.nuspec')), '<package><metadata><license type="expression">MIT</license></metadata></package>')
    $bytes = [Text.Encoding]::UTF8.GetBytes('synthetic-package:'+ $package.id)
    [IO.File]::WriteAllBytes((Join-Path $directory ($package.id.ToLowerInvariant()+'.'+$package.version+'.nupkg')), $bytes)
    $hash = [Security.Cryptography.SHA256]::Create()
    try { $package.archiveSha256 = ([BitConverter]::ToString($hash.ComputeHash($bytes))).Replace('-', '') }
    finally { $hash.Dispose() }
}
[IO.File]::WriteAllText($policyPath, ($artifactApproval|ConvertTo-Json -Depth 8))
$libraries["$($first.id)/$($first.version)"].sha512 = $first.contentHash
[IO.File]::WriteAllText($assets, (@{libraries=$libraries;packageFolders=$folders}|ConvertTo-Json -Depth 6))
function Get-FileHash { throw 'Unexpected dependency on module-provided Get-FileHash.' }
try {
    & $guard -Repository $fixture -AssetsPath $assets | Out-Null
    $script:checks++
    $archive = Join-Path $firstDir ($first.id.ToLowerInvariant()+'.'+$first.version+'.nupkg')
    [IO.File]::AppendAllText($archive, 'tampered')
    Reject 'package/license differs from reviewed artifact' $assets
}
finally { Remove-Item Function:\Get-FileHash }
Write-Output "Recovery dependency guard regression checks passed: $script:checks."
