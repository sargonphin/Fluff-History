$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$source = Join-Path $PSScriptRoot '..\installer\Lifecycle.ps1'
$tokens = $null; $errors = $null
$syntax = [Management.Automation.Language.Parser]::ParseFile([IO.Path]::GetFullPath($source),[ref]$tokens,[ref]$errors)
if ($errors) { throw 'Lifecycle syntax error.' }
foreach ($function in $syntax.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst]},$false)) {
    . ([scriptblock]::Create($function.Extent.Text))
}
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('FluffHistory-InstallerTest-' + [guid]::NewGuid().ToString('N'))
$root = Join-Path $temporary 'Programs\Fluff-History'
$records = Join-Path $temporary 'State\Installer'
$record = Join-Path $records 'installation-v1.json'
$pending = Join-Path $records 'pending-v1.json'
$Version = '1.10.0.248'
$held = $null
$mapped = [IntPtr]::Zero
if (-not ('FluffHistoryLoadedImageTest' -as [type])) {
    Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class FluffHistoryLoadedImageTest { [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags); [DllImport("kernel32.dll")] public static extern bool FreeLibrary(IntPtr module); }'
}
try {
    foreach ($valid in @('0.24.1.2','1.10.0.0','2.0.0.0','65535.65535.65535.65535')) {
        if (!(IsProductVersion $valid)) { throw 'Canonical product build version was rejected.' }
    }
    foreach ($invalid in @('0.0.0.0','1.02.3.4','1.2.3.65536','1.2.3','1.2.3.4.5','..\1.2.3.4','1.2.3.4 ','-1.2.3.4','999999999999999999999999999.0.0.0')) {
        if (IsProductVersion $invalid) { throw 'Unsafe product build version was accepted.' }
    }
    New-Item -ItemType Directory -Path $records -Force | Out-Null
    $known = @()
    foreach ($versionName in @('0.24.1.1',$Version)) {
        $directory = Join-Path $root ('Versions\' + $versionName)
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
        $inventory = @()
        foreach ($name in @('FluffHistory.PropertySheet.dll','FluffHistory.ExplorerCommand.dll','managed.dat')) {
            $file = Join-Path $directory $name
            if ($versionName -ne $Version -and $name -eq 'FluffHistory.PropertySheet.dll') {
                Copy-Item -LiteralPath (Join-Path $env:WINDIR 'System32\version.dll') -Destination $file
            } else { [IO.File]::WriteAllText($file,$versionName + '/' + $name) }
            $inventory += @{path=$name;sha256=(Get-FileHash -LiteralPath $file).Hash;length=(Get-Item -LiteralPath $file).Length}
        }
        $manifest = Join-Path $directory 'installed-files.json'
        WriteJson $manifest @{schema=1;version=$versionName;files=$inventory}
        $known += @{version=$versionName;manifestSha256=(Get-FileHash -LiteralPath $manifest).Hash}
    }
    WriteJson $record @{schema=1;version=$Version;root=$root;knownVersions=$known}
    $oldDll = Join-Path $root 'Versions\0.24.1.1\FluffHistory.ExplorerCommand.dll'
    $held = [IO.File]::Open($oldDll,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    $oldPage = Join-Path $root 'Versions\0.24.1.1\FluffHistory.PropertySheet.dll'
    # Map an actual PE image, without executing its entry point. Windows reports this
    # loaded-image deletion refusal as access denied, distinct from a file-sharing lock.
    $mapped = [FluffHistoryLoadedImageTest]::LoadLibraryEx($oldPage,[IntPtr]::Zero,1)
    if ($mapped -eq [IntPtr]::Zero) { throw 'Unable to map the owned native-image fixture.' }
    RetireOldPayloads
    $marker = ReadJson (Join-Path $records 'deferred-shell-v1.json')
    if (@($marker.files | Where-Object path -eq $oldDll).Count -ne 1) { throw 'Sharing refusal lost deferred ownership.' }
    if (@($marker.files | Where-Object path -eq $oldPage).Count -ne 1) { throw 'Mapped image refusal lost deferred ownership.' }
    if (!(Test-Path -LiteralPath (Join-Path $root ('Versions\' + $Version + '\managed.dat')))) { throw 'Active payload was removed.' }
    CheckTree $root
    CheckOwnedVersions
    $held.Dispose(); $held = $null
    CleanupDeferred
    $marker = ReadJson (Join-Path $records 'deferred-shell-v1.json')
    if (@($marker.files | Where-Object path -eq $oldPage).Count -ne 1 -or !(Test-Path -LiteralPath $oldPage)) { throw 'A later update lost the still-mapped image.' }
    CheckOwnedVersions
    [void][FluffHistoryLoadedImageTest]::FreeLibrary($mapped); $mapped = [IntPtr]::Zero
    CleanupDeferred
    CheckOwnedVersions
    if (Test-Path -LiteralPath $oldDll) { throw 'Unlocked old DLL was not cleaned.' }
    if (Test-Path -LiteralPath $oldPage) { throw 'Unmapped native image was not cleaned.' }
    $activeData = Join-Path $root ('Versions\' + $Version + '\managed.dat')
    $bytes = [IO.File]::ReadAllBytes($activeData)
    Remove-Item -LiteralPath $activeData
    $rejected = $false
    try { CheckOwnedVersions } catch { $rejected = $true }
    if (!$rejected) { throw 'An incomplete active payload was accepted.' }
    [IO.File]::WriteAllBytes($activeData,$bytes)
    CheckOwnedVersions
    $activeDll = Join-Path $root ('Versions\' + $Version + '\FluffHistory.ExplorerCommand.dll')
    WriteJson (Join-Path $records 'deferred-shell-v1.json') @{schema=1;root=$root;files=@(@{path=$activeDll;sha256=(Get-FileHash -LiteralPath $activeDll).Hash})}
    CleanupDeferred
    if (!(Test-Path -LiteralPath $activeDll)) { throw 'Deferred cleanup removed the active native image.' }
    Write-Output 'Installer lifecycle regression passed: loaded old DLL ownership, next setup, release and active-version protection.'
} finally {
    if ($held) { $held.Dispose() }
    if ($mapped -ne [IntPtr]::Zero) { [void][FluffHistoryLoadedImageTest]::FreeLibrary($mapped) }
    # This test creates the exact private root; no link or external input is accepted.
    if ([IO.Path]::GetFullPath($temporary).StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()),[StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $temporary -Recurse -Force
    }
}
