# Executes the real build-script admission statements, stopping before SDK/compiler/publish work.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$scriptDirectory = $PSScriptRoot
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('FluffHistory-BuildPolicy-' + [guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $temporary) { throw 'Test directory collision.' }
New-Item -ItemType Directory -Path $temporary | Out-Null
$checks = 0
function RunAdmission([string]$prefix, [string]$output, [bool]$allowDirty = $false) {
    $repo = $temporary; $repository = $temporary
    $OutputDirectory = $output; $AllowUncommitted = $allowDirty
    $LocalizationCheckpoint = $false; $DevelopmentUpdateTesting = $false
    $ProductVersion = '1.2.3.4'; $ReleaseVersion = 'development'
    . ([scriptblock]::Create($prefix))
    [pscustomobject]@{ Directory = $OutputDirectory; Version = $version }
}
function Reject([scriptblock]$action, [string]$expectedError) {
    $rejected = $false
    try { & $action | Out-Null } catch {
        if (!$_.Exception.Message.StartsWith($expectedError, [StringComparison]::Ordinal)) { throw }
        $rejected = $true
    }
    if (!$rejected) { throw 'Unsafe build admission was accepted.' }
    $script:checks++
}
Push-Location $temporary
try {
    & git init --quiet
    if ($LASTEXITCODE) { throw 'Cannot initialize test repository.' }
    & git config core.autocrlf false
    if ($LASTEXITCODE) { throw 'Cannot configure fixture line endings.' }
    [IO.File]::WriteAllText((Join-Path $temporary '.gitignore'), "artifacts/`n")
    & git add .gitignore
    & git -c user.name=BuildPolicyTest -c user.email=build-policy@example.invalid commit --quiet -m 'Fixture'
    if ($LASTEXITCODE) { throw 'Cannot commit test fixture.' }
    foreach ($name in @('Build-Setup.ps1', 'Build-DeveloperPreview.ps1')) {
        $tokens = $null; $errors = $null
        $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $scriptDirectory $name), [ref]$tokens, [ref]$errors)
        if ($errors) { throw 'Build script syntax error.' }
        $body = @($ast.EndBlock.Statements | Where-Object { $_ -is [Management.Automation.Language.TryStatementAst] })
        if ($body.Count -ne 1) { throw 'Expected one build admission block.' }
        $statements = @(); $stopped = $false
        foreach ($statement in $body[0].Body.Statements) {
            $text = $statement.Extent.Text
            if (($name -eq 'Build-Setup.ps1' -and $text.StartsWith('if (!$InnoCompiler)')) -or
                ($name -eq 'Build-DeveloperPreview.ps1' -and $text.StartsWith('$sdk ='))) { $stopped = $true; break }
            $statements += $text
        }
        if (!$stopped) { throw 'Cannot isolate admission before packaging.' }
        $prefix = ($statements -join "`n").Replace('$PSScriptRoot', '$scriptDirectory')
        $actual = RunAdmission $prefix ''
        $expected = Join-Path (Join-Path $temporary 'artifacts\releases') $actual.Version
        if ($actual.Directory -cne $expected) { throw 'Default output is not the versioned ignored artifact directory.' }
        $checks++
        $explicit = Join-Path $temporary ('artifacts\custom-' + $name)
        if ((RunAdmission $prefix $explicit).Directory -cne $explicit) { throw 'Explicit output override was ignored.' }
        $checks++
        $collisionError = if ($name -eq 'Build-Setup.ps1') { 'Installer archive must be new/empty.' } else { 'Artifact output must be empty.' }
        $dirtyError = if ($name -eq 'Build-Setup.ps1') { 'Canonical installers require committed source;' } else { 'Commit source before producing a development artifact.' }
        foreach ($path in @($expected, $explicit)) {
            New-Item -ItemType Directory -Path $path -Force | Out-Null
            $marker = Join-Path $path 'preserved.txt'; [IO.File]::WriteAllText($marker, 'existing artifact')
            Reject { RunAdmission $prefix $path } $collisionError
            if ([IO.File]::ReadAllText($marker) -cne 'existing artifact') { throw 'Collision changed existing output.' }
        }
        $dirty = Join-Path $temporary 'uncommitted.txt'; [IO.File]::WriteAllText($dirty, 'dirty')
        try {
            Reject { RunAdmission $prefix '' } $dirtyError
            if ($name -eq 'Build-Setup.ps1') {
                Reject { RunAdmission $prefix '' $true } $dirtyError
                $smoke = Join-Path $temporary 'artifacts\dirty-smoke'
                if ((RunAdmission $prefix $smoke $true).Directory -cne $smoke) { throw 'Explicit dirty smoke output rejected.' }
                $checks++
            }
        } finally { Remove-Item -LiteralPath $dirty }
    }
    Write-Output "Build output/admission regression passed: $checks checks; no compiler or packaging invoked."
} finally {
    Pop-Location
    $resolved = [IO.Path]::GetFullPath($temporary)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (!$resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path $resolved -Leaf) -notmatch '^FluffHistory-BuildPolicy-[0-9a-f]{32}$') { throw 'Unsafe fixture cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
