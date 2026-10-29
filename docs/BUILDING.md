# Building Fluff-History

The current 1.1 source targets Windows 11 x64 and Windows 10 22H2 x64,
build 19045. The `v1.0.0` tag remains Windows 11-only. The compile target
remains 26100. The Desktop's SDK contract baseline is 19041; ordinary Setup requires
19045. These are distinct from the tested product target and Microsoft servicing.
Windows is the authoritative build and release-validation platform. Other operating
systems are outside the supported product and release-readiness matrix.

`installer/WindowsPlatform.props` defines the technical and tested product minimums.
MSBuild, Setup, build metadata and platform guards consume this policy. Both Windows
families use the same classic static Shell verb and native Properties page.

Windows 10 compatibility does not imply that Microsoft supports every Windows 10
edition, .NET deployment or Windows App SDK servicing configuration. In particular,
22H2 and supported LTSC releases are different targets.

## Prerequisites

- Git. Complete history is needed only for the default development build version;
  public releases always use an explicitly allocated version.
- Exact .NET SDK selected by [global.json](../global.json): 10.0.402, with roll-forward
  disabled. Install this version alongside newer SDKs; SDK updates require deliberate
  review of implicit dependencies and locked restore. Run `scripts/Test-DotNetSdk.ps1`
  to verify selection. CI and canonical Setup enforce the same policy.
- Windows: Visual Studio C++ x64 tools and Windows SDK resources/compiler/linker.
- Setup: Inno Setup **7.1.0**, with its unmodified language/license inputs.
- NuGet access for the first locked restore. Versions are in
  [Directory.Packages.props](../Directory.Packages.props) and project lockfiles.

There is no database server or separately installed .NET runtime prerequisite for
the self-contained Setup payload. The Windows App SDK components are also deployed
with the application. See [licensing](LICENSING.md) before redistributing binaries.

## Ordinary Windows validation

From the repository root, in PowerShell:

```powershell
dotnet restore FluffHistory.sln --locked-mode
dotnet build FluffHistory.sln -c Release --no-restore "-p:GitCommit=$(git rev-parse HEAD)"
dotnet test tests/FluffHistory.Tests/FluffHistory.Tests.csproj -c Release --no-build --no-restore --filter 'Category!=MetadataDiagnostic'
./scripts/Test-Localization.ps1
./scripts/Test-RecoveryDependencies.ps1
./scripts/Test-RecoveryDependencyGuard.ps1
./scripts/Test-BuildOutputPolicy.ps1
./scripts/Test-WindowsPlatformPolicy.ps1
```

The Windows Desktop build compiles native Shell components and runs the native
checks, including malformed responses, broker disconnection, outstanding-read
closure and queued-worker lifetime stress. Build output is ignored by Git.

For a disposable out-of-tree build, pass the same absolute `ArtifactsPath` to
restore/build/test, and `NativeShellOutputDirectory` to build. Point TEMP/TMP at
disposable storage before launching the SDK. Keep bulk diagnostics outside source
trees and release archives. The optional legacy account probe's script/adapter are
copied beside the test assembly, including with redirected output; live account
execution remains separately opt-in.

## Canonical development installer

Use committed source and a **new or empty** output directory. For example,
with paths chosen on a disposable build volume:

```powershell
./scripts/Build-Setup.ps1 -DotNet dotnet `
  -OutputDirectory D:\FluffHistoryOutput\Preview `
  -StagingRoot D:\FluffHistoryWork\Setup
```

Without OutputDirectory, `Build-Setup.ps1` uses the versioned ignored
`artifacts/releases/<version>` directory under the checkout. An explicit override
can keep archives elsewhere; all outputs must remain new or empty. Canonical Setup
restores locked dependencies, validates localization,
license approvals, installer lifecycle and version policy, publishes Desktop,
Agent and InstallSupport, builds native Properties and launcher images,
collects notices, signs product files and creates a hash inventory. It rejects
conflicting shared publish files and nonempty archives.

Setup requires the [Microsoft component terms](../installer/DistributionTerms.txt).
Interactive installation presents them before product activation. Silent installs
require prior same-terms acceptance or an explicit `/ACCEPTDISTRIBUTABLETERMS=<hash>`
argument after review. The hash is in build-info.json and covers the normalized
agreement text. See [licensing](LICENSING.md) for unattended installs and updates.

The default `ReleaseVersion` is `development`. Product PE version is
`0.24.1.<Git height>` unless an explicitly valid four-part version is supplied.
Public semantic release identity is separate; see [updates and releases](UPDATES-AND-RELEASES.md).
The default `SigningMode Development` uses the local Developer Preview certificate
for product images and Setup. Setup does not install its certificate. Signing is
separate from the public composition below. No signing private key belongs in source.

## Public installer composition

`SigningMode UnsignedPublic` leaves Setup and all Fluff-History PE files unsigned.
It requires no certificate or private key, and creates no sparse identity package.
Context integration is entirely per-user and needs no certificate trust or elevation.
The build-info schema is 2; `peSignerThumbprint` describes only optional development
PE signing. Current metadata has no package-family/identity/trust fields.

For publisher builds, supply explicit semantic/four-part identities, for example:

```powershell
./scripts/Build-Setup.ps1 -SigningMode UnsignedPublic `
  -ReleaseVersion <semantic-version> -ProductVersion <allocated-four-part-version> `
  -OutputDirectory <new-empty-archive> -StagingRoot <disposable-work-directory>
```

The first public `1.0.0` build is allocated `1.0.0.1`. For a given semantic core,
increase the fourth UInt16 field for every separately produced distributable build;
never reuse an existing four-part product version with different contents. A new semantic
core starts at revision1. Compare against all previously distributed Windows versions
before allocation; never use Git height for release identity. Archive the exact source,
build facts and hashes. Development PE signing does not authorize public automatic updates:
the [updater](UPDATES-AND-RELEASES.md) still requires an approved Authenticode signer.

Dirty smoke builds require explicit arguments and are recorded as such; they are not
canonical releases. Ordinary deployment uses only the unified Setup.

## Components and provenance

The solution contains Core, Storage, Engine, Application, Windows, Desktop, Agent,
InstallSupport and the test project. Standalone localization/runtime/OneDrive
tools live under `tools/`. Native Launcher and Shell sources live under `native/`.
Resource/catalog generation uses checked-in inputs, with stale-output checks;
installer translations are never fetched during a build.

`build-info.json`, image metadata and `installed-files.json` record build
and payload facts. Actual dependency versions come from resolved assets/lockfiles;
the build-info Windows App SDK label identifies the selected 2.5.1 component family and should
not be used as a component-version inventory. [TESTING](TESTING.md) explains the
checks and limits; [THIRD-PARTY-NOTICES](../THIRD-PARTY-NOTICES.md) indexes exact notices.
