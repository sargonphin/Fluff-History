[CmdletBinding()]
param([string]$Repository = (Split-Path $PSScriptRoot -Parent), [string]$ProductVersion)
$ErrorActionPreference = 'Stop'
if (!$ProductVersion) {
    $height = & git -C $Repository rev-list --count HEAD
    if ($LASTEXITCODE -or $height -notmatch '^[0-9]+$') { throw 'Cannot determine product revision.' }
    $ProductVersion = "0.24.1.$height"
}
if ($ProductVersion -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$' -or
    @($ProductVersion.Split('.') | Where-Object { [long]$_ -gt 65535 }).Count -or $ProductVersion -eq '0.0.0.0') {
    throw 'ProductVersion must contain four canonical nonzero-version UInt16 fields.'
}
$ProductVersion
