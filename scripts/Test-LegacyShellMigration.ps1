$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$tokens=$null;$errors=$null
$syntax=[Management.Automation.Language.Parser]::ParseFile([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\installer\Lifecycle.ps1')),[ref]$tokens,[ref]$errors)
if($errors){throw 'Lifecycle syntax error.'}
foreach($function in $syntax.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst]},$false)){. ([scriptblock]::Create($function.Extent.Text))}
$temporary=Join-Path ([IO.Path]::GetTempPath()) ('FluffHistory-LegacyMigration-'+[guid]::NewGuid().ToString('N'))
$root=Join-Path $temporary 'Product';$records=Join-Path $temporary 'Installer';$record=Join-Path $records 'installation-v1.json';$pending=Join-Path $records 'pending-v1.json';$Version='1.1.0.7'
$script:packages=@();$script:removals=@();$script:failRemoval=$false;$script:failDiscovery=$false
function Get-AppxPackage {param($Name,$ErrorAction) if($script:failDiscovery){throw 'Discovery unavailable'};@($script:packages | Where-Object Name -ceq $Name)}
function Remove-AppxPackage {param($Package,$ErrorAction) $script:removals+= $Package;if($script:failRemoval){throw 'Removal unavailable'};$script:packages=@($script:packages | Where-Object PackageFullName -cne $Package)}
try {
    New-Item -ItemType Directory -Path $records -Force|Out-Null
    $known=@()
    foreach($versionName in @('1.1.0.6',$Version)){
        $directory=Join-Path $root ('Versions\'+$versionName);New-Item -ItemType Directory -Path $directory -Force|Out-Null
        WriteJson (Join-Path $directory 'build-info.json') @{packageFamily='Sargonphin.FluffHistory.DeveloperPreview_c87380kbqs5fr';packageIdentity='Sargonphin.FluffHistory.DeveloperPreview'}
        [IO.File]::WriteAllText((Join-Path $directory 'FluffHistory.ExplorerCommand.dll'),'legacy image')
        $files=@(Get-ChildItem -LiteralPath $directory -File|ForEach-Object{@{path=$_.Name;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash;length=$_.Length}})
        WriteJson (Join-Path $directory 'installed-files.json') @{schema=1;version=$versionName;files=$files}
        $known+=@{version=$versionName;manifestSha256=(Get-FileHash -LiteralPath (Join-Path $directory 'installed-files.json')).Hash}
    }
    WriteJson $record @{schema=1;version=$Version;root=$root;knownVersions=$known;legacyCleanup=@()}
    $owned=[pscustomobject]@{Name='Sargonphin.FluffHistory.DeveloperPreview';PackageFamilyName='Sargonphin.FluffHistory.DeveloperPreview_c87380kbqs5fr';Publisher='CN=Fluff-History Developer Preview';Version='1.1.0.6';PackageFullName='Sargonphin.FluffHistory.DeveloperPreview_1.1.0.6_x64__c87380kbqs5fr'}
    $neighbor=[pscustomobject]@{Name=$owned.Name;PackageFamilyName='different-family';Publisher='different publisher';Version='1.1.0.6';PackageFullName='unrelated'}
    $script:packages=@($owned,$neighbor);$script:failRemoval=$true
    RetireLegacyPackages
    if(@((ReadJson $record).legacyCleanup).Count -ne 1){throw 'Removal failure lost deferred obligation.'}
    RetireOldPayloads
    $old=Join-Path $root 'Versions\1.1.0.6';if(!(Test-Path -LiteralPath (Join-Path $old 'build-info.json'))){throw 'Referenced payload was partially retired.'}
    $script:failDiscovery=$true;RetireOldPayloads
    if(!(Test-Path -LiteralPath (Join-Path $old 'FluffHistory.ExplorerCommand.dll'))){throw 'Uncertain discovery allowed deletion.'}
    $script:failDiscovery=$false;$script:failRemoval=$false;RetireLegacyPackages
    if(@((ReadJson $record).legacyCleanup).Count){throw 'Verified retirement retained an obligation.'}
    if($script:packages.Count -ne 1 -or $script:packages[0].PackageFullName -cne 'unrelated' -or $script:removals -contains 'unrelated'){throw 'Unrelated package was affected.'}
    RetireOldPayloads
    if(Test-Path -LiteralPath $old){throw 'Unreferenced owned legacy payload was not cleaned.'}
    if(!(Test-Path -LiteralPath (Join-Path $root ('Versions\'+$Version+'\build-info.json')))){throw 'Active payload was removed.'}
    'Legacy migration passed: exact identity, ownership inventory, removal refusal, discovery failure, retained payload, retry and unrelated-package preservation.'
} finally {
    if(![IO.Path]::GetFullPath($temporary).StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()),[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe migration fixture cleanup.'}
    Remove-Item -LiteralPath $temporary -Recurse -Force
}
