[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('Prepare','Install','Activate','Rollback','Uninstall')][string]$Action,
    [Parameter(Mandatory)][string]$Version, [string]$Fault = '', [string]$ExpectedManifest = '')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (![Environment]::Is64BitProcess) { throw 'The x64 installation requires the native 64-bit lifecycle host.' }
$ProgressPreference = 'SilentlyContinue'
$local = [Environment]::GetFolderPath('LocalApplicationData')
$root = Join-Path $local 'Programs\Fluff-History'
$userRoot = Join-Path $local 'Fluff-History'
$data = Join-Path $userRoot 'AppData'
$records = Join-Path $userRoot 'Installer'
$record = Join-Path $records 'installation-v1.json'
$pending = Join-Path $records 'pending-v1.json'
$packageName = 'Sargonphin.FluffHistory.DeveloperPreview'
$family = 'Sargonphin.FluffHistory.DeveloperPreview_c87380kbqs5fr'
$previewFamily = $family
$publicComposition = $false
$clsid = '{80F01E21-B634-4183-834E-CF09D7242402}'
function IsProductVersion([string]$value) {
    if ($value.Length -gt 23 -or $value -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$' -or $value -eq '0.0.0.0') { return $false }
    foreach ($component in $value.Split('.')) { if ($component.Length -gt 5 -or [int]$component -gt 65535) { return $false } }
    return $true
}
if (!(IsProductVersion $Version)) { throw 'Invalid installation version.' }
if ($Fault -notin @('','Identity','Properties','Startup','Agent','MigrationCopy','Shutdown')) { throw 'Invalid validation fault.' }
$payload = Join-Path $root ('Versions\' + $Version)
$support = Join-Path $payload 'FluffHistory.InstallSupport.exe'
function CheckParents([string]$path) {
    for ($current = [IO.Path]::GetFullPath($path); $current; $current = Split-Path $current -Parent) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Installation paths cannot be reparse points.' }
    }
}
function ReadJson([string]$path, [int]$maximum = 1048576) {
    CheckParents $path
    $file = Get-Item -LiteralPath $path -Force
    if ($file.PSIsContainer -or $file.Length -gt $maximum) { throw 'Invalid installer metadata.' }
    return Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
}
function WriteJson([string]$path, $value) {
    CheckParents $path
    $temporary = $path + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    try { $value | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $temporary -Encoding UTF8; Move-Item -LiteralPath $temporary -Destination $path -Force }
    finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
}
function RunSupport {
    param([string[]]$arguments)
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $support; $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.Arguments = ($arguments | ForEach-Object { if ($_ -match '["\r\n]') { throw 'Invalid lifecycle argument.' }; '"' + $_ + '"' }) -join ' '
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (!$process.WaitForExit(45000)) { throw ('Application lifecycle request timed out (' + $arguments[0] + '). Close active operations and retry.') }
        if ($process.ExitCode -ne 0) { throw ('Application lifecycle request failed (' + $arguments[0] + ', exit ' + $process.ExitCode + '). Close active operations and retry.') }
    }
    finally { $process.Dispose() }
}
function DevelopmentTrust([ValidateSet('Trust','RemoveIfUnused')][string]$mode, [string]$certificatePath, [string]$thumbprint) {
    if ($thumbprint -notmatch '^[0-9A-F]{40}$') { throw 'Invalid development certificate identity.' }
    $certificate = New-Object Security.Cryptography.X509Certificates.X509Certificate2 $certificatePath
    if ($certificate.Thumbprint -ne $thumbprint -or $certificate.Subject -ne 'CN=Fluff-History Developer Preview') { throw 'Unexpected development certificate.' }
    $machinePath = 'Cert:\LocalMachine\TrustedPeople\' + $thumbprint
    if ($mode -eq 'Trust' -and (Test-Path -LiteralPath $machinePath)) { return }
    $ownership = 'HKLM:\SOFTWARE\Fluff-History\DevelopmentTrust\' + $thumbprint
    if ($mode -eq 'RemoveIfUnused' -and !(Test-Path -LiteralPath $ownership)) { return }
    # Only the exact public DER and a fixed mode cross the elevation boundary. The elevated child
    # performs no package/HKCU registration, profile access, app launch or external-script loading.
    $script = @'
$ErrorActionPreference='Stop'
$bytes=[Convert]::FromBase64String('__DER__')
$certificate=New-Object Security.Cryptography.X509Certificates.X509Certificate2 (,$bytes)
if($certificate.Thumbprint -ne '__THUMB__' -or $certificate.Subject -ne 'CN=Fluff-History Developer Preview'){exit 1}
$sha=[BitConverter]::ToString(([Security.Cryptography.SHA256]::Create()).ComputeHash($bytes)).Replace('-','')
$registry=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,[Microsoft.Win32.RegistryView]::Registry64)
$keyName='SOFTWARE\Fluff-History\DevelopmentTrust\__THUMB__'
$store=New-Object Security.Cryptography.X509Certificates.X509Store 'TrustedPeople','LocalMachine'
try {
 $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
 $matching=@($store.Certificates.Find([Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,'__THUMB__',$false))
 foreach($item in $matching){if([Convert]::ToBase64String($item.RawData) -ne '__DER__'){exit 1}}
 if('__MODE__' -eq 'Trust') {
  if(!$matching.Count) {
   $store.Add($certificate)
   $key=$registry.CreateSubKey($keyName)
   try{$key.SetValue('Owned',1,[Microsoft.Win32.RegistryValueKind]::DWord);$key.SetValue('CertificateSha256',$sha)}finally{$key.Dispose()}
  }
 } else {
  $key=$registry.OpenSubKey($keyName)
  if(!$key){exit 0}
  try{$owned=$key.GetValue('Owned') -eq 1 -and $key.GetValue('CertificateSha256') -eq $sha}finally{$key.Dispose()}
  if(!$owned){exit 1}
  # Do not remove shared machine trust while any user's same-publisher package still needs it.
  Import-Module (Join-Path $PSHOME 'Modules\Appx\Appx.psd1') -Force
  if(@(Get-AppxPackage -AllUsers | Where-Object Publisher -eq 'CN=Fluff-History Developer Preview').Count){exit 0}
  foreach($item in $matching){$store.Remove($item)}
  $registry.DeleteSubKey($keyName)
 }
} finally {$store.Close();$registry.Dispose()}
exit 0
'@
    $script = $script.Replace('__DER__',[Convert]::ToBase64String($certificate.RawData)).Replace('__THUMB__',$thumbprint).Replace('__MODE__',$mode)
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script))
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\powershell.exe'
    $start.Arguments = '-NoLogo -NoProfile -NonInteractive -EncodedCommand ' + $encoded
    $start.UseShellExecute = $true; $start.Verb = 'runas'; $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $process = [Diagnostics.Process]::Start($start)
    try { if (!$process.WaitForExit(120000) -or $process.ExitCode -ne 0) { throw 'Development certificate trust could not be completed.' } }
    finally { $process.Dispose() }
    if ($mode -eq 'Trust' -and !(Test-Path -LiteralPath $machinePath)) { throw 'Windows did not confirm the exact development trust.' }
}
function OwnedProcess($process) {
    try {
        $path = $process.Path
        return $path.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase) -or
            $path.StartsWith((Join-Path ${env:ProgramFiles} ('WindowsApps\' + $packageName + '_')), [StringComparison]::OrdinalIgnoreCase)
    } catch { return $false }
}
function StopComponents {
    if ($Fault -eq 'Shutdown') { throw 'Controlled shutdown refusal.' }
    $deadline = (Get-Date).AddSeconds(30)
    do {
        $processes = @(Get-Process FluffHistory.Desktop -ErrorAction SilentlyContinue | Where-Object { OwnedProcess $_ })
        if (!$processes.Count) { break }
        foreach ($process in $processes) { if ($process.MainWindowHandle -ne 0) { [void]$process.CloseMainWindow() } }
        Start-Sleep -Milliseconds 200
    } while ((Get-Date) -lt $deadline)
    if ($processes.Count) { throw 'Close Fluff-History and its Properties pages before continuing. No process was terminated.' }
    # The old support executable remains valid before an upgrade replaces the active version.
    $existingSupport = $null
    if (Test-Path -LiteralPath $record) {
        $installed = ReadJson $record
        if (!(IsProductVersion $installed.version) -or $installed.root -ne $root) { throw 'Unrecognized installation metadata.' }
        $existingSupport = Join-Path $root ('Versions\' + $installed.version + '\FluffHistory.InstallSupport.exe')
    }
    if ($existingSupport -and (Test-Path -LiteralPath $existingSupport)) {
        $savedSupport = $script:support; $script:support = $existingSupport
        try { RunSupport -arguments @('--stop-agent') } finally { $script:support = $savedSupport }
    }
}
function BackupState {
    $backup = Join-Path $records ('Migration\' + $Version)
    CheckParents $backup; New-Item -ItemType Directory -Path $backup -Force | Out-Null
    if ($Fault -eq 'MigrationCopy') { throw 'Controlled migration copy failure.' }
    foreach ($origin in @(@{ name='canonical'; directory=$data }, @{ name='legacy'; directory=(Join-Path $local ('Packages\' + $previewFamily + '\LocalState')) })) {
        foreach ($file in @(@{name='settings-v2.json';maximum=4194304},@{name='preferences-v1.json';maximum=16384})) {
            $source = Join-Path $origin.directory $file.name
            if (!(Test-Path -LiteralPath $source)) { continue }
            CheckParents $source
            if ((Get-Item -LiteralPath $source).Length -gt $file.maximum) { throw 'Application state exceeds its bound; it was preserved.' }
            $target = Join-Path $backup ($origin.name + '-' + $file.name)
            if (!(Test-Path -LiteralPath $target)) { Copy-Item -LiteralPath $source -Destination $target }
        }
    }
    return $backup
}
function Inventory([string]$directory, [string]$versionName, [string]$manifestHash) {
    CheckParents $directory
    $manifestPath = Join-Path $directory 'installed-files.json'
    if ($manifestHash -notmatch '^[0-9A-F]{64}$' -or (Get-FileHash -LiteralPath $manifestPath).Hash -ne $manifestHash) { throw 'Owned payload inventory changed.' }
    $manifest = ReadJson $manifestPath 4194304
    if ($manifest.schema -ne 1 -or $manifest.version -ne $versionName -or @($manifest.files).Count -lt 1 -or @($manifest.files).Count -gt 10000) { throw 'Payload inventory is invalid.' }
    $files = New-Object 'Collections.Generic.Dictionary[string,object]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $manifest.files) {
        if (!$file.path -or $file.path.Length -gt 512 -or $file.path -match '(^[\\/]|:|[\x00-\x1f]|(^|[\\/])\.\.?([\\/]|$)|[. ]($|[\\/]))' -or
            $file.sha256 -notmatch '^[0-9A-F]{64}$' -or $file.length -lt 0 -or $file.path -eq 'installed-files.json') { throw 'Invalid payload path/hash.' }
        $relative = $file.path.Replace('/','\')
        if ($files.ContainsKey($relative)) { throw 'Duplicate payload path.' }
        $files.Add($relative,$file)
    }
    return ,$files
}
function CheckVersion([string]$directory, [string]$versionName, [string]$manifestHash, [bool]$allowMissing = $false) {
    $files = Inventory $directory $versionName $manifestHash
    $directories = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($relative in $files.Keys) {
        for ($parent = Split-Path $relative -Parent; $parent; $parent = Split-Path $parent -Parent) { [void]$directories.Add($parent) }
    }
    foreach ($child in Get-ChildItem -LiteralPath $directory -Directory -Recurse -Force) {
        if (!$directories.Contains($child.FullName.Substring($directory.Length+1))) { throw 'The installation directory contains an unexpected directory.' }
    }
    $actual = @(Get-ChildItem -LiteralPath $directory -File -Recurse -Force)
    if ($actual.Count -gt 10001) { throw 'Too many installed files.' }
    foreach ($file in $actual) {
        $relative = $file.FullName.Substring($directory.Length+1)
        if ($relative -eq 'installed-files.json') { continue }
        if (!$files.ContainsKey($relative)) { throw 'The installation directory contains unexpected content.' }
        $expected = $files[$relative]; CheckParents $file.FullName
        if ($file.Length -ne $expected.length -or (Get-FileHash -LiteralPath $file.FullName).Hash -ne $expected.sha256) { throw 'Installed payload hash mismatch.' }
    }
    if (!$allowMissing -and $actual.Count -ne $files.Count+1) { throw 'Installed payload is incomplete.' }
}
function CheckPayload {
    CheckTree $payload
    $transaction = ReadJson $pending
    CheckVersion $payload $Version $transaction.manifestSha256
}
function CheckOwnedVersions {
    $known = @{}
    $activeVersion = ''
    if (Test-Path -LiteralPath $record) {
        $installed = ReadJson $record
        if ($installed.schema -ne 1 -or $installed.root -ne $root -or @($installed.knownVersions).Count -gt 32) { throw 'Invalid owned installation metadata.' }
        $activeVersion = $installed.version
        foreach ($entry in $installed.knownVersions) { $known[$entry.version] = $entry.manifestSha256 }
    }
    if (Test-Path -LiteralPath $pending) {
        $transaction = ReadJson $pending
        if ($transaction.schema -ne 1 -or $transaction.root -ne $root -or !(IsProductVersion $transaction.version)) { throw 'Invalid pending installation metadata.' }
        $known[$transaction.version] = $transaction.manifestSha256
    }
    $versions = Join-Path $root 'Versions'
    $deferred = @()
    $marker = Join-Path $records 'deferred-shell-v1.json'
    if (Test-Path -LiteralPath $marker) { $deferred = @((ReadJson $marker 65536).files | ForEach-Object path) }
    if (Test-Path -LiteralPath $versions) {
        foreach ($directory in Get-ChildItem -LiteralPath $versions -Force) {
            if (!$directory.PSIsContainer -or !(IsProductVersion $directory.Name)) { throw 'An unowned deployment directory was preserved.' }
            if ($known.ContainsKey($directory.Name)) { CheckVersion $directory.FullName $directory.Name $known[$directory.Name] ($directory.Name -ne $activeVersion) }
            elseif (@(Get-ChildItem -LiteralPath $directory.FullName -Force | Where-Object { $_.PSIsContainer -or $_.FullName -notin $deferred }).Count) {
                throw 'An unowned deployment directory was preserved.'
            }
        }
    }
}
function ProductPackage {
    $packages = @(Get-AppxPackage -Name $packageName | Where-Object PackageFamilyName -eq $family)
    if ($packages.Count -gt 1) { throw 'Multiple product package identities require attention.' }
    if ($packages.Count) { return $packages[0] }
    return $null
}
function RegisterProductIdentity($info, $transaction) {
    # Invalid pins are fatal; Windows registration failure is an optional-integration outcome.
    if ($publicComposition) {
        $identity = GetSparseIdentityPolicy $PSScriptRoot
        try {
            if ($identity.Policy.packageName -cne $info.packageIdentity -or $identity.Policy.packageFamilyName -cne $info.packageFamily -or
                $identity.Policy.certificateThumbprint -cne $info.certificateThumbprint) { throw 'Sparse identity metadata/pin mismatch.' }
        } finally { $identity.Certificate.Dispose() }
    }
    $existing = ProductPackage
    if ($existing -and [version]$existing.Version -gt [version]$Version) { throw 'A newer product identity is installed.' }
    if ($existing -and [version]$existing.Version -eq [version]$Version -and (!$transaction.previous -or $transaction.previous.version -ne $Version)) { throw 'An unrecognized same-version package is already registered.' }
    if ($publicComposition -and $existing -and !$transaction.previous) { throw 'An unowned sparse identity was preserved.' }
    try {
        if ($Fault -eq 'Identity') { throw 'Controlled identity registration failure.' }
        if ($publicComposition) { SparseIdentityTrust 'Trust' $PSScriptRoot }
        if (!$existing -or [version]$existing.Version -ne [version]$Version) {
            StopComponents
            MarkPending 'identityChanged' $true
            Add-AppxPackage -Path (Join-Path $payload 'Identity.msix') -ExternalLocation $payload
        }
        $confirmed = ProductPackage
        if (!$confirmed -or [version]$confirmed.Version -ne [version]$Version) { throw 'Sparse identity registration was not confirmed.' }
        if (Test-Path -LiteralPath (Join-Path $records 'integration-warning.txt')) { Remove-Item -LiteralPath (Join-Path $records 'integration-warning.txt') }
        return $true
    } catch {
        if (!$publicComposition) { throw }
        # Never leave an old command pointing at a payload eligible for retirement.
        $current = ProductPackage
        if ($current) {
            if ([version]$current.Version -ne [version]$Version -and
                (!$transaction.previous -or [version]$current.Version -ne [version]$transaction.previous.version)) { throw 'Unexpected sparse identity was preserved.' }
            MarkPending 'identityChanged' $true
            $current | Remove-AppxPackage
            if (ProductPackage) { throw 'Failed sparse registration could not be retired safely.' }
        }
        [IO.File]::WriteAllText((Join-Path $records 'integration-warning.txt'),
            'Modern Explorer context command unavailable; application, Agent and native Properties integration remain installed. Run Setup again to retry.',[Text.Encoding]::UTF8)
        return $false
    }
}
function RemoveVersionMarker([string]$ExpectedVersion = $Version) {
    if (!(IsProductVersion $ExpectedVersion)) { throw 'Invalid active version.' }
    $marker = Join-Path $root 'current-version.txt'; CheckParents $marker
    if (Test-Path -LiteralPath $marker) {
        if ((Get-Item -LiteralPath $marker).Length -gt 64 -or [IO.File]::ReadAllText($marker) -ne $ExpectedVersion) { throw 'An unexpected active-version marker was preserved.' }
        Remove-Item -LiteralPath $marker
    }
}
function NotifyShell {
    if (!('FluffInstallerShellNotify' -as [type])) { Add-Type 'using System;using System.Runtime.InteropServices;public static class FluffInstallerShellNotify{[DllImport("shell32.dll")]public static extern void SHChangeNotify(uint e,uint f,IntPtr a,IntPtr b);}' }
    [FluffInstallerShellNotify]::SHChangeNotify(0x08000000,0,[IntPtr]::Zero,[IntPtr]::Zero)
}
function RegisterProperties {
    param([string]$RestoreDll = '')
    $dll = Join-Path $payload 'FluffHistory.PropertySheet.dll'
    if ($RestoreDll) { $dll = $RestoreDll }
    $classes = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\Classes')
    try {
        $key = $classes.CreateSubKey('CLSID\' + $clsid + '\InprocServer32')
        try { $key.SetValue('', $dll); $key.SetValue('ThreadingModel','Apartment') } finally { $key.Dispose() }
        foreach ($scope in @('*','Directory')) { $key = $classes.CreateSubKey($scope + '\shellex\PropertySheetHandlers\Fluff-History'); try { $key.SetValue('', $clsid) } finally { $key.Dispose() } }
    } finally { $classes.Dispose() }
    NotifyShell
}
function MarkPending([string]$name, $value) {
    $transaction = ReadJson $pending
    if ($transaction.root -ne $root -or $transaction.version -ne $Version) { throw 'Unexpected pending installation.' }
    $transaction | Add-Member -NotePropertyName $name -NotePropertyValue $value -Force
    WriteJson $pending $transaction
}
function CheckTree([string]$directory) {
    if (!(Test-Path -LiteralPath $directory)) { return }
    $stack = New-Object 'Collections.Generic.Stack[string]'; $stack.Push($directory); $count = 0
    while ($stack.Count) {
        foreach ($entry in Get-ChildItem -LiteralPath $stack.Pop() -Force) {
            $count++; if ($count -gt 20000 -or ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Unexpected or reparse installation content.' }
            if ($entry.PSIsContainer) { $stack.Push($entry.FullName) }
        }
    }
}
function RememberShellFiles([string]$OnlyVersion = '') {
    $owned = @()
    $marker = Join-Path $records 'deferred-shell-v1.json'
    if (Test-Path -LiteralPath $marker) { $owned = @((ReadJson $marker 65536).files) }
    if (Test-Path -LiteralPath $record) {
        $installed = ReadJson $record
        foreach ($entry in @($installed.knownVersions)) {
            if ($OnlyVersion -and $entry.version -ne $OnlyVersion) { continue }
            if (!(IsProductVersion $entry.version)) { throw 'Invalid owned version.' }
            $directory = Join-Path $root ('Versions\' + $entry.version); CheckParents $directory
            $names = @('FluffHistory.PropertySheet.dll','FluffHistory.ExplorerCommand.dll')
            if ($OnlyVersion) { $names += 'installed-files.json' }
            foreach ($name in $names) {
                $file = Join-Path $directory $name
                if (Test-Path -LiteralPath $file) { $owned += [ordered]@{path=$file;sha256=(Get-FileHash -LiteralPath $file).Hash} }
            }
        }
    }
    # Property-name grouping does not read ordered-dictionary keys in Windows PowerShell 5.1.
    # Use an explicit key expression so each retained file keeps its own ownership record.
    $owned = @($owned | Group-Object { $_.path } | ForEach-Object { $_.Group[0] })
    if ($owned.Count -gt 96) { throw 'Too many retained Shell images. Close Properties windows and retry.' }
    WriteJson $marker ([ordered]@{schema=1;root=$root;files=$owned})
}
function RetireOldPayloads {
    $installed = ReadJson $record
    foreach ($entry in @($installed.knownVersions)) {
        if ($entry.version -eq $Version) { continue }
        $directory = Join-Path $root ('Versions\' + $entry.version)
        CheckTree $directory; CheckVersion $directory $entry.version $entry.manifestSha256 $true
        $files = Inventory $directory $entry.version $entry.manifestSha256
        foreach ($relative in $files.Keys) {
            if ($relative -in @('FluffHistory.PropertySheet.dll','FluffHistory.ExplorerCommand.dll')) { continue }
            $file = Join-Path $directory $relative
            if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file }
        }
        # Shell images may remain mapped. Persist their exact hash-bound names before retiring the inventory.
        RememberShellFiles $entry.version
        foreach ($child in Get-ChildItem -LiteralPath $directory -Directory -Recurse -Force | Sort-Object { $_.FullName.Length } -Descending) {
            if (!(Get-ChildItem -LiteralPath $child.FullName -Force | Select-Object -First 1)) { Remove-Item -LiteralPath $child.FullName }
        }
        $installed.knownVersions = @($installed.knownVersions | Where-Object version -ne $entry.version)
        WriteJson $record $installed
        CleanupDeferred
    }
}
function CleanupDeferred {
    $marker = Join-Path $records 'deferred-shell-v1.json'
    if (!(Test-Path -LiteralPath $marker)) { return }
    $deferred = ReadJson $marker 65536
    if ($deferred.schema -ne 1 -or $deferred.root -ne $root -or @($deferred.files).Count -gt 96) { throw 'Invalid deferred cleanup marker.' }
    $activeVersion = if (Test-Path -LiteralPath $record) { (ReadJson $record).version } else { '' }
    $remaining = @()
    foreach ($file in $deferred.files) {
        $parent = Split-Path $file.path -Parent; $leaf = Split-Path $file.path -Leaf; $versionName = Split-Path $parent -Leaf
        if (!(IsProductVersion $versionName) -or $parent -ne (Join-Path $root ('Versions\' + $versionName)) -or
            $leaf -notin @('FluffHistory.PropertySheet.dll','FluffHistory.ExplorerCommand.dll','installed-files.json') -or $file.sha256 -notmatch '^[0-9A-F]{64}$') { throw 'Deferred cleanup path is not owned.' }
        if ($versionName -eq $activeVersion) { $remaining += $file; continue }
        CheckParents $file.path
        if (!(Test-Path -LiteralPath $file.path)) { continue }
        if ((Get-FileHash -LiteralPath $file.path).Hash -ne $file.sha256) { throw 'Deferred Shell image changed; it was preserved.' }
        try { Remove-Item -LiteralPath $file.path } catch [IO.IOException] { $remaining += $file; continue }
        if (!(Get-ChildItem -LiteralPath $parent -Force | Select-Object -First 1)) { Remove-Item -LiteralPath $parent }
    }
    WriteJson $marker ([ordered]@{schema=1;root=$root;files=$remaining})
}
function RemoveProperties {
    $classes = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Classes',$true)
    if (!$classes) { return }
    try {
        $key = $classes.OpenSubKey('CLSID\' + $clsid + '\InprocServer32')
        $dll = $null; if ($key) { try { $dll = [string]$key.GetValue('') } finally { $key.Dispose() } }
        if ($dll -and !$dll.StartsWith($root + '\Versions\',[StringComparison]::OrdinalIgnoreCase) -and !$dll.StartsWith((Join-Path $userRoot 'ShellIntegration') + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'An unrelated Properties registration was preserved.' }
        foreach ($scope in @('*','Directory')) {
            $name = $scope + '\shellex\PropertySheetHandlers\Fluff-History'; $key = $classes.OpenSubKey($name)
            if ($key) { try { $owned = $key.GetValue('') -eq $clsid } finally { $key.Dispose() }; if (!$owned) { throw 'An unrelated handler was preserved.' }; $classes.DeleteSubKey($name) }
        }
        if ($dll) { $classes.DeleteSubKeyTree('CLSID\' + $clsid) }
    } finally { $classes.Dispose() }
    NotifyShell
}
CheckParents $root; CheckParents $records
New-Item -ItemType Directory -Path $records -Force | Out-Null
try {
    $buildPath = Join-Path $payload 'build-info.json'
    if (Test-Path -LiteralPath $buildPath) {
        $composition = ReadJson $buildPath
        if ($composition.PSObject.Properties.Name -contains 'signingMode' -and $composition.signingMode -eq 'UnsignedPublic') {
            $publicComposition = $true
            $packageName = 'Sargonphin.FluffHistory'; $family = 'Sargonphin.FluffHistory_53b6jha2b007a'
            # Prepare runs the extracted lifecycle before payload installation. It only
            # inspects existing metadata; trust operations run from the installed helpers.
            if ($Action -ne 'Prepare') { . (Join-Path $PSScriptRoot 'SparseIdentityTrust.ps1') }
        }
    }
    switch ($Action) {
        'Prepare' {
            if ((New-Object Security.Principal.WindowsPrincipal ([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
                throw 'Run Setup normally from the Windows account that will use Fluff-History, without Run as administrator.'
            }
            CleanupDeferred
            CheckTree $root
            CheckOwnedVersions
            if (Test-Path -LiteralPath $root) {
                $unexpected = @(Get-ChildItem -LiteralPath $root -Force | Where-Object { $_.Name -notin @('Versions','FluffHistory.Agent.exe','current-version.txt','unins000.exe','unins000.dat','unins000.msg') })
                if ($unexpected.Count) { throw 'The installation directory contains unexpected content.' }
                if (!(Test-Path -LiteralPath $record) -and (Get-ChildItem -LiteralPath $root -Force | Select-Object -First 1) -and !(Test-Path -LiteralPath $pending)) {
                    $deferredMarker = Join-Path $records 'deferred-shell-v1.json'
                    if (!(Test-Path -LiteralPath $deferredMarker)) { throw 'An existing nonempty directory is not an owned installation.' }
                    $allowed = @((ReadJson $deferredMarker).files | ForEach-Object path)
                    $unowned = @(Get-ChildItem -LiteralPath $root -File -Recurse | Where-Object { $_.FullName -notin $allowed })
                    if ($unowned.Count) { throw 'An existing nonempty directory is not an owned installation.' }
                }
            }
            StopComponents
            $backup = BackupState
            if (!$ExpectedManifest) { throw 'Setup did not provide its payload inventory.' }
            $expected = ReadJson $ExpectedManifest 4194304
            if ($expected.schema -ne 1 -or $expected.version -ne $Version) { throw 'Unexpected setup payload inventory.' }
            $previous = if (Test-Path -LiteralPath $record) { ReadJson $record } else { $null }
            $previousDll = [Microsoft.Win32.Registry]::GetValue('HKEY_CURRENT_USER\Software\Classes\CLSID\' + $clsid + '\InprocServer32','','')
            $previousRun = [Microsoft.Win32.Registry]::GetValue('HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run','Fluff-History',$null)
            WriteJson $pending ([ordered]@{schema=1;version=$Version;root=$root;manifestSha256=(Get-FileHash -LiteralPath $ExpectedManifest).Hash;backup=$backup;previous=$previous;previousDll=$previousDll;previousRun=$previousRun;identityChanged=$false;trustAdded=$false;startupAdded=$false})
        }
        'Install' {
            CheckPayload; RunSupport -arguments @('--validate-state',$data)
            $legacy = Join-Path $local ('Packages\' + $previewFamily + '\LocalState')
            if (Test-Path -LiteralPath $legacy) { RunSupport -arguments @('--validate-state',$legacy) }
            New-Item -ItemType Directory -Path $data -Force | Out-Null
            foreach ($name in @('settings-v2.json','preferences-v1.json')) {
                $target = Join-Path $data $name; $source = Join-Path $legacy $name
                if (!(Test-Path -LiteralPath $target) -and (Test-Path -LiteralPath $source)) { CheckParents $source; Copy-Item -LiteralPath $source -Destination $target }
            }
            $info = ReadJson (Join-Path $payload 'build-info.json')
            $transaction = ReadJson $pending; $trusted = $false
            if ($info.developmentSigning) {
                $certificate = New-Object Security.Cryptography.X509Certificates.X509Certificate2 (Join-Path $payload 'identity.cer')
                if ($certificate.Thumbprint -ne $info.certificateThumbprint -or $certificate.Subject -ne 'CN=Fluff-History Developer Preview') { throw 'Unexpected development trust payload.' }
                $alreadyTrusted = Test-Path -LiteralPath ('Cert:\LocalMachine\TrustedPeople\' + $certificate.Thumbprint)
                DevelopmentTrust 'Trust' (Join-Path $payload 'identity.cer') $certificate.Thumbprint
                $trusted = Test-Path -LiteralPath ('HKLM:\SOFTWARE\Fluff-History\DevelopmentTrust\' + $certificate.Thumbprint)
                if (!$alreadyTrusted) { MarkPending 'trustAdded' $true; MarkPending 'certificateThumbprint' $certificate.Thumbprint }
            }
            $modernContextMenu = RegisterProductIdentity $info $transaction
            if ($publicComposition -and $transaction.previous -and
                ($transaction.previous.PSObject.Properties.Name -notcontains 'packageIdentity' -or $transaction.previous.packageIdentity -eq 'Sargonphin.FluffHistory.DeveloperPreview')) {
                $oldPreview = @(Get-AppxPackage -Name 'Sargonphin.FluffHistory.DeveloperPreview' | Where-Object PackageFamilyName -eq $previewFamily)
                foreach ($old in $oldPreview) {
                    if ([version]$old.Version -ne [version]$transaction.previous.version) { throw 'Unexpected preview identity was preserved.' }
                    MarkPending 'previewIdentityRemoved' $true
                    $old | Remove-AppxPackage
                }
            }
            if ($Fault -eq 'Properties') { throw 'Controlled Properties registration failure.' }
            RemoveProperties; RegisterProperties
            if ($Fault -eq 'Startup') { throw 'Controlled startup registration failure.' }
            $startup = '"' + (Join-Path $root 'FluffHistory.Agent.exe') + '"'
            if ($startup.Length -gt 260) { throw 'Startup command exceeds the Windows bound.' }
            $run = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\Microsoft\Windows\CurrentVersion\Run')
            try {
                $old = $run.GetValue('Fluff-History')
                if ($old -and $old -ne $startup) { throw 'An unrelated startup value was preserved.' }
                if (!$old) { $run.SetValue('Fluff-History',$startup); MarkPending 'startupAdded' $true }
            } finally { $run.Dispose() }
            [IO.File]::WriteAllText((Join-Path $root 'current-version.txt'),$Version,[Text.Encoding]::ASCII)
            $known = @()
            if ($transaction.previous) {
                $known = @($transaction.previous.knownVersions | Where-Object { $_.version -ne $Version })
                if ($transaction.previous.certificateThumbprint -eq $info.certificateThumbprint -and $transaction.previous.ownedDevelopmentTrust) { $trusted = $true }
            }
            $manifestHash = (Get-FileHash -LiteralPath (Join-Path $payload 'installed-files.json')).Hash
            $known += [ordered]@{version=$Version;manifestSha256=$manifestHash}
            if ($known.Count -gt 32) { throw 'Too many retained deployment versions.' }
            $releaseVersion = if ($info.PSObject.Properties.Name -contains 'releaseVersion') { $info.releaseVersion } else { 'development' }
            $ownedSparse = $false
            if ($publicComposition) {
                $publicIdentity = GetSparseIdentityPolicy $PSScriptRoot
                try { $ownedSparse = Test-Path -LiteralPath ('HKLM:\Software\Fluff-History\SparseIdentityTrust\' + $publicIdentity.Policy.certificateSha256) }
                finally { $publicIdentity.Certificate.Dispose() }
            }
            WriteJson $record ([ordered]@{schema=1;version=$Version;releaseVersion=$releaseVersion;root=$root;manifestSha256=$manifestHash;knownVersions=$known;certificateThumbprint=$info.certificateThumbprint;ownedDevelopmentTrust=$trusted;ownedSparseTrust=$ownedSparse;packageIdentity=$packageName;packageFamily=$family;modernContextMenuAvailable=$modernContextMenu;activated=$false})
        }
        'Activate' {
            if ($Fault -eq 'Agent') { throw 'Controlled agent first-launch failure.' }
            RunSupport -arguments @('--agent-ready',$Version)
            $installed = ReadJson $record; $installed.activated = $true; WriteJson $record $installed
            if (Test-Path -LiteralPath $pending) { Remove-Item -LiteralPath $pending }
            if (Test-Path -LiteralPath (Join-Path $records 'last-error.txt')) { Remove-Item -LiteralPath (Join-Path $records 'last-error.txt') }
            # Only old, hash-validated payloads are eligible. A sharing refusal leaves files for a later setup.
            try { RetireOldPayloads } catch [IO.IOException] { }
        }
        'Rollback' {
            # Recoverable failure: remove integration before Inno rolls back new files. Retain state backup.
            if (Test-Path -LiteralPath $support) { RunSupport -arguments @('--stop-agent') }
            $transaction = ReadJson $pending
            $currentDll = [Microsoft.Win32.Registry]::GetValue('HKEY_CURRENT_USER\Software\Classes\CLSID\' + $clsid + '\InprocServer32','','')
            if ($currentDll -eq (Join-Path $payload 'FluffHistory.PropertySheet.dll')) { RemoveProperties }
            $existing = ProductPackage
            if ($transaction.identityChanged -and $existing -and [version]$existing.Version -eq [version]$Version) { $existing | Remove-AppxPackage }
            $run = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run',$true)
            if ($run) { try { if ($transaction.startupAdded -and $run.GetValue('Fluff-History') -eq ('"' + (Join-Path $root 'FluffHistory.Agent.exe') + '"')) { $run.DeleteValue('Fluff-History') } } finally { $run.Dispose() } }
            if ($transaction.previous) {
                $previousPayload = Join-Path $root ('Versions\' + $transaction.previous.version)
                CheckParents $previousPayload
                if ($transaction.identityChanged -or ($transaction.PSObject.Properties.Name -contains 'previewIdentityRemoved' -and $transaction.previewIdentityRemoved)) { Add-AppxPackage -Path (Join-Path $previousPayload 'Identity.msix') -ExternalLocation $previousPayload }
                [IO.File]::WriteAllText((Join-Path $root 'current-version.txt'),$transaction.previous.version,[Text.Encoding]::ASCII)
                WriteJson $record $transaction.previous
            } else {
                if (Test-Path -LiteralPath $record) { Remove-Item -LiteralPath $record }
                RemoveVersionMarker
            }
            if ($transaction.previousDll -and (Test-Path -LiteralPath $transaction.previousDll)) { RegisterProperties $transaction.previousDll }
            if ($transaction.trustAdded) {
                DevelopmentTrust 'RemoveIfUnused' (Join-Path $payload 'identity.cer') $transaction.certificateThumbprint
            }
            if ($publicComposition) { SparseIdentityTrust 'RemoveIfUnused' $PSScriptRoot }
        }
        'Uninstall' {
            CheckTree $root
            CheckOwnedVersions
            StopComponents
            RememberShellFiles
            RemoveProperties
            $existing = ProductPackage
            if ($existing) { $existing | Remove-AppxPackage }
            $run = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run',$true)
            if ($run) { try { if ($run.GetValue('Fluff-History') -eq ('"' + (Join-Path $root 'FluffHistory.Agent.exe') + '"')) { $run.DeleteValue('Fluff-History') } } finally { $run.Dispose() } }
            $activeVersion = $Version
            if (Test-Path -LiteralPath $record) {
                $installed = ReadJson $record
                $activeVersion = $installed.version
                if ($installed.ownedDevelopmentTrust -and $installed.certificateThumbprint -match '^[0-9A-F]{40}$') {
                    $activeCertificate = Join-Path $root ('Versions\' + $activeVersion + '\identity.cer')
                    DevelopmentTrust 'RemoveIfUnused' $activeCertificate $installed.certificateThumbprint
                }
                if ($publicComposition) { SparseIdentityTrust 'RemoveIfUnused' $PSScriptRoot }
                Remove-Item -LiteralPath $record
            }
            # A failed upgrade can leave a newer Inno uninstaller while registration rolled back.
            RemoveVersionMarker $activeVersion
            if (Test-Path -LiteralPath (Join-Path $records 'integration-warning.txt')) { Remove-Item -LiteralPath (Join-Path $records 'integration-warning.txt') }
            if (Test-Path -LiteralPath $pending) {
                $transaction = ReadJson $pending
                if ($transaction.root -ne $root -or $transaction.version -ne $Version) { throw 'Unexpected pending installation was preserved.' }
                Remove-Item -LiteralPath $pending
            }
        }
    }
    exit 0
} catch {
    # Installer diagnostics only, bounded and never containing configuration contents or credentials.
    $message = ('Fluff-History lifecycle failed during ' + $Action + ': ' + $_.Exception.Message) -replace '[\r\n\p{Cc}]',' '
    if ($message.Length -gt 2048) { $message = $message.Substring(0,2048) }
    try { [IO.File]::WriteAllText((Join-Path $records 'last-error.txt'),$message,[Text.Encoding]::UTF8) } catch { }
    Write-Error $message -ErrorAction Continue
    exit 1
}
