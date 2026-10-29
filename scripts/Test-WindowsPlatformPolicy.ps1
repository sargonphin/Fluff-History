$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path $PSScriptRoot -Parent
. "$repo\installer\WindowsPlatform.ps1"
$policy = GetWindowsPlatformPolicy (Join-Path $repo 'installer')
if ($policy.TechnicalMinimum -ne [version]'10.0.19041.0' -or $policy.ProductMinimum -ne [version]'10.0.19045.0' ) { throw 'Unexpected Windows compatibility policy.' }
foreach ($build in @(17763,19041,19044,19045,21999,22000,22631,26100)) {
    $version = [version]('10.0.' + $build + '.0')
    if (($version -ge $policy.ProductMinimum) -ne ($build -ge 19045)) { throw 'Installer product floor drift.' }
    if ((SupportsExplorerContextCommand $version) -ne ($build -ge 19045)) { throw 'Explorer compatibility admission drift.' }
}
[xml]$desktop = [IO.File]::ReadAllText((Join-Path $repo 'src\FluffHistory.Desktop\FluffHistory.Desktop.csproj'))
foreach ($name in @('TargetPlatformMinVersion','SupportedOSPlatformVersion')) {
    if ($desktop.SelectSingleNode('/Project/PropertyGroup/' + $name).InnerText -cne '$(WindowsTechnicalMinimum)') { throw 'Desktop minimum is not bound to policy.' }
}
$setup = [IO.File]::ReadAllText((Join-Path $repo 'installer\FluffHistory.iss'))
if ($setup -notmatch '(?m)^MinVersion=\{#ProductMinimumWindows\}\s*$' -or $setup -notmatch '(?m)^PrivilegesRequired=lowest\s*$' -or
    $setup -notmatch '(?m)^ArchitecturesAllowed=x64os\s*$') { throw 'Setup platform/per-user policy drift.' }
$build = [IO.File]::ReadAllText((Join-Path $repo 'scripts\Build-Setup.ps1'))
if (!$build.Contains('--define=ProductMinimumWindows=$($platform.ProductMinimum.ToString(3))') -or
    !$build.Contains('minimumWindows=$platform.ProductMinimum.ToString()')) { throw 'Setup build is not bound to platform policy.' }
'Windows platform policy passed: technical 19041, product 19045 x64; classic static verb; boundary and composition checks.'
