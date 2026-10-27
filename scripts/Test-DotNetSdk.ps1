[CmdletBinding()]
param([string]$DotNet = 'dotnet', [string]$Repository = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$policy = Get-Content -LiteralPath (Join-Path $Repository 'global.json') -Raw | ConvertFrom-Json
if ($policy.sdk.version -notmatch '^\d+\.\d+\.\d+$' -or
    $policy.sdk.rollForward -cne 'disable' -or $policy.sdk.allowPrerelease -ne $false) {
    throw 'An exact stable .NET SDK with roll-forward disabled is required.'
}
Push-Location $Repository
try {
    $selected = @(& $DotNet --version)
    if ($LASTEXITCODE -ne 0 -or $selected.Count -ne 1 -or $selected[0].Trim() -cne $policy.sdk.version) {
        throw "Selected .NET SDK does not match global.json ($($policy.sdk.version)). Install that exact SDK."
    }
    Write-Output "Exact .NET SDK verified: $($policy.sdk.version)."
} finally { Pop-Location }
