$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repo=Split-Path $PSScriptRoot -Parent
$temporary=Join-Path ([IO.Path]::GetTempPath()) ('FluffHistory-TermsPolicy-'+[guid]::NewGuid().ToString('N'))
$nuget=if($env:NUGET_PACKAGES){$env:NUGET_PACKAGES}else{Join-Path $env:USERPROFILE '.nuget\packages'}
$policyPath=Join-Path $temporary 'installer\DistributionTermsPolicy.json'
$checks=0
function Reject([scriptblock]$action,[string]$reason) {
    $rejected=$false
    try{& $action | Out-Null}catch{if(!$_.Exception.Message.Contains($reason)){throw};$rejected=$true}
    if(!$rejected){throw 'Invalid distribution policy was accepted.'};$script:checks++
}
try {
    foreach($relative in @('installer\DistributionTermsPolicy.json','installer\DistributionTerms.txt','src\FluffHistory.Desktop\packages.lock.json')){
        $target=Join-Path $temporary $relative;New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $repo $relative) -Destination $target
    }
    $policy=Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
    $notice=Join-Path $temporary ('installer\ThirdPartyNotices\'+$policy.sdkNet.notice)
    New-Item -ItemType Directory -Path (Split-Path $notice -Parent) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repo ('installer\ThirdPartyNotices\'+$policy.sdkNet.notice)) -Destination $notice
    $cache=Join-Path $temporary 'packages'
    foreach($component in $policy.appSdk){
        $path=$component.id+'\'+$component.version+'\license.txt';$target=Join-Path $cache $path
        New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $nuget $path) -Destination $target
    }
    $get=Join-Path $PSScriptRoot 'Get-DistributionTerms.ps1'
    $baseline=& $get -Repository $temporary -NuGetRoot $cache;$checks++
    $agreement=Join-Path $temporary 'installer\DistributionTerms.txt';$original=[IO.File]::ReadAllText($agreement)
    [IO.File]::WriteAllText($agreement,$original.Replace("`r`n","`n").Replace("`n","`r`n"),[Text.UTF8Encoding]::new($true))
    if((& $get -Repository $temporary -NuGetRoot $cache).Sha256 -cne $baseline.Sha256){throw 'BOM/newlines changed agreement identity.'};$checks++
    [IO.File]::WriteAllText($agreement,$original+"`nMaterial fixture-only clause.`n",[Text.UTF8Encoding]::new($false))
    if((& $get -Repository $temporary -NuGetRoot $cache).Sha256 -ceq $baseline.Sha256){throw 'Changed agreement reused prior consent.'};$checks++
    [IO.File]::WriteAllText($agreement,$original,[Text.UTF8Encoding]::new($false))
    $sourcePolicy=[IO.File]::ReadAllText($policyPath)
    $policy.revision='2000-01-01.1';$policy | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $policyPath
    Reject {& $get -Repository $temporary -NuGetRoot $cache} 'revision mismatch'
    [IO.File]::WriteAllText($policyPath,$sourcePolicy)
    $policy=Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json;$policy.appSdk=@($policy.appSdk | Select-Object -Skip 1)
    $policy | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $policyPath
    Reject {& $get -Repository $temporary -NuGetRoot $cache} 'Incomplete Microsoft'
    [IO.File]::WriteAllText($policyPath,$sourcePolicy)
    $policy=Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
    $first=$policy.appSdk[0];$license=Join-Path $cache ($first.id+'\'+$first.version+'\license.txt')
    [IO.File]::AppendAllText($license,'changed')
    Reject {& $get -Repository $temporary -NuGetRoot $cache} 'license changed'
    Copy-Item -LiteralPath (Join-Path $nuget ($first.id+'\'+$first.version+'\license.txt')) -Destination $license -Force
    [IO.File]::AppendAllText($notice,'changed')
    Reject {& $get -Repository $temporary -NuGetRoot $cache} 'notice changed'
    Write-Output "Distribution policy regression passed: $checks cases."
} finally {
    $full=[IO.Path]::GetFullPath($temporary);$parent=[IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if(!$full.StartsWith($parent,[StringComparison]::OrdinalIgnoreCase) -or (Split-Path $full -Leaf) -notmatch '^FluffHistory-TermsPolicy-[0-9a-f]{32}$'){throw 'Unsafe fixture cleanup path.'}
    if(Test-Path -LiteralPath $full){Remove-Item -LiteralPath $full -Recurse -Force}
}
