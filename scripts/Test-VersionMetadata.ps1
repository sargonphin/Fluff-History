[CmdletBinding()]
param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$ProductVersion, [string]$ReleaseVersion)
$ErrorActionPreference = 'Stop'
$expected = & "$PSScriptRoot\Get-ProductVersion.ps1" -ProductVersion $ProductVersion
$info = (Get-Item -LiteralPath $Path).VersionInfo
$fileNumeric = '{0}.{1}.{2}.{3}' -f $info.FileMajorPart,$info.FileMinorPart,$info.FileBuildPart,$info.FilePrivatePart
$productNumeric = '{0}.{1}.{2}.{3}' -f $info.ProductMajorPart,$info.ProductMinorPart,$info.ProductBuildPart,$info.ProductPrivatePart
# Inno reserves trailing space in its textual fields; compare the semantic value
# while still checking every fixed numeric field independently.
if ($info.FileVersion.TrimEnd(' ',[char]0) -cne $expected -or $info.ProductVersion.TrimEnd(' ',[char]0) -cne $expected -or
    $fileNumeric -cne $expected -or $productNumeric -cne $expected) {
    throw "PE version mismatch for $Path (file=$($info.FileVersion)/$fileNumeric, product=$($info.ProductVersion)/$productNumeric; expected=$expected)."
}
if ($ReleaseVersion) {
    $release = & "$PSScriptRoot\Get-ReleaseVersion.ps1" -ReleaseVersion $ReleaseVersion -ProductVersion $ProductVersion
    if ($info.ProductName.TrimEnd(' ',[char]0) -cne 'Fluff-History' -or $info.CompanyName.TrimEnd(' ',[char]0) -cne 'Fish & Dog Software' -or
        $info.FileDescription.TrimEnd(' ',[char]0) -cne ('Fluff-History Setup [' + $release + ']')) {
        throw 'Signed Setup product/release identity resource mismatch.'
    }
}
