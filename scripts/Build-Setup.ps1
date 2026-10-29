[CmdletBinding()]
param([string]$OutputDirectory, [string]$DotNet = 'dotnet', [string]$InnoCompiler, [string]$StagingRoot,
    [switch]$AllowUncommitted, [switch]$LocalizationCheckpoint, [string]$ProductVersion, [string]$ReleaseVersion = 'development',
    [switch]$DevelopmentUpdateTesting, [ValidateSet('Development','UnsignedPublic')][string]$SigningMode = 'Development')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ([Environment]::OSVersion.Platform -ne 'Win32NT') { throw 'Setup requires Windows.' }
$repo = Split-Path $PSScriptRoot -Parent
& "$PSScriptRoot\Test-DotNetSdk.ps1" -DotNet $DotNet
Push-Location $repo
try {
    $sha = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -or $sha -notmatch '^[0-9a-f]{40}$') { throw 'Invalid source version.' }
    $dirty = [bool](& git status --porcelain)
    if ($LocalizationCheckpoint -and !$OutputDirectory) { throw 'An incomplete-localization checkpoint requires an explicit output directory.' }
    if ($dirty -and (!$AllowUncommitted -or !$OutputDirectory)) { throw 'Canonical installers require committed source; dirty smoke builds require an explicit empty output directory.' }
    $version = & "$PSScriptRoot\Get-ProductVersion.ps1" -Repository $repo -ProductVersion $ProductVersion
    $release = & "$PSScriptRoot\Get-ReleaseVersion.ps1" -ReleaseVersion $ReleaseVersion -ProductVersion $version
    if ($DevelopmentUpdateTesting -and (!$OutputDirectory -or $release -eq 'development')) { throw 'Update tests require explicit output and controlled release identity.' }
    if ($DevelopmentUpdateTesting -and $SigningMode -ne 'Development') { throw 'Update test signing is restricted to development composition.' }
    if (!$OutputDirectory) { $OutputDirectory = Join-Path (Join-Path $repo 'artifacts\releases') $version }
    $OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
    if ((Test-Path -LiteralPath $OutputDirectory) -and (Get-ChildItem -LiteralPath $OutputDirectory -Force | Select-Object -First 1)) { throw 'Installer archive must be new/empty.' }
    if (!$InnoCompiler) {
        $InnoCompiler = @((Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'), (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 7\ISCC.exe'), (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe')) |
            Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    }
    if (!$InnoCompiler -or (& $InnoCompiler --version) -ne '7.1.0') { throw 'Inno Setup 7.1.0 compiler is required.' }
    . "$repo\installer\WindowsPlatform.ps1"
    $platform = GetWindowsPlatformPolicy (Join-Path $repo 'installer')
    & "$PSScriptRoot\Test-WindowsPlatformPolicy.ps1"
    & "$PSScriptRoot\Test-RecoveryDependencies.ps1" -Repository $repo
    & "$PSScriptRoot\Test-Localization.ps1" -DotNet $DotNet -LocalizationCheckpoint:$LocalizationCheckpoint
    & "$PSScriptRoot\Test-InstallerLifecycle.ps1"
    & "$PSScriptRoot\Test-ClassicVerb.ps1"
    & "$PSScriptRoot\Test-LegacyShellMigration.ps1"
    & "$PSScriptRoot\Test-SetupSigning.ps1"
    & "$PSScriptRoot\Test-VersionPolicy.ps1"
    if (!$StagingRoot) { $StagingRoot = Join-Path $repo 'artifacts\setup' }
    $staging = Join-Path ([IO.Path]::GetFullPath($StagingRoot)) ([guid]::NewGuid().ToString('N'))
    $buildArtifacts = Join-Path $staging 'Build'
    $nativeOutput = Join-Path $staging 'NativeShell'
    & $DotNet restore FluffHistory.sln --locked-mode "-p:ArtifactsPath=$buildArtifacts"
    if ($LASTEXITCODE) { throw 'Locked restore failed.' }
    $distributionTerms = & "$PSScriptRoot\Get-DistributionTerms.ps1" -Repository $repo
    $payload = Join-Path $staging 'Payload'
    $sourceSha = if ($dirty) { "$sha-dirty" } else { $sha }
    $signing = & "$PSScriptRoot\Get-SetupSigning.ps1" -SigningMode $SigningMode
    $certificate = $signing.Certificate
    $signerHash = ''
    if ($DevelopmentUpdateTesting) {
        $hashAlgorithm = [Security.Cryptography.SHA256]::Create()
        try { $signerHash = [BitConverter]::ToString($hashAlgorithm.ComputeHash($certificate.RawData)).Replace('-','') } finally { $hashAlgorithm.Dispose() }
    }
    foreach ($project in @('Desktop','Agent','InstallSupport')) {
        $output = Join-Path $staging $project
        & $DotNet publish "src\FluffHistory.$project\FluffHistory.$project.csproj" -c Release --no-restore "-p:ArtifactsPath=$buildArtifacts" "-p:NativeShellOutputDirectory=$nativeOutput" "-p:ProductVersion=$version" "-p:ReleaseVersion=$release" "-p:DevelopmentUpdateTesting=$($DevelopmentUpdateTesting.ToString().ToLowerInvariant())" "-p:UpdateSignerCertificateSha256=$signerHash" "-p:GitCommit=$sourceSha" -o $output
        if ($LASTEXITCODE) { throw "$project publish failed." }
        New-Item -ItemType Directory -Path $payload -Force | Out-Null
        foreach ($file in Get-ChildItem -LiteralPath $output -File -Recurse) {
            $relative = $file.FullName.Substring($output.Length + 1); $target = Join-Path $payload $relative
            New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
            if (Test-Path -LiteralPath $target) {
                if ((Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) { throw "Conflicting shared publish file: $relative" }
            } else { Copy-Item -LiteralPath $file.FullName -Destination $target }
        }
    }
    Copy-Item -LiteralPath (Join-Path $nativeOutput 'FluffHistory.PropertySheet.dll'),(Join-Path $nativeOutput 'FluffHistory.AgentLauncher.exe'),(Join-Path $nativeOutput 'FluffHistory.ShellLauncher.exe') -Destination $payload
    New-Item -ItemType Directory -Path (Join-Path $payload 'Installer'),(Join-Path $payload 'ThirdPartyNotices') -Force | Out-Null
    Copy-Item -LiteralPath installer\Lifecycle.ps1 -Destination (Join-Path $payload 'Installer')
    Copy-Item -LiteralPath installer\WindowsPlatform.ps1,installer\WindowsPlatform.props -Destination (Join-Path $payload 'Installer')
    Copy-Item -LiteralPath $distributionTerms.File -Destination (Join-Path $payload 'Installer\DistributionTerms.txt')
    Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination (Join-Path $payload 'LICENSE')
    $innoLicense = Join-Path (Split-Path $InnoCompiler -Parent) 'LICENSE.TXT'
    if (Test-Path -LiteralPath $innoLicense) { Copy-Item -LiteralPath $innoLicense -Destination (Join-Path $payload 'ThirdPartyNotices\InnoSetup.txt') }
    $nuget = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
    $notices = Join-Path $payload 'ThirdPartyNotices'
    # Some expression-licensed packages omit the actual license text from their nupkg.
    # Keep version-pinned upstream snapshots/provenance in the source tree for those dependencies.
    foreach ($notice in Get-ChildItem -LiteralPath (Join-Path $repo 'installer\ThirdPartyNotices') -File | Where-Object { $_.Extension -in @('.txt','.rtf') }) {
        Copy-Item -LiteralPath $notice.FullName -Destination $notices
    }
    $runtime = Get-Content -LiteralPath (Join-Path $payload 'FluffHistory.Desktop.runtimeconfig.json') -Raw | ConvertFrom-Json
    $runtimeVersion = ($runtime.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App').version
    $packages = @(@{name='microsoft.netcore.app.runtime.win-x64';version=$runtimeVersion})
    $lock = Get-Content -LiteralPath src\FluffHistory.Desktop\packages.lock.json -Raw | ConvertFrom-Json
    foreach ($framework in $lock.dependencies.PSObject.Properties) {
        foreach ($dependency in $framework.Value.PSObject.Properties) {
            if ($dependency.Value.type -ne 'Project') { $packages += @{name=$dependency.Name.ToLowerInvariant();version=$dependency.Value.resolved} }
        }
    }
    foreach ($package in $packages | Sort-Object { $_.name + '/' + $_.version } -Unique) {
        $directory = Join-Path $nuget ($package.name + '\' + $package.version)
        foreach ($notice in Get-ChildItem -LiteralPath $directory -File | Where-Object { $_.Name -match '(?i)license|notice' -and $_.Extension -in @('','.txt','.md') }) {
            $target = Join-Path $notices ($package.name + '\' + $package.version)
            New-Item -ItemType Directory -Path $target -Force | Out-Null
            Copy-Item -LiteralPath $notice.FullName -Destination $target
        }
    }
    $sdk = Join-Path $nuget 'microsoft.windows.sdk.buildtools\10.0.26100.6584\bin\10.0.26100.0\x64'
    foreach ($file in @(Get-ChildItem -LiteralPath $payload -File | Where-Object { $signing.PeSigning -and ($_.Name -like 'FluffHistory.*.exe' -or $_.Name -like 'FluffHistory.*.dll') })) {
        & "$sdk\signtool.exe" sign /fd SHA256 /s My /sha1 $certificate.Thumbprint $file.FullName
        if ($LASTEXITCODE) { throw 'Product binary signing failed.' }
    }
    [ordered]@{schema=2;version=$version;releaseVersion=$release;developmentUpdateTesting=[bool]$DevelopmentUpdateTesting;gitCommit=$sourceSha;uncommittedChanges=$dirty;localizationCheckpoint=[bool]$LocalizationCheckpoint;architecture='x64';minimumWindows=$platform.ProductMinimum.ToString();
        technicalPlatformMinimum=$platform.TechnicalMinimum.ToString();developmentSigning=$signing.PeSigning;
        signingMode=$SigningMode;productPeSigning=$(if ($signing.PeSigning) {'DevelopmentSelfSigned'} else {'Unsigned'});setupPeSigning=$(if ($signing.PeSigning) {'DevelopmentSelfSigned'} else {'Unsigned'});
        distributionTermsRevision=$distributionTerms.Revision;distributionTermsSha256=$distributionTerms.Sha256;
        peSignerThumbprint=$signing.PeSignerThumbprint;dotnetSdk=(& $DotNet --version);windowsAppSdk='2.5.1';
        dotnetSelfContained=$true;windowsAppSdkSelfContained=$true;innoSetup='7.1.0'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $payload 'build-info.json') -Encoding UTF8
    $files = @(Get-ChildItem -LiteralPath $payload -File -Recurse | Sort-Object FullName | ForEach-Object {
        [ordered]@{path=$_.FullName.Substring($payload.Length+1);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash;length=$_.Length}
    })
    [ordered]@{schema=1;version=$version;files=$files} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $payload 'installed-files.json') -Encoding UTF8
    & "$PSScriptRoot\Test-SetupPayload.ps1" -Payload $payload
    & "$PSScriptRoot\Test-LocalizationRuntime.ps1" -Payload $payload -ScratchDirectory (Join-Path $staging 'LocalizationRuntime') -MakePri (Join-Path $sdk 'makepri.exe') -DotNet $DotNet
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    # Inno writes translation/deprecation warnings to stderr even on success. PS5's
    # native-command error handling must not replace the compiler's actual exit code.
    $compilerOut = Join-Path $staging 'inno-stdout.log'; $compilerErr = Join-Path $staging 'inno-stderr.log'
    $compilerArguments = @("--define=Payload=`"$payload`"", "--define=ProductVersion=$version", "--define=ProductMinimumWindows=$($platform.ProductMinimum.ToString(3))", "--define=ReleaseVersion=$release", "--define=SigningMode=$SigningMode", "--define=DistributionTermsHash=$($distributionTerms.Sha256)", "--output-dir=`"$OutputDirectory`"", "`"$(Join-Path $repo 'installer\FluffHistory.iss')`"")
    $compiler = Start-Process -FilePath $InnoCompiler -ArgumentList $compilerArguments -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput $compilerOut -RedirectStandardError $compilerErr
    Get-Content -LiteralPath $compilerOut | Write-Output
    Get-Content -LiteralPath $compilerErr | ForEach-Object { Write-Warning $_ }
    if ($compiler.ExitCode -ne 0) { throw "Setup compilation failed with exit code $($compiler.ExitCode)." }
    $setup = Join-Path $OutputDirectory 'Fluff-History-Setup.exe'
    if ($signing.PeSigning) {
        & "$sdk\signtool.exe" sign /fd SHA256 /s My /sha1 $certificate.Thumbprint $setup
        if ($LASTEXITCODE) { throw 'Setup signing failed.' }
    } elseif ((Get-AuthenticodeSignature -LiteralPath $setup).Status -ne 'NotSigned') { throw 'Public Setup must be unsigned.' }
    & "$PSScriptRoot\Test-VersionMetadata.ps1" -Path $setup -ProductVersion $version -ReleaseVersion $release
    Copy-Item -LiteralPath (Join-Path $payload 'build-info.json'),(Join-Path $payload 'installed-files.json'),(Join-Path $payload 'LICENSE') -Destination $OutputDirectory
    Copy-Item -LiteralPath $distributionTerms.File -Destination (Join-Path $OutputDirectory 'DistributionTerms.txt')
    Copy-Item -LiteralPath (Join-Path $payload 'ThirdPartyNotices') -Destination $OutputDirectory -Recurse
    $hash = (Get-FileHash -LiteralPath $setup).Hash
    "$hash  Fluff-History-Setup.exe" | Set-Content -LiteralPath (Join-Path $OutputDirectory 'Fluff-History-Setup.exe.sha256') -Encoding ascii
    [ordered]@{schema=2;version=$version;releaseVersion=$release;developmentUpdateTesting=[bool]$DevelopmentUpdateTesting;gitCommit=$sourceSha;setupSha256=$hash;payloadManifestSha256=(Get-FileHash -LiteralPath (Join-Path $payload 'installed-files.json')).Hash;
        agentSha256=(Get-FileHash -LiteralPath (Join-Path $payload 'FluffHistory.Agent.exe')).Hash;
        propertySheetSha256=(Get-FileHash -LiteralPath (Join-Path $payload 'FluffHistory.PropertySheet.dll')).Hash;
        shellLauncherSha256=(Get-FileHash -LiteralPath (Join-Path $payload 'FluffHistory.ShellLauncher.exe')).Hash} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'delivery-info.json') -Encoding UTF8
    Write-Output "Setup archive: $OutputDirectory"
    Write-Output "Setup SHA256: $hash"
} finally { Pop-Location }
