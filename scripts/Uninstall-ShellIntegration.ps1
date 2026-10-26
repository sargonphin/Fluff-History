[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Fluff-History\ShellIntegration'))
$clsid = '{80F01E21-B634-4183-834E-CF09D7242402}'
$classes = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Classes', $true)
$dll = $null
try {
    foreach ($scope in @('*','Directory')) {
        $path = $scope + '\shellex\PropertySheetHandlers\Fluff-History'
        $key = $classes.OpenSubKey($path)
        if ($key) {
            try { if ($key.GetValue('') -ne $clsid) { throw 'An unrelated handler occupies this registration; it was preserved.' } }
            finally { $key.Dispose() }
        }
    }
    $comPath = 'CLSID\' + $clsid
    $com = $classes.OpenSubKey($comPath + '\InprocServer32')
    if ($com) {
        try {
            $dll = [string]$com.GetValue(''); $resolved = [IO.Path]::GetFullPath($dll)
            if ($resolved -ne $dll -or !$resolved.StartsWith($root + '\',[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -ne 'FluffHistory.PropertySheet.dll' -or
                (Split-Path (Split-Path $resolved -Parent) -Parent) -ne $root -or (Split-Path (Split-Path $resolved -Parent) -Leaf) -notmatch '^0\.24\.0\.\d{1,5}$') { throw 'Unrelated COM registration preserved.' }
            for ($current = Split-Path $resolved -Parent; $current; $current = Split-Path $current -Parent) {
                if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Reparse-based cleanup path preserved.' }
            }
        }
        finally { $com.Dispose() }
    }
    foreach ($scope in @('*','Directory')) { $classes.DeleteSubKeyTree($scope + '\shellex\PropertySheetHandlers\Fluff-History',$false) }
    $classes.DeleteSubKeyTree($comPath,$false)
} finally { $classes.Dispose() }
Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class FluffHistoryShellCleanupNotify { [DllImport("shell32.dll")] public static extern void SHChangeNotify(uint e,uint f,IntPtr a,IntPtr b); }'
[FluffHistoryShellCleanupNotify]::SHChangeNotify(0x08000000,0,[IntPtr]::Zero,[IntPtr]::Zero)
if (!(Get-AppxPackage -Name 'Sargonphin.FluffHistory.DeveloperPreview')) { Write-Output 'Package is absent; leftover development Properties registration has been removed.' }
if ($dll) {
    # Exact checked file only; an in-use image may remain until its host exits naturally.
    try { Remove-Item -LiteralPath $dll -Force -ErrorAction Stop; Remove-Item -LiteralPath (Split-Path $dll -Parent) -ErrorAction Stop }
    catch { Write-Output 'Registration removed. The versioned DLL may remain loaded until its host exits; no restart is required for cleanup.' }
}
$marker = Join-Path $root 'registration-v1.json'
if (Test-Path -LiteralPath $marker) { Remove-Item -LiteralPath $marker -Force }
Write-Output 'Fluff-History development Properties registration removed. Explorer was not restarted.'
