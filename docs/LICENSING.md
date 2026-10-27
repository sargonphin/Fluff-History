# Licensing and provenance

Fluff-History's first-party source, documentation and composite artwork additions
are provided under the [MIT License](../LICENSE), with the public attribution
`Copyright (c) 2026 Fish & Dog Software`. Preserve that notice when redistributing
copies or substantial portions. Canonical Setup includes the same first-party
LICENSE in its installed payload and output archive.

Third-party material retains its own licenses and notices. MIT for the project's
own contributions does not relicense the Windows App SDK binaries, Windows OS
resources or other dependencies. This inventory records actual artifacts and
conditions; it is not a blanket legal approval of a distribution or license combination.

## Redistributed material

| Material / locked version | Evidence / terms | Treatment |
| --- | --- | --- |
| Self-contained Microsoft.NETCore.App runtime 10.0.13 | Restored runtime package LICENSE/THIRD-PARTY-NOTICES | Runtime is shipped; preserve its exact Microsoft/.NET and incorporated third-party notices |
| Microsoft.Windows.SDK.NET.Ref runtime pack 10.0.26100.57 | Runtime deps identify the pack; its nuspec references [Windows SDK terms](https://aka.ms/WinSDKLicenseURL) rather than embedding a license file | SDK.NET/WinRT runtime material ships; exact terms are included verbatim and the official REDIST list explicitly covers these runtime files |
| System.IO.Hashing 10.0.0 | NuGet nuspec MIT; THIRD-PARTY-NOTICES.TXT | Storage dependency; preserve package notices |
| Microsoft.Identity.Client / Broker 4.83.3 | Actual nupkgs declare MIT; pinned upstream text | Desktop/WAM only; exact notice in installer/ThirdPartyNotices |
| Microsoft.Identity.Client.NativeInterop 0.20.4 | Actual nupkg declares MIT; archive/content/notice hashes approved | Native auth runtime; do not extrapolate to later versions |
| Microsoft.IdentityModel.Abstractions 8.14.0 | Transitive nuspec MIT; exact upstream notice | Preserve its separate copyright/license |
| Windows App SDK Base 2.0.4, Foundation 2.3.12, WinUI 2.3.9, InteractiveExperiences 2.1.9 | Exact packages' Microsoft Windows App SDK license.txt | Self-contained selected components; not blanket MIT binary artifacts |
| Microsoft.Web.WebView2 1.0.3719.77 | Package LICENSE.txt/NOTICE.txt (Microsoft copyright, BSD-style conditions) | Transitive SDK material; package inclusion does not mean the app implements a browser or bundles an Evergreen installation |
| Inno Setup 7.1.0 setup/uninstall and language bases | Inno Setup License, retained translator credits | Compiler is build tooling; generated Setup/uninstall include Inno components |
| Icons8 Flat Color Icons folder derivative | artwork/LICENSE-Icons8.txt, MIT | Preserve base notice with generated application logos/ICO |
| Fluent System Icons History 20 Regular derivative | native/Shell/Assets/LICENSE.txt, MIT | Preserve notice with native Shell history icon |
| Static MSVC runtime portions | Microsoft toolchain redistribution terms | Native images link runtime statically; toolchain rights are separate from first-party source licensing |

The selected Windows App SDK components match the relevant subset of the 2.5.1
umbrella nuspec. The unused umbrella AI/ML/Search/Widgets/DWrite/runtime packages
are not dependencies simply because a build-info family label says 2.5.1.
Publish output and its inventory decide actual files, not the package name alone.

The Windows App SDK package license section 3 permits specified distributable files
and sets requirements concerning functionality, downstream terms, notices and other
conditions. Its upstream source license is not a substitute for the binary terms.
The downstream implementation below preserves those protections while keeping
first-party MIT separate. The requirements are distinct from a blanket prohibition
on redistribution. The
[upstream source license](https://github.com/microsoft/WindowsAppSDK/blob/main/LICENSE)
must be read separately from the exact restored package license.

Inno has its own permissive conditions, including retained origin/notices; it is not
MIT. See the [exact retained terms](../installer/Languages/InnoSetup-LICENSE.txt)
and [upstream license](https://jrsoftware.org/files/is/license.txt). The current build
copies the installed compiler's license into the product notices.

## Build/test-only dependencies

| Material / version | Declared evidence |
| --- | --- |
| .NET SDK 10.0.402 / Roslyn compiler tooling | SDK licensing/third-party material; localization audit references SDK Roslyn assemblies |
| Microsoft.NET.ILLink.Tasks 10.0.13 | MIT plus third-party notices; helper single-file build tooling |
| Microsoft.Windows.SDK.BuildTools 10.0.26100.6584 | Windows SDK terms; RC/link/sign/package tools, not a blanket MIT package |
| Microsoft.Windows.SDK.BuildTools.MSIX 1.7.251221100 | sdk_license.txt / NOTICE.txt |
| Microsoft.NET.Test.Sdk, CodeCoverage, TestPlatform.ObjectModel, TestHost 18.11.0 | NuGet MIT declarations and available test-platform notices |
| xunit/core/assert/extensibility 2.9.3; analyzers 1.18.0; runner.visualstudio 3.1.5 | Apache-2.0 declarations |
| xunit.abstractions 2.0.3 | Legacy licenseUrl; verify exact Apache-2.0 upstream terms when redistributing test tooling |
| GitHub Actions checkout/setup-dotnet/upload-artifact | Hosted CI actions are not application payload; workflow references upstream projects |

Build/test dependencies are distinct from the shipped application; a fork distributing
those tools must review their own terms. Dependency locks and restored assets identify
the graph, not a claim that every package file is shipped.

## Artwork and operating-system resources

The application composite source and reproducible ICO/logo conversion are in
[artwork](../artwork/README.md). Icons8 attribution applies to the base folder, not a
claim that Icons8 authored the added clock artwork. The first-party composite
contribution is subject to the first-party license status described above.

Known-folder/drive/Explorer/Task Scheduler/gear icons are loaded from the installed
Windows OS. Segoe Fluent Icons glyphs are referenced through the installed font.
These Windows resources are not copied into the repository or represented as MIT
assets. The separately bundled Fluent history SVG/ICO is covered by its retained
MIT notice. No third-party license text has been rewritten for style.

## Build validation and notice locations

[THIRD-PARTY-NOTICES](../THIRD-PARTY-NOTICES.md) indexes exact tracked notices.
Canonical Setup collects top-level LICENSE/NOTICE files (including extensionless
ones) from runtime and resolved Desktop dependencies and retains the pinned auth,
artwork and Inno notices. Its payload inventory provides actual file hashes/lengths.
This collector is useful evidence, not a substitute for reviewing each package's
distribution conditions and nested incorporated material before redistribution.
The implicit Windows SDK.NET runtime pack is outside the Desktop lockfile collection
and has no embedded top-level license text to copy. Its exact nuspec-linked license
RTF is therefore retained separately as
`installer/ThirdPartyNotices/WindowsSDK-NETRef-10.0.26100.57.rtf`, pinned by SHA-256
and copied verbatim into Setup. The official
[SDK redistribution list](https://learn.microsoft.com/en-us/legal/windows-sdk/redist#microsoftwindowssdknetref)
explicitly permits the pack's unmodified runtime files as part of an application.

`installer/RecoveryDependencyApproval.json` records reviewed authentication versions,
NuGet content hashes, archive SHA-256s and normalized notice hashes. Builds verify
pins, locks, resolved assets, actual archive bytes and MIT declarations; negative
regressions reject modifications. Upgrades require deliberate new artifact/license
review and policy/notices updates. No permanent version ceiling is asserted.

## Microsoft component terms and acceptance

[DistributionTerms.txt](../installer/DistributionTerms.txt) is Fluff-History's
downstream terms layer for the redistributed Microsoft components. It preserves
Microsoft's component use/redistribution restrictions, notices, intellectual-property
protections and warranty/liability protections, subject to mandatory law and separate
applicable rights. It does not replace first-party MIT, relicense independent
open-source components or pass every SDK developer-only obligation to end users.
Exact upstream licenses remain separate and unmodified in ThirdPartyNotices.

This is the project's documented engineering interpretation of Windows App SDK
section 3 and the nuspec-linked Windows SDK license section 2(a). Microsoft does
not prescribe a particular dialog or consent database. The implementation is not
Microsoft certification or formal legal approval. Publisher obligations such as
adding primary functionality, preserving distribution restrictions, retaining valid
notices and the applicable indemnity remain obligations of the distributor; they
are not silently transferred to the end user by a startup popup.

| Distribution path | Acceptance behavior |
| --- | --- |
| Interactive Setup | New terms require the standard license page before installation/activation. Declining/cancelling does not install the product. Previously accepted identical terms can be skipped |
| SILENT / VERYSILENT | Identical prior per-user acceptance is sufficient. Otherwise the operator must review the terms and pass the exact `/ACCEPTDISTRIBUTABLETERMS=<hash>` value. Missing or wrong assent is rejected before the wizard/product mutation, without a background agreement dialog |
| Automatic update | The helper never supplies an acceptance flag. Identical terms inherit the existing acceptance. Changed terms are refused, preserve the previous installation, and use the existing failed-update/old-Agent recovery path without an immediate retry loop |
| Manual in-app update | The verified helper opens ordinary interactive Setup so changed terms can be reviewed. Cancelling preserves the previous installation; the old Agent can resume through existing recovery |
| Older update helpers | A helper that always runs silent cannot manufacture acceptance. If it has no matching prior agreement, run the new Setup interactively or supply explicit operator assent once |

The sole acceptance value is a fingerprint in Inno Setup's existing **per-user
previous-installation/uninstall registry data**. It is not a settings JSON field,
Agent preference, repository control, history record or new consent database.
Successful Setup retains it; uninstall removes it with Inno's owned registration,
so a subsequent fresh install asks again. The value remembers assent, not security
authority: signature validation, exact update ownership and history authority remain
independent.

The fingerprint covers the complete agreement text with only UTF-8 BOM/newline
representation normalized. Any substantive change produces a different fingerprint
and requires new assent; other wording changes conservatively do so too. Release
or dependency versions alone do not invalidate unchanged terms. The source policy
pins exact Microsoft license evidence and rejects drift until deliberately reviewed.
When upstream obligations change, review whether the downstream agreement needs
changes; never treat a policy/hash update alone as permission to weaken protection.

For unattended installation, review the release's agreement and exact notices,
then use its accompanying build metadata:

```powershell
$info = Get-Content .\build-info.json -Raw | ConvertFrom-Json
.\Fluff-History-Setup.exe /VERYSILENT /NORESTART "/ACCEPTDISTRIBUTABLETERMS=$($info.distributionTermsSha256)"
```

Setup independently compares that value with its compiled agreement fingerprint.
The archive includes DistributionTerms.txt, the first-party LICENSE and exact
third-party notices so review does not require installing first. An agreement flag
is explicit operator assent; it is never generated automatically from an update
request or treated as an exception to signature/ownership checks.

## Future Store distribution

For a Store route, provide the first-party license and scoped Microsoft component
terms through Partner Center's application/additional license terms, while retaining
the same exact notices in the package. Validate the chosen acquisition/deployment
model and certification requirements before shipping it. Microsoft's
[Store application terms](https://www.microsoft.com/en-US/servicesagreement) allow
separate application terms; unattended acquisition alone does not establish that a
second application popup is necessary.

No first-launch popup is added to the current application. A main-window-only gate
would miss Agent and Shell entry paths. If a future non-Store/sideloaded route lacks
an adequate pre-use agreement mechanism, review an equivalent mechanism for that
route before enabling it; do not silently reuse this Setup acceptance as universal
consent across accounts or distribution systems.
