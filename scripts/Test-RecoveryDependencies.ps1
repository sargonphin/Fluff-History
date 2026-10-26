[CmdletBinding()]
param([string]$Repository = (Split-Path $PSScriptRoot -Parent), [string]$AssetsPath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# Deliberately data driven: newer releases can be approved after package/license review.
# Do not replace this with a permanent version ceiling or a floating "MIT" allow-list.
$approval = Get-Content -LiteralPath (Join-Path $Repository 'installer/RecoveryDependencyApproval.json') -Raw | ConvertFrom-Json
if ($approval.schema -ne 1 -or !$approval.packages.Count) { throw 'Invalid recovery dependency approval.' }
function TextHash([string]$Text) {
    $hash = [Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($Text.Replace("`r`n", "`n"))))).Replace('-', '') }
    finally { $hash.Dispose() }
}
function ArchiveHash([string]$Path) {
    # Do not depend on module-provided Get-FileHash in nested Windows PowerShell.
    $hash = [Security.Cryptography.SHA256]::Create()
    try {
        $stream = [IO.File]::OpenRead($Path)
        try { ([BitConverter]::ToString($hash.ComputeHash($stream))).Replace('-', '') }
        finally { $stream.Dispose() }
    }
    finally { $hash.Dispose() }
}
$approved = @{}
[xml]$central = Get-Content -LiteralPath (Join-Path $Repository 'Directory.Packages.props') -Raw
foreach ($package in $approval.packages) {
    if ($approved.ContainsKey($package.id) -or $package.license -ne 'MIT') { throw 'Invalid or unreviewed recovery license approval.' }
    $approved.Add($package.id, $package)
    if ($package.direct) {
        $pin = @($central.Project.ItemGroup.PackageVersion | Where-Object Include -eq $package.id)
        if ($pin.Count -ne 1 -or $pin[0].Version -ne "[$($package.version)]") { throw "Recovery dependency requires reviewed exact pin: $($package.id)" }
    }
    $notice = Join-Path $Repository $package.notice
    if (!(Test-Path -LiteralPath $notice) -or (TextHash ([IO.File]::ReadAllText($notice))) -ne $package.noticeSha256) {
        throw "Reviewed MIT notice changed or missing: $($package.id)"
    }
}
$seenProjects = @{}
foreach ($area in @('src', 'tools')) {
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $Repository $area) -Filter packages.lock.json -Recurse -File) {
        $relative = $file.FullName.Substring(([IO.Path]::GetFullPath($Repository)).TrimEnd('\', '/').Length + 1).Replace('\', '/')
        $lock = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
        $seen = @{}
        foreach ($framework in $lock.dependencies.PSObject.Properties) {
            foreach ($entry in $framework.Value.PSObject.Properties | Where-Object Name -like 'Microsoft.Identity*') {
                if ($relative -notin $approval.lockFiles -or !$approved.ContainsKey($entry.Name)) { throw "Unapproved recovery dependency/project: $relative / $($entry.Name)" }
                $package = $approved[$entry.Name]
                if ($entry.Value.resolved -ne $package.version -or $entry.Value.contentHash -ne $package.contentHash) { throw "Recovery lock/version/hash drift: $relative / $($entry.Name)" }
                if ($package.direct -and ($entry.Value.type -ne 'Direct' -or $entry.Value.requested -ne "[$($package.version), $($package.version)]")) { throw "Recovery dependency is not directly exact-pinned: $($entry.Name)" }
                $seen[$entry.Name] = $true
            }
        }
        if ($relative -in $approval.lockFiles) {
            foreach ($id in $approved.Keys) { if (!$seen.ContainsKey($id)) { throw "Missing approved recovery dependency: $relative / $id" } }
            $seenProjects[$relative] = $true
        }
    }
}
foreach ($file in $approval.lockFiles) { if (!$seenProjects.ContainsKey($file)) { throw "Missing recovery lock file: $file" } }
if ($AssetsPath) {
    # Build-time check also verifies what restore actually selected and the downloaded package bytes.
    $assets = Get-Content -LiteralPath $AssetsPath -Raw | ConvertFrom-Json
    foreach ($library in $assets.libraries.PSObject.Properties | Where-Object Name -like 'Microsoft.Identity*') {
        $id, $version = $library.Name.Split('/')
        if (!$approved.ContainsKey($id) -or $version -ne $approved[$id].version) { throw "Unapproved restored recovery dependency: $($library.Name)" }
    }
    foreach ($package in $approval.packages) {
        $key = "$($package.id)/$($package.version)"
        $library = $assets.libraries.PSObject.Properties[$key]
        if (!$library -or $library.Value.sha512 -ne $package.contentHash) { throw "Restored recovery dependency/hash missing or changed: $key" }
        $directory = $null
        foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
            $candidate = Join-Path $folder $library.Value.path
            if (Test-Path -LiteralPath $candidate) { $directory = $candidate; break }
        }
        if (!$directory) { throw "Restored recovery package directory missing: $key" }
        $base = $package.id.ToLowerInvariant()
        $nuspec = Join-Path $directory ($base + '.nuspec')
        $archive = Join-Path $directory ($base + '.' + $package.version + '.nupkg')
        [xml]$spec = Get-Content -LiteralPath $nuspec -Raw
        if ($spec.package.metadata.license.type -ne 'expression' -or $spec.package.metadata.license.InnerText -ne $package.license -or
            (ArchiveHash $archive) -ne $package.archiveSha256) {
            throw "Restored recovery package/license differs from reviewed artifact: $key"
        }
    }
}
Write-Output 'Recovery dependency approval passed: exact reviewed versions, lock hashes and MIT notices.'
