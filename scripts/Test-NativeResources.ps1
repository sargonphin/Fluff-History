$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path $PSScriptRoot -Parent
$generated = Join-Path $repo 'native\Shell\Generated'
$manifest = Get-Content -LiteralPath (Join-Path $generated 'catalog-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
function TextHash([string]$value) {
    $hash = [Security.Cryptography.SHA256]::Create()
    try { [BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($value.Replace("`r`n","`n")))).Replace('-','') }
    finally { $hash.Dispose() }
}
foreach ($catalogInput in $manifest.inputs.PSObject.Properties) {
    [xml]$xml = [IO.File]::ReadAllText((Join-Path $repo $catalogInput.Name))
    $parts = New-Object 'Collections.Generic.List[string]'
    foreach ($key in $manifest.keys) {
        $entry = @($xml.root.data | Where-Object { $_.GetAttribute('name') -ceq $key })
        if ($entry.Count -ne 1) { throw "Missing/duplicate native resource $key in $($catalogInput.Name)." }
        $parts.Add($key); $parts.Add($entry[0].SelectSingleNode('value').InnerText)
    }
    if ((TextHash ([string]::Join([string][char]0,$parts.ToArray()))) -cne $catalogInput.Value) {
        throw 'Native resource source changed. Run localization generate-native before building.'
    }
}
foreach ($output in $manifest.outputs.PSObject.Properties) {
    if ((TextHash ([IO.File]::ReadAllText((Join-Path $generated $output.Name)))) -cne $output.Value) {
        throw 'Generated native resources changed. Regenerate from the canonical catalogs.'
    }
}
Write-Output "Native resource inputs/output current: $(@($manifest.languages).Count) locales."
