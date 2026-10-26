[CmdletBinding()]
param([string]$OutputDirectory, [string]$DotNet = 'dotnet', [switch]$AllowUncommitted)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ([Environment]::OSVersion.Platform -ne 'Win32NT') { throw 'Developer Preview packaging requires Windows.' }
$repository = Split-Path $PSScriptRoot -Parent
Push-Location $repository
try {
    $sha = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Git commit could not be read.' }
    $dirty = [bool](& git status --porcelain)
    if ($dirty -and !$AllowUncommitted) { throw 'Commit source before producing a development artifact. Use -AllowUncommitted only for a local smoke build.' }
    $embeddedSha = if ($dirty) { "$sha-dirty" } else { $sha }
    $height = [int](& git rev-list --count HEAD)
    if ($LASTEXITCODE -ne 0 -or $height -gt 65535) { throw 'Git height exceeds the MSIX version field.' }
    $version = "0.24.0.$height"
    if (!$OutputDirectory) { $OutputDirectory = Join-Path (Join-Path $repository 'artifacts\releases') $version }
    $OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
    if ((Test-Path -LiteralPath $OutputDirectory) -and (Get-ChildItem -LiteralPath $OutputDirectory -Force | Select-Object -First 1)) {
        throw 'Artifact output must be empty. Choose a new output directory; existing artifacts are preserved.'
    }
    $sdk = (& $DotNet --version)
    if ($LASTEXITCODE -ne 0 -or $sdk -notlike '10.0.*') { throw 'A supported .NET 10 SDK is required.' }
    & $DotNet restore FluffHistory.sln --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }
    $nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
    $signTool = Join-Path $nugetRoot 'microsoft.windows.sdk.buildtools\10.0.26100.6584\bin\10.0.26100.0\x64\signtool.exe'
    if (!(Test-Path -LiteralPath $signTool)) { throw 'Locked Windows SDK SignTool was not restored.' }
    $subject = 'CN=Fluff-History Developer Preview'
    $certificate = Get-ChildItem Cert:\CurrentUser\My | Where-Object {
        $_.Subject -eq $subject -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(7) -and
        (@($_.EnhancedKeyUsageList | ForEach-Object { [string]$_.ObjectId }) -contains '1.3.6.1.5.5.7.3.3')
    } | Sort-Object NotAfter -Descending | Select-Object -First 1
    if (!$certificate) {
        $certificate = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject -CertStoreLocation Cert:\CurrentUser\My `
            -HashAlgorithm SHA256 -KeyAlgorithm RSA -KeyLength 2048 -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddYears(1)
    }
    $staging = Join-Path $repository ('artifacts\developer-preview\staging-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
    & $DotNet build src\FluffHistory.Desktop\FluffHistory.Desktop.csproj -c Release --no-restore `
        "-p:GitCommit=$embeddedSha" "-p:DeveloperPreviewVersion=$version" -p:GenerateAppxPackageOnBuild=true `
        -p:AppxSymbolPackageEnabled=false `
        "-p:AppxPackageDir=$staging\" -p:UapAppxPackageBuildMode=SideloadOnly
    if ($LASTEXITCODE -ne 0) { throw 'Release self-contained MSIX build failed.' }
    $packages = @(Get-ChildItem -LiteralPath $staging -Recurse -File -Filter '*.msix')
    if ($packages.Count -ne 1) { throw 'Expected exactly one x64 MSIX.' }
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $msix = Join-Path $OutputDirectory "Fluff-History-Developer-Preview_${version}_x64.msix"
    Copy-Item -LiteralPath $packages[0].FullName -Destination $msix
    & $signTool sign /fd SHA256 /s My /sha1 $certificate.Thumbprint $msix
    if ($LASTEXITCODE -ne 0) { throw 'Certificate-store signing failed.' }
    Export-Certificate -Cert $certificate -FilePath (Join-Path $OutputDirectory 'Fluff-History-Developer-Preview.cer') | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-DeveloperPreview.ps1') -Destination $OutputDirectory
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Uninstall-DeveloperPreview.ps1') -Destination $OutputDirectory
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-ShellIntegration.ps1') -Destination $OutputDirectory
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Uninstall-ShellIntegration.ps1') -Destination $OutputDirectory
    $shellPayload = Join-Path $OutputDirectory 'ShellIntegration'
    New-Item -ItemType Directory -Path $shellPayload | Out-Null
    $propertyDll = Join-Path $shellPayload 'FluffHistory.PropertySheet.dll'
    Copy-Item -LiteralPath (Join-Path $repository 'artifacts\native-shell\FluffHistory.PropertySheet.dll') -Destination $propertyDll
    & $signTool sign /fd SHA256 /s My /sha1 $certificate.Thumbprint $propertyDll
    if ($LASTEXITCODE -ne 0) { throw 'Properties payload signing failed.' }
    Copy-Item -LiteralPath (Join-Path $repository 'artifacts\native-shell\native-build-info.json') -Destination $shellPayload
    New-Item -ItemType Directory -Path (Join-Path $OutputDirectory 'Screenshots') | Out-Null
    [ordered]@{ packageIdentity = 'Sargonphin.FluffHistory.DeveloperPreview'; version = $version; gitCommit = $embeddedSha; uncommittedChanges = $dirty;
        windowsAppSdk = '2.5.1'; dotnetSdk = $sdk; target = 'net10.0-windows10.0.26100.0'; minimumWindows = '10.0.22000.0';
        architecture = 'x64'; windowsAppSdkSelfContained = $true; dotnetSelfContained = $true;
        certificateSubject = $subject; certificateThumbprint = $certificate.Thumbprint;
        msixSha256 = (Get-FileHash -LiteralPath $msix -Algorithm SHA256).Hash;
        propertySheetSha256 = (Get-FileHash -LiteralPath $propertyDll -Algorithm SHA256).Hash;
        propertySheetDeployment = 'Development-only external per-user registration; production installer deferred to M25'
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'build-info.json') -Encoding UTF8
    Write-Output "Signed Developer Preview: $msix"
    Write-Output "Public certificate: $(Join-Path $OutputDirectory 'Fluff-History-Developer-Preview.cer')"
} finally { Pop-Location }
