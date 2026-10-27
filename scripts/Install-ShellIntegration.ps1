[CmdletBinding()]
param([string]$ArtifactDirectory = $PSScriptRoot)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$package = Get-AppxPackage -Name 'Sargonphin.FluffHistory.DeveloperPreview'
if (!$package) { throw 'Install the matching Developer Preview package first.' }
$info = Get-Content -LiteralPath (Join-Path $ArtifactDirectory 'build-info.json') -Raw | ConvertFrom-Json
if ($info.version -notmatch '^0\.24\.0\.\d{1,5}$' -or $package.Version -ne $info.version) { throw 'The installed package must match this development archive.' }
$payload = Join-Path $ArtifactDirectory 'ShellIntegration\FluffHistory.PropertySheet.dll'
if ((Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash -ne $info.propertySheetSha256) { throw 'Properties payload hash does not match this archive.' }
$root = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Fluff-History\ShellIntegration'
$directory = Join-Path $root $info.version
$dll = Join-Path $directory 'FluffHistory.PropertySheet.dll'
for ($current = $directory; $current; $current = Split-Path $current -Parent) {
    if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Shell integration directories cannot be reparse points.' }
}
New-Item -ItemType Directory -Path $directory -Force | Out-Null
if (Test-Path -LiteralPath $dll) {
    if ((Get-Item -LiteralPath $dll -Force).Attributes -band ([IO.FileAttributes]::ReparsePoint -bor [IO.FileAttributes]::Directory)) { throw 'The versioned Properties DLL must be an ordinary file.' }
    if ((Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash -ne $info.propertySheetSha256) { throw 'Existing versioned Properties DLL differs. Use a new archive version.' }
} else { Copy-Item -LiteralPath $payload -Destination $dll }
$clsid = '{80F01E21-B634-4183-834E-CF09D7242402}'
$classes = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Classes', $true)
try {
    $existing = $classes.OpenSubKey('CLSID\' + $clsid + '\InprocServer32')
    if ($existing) {
        try { $old = [string]$existing.GetValue(''); if (!$old.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'The Properties CLSID is already registered to an unrelated path.' } }
        finally { $existing.Dispose() }
    }
    foreach ($scope in @('*','Directory')) {
        $key = $classes.OpenSubKey($scope + '\shellex\PropertySheetHandlers\Fluff-History')
        if ($key) { try { if ($key.GetValue('') -ne $clsid) { throw 'An unrelated Properties registration uses this name.' } } finally { $key.Dispose() } }
    }
    $com = $classes.CreateSubKey('CLSID\' + $clsid + '\InprocServer32')
    try { $com.SetValue('', $dll); $com.SetValue('ThreadingModel', 'Apartment') } finally { $com.Dispose() }
    foreach ($scope in @('*','Directory')) {
        $key = $classes.CreateSubKey($scope + '\shellex\PropertySheetHandlers\Fluff-History')
        try { $key.SetValue('', $clsid) } finally { $key.Dispose() }
    }
} finally { $classes.Dispose() }
[ordered]@{ schema=1; version=$info.version; dll=$dll; packageFamily=$package.PackageFamilyName } | ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $root 'registration-v1.json') -Encoding UTF8
Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class FluffHistoryShellNotify { [DllImport("shell32.dll")] public static extern void SHChangeNotify(uint e,uint f,IntPtr a,IntPtr b); }'
[FluffHistoryShellNotify]::SHChangeNotify(0x08000000,0,[IntPtr]::Zero,[IntPtr]::Zero)
Write-Output "Development Properties page registered per-user: $dll"
Write-Output 'Run Uninstall-ShellIntegration.ps1 before removing the package. Use canonical Setup for normal deployment.'
