[CmdletBinding()]
param([Parameter(Mandatory)][string]$Payload)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$info = Get-Content -LiteralPath (Join-Path $Payload 'build-info.json') -Raw | ConvertFrom-Json
$files = Get-Content -LiteralPath (Join-Path $Payload 'installed-files.json') -Raw | ConvertFrom-Json
. "$PSScriptRoot\..\installer\WindowsPlatform.ps1"
$platform = GetWindowsPlatformPolicy (Join-Path $Payload 'Installer')
if ($info.minimumWindows -cne $platform.ProductMinimum.ToString() -or
    $info.technicalPlatformMinimum -cne $platform.TechnicalMinimum.ToString()) { throw 'Windows platform metadata mismatch.' }
$distributionTerms = & "$PSScriptRoot\Get-DistributionTerms.ps1"
if ($info.distributionTermsSha256 -cne $distributionTerms.Sha256 -or $info.distributionTermsRevision -cne $distributionTerms.Revision -or
    (Get-FileHash -LiteralPath (Join-Path $Payload 'Installer\DistributionTerms.txt')).Hash -cne (Get-FileHash -LiteralPath $distributionTerms.File).Hash -or
    (Get-FileHash -LiteralPath (Join-Path $Payload ('ThirdPartyNotices\'+(Split-Path $distributionTerms.SdkNotice -Leaf)))).Hash -cne
    (Get-FileHash -LiteralPath $distributionTerms.SdkNotice).Hash) { throw 'Distribution agreement or exact SDK notice mismatch.' }
if ((Get-FileHash -LiteralPath (Join-Path $Payload 'LICENSE')).Hash -cne
    (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot '..\LICENSE')).Hash) { throw 'First-party license notice differs from source.' }
if ($info.version -cne $files.version -or (& "$PSScriptRoot\Get-ProductVersion.ps1" -ProductVersion $info.version) -cne $info.version) { throw 'Version metadata mismatch.' }
$desktop = Get-Content -LiteralPath (Join-Path $Payload 'FluffHistory.Desktop.deps.json') -Raw | ConvertFrom-Json
$packages = @($desktop.libraries.PSObject.Properties.Name | ForEach-Object { ($_ -split '/')[0] })
if ($desktop.libraries.PSObject.Properties.Name -notcontains ('runtimepack.Microsoft.Windows.SDK.NET.Ref/'+$distributionTerms.SdkNetVersion)) { throw 'Windows SDK runtime pack license policy drift.' }
foreach ($removed in @('Microsoft.WindowsAppSDK','Microsoft.WindowsAppSDK.AI','Microsoft.WindowsAppSDK.ML',
    'Microsoft.WindowsAppSDK.Search','Microsoft.WindowsAppSDK.Widgets','Microsoft.WindowsAppSDK.DWrite',
    'Microsoft.Windows.AI.MachineLearning','System.Numerics.Tensors')) {
    if ($packages -contains $removed) { throw "Unused dependency family returned to the runtime graph: $removed" }
}
foreach ($required in @('Microsoft.WindowsAppSDK.WinUI','Microsoft.WindowsAppSDK.Foundation','Microsoft.WindowsAppSDK.InteractiveExperiences')) {
    if ($packages -notcontains $required) { throw "Missing required dependency family: $required" }
}
foreach ($removed in @('onnxruntime.dll','DirectML.dll','Microsoft.Windows.AI.MachineLearning.dll','System.Numerics.Tensors.dll')) {
    if (Test-Path -LiteralPath (Join-Path $Payload $removed)) { throw "Unused runtime asset returned: $removed" }
}
foreach ($required in @('Microsoft.Windows.AppLifecycle.Projection.dll','Microsoft.Windows.Storage.Pickers.Projection.dll',
    'Microsoft.Windows.Storage.Pickers.winmd','Microsoft.WindowsAppRuntime.dll','Microsoft.Windows.SDK.NET.dll','WinRT.Runtime.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $Payload $required))) { throw "Missing required activation component: $required" }
}
foreach ($name in @('FluffHistory.Desktop.exe','FluffHistory.Desktop.pri','FluffHistory.Agent.exe','FluffHistory.InstallSupport.exe','FluffHistory.AgentLauncher.exe','FluffHistory.PropertySheet.dll','FluffHistory.ShellLauncher.exe','Installer\Lifecycle.ps1','coreclr.dll','Microsoft.ui.xaml.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $Payload $name))) { throw "Missing installer payload: $name" }
}
foreach ($file in $files.files) {
    if ($file.path -match '(^[\/]|:|(^|[\/])\.\.?([\/]|$))' -or (Get-FileHash -LiteralPath (Join-Path $Payload $file.path)).Hash -ne $file.sha256) { throw 'Payload inventory hash/path mismatch.' }
}
$productImages = @(Get-ChildItem -LiteralPath $Payload -File | Where-Object { $_.Name -like 'FluffHistory.*' -and $_.Extension -in @('.exe','.dll') })
foreach ($binary in $productImages) {
    & "$PSScriptRoot\Test-VersionMetadata.ps1" -Path $binary.FullName -ProductVersion $info.version
    $signature = Get-AuthenticodeSignature -LiteralPath $binary.FullName
    if ($info.developmentSigning) {
        if (!$signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ne $info.peSignerThumbprint) { throw 'Product signing mismatch.' }
    } elseif ($info.signingMode -cne 'UnsignedPublic' -or $info.productPeSigning -cne 'Unsigned' -or $signature.Status -ne 'NotSigned') { throw 'Public product PE must be unsigned.' }
}
$retired=@('FluffHistory.ExplorerCommand.dll','Identity.msix','SparseIdentityTrust.ps1','SparseIdentity.json','SparseIdentity.cer','Identity.appxmanifest','identity.cer')
foreach ($file in Get-ChildItem -LiteralPath $Payload -File -Recurse) {
    if ($file.Name -in $retired) { throw ('Retired modern context artifact in payload: ' + $file.Name) }
}
foreach ($field in @('packageIdentity','packageFamily','identityVersion','certificateThumbprint','sparseIdentitySigning','identitySha256','modernExplorerMinimum','shellPackageMinimum')) {
    if ($info.PSObject.Properties.Name -contains $field) { throw ('Obsolete build metadata: ' + $field) }
}
if ($info.schema -ne 2) { throw 'Unexpected current build-info schema.' }
$setupSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\installer\FluffHistory.iss') -Raw
if ($setupSource -match '(?im)^\s*SetupIconFile\s*=') { throw 'Setup must retain the default Inno installer icon.' }
$descriptions = @{
    'FluffHistory.Desktop.exe' = 'Fluff-History UI'
    'FluffHistory.Agent.exe' = 'Fluff-History Background Agent'
    'FluffHistory.AgentLauncher.exe' = 'Fluff-History Background Agent'
    'FluffHistory.ShellLauncher.exe' = 'Fluff-History Shell Launcher'
    'FluffHistory.InstallSupport.exe' = 'Fluff-History Installation Helper'
}
foreach ($name in $descriptions.Keys) {
    $version = (Get-Item -LiteralPath (Join-Path $Payload $name)).VersionInfo
    if ($version.FileDescription -ne $descriptions[$name] -or $version.ProductName -ne 'Fluff-History' -or $version.CompanyName -ne 'Fish & Dog Software') {
        throw "User-facing executable metadata mismatch: $name"
    }
}
Write-Output "Installer static validation passed: $(@($files.files).Count) inventoried files, version $($info.version)."
