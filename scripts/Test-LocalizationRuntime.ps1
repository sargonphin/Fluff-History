[CmdletBinding()]
param([Parameter(Mandatory)][string]$Payload, [Parameter(Mandatory)][string]$ScratchDirectory,
    [Parameter(Mandatory)][string]$MakePri, [string]$DotNet = 'dotnet')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ([Environment]::OSVersion.Platform -ne 'Win32NT') { throw 'MRT runtime validation requires Windows.' }
$Payload = [IO.Path]::GetFullPath($Payload)
$scratch = Join-Path ([IO.Path]::GetFullPath($ScratchDirectory)) ([guid]::NewGuid().ToString('N'))
$fixture = Join-Path $scratch 'Fixture'
New-Item -ItemType Directory -Force -Path (Join-Path $fixture 'Strings\en-US'),(Join-Path $fixture 'Strings\de-DE') | Out-Null
# Deliberately incomplete, NONSHIPPING fixture. Never alter a shipping catalog or its parity rules.
$english = @'
<?xml version="1.0" encoding="utf-8"?><root><resheader name="resmimetype"><value>text/microsoft-resx</value></resheader><resheader name="version"><value>2.0</value></resheader><data name="UILanguage"><value>en-US</value></data><data name="Translated"><value>Ready</value></data><data name="EnglishOnly"><value>English fallback</value></data><data name="EnglishOnlyFormat"><value>Files: {0:N0}</value></data></root>
'@
$german = @'
<?xml version="1.0" encoding="utf-8"?><root><resheader name="resmimetype"><value>text/microsoft-resx</value></resheader><resheader name="version"><value>2.0</value></resheader><data name="UILanguage"><value>de-DE</value></data><data name="Translated"><value>Bereit</value></data></root>
'@
[IO.File]::WriteAllText((Join-Path $fixture 'Strings\en-US\Resources.resw'),$english,(New-Object Text.UTF8Encoding($false)))
[IO.File]::WriteAllText((Join-Path $fixture 'Strings\de-DE\Resources.resw'),$german,(New-Object Text.UTF8Encoding($false)))
$config = Join-Path $scratch 'priconfig.xml'
& $MakePri createconfig /cf $config /dq en-US /o | Out-Null
if ($LASTEXITCODE) { throw 'MRT test configuration failed.' }
[xml]$xml = Get-Content -LiteralPath $config -Raw
foreach ($packaging in @($xml.SelectNodes('//packaging'))) { $null = $packaging.ParentNode.RemoveChild($packaging) }
$xml.Save($config)
$pri = Join-Path $scratch 'fallback.pri'
& $MakePri new /pr $fixture /cf $config /of $pri /in FluffHistory.Localization.Test /o | Out-Null
if ($LASTEXITCODE) { throw 'MRT nonshipping fixture compilation failed.' }
$project = Join-Path $PSScriptRoot '..\tools\FluffHistory.Localization.Runtime\FluffHistory.Localization.Runtime.csproj'
$artifacts = Join-Path $scratch 'Artifacts'
& $DotNet restore $project --locked-mode "-p:ArtifactsPath=$artifacts" "-p:ProbePayload=$Payload"
if ($LASTEXITCODE) { throw 'MRT test-host locked restore failed.' }
$result = Join-Path $scratch 'runtime-fallback.json'
& $DotNet run --project $project -c Release --no-restore "-p:ArtifactsPath=$artifacts" "-p:ProbePayload=$Payload" -- $Payload $pri $result
if ($LASTEXITCODE) { throw 'MRT per-key fallback regression failed.' }
Write-Output "MRT fallback evidence: $result"
