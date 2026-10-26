# Version-pinned third-party notices

## Authentication

Some NuGet packages declare an MIT license expression without including the text.
The canonical Setup includes these upstream snapshots unchanged:

| Snapshot | Packages | Upstream source |
| --- | --- | --- |
| MSAL.NET-4.83.3.txt | Microsoft.Identity.Client and Microsoft.Identity.Client.Broker 4.83.3 | [MSAL source commit](https://github.com/AzureAD/microsoft-authentication-library-for-dotnet/blob/64076eedf396c11d046c63be068a92f6fa10aaf2/LICENSE) |
| NativeInterop-0.20.4.txt | Microsoft.Identity.Client.NativeInterop 0.20.4 | [Exact NuGet package](https://www.nuget.org/packages/Microsoft.Identity.Client.NativeInterop/0.20.4) declares SPDX MIT; no bundled license file. MIT notice with Microsoft copyright from its nuspec. |
| IdentityModel-Abstractions-8.14.0.txt | Microsoft.IdentityModel.Abstractions 8.14.0 | [IdentityModel source commit](https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet/blob/c8f7d87bcda35557a68f6cb9c55856a2ee733856/LICENSE.txt) |

Source commits match the locked packages' nuspec repository metadata where provided.
All three reviewed authentication packages declare `<license type="expression">MIT</license>`
in their actual nupkgs. Broker 4.83.3 depends on NativeInterop 0.20.4, which is also
an explicit exact direct pin. NativeInterop 0.20.4 does not contain the restrictive
separate license present in the previously investigated0.20.6 package. Do not
extrapolate a license decision to all later versions. These notices do not relicense
other packages or the Windows-owned WAM service.

`installer/RecoveryDependencyApproval.json` records the reviewed versions, NuGet
content hashes, downloaded archive SHA256s and normalized MIT-notice hashes.
`Test-RecoveryDependencies.ps1` checks central exact pins, both lock graphs and
notices; every Desktop/diagnostic build additionally checks resolved assets, actual
package archive bytes and nuspec MIT declarations. Canonical Setup and CI run the
source guard; CI also runs negative regression fixtures. Agent/Shell/library graphs
cannot acquire unapproved identity dependencies.

To upgrade, review the exact new packages and transitive graph, including bundled
license/notice files and declared SPDX license, update central pins and locks,
preserve appropriate notices/provenance, then deliberately update the approval data
and run the guard regressions. The code imposes no permanent version ceiling.
The generic Setup collector also preserves extensionless LICENSE/NOTICE files.

Archive SHA256 verification uses a streaming .NET implementation, independent of
PowerShell's module-provided `Get-FileHash`. The regression harness includes a full
positive restored-artifact check with that command blocked and rejects a modified
archive in the same environment. This covers nested Windows PowerShell launched
by MSBuild as well as the standalone PowerShell CI step.

## Windows SDK.NET runtime pack

WindowsSDK-NETRef-10.0.26100.57.rtf is the verbatim RTF obtained on 2026-10-26 from
the exact package's nuspec license URL, [WinSDKLicenseURL](https://aka.ms/WinSDKLicenseURL),
resolving to [Microsoft's sdk_license.rtf](https://download.microsoft.com/download/0/F/F/0FF2B061-47DD-4F55-89B6-FD1D8C44F14D/sdk_license.rtf).
DistributionTermsPolicy.json pins its raw SHA-256; Git preserves its original bytes
and Setup copies the same file. It is Microsoft SDK terms, not an MIT notice.

The [official REDIST list](https://learn.microsoft.com/en-us/legal/windows-sdk/redist#microsoftwindowssdknetref)
explicitly includes this package's unmodified runtime files. Get-DistributionTerms.ps1
also checks the exact selected Windows App SDK component licenses against the
source lock/policy; Test-SetupPayload.ps1 checks the actual SDK.NET runtime version
and shipped agreement/notice bytes. These are deliberate review guards, not a
permanent version ceiling or a claim of Microsoft certification.

The scoped downstream DistributionTerms.txt agreement is separate from these full
upstream notices and from first-party MIT. See [licensing](../../docs/LICENSING.md)
for the interpretation and interactive/unattended acceptance behavior.
