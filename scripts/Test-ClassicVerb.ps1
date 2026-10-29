$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$tokens=$null;$errors=$null
$syntax=[Management.Automation.Language.Parser]::ParseFile([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\installer\Lifecycle.ps1')),[ref]$tokens,[ref]$errors)
if ($errors) { throw 'Lifecycle syntax error.' }
foreach ($function in $syntax.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst]},$false)) { . ([scriptblock]::Create($function.Extent.Text)) }
$fixture='Software\Fluff-History-ClassicVerbTest-'+[guid]::NewGuid().ToString('N')
$classes=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($fixture)
$launcher='C:\Owned path\FluffHistory.ShellLauncher.exe'
$checks=0
try {
    $paths=@(ClassicVerbPaths)
    if (($paths -join '|') -cne '*\shell\FluffHistory.RestorePreviousVersion|Directory\shell\FluffHistory.RestorePreviousVersion') { throw 'Static verb scope drift.' };$checks++
    $neighbor=$classes.CreateSubKey('*\shell\OtherApp\command');$neighbor.SetValue('','unrelated');$neighbor.Dispose()
    $before=@(ClassicSnapshot $classes $launcher)
    # Model an interrupted first registration, then restore the actual empty predecessor.
    $partial=$classes.CreateSubKey($paths[0]);$partial.SetValue('MUIVerb',('@'+$launcher+',-3000'));$partial.Dispose()
    RestoreClassicSnapshot $classes $launcher $before
    foreach($path in $paths){$key=$classes.OpenSubKey($path);if($key){$key.Dispose();throw 'Partial first installation survived rollback.'};$checks++}
    ClassicVerbs 'Install' $classes $launcher
    ClassicVerbs 'Install' $classes $launcher
    foreach ($path in $paths) {
        $key=$classes.OpenSubKey($path)
        try {
            if ($key.GetValue('MUIVerb') -cne ('@'+$launcher+',-3000') -or $key.GetValue('Icon') -cne ('"'+$launcher+'",0') -or $key.GetValue('MultiSelectModel') -cne 'Single') { throw 'Static presentation drift.' };$checks+=3
            $command=$key.OpenSubKey('command');try {if($command.GetValue('') -cne ('"'+$launcher+'" "%1"')){throw 'Command quoting drift.'};$checks++}finally{$command.Dispose()}
        } finally { $key.Dispose() }
    }
    $classes.DeleteSubKeyTree($paths[1]);ClassicVerbs 'Install' $classes $launcher;$checks++
    $snapshot=@(ClassicSnapshot $classes $launcher)
    $classes.DeleteSubKeyTree($paths[1]);RestoreClassicSnapshot $classes $launcher $snapshot
    $restored=@(ClassicSnapshot $classes $launcher)
    if(($snapshot|ConvertTo-Json -Depth 6 -Compress) -cne ($restored|ConvertTo-Json -Depth 6 -Compress)){throw 'Existing registration rollback changed its predecessor.'};$checks++
    $collision=$classes.OpenSubKey($paths[1]+'\command',$true);$collision.SetValue('','other application');$collision.Dispose()
    foreach($mode in @('Install','Remove')){
        $rejected=$false;try{ClassicVerbs $mode $classes $launcher}catch{$rejected=$true}
        if(!$rejected){throw 'Unowned verb was modified.'};$checks++
        $key=$classes.OpenSubKey($paths[0]);if(!$key){throw 'Partial destructive cleanup before validation.'};$key.Dispose()
    }
    $collision=$classes.OpenSubKey($paths[1]+'\command',$true);$collision.SetValue('',(ClassicCommand $launcher));$collision.Dispose()
    ClassicVerbs 'Remove' $classes $launcher;ClassicVerbs 'Remove' $classes $launcher
    foreach($path in $paths){if($classes.OpenSubKey($path)){throw 'Owned verb remained.'};$checks++}
    $neighbor=$classes.OpenSubKey('*\shell\OtherApp\command');try{if($neighbor.GetValue('') -cne 'unrelated'){throw 'Neighbor damaged.'};$checks++}finally{$neighbor.Dispose()}
    foreach($invalid in @('relative.exe','C:\bad"name.exe','C:\%TEMP%\bad.exe')){$rejected=$false;try{ClassicCommand $invalid|Out-Null}catch{$rejected=$true};if(!$rejected){throw 'Unsafe executable path.'};$checks++}
    "Classic static verb: $checks ownership/quoting/repair/uninstall checks passed in isolated HKCU fixture."
}finally{$classes.Dispose();[Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($fixture,$false)}
