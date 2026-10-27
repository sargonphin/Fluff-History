$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('FluffHistory-SdkPolicy-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
$checks = 0
try {
    $fake = Join-Path $temporary 'dotnet.ps1'
    foreach ($case in @(
        @{ roll='disable'; selected='10.0.402'; exit=0; accepted=$true },
        @{ roll='disable'; selected='10.0.403'; exit=0; accepted=$false },
        @{ roll='disable'; selected='10.0.402'; exit=1; accepted=$false },
        @{ roll='latestPatch'; selected='10.0.402'; exit=0; accepted=$false }
    )) {
        @{sdk=@{version='10.0.402';rollForward=$case.roll;allowPrerelease=$false}} |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $temporary 'global.json') -Encoding UTF8
        "Write-Output '$($case.selected)'; `$global:LASTEXITCODE = $($case.exit)" |
            Set-Content -LiteralPath $fake -Encoding UTF8
        $accepted = $true
        try { & "$PSScriptRoot\Test-DotNetSdk.ps1" -Repository $temporary -DotNet $fake | Out-Null }
        catch { $accepted = $false }
        if ($accepted -ne $case.accepted) { throw 'SDK selection regression.' }
        $checks++
    }
    Write-Output "SDK policy: $checks cases passed (exact, patch mismatch, command failure, roll-forward rejection)."
} finally {
    # Only this invocation's freshly created fixture is removed.
    if ([IO.Path]::GetFullPath($temporary).StartsWith([IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $temporary -Recurse -Force
    }
}
