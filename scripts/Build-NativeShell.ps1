[CmdletBinding()]
param([string]$OutputDirectory, [string]$ProductVersion)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ([Environment]::OSVersion.Platform -ne 'Win32NT') { throw 'Native Shell components require Windows.' }
$repo = Split-Path $PSScriptRoot -Parent
& "$PSScriptRoot\Test-NativeResources.ps1"
$head = (& git -C $repo rev-parse HEAD).Trim()
if ($LASTEXITCODE -or $head -notmatch '^[0-9a-f]{40}$') { throw 'Invalid native source version.' }
$ProductVersion = & "$PSScriptRoot\Get-ProductVersion.ps1" -Repository $repo -ProductVersion $ProductVersion
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repo 'artifacts\native-shell' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$locator = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (!(Test-Path -LiteralPath $locator)) { throw 'Install MSVC Build Tools (VC.Tools.x86.x64) and Windows 11 SDK 26100.' }
$installation = (& $locator -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)
if (!$installation) { throw 'MSVC x64 build tools were not found.' }
$vc = (Get-ChildItem -LiteralPath (Join-Path $installation 'VC\Tools\MSVC') -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1).FullName
$sdk = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10'
$sdkVersion = '10.0.26100.0'
if (!(Test-Path -LiteralPath (Join-Path $sdk "Include\$sdkVersion\um\Windows.h"))) { throw 'Windows 11 SDK 10.0.26100.0 is required.' }
$oldInclude = $env:INCLUDE; $oldLib = $env:LIB; $oldPath = $env:PATH
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
Push-Location $OutputDirectory
try {
    $env:INCLUDE = "$vc\include;$sdk\Include\$sdkVersion\ucrt;$sdk\Include\$sdkVersion\shared;$sdk\Include\$sdkVersion\um;$sdk\Include\$sdkVersion\winrt"
    $env:LIB = "$vc\lib\x64;$sdk\Lib\$sdkVersion\ucrt\x64;$sdk\Lib\$sdkVersion\um\x64"
    $env:PATH = "$sdk\bin\$sdkVersion\x64;$env:PATH"
    $source = Join-Path $repo 'native\Shell'
    $versionTemplate = Get-Content -LiteralPath (Join-Path $repo 'native\Version.rc.in') -Raw
    foreach ($type in @('Dll','App')) {
        $description = if ($type -eq 'App') { 'Fluff-History Background Agent' } else { 'Fluff-History Shell Integration' }
        $versionTemplate.Replace('@VERSION@',$ProductVersion).Replace('@VERSION_NUMERIC@',$ProductVersion.Replace('.',',')).Replace('@HEAD@',$head).Replace('@TYPE@',('VFT_' + $type.ToUpperInvariant())).Replace('@DESCRIPTION@',$description) |
            Set-Content -LiteralPath ("Version$type.rc") -Encoding ASCII
        $icon = (Join-Path $repo 'src\FluffHistory.Desktop\Assets\FluffHistory.ico').Replace('\','\\')
        Add-Content -LiteralPath ("Version$type.rc") -Value ('1 ICON "' + $icon + '"') -Encoding ASCII
        & "$sdk\bin\$sdkVersion\x64\rc.exe" /nologo /fo "Version$type.res" "Version$type.rc"
        if ($LASTEXITCODE) { throw 'Native version resource compilation failed.' }
    }
    & "$sdk\bin\$sdkVersion\x64\rc.exe" /nologo /c65001 /fo Properties.res "$source\Properties.rc"
    if ($LASTEXITCODE) { throw 'Native Properties resource compilation failed.' }
    & "$sdk\bin\$sdkVersion\x64\rc.exe" /nologo /c65001 "/I$source" /fo ShellLauncher.res "$source\ShellLauncher.rc"
    if ($LASTEXITCODE) { throw 'Native launcher resource compilation failed.' }
    foreach ($component in @(,@('PropertySheet','FluffHistory.PropertySheet'))) {
        $arguments = @('/nologo','/std:c++20','/EHsc','/MT','/O2','/W4','/WX','/guard:cf','/DUNICODE','/D_UNICODE','/D_WIN32_WINNT=0x0A00','/LD',"$source\$($component[0]).cpp")
        if ($component[0] -eq 'PropertySheet') { $arguments += 'Properties.res' }
        $arguments += @('VersionDll.res','/link',"/OUT:$($component[1]).dll","/DEF:$source\Shell.def",'/DYNAMICBASE','/NXCOMPAT','/guard:cf','shell32.lib','shlwapi.lib','ole32.lib','uuid.lib','comctl32.lib','bcrypt.lib','user32.lib','advapi32.lib')
        & "$vc\bin\Hostx64\x64\cl.exe" @arguments
        if ($LASTEXITCODE) { throw "Native $($component[0]) build failed." }
    }
    (Get-Content -LiteralPath VersionApp.rc -Raw).Replace('Fluff-History Background Agent','Fluff-History Shell Launcher') | Set-Content -LiteralPath VersionShellLauncher.rc -Encoding ASCII
    & "$sdk\bin\$sdkVersion\x64\rc.exe" /nologo /fo VersionShellLauncher.res VersionShellLauncher.rc
    if ($LASTEXITCODE) { throw 'Shell launcher version resource build failed.' }
    & "$vc\bin\Hostx64\x64\cl.exe" /nologo /std:c++20 /EHsc /MT /O2 /W4 /WX /guard:cf /DUNICODE /D_UNICODE /D_WIN32_WINNT=0x0A00 "$repo\native\Launcher\AgentLauncher.cpp" VersionApp.res /link /SUBSYSTEM:WINDOWS /OUT:FluffHistory.AgentLauncher.exe /DYNAMICBASE /NXCOMPAT /guard:cf
    if ($LASTEXITCODE) { throw 'Native agent startup launcher build failed.' }
    & "$vc\bin\Hostx64\x64\cl.exe" /nologo /std:c++20 /EHsc /MT /O2 /W4 /WX /guard:cf /DUNICODE /D_UNICODE /D_WIN32_WINNT=0x0A00 "$source\ShellLauncher.cpp" ShellLauncher.res VersionShellLauncher.res /link /SUBSYSTEM:WINDOWS /OUT:FluffHistory.ShellLauncher.exe /DYNAMICBASE /NXCOMPAT /guard:cf /MANIFEST:EMBED "/MANIFESTINPUT:$source\ShellLauncher.manifest" shell32.lib shlwapi.lib ole32.lib bcrypt.lib user32.lib
    if ($LASTEXITCODE) { throw 'Native Shell launcher build failed.' }
    & "$vc\bin\Hostx64\x64\cl.exe" /nologo /std:c++20 /EHsc /MT /O2 /W4 /WX /DUNICODE /D_UNICODE /D_WIN32_WINNT=0x0A00 "$source\ClassicLauncherTests.cpp" /link /OUT:ClassicLauncherTests.exe /MANIFEST:EMBED "/MANIFESTINPUT:$source\ShellLauncher.manifest" shell32.lib shlwapi.lib ole32.lib bcrypt.lib user32.lib
    if ($LASTEXITCODE) { throw 'Native launcher tests build failed.' }
    & .\ClassicLauncherTests.exe $OutputDirectory
    if ($LASTEXITCODE) { throw 'Native launcher tests failed.' }
    & "$vc\bin\Hostx64\x64\cl.exe" /nologo /std:c++20 /EHsc /MT /O2 /W4 /WX /DUNICODE /D_UNICODE /D_WIN32_WINNT=0x0A00 "$source\ShellTests.cpp" /link /OUT:ShellTests.exe /MANIFEST:EMBED "/MANIFESTINPUT:$source\ShellTestHost.manifest" shell32.lib shlwapi.lib ole32.lib uuid.lib comctl32.lib bcrypt.lib user32.lib
    if ($LASTEXITCODE) { throw 'Native Shell tests build failed.' }
    & .\ShellTests.exe $OutputDirectory
    if ($LASTEXITCODE) { throw 'Native Shell checks failed.' }
    & "$vc\bin\Hostx64\x64\cl.exe" /nologo /std:c++20 /EHsc /MT /O2 /W4 /WX /DUNICODE /D_UNICODE /D_WIN32_WINNT=0x0A00 "$source\ShellFaultTests.cpp" Properties.res VersionDll.res /link /OUT:ShellFaultTests.exe /MANIFEST:EMBED "/MANIFESTINPUT:$source\ShellTestHost.manifest" shell32.lib shlwapi.lib ole32.lib uuid.lib comctl32.lib bcrypt.lib user32.lib
    if ($LASTEXITCODE) { throw 'Native Shell fault tests build failed.' }
    & .\ShellFaultTests.exe
    if ($LASTEXITCODE) { throw 'Native Shell fault checks failed.' }
    [ordered]@{ architecture='x64'; productVersion=$ProductVersion; gitCommit=$head; msvc=(Split-Path $vc -Leaf); sdk=$sdkVersion; runtime='static'; components=@('FluffHistory.ShellLauncher.exe','FluffHistory.PropertySheet.dll','FluffHistory.AgentLauncher.exe') } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'native-build-info.json') -Encoding UTF8
} finally { Pop-Location; $env:INCLUDE = $oldInclude; $env:LIB = $oldLib; $env:PATH = $oldPath }
