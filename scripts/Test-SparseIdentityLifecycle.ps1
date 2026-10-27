$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$installer = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\installer'))
. (Join-Path $installer 'SparseIdentityTrust.ps1')
$policyReader = (Get-Command GetSparseIdentityPolicy).ScriptBlock
$tokens=$null;$errors=$null
$syntax = [Management.Automation.Language.Parser]::ParseFile((Join-Path $installer 'Lifecycle.ps1'),[ref]$tokens,[ref]$errors)
if ($errors) { throw 'Lifecycle syntax error.' }
$function = $syntax.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'RegisterProductIdentity'},$false)
if (@($function).Count -ne 1) { throw 'Missing actual identity lifecycle.' }
. ([scriptblock]::Create($function.Extent.Text))
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('FluffHistory-SparseLifecycle-' + [guid]::NewGuid().ToString('N'))
$records = $temporary; $payload = Join-Path $temporary 'payload'
New-Item -ItemType Directory -Path $temporary | Out-Null
$Version='0.99.0.2'; $publicComposition=$true; $checks=0
function GetSparseIdentityPolicy { param([string]$directory) & $policyReader $installer }
function ProductPackage { return $script:package }
function StopComponents { }
function MarkPending { param($name,$value) $script:marks[$name]=$value }
function SparseIdentityTrust { param($mode,$directory) $script:trustCalls++; if($script:fail -eq 'trust'){throw 'Synthetic trust failure.'} }
function Add-AppxPackage { param($Path,$ExternalLocation)
    $script:adds++; if($script:fail -eq 'registration'){throw 'Synthetic registration failure.'}
    $script:package=[pscustomobject]@{Version=$Version}
}
function Remove-AppxPackage { [CmdletBinding()]param([Parameter(ValueFromPipeline)]$InputObject)
    process { $script:removes++; $script:package=$null }
}
try {
    foreach ($condition in @('success','trust','registration','upgrade-registration','retry','bad-pin','newer')) {
        $script:package=$null; $script:fail=$condition; $script:adds=0; $script:removes=0; $script:trustCalls=0; $script:marks=@{}
        $Fault=''; $transaction=[pscustomobject]@{previous=$null}
        $pin=Get-Content -LiteralPath (Join-Path $installer 'SparseIdentity.json') -Raw | ConvertFrom-Json
        $info=[pscustomobject]@{packageIdentity=$pin.packageName;packageFamily=$pin.packageFamilyName;certificateThumbprint=$pin.certificateThumbprint}
        if ($condition -eq 'upgrade-registration') {
            $script:package=[pscustomobject]@{Version='0.99.0.1'}; $script:fail='registration'
            $transaction.previous=[pscustomobject]@{version='0.99.0.1'}
        }
        if ($condition -eq 'retry') {
            $script:package=[pscustomobject]@{Version=$Version}; $transaction.previous=[pscustomobject]@{version=$Version}
        }
        if ($condition -eq 'bad-pin') { $info.certificateThumbprint='changed' }
        if ($condition -eq 'newer') { $script:package=[pscustomobject]@{Version='0.99.0.3'} }
        $warning=Join-Path $records 'integration-warning.txt'
        if(Test-Path -LiteralPath $warning){Remove-Item -LiteralPath $warning}
        if($condition -in @('bad-pin','newer')) {
            $rejected=$false
            try { RegisterProductIdentity $info $transaction | Out-Null } catch {
                if ($_.Exception.Message -notmatch 'metadata/pin mismatch|newer product identity') { throw }; $rejected=$true
            }
            if(!$rejected -or $script:adds -or $script:removes -or $script:trustCalls){throw 'Invalid identity crossed trust/registration admission.'}
        } else {
            $available = RegisterProductIdentity $info $transaction
            $expected=$condition -in @('success','retry')
            if($available -ne $expected -or (Test-Path -LiteralPath $warning) -eq $expected){throw 'Integration status/warning mismatch.'}
            if($condition -eq 'retry' -and $script:adds){throw 'Same-version repair unnecessarily replaced identity.'}
            if($condition -eq 'upgrade-registration' -and ($script:removes -ne 1 -or !$script:marks.identityChanged)){throw 'Stale old integration was not owned/retired.'}
        }
        $checks++
    }
    "Sparse lifecycle: $checks actual-function cases passed; no packages or trust stores were changed."
} finally {
    if (![IO.Path]::GetFullPath($temporary).StartsWith([IO.Path]::GetTempPath(),[StringComparison]::OrdinalIgnoreCase)) { throw 'Lifecycle fixture escaped temporary storage.' }
    Remove-Item -LiteralPath $temporary -Recurse -Force
}
