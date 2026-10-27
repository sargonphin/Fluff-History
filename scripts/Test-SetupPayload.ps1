[CmdletBinding()]
param([Parameter(Mandatory)][string]$Payload)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$info = Get-Content -LiteralPath (Join-Path $Payload 'build-info.json') -Raw | ConvertFrom-Json
$files = Get-Content -LiteralPath (Join-Path $Payload 'installed-files.json') -Raw | ConvertFrom-Json
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
foreach ($name in @('FluffHistory.Desktop.exe','FluffHistory.Agent.exe','FluffHistory.InstallSupport.exe','FluffHistory.AgentLauncher.exe','FluffHistory.PropertySheet.dll','FluffHistory.ExplorerCommand.dll','Identity.msix','Installer\Lifecycle.ps1','coreclr.dll','Microsoft.ui.xaml.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $Payload $name))) { throw "Missing installer payload: $name" }
}
foreach ($file in $files.files) {
    if ($file.path -match '(^[\/]|:|(^|[\/])\.\.?([\/]|$))' -or (Get-FileHash -LiteralPath (Join-Path $Payload $file.path)).Hash -ne $file.sha256) { throw 'Payload inventory hash/path mismatch.' }
}
if ((Get-FileHash -LiteralPath (Join-Path $Payload 'Identity.msix')).Hash -ne $info.identitySha256) { throw 'Identity hash mismatch.' }
$productImages = @(Get-ChildItem -LiteralPath $Payload -File | Where-Object { $_.Name -like 'FluffHistory.*' -and $_.Extension -in @('.exe','.dll') })
foreach ($binary in $productImages) {
    & "$PSScriptRoot\Test-VersionMetadata.ps1" -Path $binary.FullName -ProductVersion $info.version
    $signature = Get-AuthenticodeSignature -LiteralPath $binary.FullName
    if ($info.developmentSigning) {
        if (!$signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ne $info.certificateThumbprint) { throw 'Product signing mismatch.' }
    } elseif ($info.signingMode -cne 'UnsignedPublic' -or $info.productPeSigning -cne 'Unsigned' -or $signature.Status -ne 'NotSigned') { throw 'Public product PE must be unsigned.' }
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$identity = [IO.Compression.ZipFile]::OpenRead((Join-Path $Payload 'Identity.msix'))
try {
    $entry = $identity.GetEntry('AppxManifest.xml')
    if (!$entry) { throw 'Missing sparse manifest.' }
    $reader = New-Object IO.StreamReader($entry.Open())
    try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    if ($manifest.Package.Identity.Version -cne $info.version -or $info.identityVersion -cne $info.version -or
        $manifest.Package.Identity.Name -cne $info.packageIdentity) { throw 'Sparse identity metadata mismatch.' }
    if (!$info.developmentSigning) {
        . "$PSScriptRoot\..\installer\SparseIdentityTrust.ps1"
        $sparse = GetSparseIdentityPolicy (Join-Path $Payload 'Installer')
        try {
            if ($manifest.Package.Identity.Publisher -cne $sparse.Policy.publisher -or $info.certificateThumbprint -cne $sparse.Policy.certificateThumbprint -or
                $info.packageFamily -cne $sparse.Policy.packageFamilyName -or $info.sparseIdentitySigning -cne 'SelfSigned' -or
                (Test-Path -LiteralPath (Join-Path $Payload 'identity.cer'))) { throw 'Public sparse signing composition mismatch.' }
            $signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $Payload 'Identity.msix')
            if (!$signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -cne $sparse.Policy.certificateThumbprint) { throw 'Sparse package signature mismatch.' }
        } finally { $sparse.Certificate.Dispose() }
    }
    [xml]$sourceManifest = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\installer\Identity.appxmanifest'))
    $expectedLanguages = @($sourceManifest.Package.Resources.Resource | ForEach-Object { $_.Language })
    $actualLanguages = @($manifest.Package.Resources.Resource | ForEach-Object { $_.Language })
    if ($expectedLanguages.Count -ne 44 -or $expectedLanguages[0] -cne 'en-US' -or
        [string]::Join('|',$actualLanguages) -cne [string]::Join('|',$expectedLanguages)) { throw 'Sparse identity language declarations mismatch.' }
} finally { $identity.Dispose() }
$setupSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\installer\FluffHistory.iss') -Raw
if ($setupSource -match '(?im)^\s*SetupIconFile\s*=') { throw 'Setup must retain the default Inno installer icon.' }
$descriptions = @{
    'FluffHistory.Desktop.exe' = 'Fluff-History UI'
    'FluffHistory.Agent.exe' = 'Fluff-History Background Agent'
    'FluffHistory.AgentLauncher.exe' = 'Fluff-History Background Agent'
    'FluffHistory.InstallSupport.exe' = 'Fluff-History Installation Helper'
}
foreach ($name in $descriptions.Keys) {
    $version = (Get-Item -LiteralPath (Join-Path $Payload $name)).VersionInfo
    if ($version.FileDescription -ne $descriptions[$name] -or $version.ProductName -ne 'Fluff-History' -or $version.CompanyName -ne 'Fish & Dog Software') {
        throw "User-facing executable metadata mismatch: $name"
    }
}
Write-Output "Installer static validation passed: $(@($files.files).Count) inventoried files, version $($info.version)."
