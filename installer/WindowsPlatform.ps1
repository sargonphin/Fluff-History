function GetWindowsPlatformPolicy([string]$directory = $PSScriptRoot) {
    [xml]$policy = [IO.File]::ReadAllText((Join-Path $directory 'WindowsPlatform.props'))
    $values = $policy.Project.PropertyGroup
    [pscustomobject]@{
        TechnicalMinimum = [version]$values.WindowsTechnicalMinimum
        ProductMinimum = [version]$values.WindowsProductMinimum
    }
}
function SupportsExplorerContextCommand([version]$version = [Environment]::OSVersion.Version) {
    return $version -ge (GetWindowsPlatformPolicy).ProductMinimum
}
