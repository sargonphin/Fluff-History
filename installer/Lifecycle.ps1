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
$previewFamily = 'Sargonphin.FluffHistory.DeveloperPreview_c87380kbqs5fr'
$clsid = '{80F01E21-B634-4183-834E-CF09D7242402}'
function IsProductVersion([string]$value) {
    if ($value.Length -gt 23 -or $value -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$' -or $value -eq '0.0.0.0') { return $false }
    foreach ($component in $value.Split('.')) { if ($component.Length -gt 5 -or [int]$component -gt 65535) { return $false } }
    return $true
}
if (!(IsProductVersion $Version)) { throw 'Invalid installation version.' }
if ($Action -ne 'Prepare') { . (Join-Path $PSScriptRoot 'WindowsPlatform.ps1') }
if ($Fault -notin @('','ClassicVerb','Properties','Startup','Agent','MigrationCopy','Shutdown')) { throw 'Invalid validation fault.' }
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
function OwnedProcess($process) {
    try {
        $path = $process.Path
        return $path.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase) -or
            $path.StartsWith((Join-Path ${env:ProgramFiles} 'WindowsApps\Sargonphin.FluffHistory.DeveloperPreview_'), [StringComparison]::OrdinalIgnoreCase)
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
# Fixed legacy identities are migration-only, never a current build requirement.
function LegacyPackages {
    $result = @()
    foreach ($known in @(
        @{name='Sargonphin.FluffHistory';family='Sargonphin.FluffHistory_53b6jha2b007a';publisher='CN=Fluff-History Sparse Identity'},
        @{name='Sargonphin.FluffHistory.DeveloperPreview';family='Sargonphin.FluffHistory.DeveloperPreview_c87380kbqs5fr';publisher='CN=Fluff-History Developer Preview'})) {
        foreach ($item in @(Get-AppxPackage -Name $known.name -ErrorAction Stop)) {
            if ($item.Name -ceq $known.name -and $item.PackageFamilyName -ceq $known.family -and $item.Publisher -ceq $known.publisher) {
                $legacyVersion=[string]$item.Version
                $publisherId=$known.family.Substring($known.name.Length+1)
                if (!(IsProductVersion $legacyVersion) -or $item.PackageFullName -cne ($known.name+'_'+$legacyVersion+'_x64__'+$publisherId)) { throw 'Unexpected legacy package identity; payloads were preserved.' }
                $result += $item
            }
        }
    }
    if ($result.Count -gt 32) { throw 'Too many legacy registrations; payloads were preserved.' }
    return $result
}
function LegacyReferences([string]$versionName) {
    # Query failure must prevent deletion, including during a later deferred cleanup.
    return @((LegacyPackages) | Where-Object { [string]$_.Version -ceq $versionName }).Count -gt 0
}
function RetireLegacyPackages {
    $installed = ReadJson $record
    $remaining = @()
    if ($installed.PSObject.Properties.Name -contains 'integrationSchema' -and $installed.integrationSchema -eq 2 -and
        @($installed.knownVersions).Count -eq 1 -and $installed.knownVersions[0].version -eq $Version -and
        @($installed.legacyCleanup).Count -eq 0) { return } # Fresh/current-only installs have no AppX dependency.
    try {
        foreach ($item in @(LegacyPackages)) {
            $matches = @($installed.knownVersions | Where-Object { $_.version -ceq [string]$item.Version })
            if ($matches.Count -ne 1) { continue } # No ownership proof: preserve unrelated/unowned registration.
            $previousPayload = Join-Path $root ('Versions\' + $matches[0].version)
            CheckVersion $previousPayload $matches[0].version $matches[0].manifestSha256 $true
            $legacyInfo = ReadJson (Join-Path $previousPayload 'build-info.json')
            if ($legacyInfo.PSObject.Properties.Name -notcontains 'packageFamily' -or
                $legacyInfo.packageFamily -cne $item.PackageFamilyName -or $legacyInfo.packageIdentity -cne $item.Name) {
                $remaining += [ordered]@{version=$matches[0].version;ownershipUnproven=$true}; continue
            }
            # Only exact current-user package removal. Never AllUsers or a wildcard removal.
            try { Remove-AppxPackage -Package $item.PackageFullName -ErrorAction Stop } catch { }
            if (LegacyReferences $matches[0].version) {
                $remaining += [ordered]@{version=$matches[0].version;packageFullName=$item.PackageFullName;packageFamily=$item.PackageFamilyName}
            }
        }
    } catch {
        # No payload deletion when discovery is uncertain. Keep the bounded inventory.
        $remaining = @($installed.knownVersions | ForEach-Object { [ordered]@{version=$_.version;discoveryUnavailable=$true} })
    }
    $installed | Add-Member -NotePropertyName legacyCleanup -NotePropertyValue $remaining -Force
    WriteJson $record $installed
    $warning = Join-Path $records 'integration-warning.txt'
    if ($remaining.Count) {
        [IO.File]::WriteAllText($warning,'Legacy Explorer package retirement is pending. Referenced payloads were retained. Run Setup again to retry. Existing certificate trust was left unchanged.',[Text.Encoding]::UTF8)
    } elseif (Test-Path -LiteralPath $warning) { Remove-Item -LiteralPath $warning }
}
function ClassicVerbPaths { @('*\shell\FluffHistory.RestorePreviousVersion','Directory\shell\FluffHistory.RestorePreviousVersion') }
function ClassicCommand([string]$launcher) {
    if (!$launcher -or $launcher.IndexOfAny([char[]]'"%') -ge 0 -or $launcher -match '[\x00-\x1f]' -or ![IO.Path]::IsPathRooted($launcher)) { throw 'Invalid owned Shell launcher path.' }
    return '"' + $launcher + '" "%1"'
}
function ValidateClassicKey($key,[string]$launcher) {
    if (@($key.GetValueNames() | Where-Object { $_ -notin @('MUIVerb','Icon','MultiSelectModel') }).Count -or
        @($key.GetSubKeyNames() | Where-Object { $_ -cne 'command' }).Count) { throw 'Unexpected classic verb content was preserved.' }
    foreach ($pair in @(@('MUIVerb',('@' + $launcher + ',-3000')),@('Icon',('"' + $launcher + '",0')),@('MultiSelectModel','Single'))) {
        $value=$key.GetValue($pair[0],$null)
        if ($null -ne $value -and ($key.GetValueKind($pair[0]) -ne [Microsoft.Win32.RegistryValueKind]::String -or $value -cne $pair[1])) { throw 'An unrelated classic verb value was preserved.' }
    }
    $command=$key.OpenSubKey('command')
    if ($command) {
        try {
            if (@($command.GetSubKeyNames()).Count -or @($command.GetValueNames() | Where-Object { $_ -cne '' }).Count -or
                ($null -ne $command.GetValue('',$null) -and ($command.GetValueKind('') -ne [Microsoft.Win32.RegistryValueKind]::String -or $command.GetValue('') -cne (ClassicCommand $launcher)))) { throw 'An unrelated classic command was preserved.' }
        } finally { $command.Dispose() }
    }
}
function ClassicVerbs([ValidateSet('Install','Remove')][string]$mode,$classes,[string]$launcher) {
    # Validate both before mutation. Only the two exact keys belong to this feature.
    foreach ($path in (ClassicVerbPaths)) {
        $key=$classes.OpenSubKey($path)
        if ($key) { try { ValidateClassicKey $key $launcher } finally { $key.Dispose() } }
    }
    foreach ($path in (ClassicVerbPaths)) {
        if ($mode -eq 'Remove') { $classes.DeleteSubKeyTree($path,$false); continue }
        $key=$classes.CreateSubKey($path)
        try {
            $key.SetValue('MUIVerb',('@' + $launcher + ',-3000'))
            $key.SetValue('Icon',('"' + $launcher + '",0'))
            $key.SetValue('MultiSelectModel','Single')
            $command=$key.CreateSubKey('command')
            try { $command.SetValue('',(ClassicCommand $launcher)) } finally { $command.Dispose() }
        } finally { $key.Dispose() }
    }
}
function RegisterClassic([string]$mode) {
    $classes=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\Classes')
    try { ClassicVerbs $mode $classes (Join-Path $root 'FluffHistory.ShellLauncher.exe') } finally { $classes.Dispose() }
    NotifyShell
}
function ClassicSnapshot($classes,[string]$launcher) {
    foreach ($path in (ClassicVerbPaths)) {
        $key=$classes.OpenSubKey($path)
        $snapshot=[ordered]@{path=$path;exists=($null -ne $key);values=@();commandExists=$false;commandValue=$null}
        if ($key) {
            try {
                ValidateClassicKey $key $launcher
                $snapshot.values=@($key.GetValueNames() | ForEach-Object { @{name=$_;value=$key.GetValue($_)} })
                $command=$key.OpenSubKey('command')
                if ($command) { try { $snapshot.commandExists=$true;$snapshot.commandValue=$command.GetValue('',$null) } finally { $command.Dispose() } }
            } finally { $key.Dispose() }
        }
        [pscustomobject]$snapshot
    }
}
function RestoreClassicSnapshot($classes,[string]$launcher,$snapshots) {
    $paths=@(ClassicVerbPaths)
    if (@($snapshots).Count -ne 2 -or ($snapshots.path -join '|') -cne ($paths -join '|')) { throw 'Invalid classic registration rollback ownership.' }
    $expected=@{MUIVerb=('@'+$launcher+',-3000');Icon=('"'+$launcher+'",0');MultiSelectModel='Single'}
    foreach ($snapshot in $snapshots) {
        if ($snapshot.exists -isnot [bool] -or $snapshot.commandExists -isnot [bool] -or @($snapshot.values).Count -gt 3 -or
            @($snapshot.values | Group-Object { $_.name } | Where-Object Count -gt 1).Count) { throw 'Invalid classic rollback shape.' }
        foreach ($value in $snapshot.values) { if (!$expected.ContainsKey($value.name) -or $value.value -cne $expected[$value.name]) { throw 'Invalid classic rollback value.' } }
        if ($null -ne $snapshot.commandValue -and $snapshot.commandValue -cne (ClassicCommand $launcher)) { throw 'Invalid classic rollback command.' }
    }
    # Current keys must still contain only our values before exact rollback.
    ClassicVerbs 'Remove' $classes $launcher
    foreach ($snapshot in $snapshots) {
        if (!$snapshot.exists) { continue }
        $key=$classes.CreateSubKey($snapshot.path)
        try {
            foreach ($value in $snapshot.values) {
                if ($value.name -notin @('MUIVerb','Icon','MultiSelectModel') -or $value.value -isnot [string]) { throw 'Invalid classic rollback value.' }
                $key.SetValue($value.name,$value.value)
            }
            if ($snapshot.commandExists) {
                $command=$key.CreateSubKey('command')
                try { if ($null -ne $snapshot.commandValue) { if ($snapshot.commandValue -cne (ClassicCommand $launcher)) { throw 'Invalid classic rollback command.' };$command.SetValue('',$snapshot.commandValue) } } finally { $command.Dispose() }
            }
            ValidateClassicKey $key $launcher
        } finally { $key.Dispose() }
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
        try { if (LegacyReferences $entry.version) { continue } } catch { continue }
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
        try { if (LegacyReferences $versionName) { $remaining += $file; continue } } catch { $remaining += $file; continue }
        CheckParents $file.path
        if (!(Test-Path -LiteralPath $file.path)) { continue }
        if ((Get-FileHash -LiteralPath $file.path).Hash -ne $file.sha256) { throw 'Deferred Shell image changed; it was preserved.' }
        try { Remove-Item -LiteralPath $file.path }
        catch [IO.IOException] { $remaining += $file; continue }
        catch [System.UnauthorizedAccessException] { $remaining += $file; continue }
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
    switch ($Action) {
        'Prepare' {
            if ((New-Object Security.Principal.WindowsPrincipal ([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
                throw 'Run Setup normally from the Windows account that will use Fluff-History, without Run as administrator.'
            }
            CleanupDeferred
            CheckTree $root
            CheckOwnedVersions
            if (Test-Path -LiteralPath $root) {
                $unexpected = @(Get-ChildItem -LiteralPath $root -Force | Where-Object { $_.Name -notin @('Versions','FluffHistory.Agent.exe','FluffHistory.ShellLauncher.exe','current-version.txt','unins000.exe','unins000.dat','unins000.msg') })
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
            $classes=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\Classes')
            try { $previousClassic=@(ClassicSnapshot $classes (Join-Path $root 'FluffHistory.ShellLauncher.exe')) } finally { $classes.Dispose() }
            WriteJson $pending ([ordered]@{schema=1;version=$Version;root=$root;manifestSha256=(Get-FileHash -LiteralPath $ExpectedManifest).Hash;backup=$backup;previous=$previous;previousDll=$previousDll;previousRun=$previousRun;previousClassic=$previousClassic;classicAdded=$false;startupAdded=$false})
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
            $transaction = ReadJson $pending
            if ($Fault -eq 'ClassicVerb') { throw 'Controlled classic verb registration failure.' }
            MarkPending 'classicAdded' $true
            RegisterClassic 'Install'
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
            }
            $manifestHash = (Get-FileHash -LiteralPath (Join-Path $payload 'installed-files.json')).Hash
            $known += [ordered]@{version=$Version;manifestSha256=$manifestHash}
            if ($known.Count -gt 32) { throw 'Too many retained deployment versions.' }
            $releaseVersion = if ($info.PSObject.Properties.Name -contains 'releaseVersion') { $info.releaseVersion } else { 'development' }
            WriteJson $record ([ordered]@{schema=1;version=$Version;releaseVersion=$releaseVersion;root=$root;manifestSha256=$manifestHash;knownVersions=$known;integrationSchema=2;classicVerbAvailable=$true;propertiesRegistered=$true;activated=$false;legacyCleanup=@()})
        }
        'Activate' {
            if ($Fault -eq 'Agent') { throw 'Controlled agent first-launch failure.' }
            RunSupport -arguments @('--agent-ready',$Version)
            $installed = ReadJson $record; $installed.activated = $true; WriteJson $record $installed
            if (Test-Path -LiteralPath $pending) { Remove-Item -LiteralPath $pending }
            if (Test-Path -LiteralPath (Join-Path $records 'last-error.txt')) { Remove-Item -LiteralPath (Join-Path $records 'last-error.txt') }
            # Activation is committed. Optional legacy retirement cannot roll it back.
            try { RetireLegacyPackages } catch { }
            # Only old, hash-validated payloads are eligible. A sharing refusal leaves files for a later setup.
            try { RetireOldPayloads } catch { }
        }
        'Rollback' {
            # Recoverable failure: remove integration before Inno rolls back new files. Retain state backup.
            if (Test-Path -LiteralPath $support) { RunSupport -arguments @('--stop-agent') }
            $transaction = ReadJson $pending
            $currentDll = [Microsoft.Win32.Registry]::GetValue('HKEY_CURRENT_USER\Software\Classes\CLSID\' + $clsid + '\InprocServer32','','')
            if ($currentDll -eq (Join-Path $payload 'FluffHistory.PropertySheet.dll')) { RemoveProperties }
            if ($transaction.classicAdded) {
                $classes=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\Classes')
                try { RestoreClassicSnapshot $classes (Join-Path $root 'FluffHistory.ShellLauncher.exe') $transaction.previousClassic } finally { $classes.Dispose() }
                NotifyShell
            }
            $run = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run',$true)
            if ($run) { try { if ($transaction.startupAdded -and $run.GetValue('Fluff-History') -eq ('"' + (Join-Path $root 'FluffHistory.Agent.exe') + '"')) { $run.DeleteValue('Fluff-History') } } finally { $run.Dispose() } }
            if ($transaction.previous) {
                $previousPayload = Join-Path $root ('Versions\' + $transaction.previous.version)
                CheckParents $previousPayload
                [IO.File]::WriteAllText((Join-Path $root 'current-version.txt'),$transaction.previous.version,[Text.Encoding]::ASCII)
                WriteJson $record $transaction.previous
            } else {
                if (Test-Path -LiteralPath $record) { Remove-Item -LiteralPath $record }
                RemoveVersionMarker
            }
            if ($transaction.previousDll -and (Test-Path -LiteralPath $transaction.previousDll)) { RegisterProperties $transaction.previousDll }
        }
        'Uninstall' {
            CheckTree $root
            CheckOwnedVersions
            StopComponents
            if (Test-Path -LiteralPath $record) {
                RetireLegacyPackages
                if (@((ReadJson $record).legacyCleanup).Count) { throw 'A legacy package still references installed files. Uninstall preserved the payload; retry after resolving the exact registration.' }
            }
            RememberShellFiles
            RegisterClassic 'Remove'
            RemoveProperties
            $run = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run',$true)
            if ($run) { try { if ($run.GetValue('Fluff-History') -eq ('"' + (Join-Path $root 'FluffHistory.Agent.exe') + '"')) { $run.DeleteValue('Fluff-History') } } finally { $run.Dispose() } }
            $activeVersion = $Version
            if (Test-Path -LiteralPath $record) {
                $installed = ReadJson $record
                $activeVersion = $installed.version
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
