# Building Fluff-History

Fluff-History is a pre-release Windows 11 x64 application. The Desktop project and
Setup require Windows build 22000 or later; the current compile target is 26100.
Windows is the authoritative build and release-validation platform. Other operating
systems are outside the supported product and release-readiness matrix.

## Prerequisites

- Git with complete history for canonical build-version calculation.
- .NET SDK selected by [global.json](../global.json): 10.0.402, latest patch roll-forward.
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

Without OutputDirectory, both packaging scripts use the versioned ignored
`artifacts/releases/<version>` directory under the checkout. An explicit override
can keep archives elsewhere; all outputs must remain new or empty. Canonical Setup
restores locked dependencies, validates localization,
license approvals, installer lifecycle and version policy, publishes Desktop,
Agent and InstallSupport, builds native images and a sparse identity package,
collects notices, signs product files and creates a hash inventory. It rejects
conflicting shared publish files and nonempty archives.

Setup requires the [Microsoft component terms](../installer/DistributionTerms.txt).
Interactive installation presents them before product activation. Silent installs
require prior same-terms acceptance or an explicit `/ACCEPTDISTRIBUTABLETERMS=<hash>`
argument after review. The hash is in build-info.json and covers the normalized
agreement text. See [licensing](LICENSING.md) for unattended installs and updates.

The default `ReleaseVersion` is `development`. Product PE/package identity is
`0.24.1.<Git height>` unless an explicitly valid four-part version is supplied.
Public semantic release identity is separate; see [updates and releases](UPDATES-AND-RELEASES.md).
The current signing path uses a local Developer Preview certificate and records
that fact. This path produces locally signed development packages; publisher-trusted
or Store delivery requires its respective signing workflow. No signing private key
belongs in source control.

`Build-DeveloperPreview.ps1` and the install/uninstall development scripts remain
legacy diagnostics. Ordinary deployment uses the unified Setup. Dirty smoke builds
require explicit arguments and are recorded as such; they are not canonical releases.

## Components and provenance

The solution contains Core, Storage, Engine, Application, Windows, Desktop, Agent,
InstallSupport and the test project. Standalone localization/runtime/OneDrive
tools live under `tools/`. Native Launcher and Shell sources live under `native/`.
Resource/catalog generation uses checked-in inputs, with stale-output checks;
installer translations are never fetched during a build.

`build-info.json`, the signed image metadata and `installed-files.json` record build
and payload facts. Actual dependency versions come from resolved assets/lockfiles;
the build-info Windows App SDK label identifies the selected 2.5.1 component family and should
not be used as a component-version inventory. [TESTING](TESTING.md) explains the
checks and limits; [THIRD-PARTY-NOTICES](../THIRD-PARTY-NOTICES.md) indexes exact notices.
