[CmdletBinding()]
param([string]$ReleaseVersion = 'development', [string]$ProductVersion)
$ErrorActionPreference = 'Stop'
if (!$ReleaseVersion -or $ReleaseVersion -ceq 'development') { return 'development' }
if ($ReleaseVersion -cnotmatch '^v?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-beta\.(0|[1-9][0-9]*))?$') { throw 'Invalid public ReleaseVersion.' }
$canonical = $ReleaseVersion.TrimStart('v')
foreach ($component in ($canonical -split '[.\-]' | Where-Object { $_ -ne 'beta' })) {
    if ($component.Length -gt 10 -or [long]$component -gt [int]::MaxValue) { throw 'Release component exceeds Int32.' }
}
if ($ProductVersion) {
    $build = & "$PSScriptRoot\Get-ProductVersion.ps1" -ProductVersion $ProductVersion
    if (($build.Split('.')[0..2] -join '.') -cne ($canonical.Split('-')[0])) { throw 'ReleaseVersion core must match ProductVersion core.' }
}
$canonical
