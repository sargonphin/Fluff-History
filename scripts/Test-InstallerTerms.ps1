[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('Build','Run')][string]$Mode,
    [Parameter(Mandatory)][string]$Directory, [string]$InnoCompiler, [switch]$Interactive)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Set-StrictMode -Version Latest
if ($env:OS -ne 'Windows_NT') { throw 'Installer terms validation requires Windows.' }
$Directory = [IO.Path]::GetFullPath($Directory)
function AgreementHash([string]$text) {
    $sha=[Security.Cryptography.SHA256]::Create()
    try {[BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($text.Replace("`r`n","`n").Replace("`r","`n")))).Replace('-','')}
    finally {$sha.Dispose()}
}
if ($Mode -eq 'Build') {
    if ((Test-Path -LiteralPath $Directory) -and (Get-ChildItem -LiteralPath $Directory -Force | Select-Object -First 1)) { throw 'Choose empty fixture output.' }
    if (!$InnoCompiler) {
        $InnoCompiler=@((Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'),(Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 7\ISCC.exe')) | Where-Object {Test-Path -LiteralPath $_} | Select-Object -First 1
    }
    if (!$InnoCompiler -or (& $InnoCompiler --version) -cne '7.1.0') { throw 'Pinned Inno Setup 7.1.0 required.' }
    New-Item -ItemType Directory -Path $Directory -Force | Out-Null
    $repo=Split-Path $PSScriptRoot -Parent
    $text=[IO.File]::ReadAllText((Join-Path $repo 'installer\DistributionTerms.txt'))
    $one=AgreementHash $text; $two=AgreementHash ($text+"`nFixture-only changed terms; not a shipping agreement.`n")
    if ((AgreementHash ($text.Replace("`r`n","`n").Replace("`n","`r`n"))) -cne $one) { throw 'Line endings changed consent identity.' }
    $tag=[guid]::NewGuid().ToString('N');$app=[guid]::NewGuid().ToString();$ui=[guid]::NewGuid().ToString();$cancel=[guid]::NewGuid().ToString()
    foreach($case in @(@('one',$app,'1.0.0.1',$one,$text,'main'),@('same',$app,'1.0.0.2',$one,$text,'main'),@('changed',$app,'1.0.0.3',$two,($text+"`nFixture-only changed terms; not a shipping agreement.`n"),'main'),@('interactive',$ui,'1.0.0.1',$one,$text,'interactive'),@('cancel',$cancel,'1.0.0.1',$one,$text,'cancel'))) {
        $name=$case[0];$license=Join-Path $Directory ($name+'.txt');[IO.File]::WriteAllText($license,$case[4],[Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText((Join-Path $Directory ($name+'.marker')),$name,[Text.UTF8Encoding]::new($false))
        $source=@'
[Setup]
AppId={{__APP__}
AppName=Fluff-History Terms Fixture
AppVersion=__VERSION__
DefaultDirName={localappdata}\FluffHistory-TermsFixtures\__TAG__\__SCOPE__
PrivilegesRequired=lowest
SetupArchitecture=x64
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
DisableWelcomePage=yes
DisableStartupPrompt=yes
Uninstallable=yes
LicenseFile=__LICENSE__
OutputDir=__OUTPUT__
OutputBaseFilename=__NAME__
[Files]
Source: "__MARKER__"; DestDir: "{app}"; DestName: "marker.txt"; Flags: ignoreversion
[Code]
#include "__INCLUDE__"
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin Result := RequireDistributionTerms(); end;
'@
        $source=$source.Replace('__APP__',$case[1]).Replace('__VERSION__',$case[2]).Replace('__TAG__',$tag).Replace('__SCOPE__',$case[5]).Replace('__LICENSE__',$license).Replace('__OUTPUT__',$Directory).Replace('__NAME__',$name).Replace('__MARKER__',(Join-Path $Directory ($name+'.marker'))).Replace('__INCLUDE__',(Join-Path $repo 'installer\DistributionTerms.iss'))
        $iss=Join-Path $Directory ($name+'.iss');[IO.File]::WriteAllText($iss,$source,[Text.UTF8Encoding]::new($false))
        & $InnoCompiler ("--define=DistributionTermsHash="+$case[3]) $iss *> (Join-Path $Directory ($name+'-compile.log'))
        if($LASTEXITCODE){throw "Fixture compile failed: $name"}
    }
    @{schema=1;tag=$tag;one=$one;two=$two;app=$app;ui=$ui;cancel=$cancel} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Directory 'fixture.json') -Encoding UTF8
    Write-Output 'Native terms fixtures compiled from actual shared Setup code; newline identity stable.'
    return
}
$fixture=Get-Content -LiteralPath (Join-Path $Directory 'fixture.json') -Raw | ConvertFrom-Json
if($fixture.schema -ne 1 -or $fixture.tag -notmatch '^[0-9a-f]{32}$' -or $fixture.one -notmatch '^[0-9A-F]{64}$' -or $fixture.two -notmatch '^[0-9A-F]{64}$'){throw 'Invalid owned fixture metadata.'}
$root=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('FluffHistory-TermsFixtures\'+$fixture.tag)
if(Test-Path -LiteralPath $root){throw 'Fixture was already installed; choose a new campaign.'}
$cases=New-Object 'Collections.Generic.List[object]'
function RunSilent([string]$name,[string[]]$arguments,[bool]$success) {
    $log=Join-Path $Directory ($cases.Count.ToString()+'-'+$name+'.log')
    $p=Start-Process -FilePath (Join-Path $Directory ($name+'.exe')) -ArgumentList ($arguments+@('/NORESTART',('/LOG="'+$log+'"'))) -PassThru -WindowStyle Hidden
    try {
        if(!$p.WaitForExit(30000)){throw 'Owned fixture exceeded deadline; not terminated.'}
        if(($p.ExitCode -eq 0) -ne $success){throw "Unexpected fixture exit: $name / $($p.ExitCode)"}
        $cases.Add(@{name=$name;arguments=$arguments;exit=$p.ExitCode;passed=$true})
    } finally {$p.Dispose()}
}
function Marker([string]$expected) {
    $path=Join-Path $root 'main\marker.txt'
    if($expected){if([IO.File]::ReadAllText($path) -cne $expected){throw 'Rejected setup changed installed fixture.'}}
    elseif(Test-Path -LiteralPath $path){throw 'Unaccepted installer wrote its payload.'}
}
function RunInteractive([string]$name,[bool]$accept) {
    if(!('InstallerTermsControls' -as [type])) {
        Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class InstallerTermsControls {
    public sealed class Control { public IntPtr Handle; public int Process; public string Name; public bool Enabled; }
    delegate bool EnumProc(IntPtr h, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr h, EnumProc cb, IntPtr data);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h,StringBuilder b,int n);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h,StringBuilder b,int n);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr h);
    [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr h,out int p);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h,uint flag);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    public static Control[] For(int[] owners) {
        var rows=new List<Control>();
        EnumProc child=(h,d)=>{int p;GetWindowThreadProcessId(h,out p);
            if(Array.IndexOf(owners,p)<0 || !IsWindowVisible(h)) return true;
            var c=new StringBuilder(128);GetClassName(h,c,128);
            if(!c.ToString().EndsWith("Button",StringComparison.Ordinal)) return true;
            var b=new StringBuilder(1024);GetWindowText(h,b,1024);
            rows.Add(new Control{Handle=h,Process=p,Name=b.ToString().Replace("&","").Trim(),Enabled=IsWindowEnabled(h)});return true;};
        EnumWindows((h,d)=>{int p;GetWindowThreadProcessId(h,out p);if(Array.IndexOf(owners,p)>=0)EnumChildWindows(h,child,IntPtr.Zero);return true;},IntPtr.Zero);
        return rows.ToArray();
    }
    public static void Click(Control c) { int p;GetWindowThreadProcessId(c.Handle,out p);
        if(p!=c.Process)throw new InvalidOperationException("Fixture control expired.");
        SetForegroundWindow(GetAncestor(c.Handle,2));
        if(!PostMessage(c.Handle,0xF5,IntPtr.Zero,IntPtr.Zero))throw new InvalidOperationException("Fixture click failed.");
    }
}
"@
    }
    $p=Start-Process -FilePath (Join-Path $Directory ($name+'.exe')) -ArgumentList @('/NORESTART',('/LOG="'+(Join-Path $Directory ($name+'-interactive.log'))+'"')) -PassThru
    try {
        $deadline=[DateTime]::UtcNow.AddSeconds(20);$next=$null;$radio=$null
        do {
            $owned=[int[]](@($p.Id)+@(Get-CimInstance Win32_Process | Where-Object ParentProcessId -eq $p.Id | Select-Object -ExpandProperty ProcessId))
            $all=@([InstallerTermsControls]::For($owned));$radio=$all | Where-Object Name -match '^I accept' | Select-Object -First 1
            $next=$all | Where-Object Name -match '^(Next|Install)' | Select-Object -First 1
            if(!$radio -or !$next){Start-Sleep -Milliseconds 100}
        } while((!$radio -or !$next) -and [DateTime]::UtcNow -lt $deadline)
        if(!$radio -or !$next -or $next.Enabled){throw 'Unaccepted license did not disable Next.'}
        if($accept){
            [InstallerTermsControls]::Click($radio);Start-Sleep -Milliseconds 100
            [InstallerTermsControls]::Click($next)
            $deadline=[DateTime]::UtcNow.AddSeconds(20);$finish=$null
            do {
                $finish=@([InstallerTermsControls]::For($owned) | Where-Object Name -match '^Finish') | Select-Object -First 1
                if(!$finish){Start-Sleep -Milliseconds 100}
            } while(!$finish -and [DateTime]::UtcNow -lt $deadline)
            if(!$finish){throw 'Accepted fixture did not finish.'}
            [InstallerTermsControls]::Click($finish)
        } else {
            $cancel=$all | Where-Object Name -eq 'Cancel' | Select-Object -First 1
            [InstallerTermsControls]::Click($cancel);Start-Sleep -Milliseconds 200
            $yes=@([InstallerTermsControls]::For($owned) | Where-Object Name -eq 'Yes') | Select-Object -First 1
            if($yes){[InstallerTermsControls]::Click($yes)}
        }
        if(!$p.WaitForExit(20000) -or (($p.ExitCode -eq 0) -ne $accept)){throw 'Unexpected interactive outcome.'}
        $cases.Add(@{name=$name;interactive=$true;accepted=$accept;exit=$p.ExitCode;passed=$true})
    } finally {$p.Dispose()}
}
$failure=$null
try {
    RunSilent 'one' @('/SILENT') $false;Marker ''
    RunSilent 'one' @('/VERYSILENT') $false;Marker ''
    RunSilent 'one' @('/VERYSILENT','/UPDATEHANDOFF=00000000000000000000000000000000') $false;Marker ''
    RunSilent 'one' @('/VERYSILENT','/ACCEPTDISTRIBUTABLETERMS=wrong') $false;Marker ''
    RunSilent 'one' @('/SILENT',('/ACCEPTDISTRIBUTABLETERMS='+$fixture.one)) $true;Marker 'one'
    RunSilent 'same' @('/VERYSILENT') $true;Marker 'same'
    RunSilent 'same' @('/SILENT') $true;Marker 'same'
    RunSilent 'same' @('/VERYSILENT','/ACCEPTDISTRIBUTABLETERMS=wrong') $false;Marker 'same'
    RunSilent 'changed' @('/VERYSILENT') $false;Marker 'same'
    RunSilent 'changed' @('/SILENT',('/ACCEPTDISTRIBUTABLETERMS='+$fixture.one)) $false;Marker 'same'
    RunSilent 'same' @('/VERYSILENT') $true;Marker 'same'
    RunSilent 'changed' @('/VERYSILENT',('/ACCEPTDISTRIBUTABLETERMS='+$fixture.two)) $true;Marker 'changed'
    if($Interactive){RunInteractive 'interactive' $true;RunSilent 'interactive' @('/VERYSILENT') $true;RunInteractive 'cancel' $false;RunSilent 'cancel' @('/VERYSILENT') $false}
    $uninstaller=Join-Path $root 'main\unins000.exe'
    $p=Start-Process $uninstaller -ArgumentList '/VERYSILENT /NORESTART' -PassThru -WindowStyle Hidden
    try{if(!$p.WaitForExit(30000) -or $p.ExitCode -ne 0){throw 'Fixture uninstall failed.'}}finally{$p.Dispose()}
    RunSilent 'one' @('/VERYSILENT') $false;Marker ''
} catch {$failure=$_.Exception.Message}
finally {
    foreach($scope in @('main','interactive','cancel')){
        $uninstaller=Join-Path $root ($scope+'\unins000.exe')
        # Inno can defer deletion of its EXE after the DAT has already been removed.
        # A second cleanup launch at that point opens a missing-DAT error dialog.
        $uninstallData=Join-Path $root ($scope+'\unins000.dat')
        if((Test-Path -LiteralPath $uninstaller) -and (Test-Path -LiteralPath $uninstallData)){$p=Start-Process $uninstaller -ArgumentList '/VERYSILENT /NORESTART' -PassThru -WindowStyle Hidden;try{$null=$p.WaitForExit(30000)}finally{$p.Dispose()}}
    }
    @{passed=($null -eq $failure);error=$failure;interactive=[bool]$Interactive;cases=$cases.ToArray()} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Directory 'result.json') -Encoding UTF8
}
if($failure){throw $failure}
Write-Output "Actual native terms acceptance passed: $($cases.Count) cases."
