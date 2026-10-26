# M20.2 synthetic Windows-account diagnostics

Run only on a disposable native Windows environment. No Microsoft password, PIN, token, repository key or user data is accepted by these scripts. Windows PowerShell 5.1 supplies its existing WinRT PasswordVault projection; the C# native adapter intentionally targets that runtime and is excluded from the .NET test build.

```powershell
$env:FLUFF_HISTORY_M202_ACCOUNT_DIAGNOSTICS = '1'
& ./Invoke-MicrosoftAccountProbe.ps1 -Mode Inspect
# For clean-machine proof, choose a fresh UUID and Inspect this exact coordinate on A AND B before Protect on A.
$sampleId = [Guid]::NewGuid()
& ./Invoke-MicrosoftAccountProbe.ps1 -Mode Inspect -SampleId $sampleId
# Inspect that same nonsecret sampleId on B before this A-only Protect invocation.
& ./Invoke-MicrosoftAccountProbe.ps1 -Mode Protect -SampleId $sampleId -Directory D:/OwnedDiagnostics/fresh-sample -AllowWindowsUI
& ./Invoke-MicrosoftAccountProbe.ps1 -Mode Recover -Directory D:/OwnedDiagnostics/fresh-sample
& ./Invoke-MicrosoftAccountProbe.ps1 -Mode Cleanup -Directory D:/OwnedDiagnostics/fresh-sample
```

Protect requires an absent directory. It generates independent random 32-byte **synthetic** secrets for each case, tries only Microsoft's documented LOCAL=user and two WEBCREDENTIALS descriptor forms, and records status codes. Optional flags=0 permits Windows-owned UI; default flags=64 requests silent behavior. One generated synthetic PasswordVault password is added under an exact UUID resource and SyntheticProbe username; an account password is never supplied. The mapping of descriptor name/resource to this vault entry is an experiment, not an assumption about account identity.

Optional `-SampleId <fresh UUID>` selects the same generated coordinate for `Inspect` and `Protect`. Inspect reports only whether that exact credential exists, without retrieving its password or enumerating other credentials. Protect refuses an already present coordinate. Omit SampleId to retain automatic UUID generation. Use a new UUID for each experiment; the example is illustrative. A matching coordinate's presence alone does not prove the protected blob will recover.

The manifest contains bounded protected synthetic blobs and SHA256 verification hashes, plus the generated nonsecret vault lookup coordinates. It never contains the plaintext synthetic secret or vault password. Keep manifests outside Git. Only those manifests may be transferred between isolated profiles/machines; never copy profile/DPAPI/credential databases or key stores. Recover queries only the exact synthetic entry and logs provider/status/boolean results. It does not retrieve existing account passwords. Cleanup removes only that exact generated synthetic vault entry; run it after all required restart/profile tests.

The opt-in `MicrosoftAccountLiveTests` wrapper checks same-profile fresh-process recovery and failure after removing the synthetic vault entry, then cleans its own artifact directory. It requires an MSA-connected user; hosted CI excludes the diagnostic category and never needs an account. The standalone script also runs under a deliberately unrelated local profile for negative controls.

A LOCAL=user or same-profile WEBCREDENTIALS roundtrip proves neither cloud roaming nor independent Microsoft-account recovery. Required account/roaming/clean-machine gates and observed limitations belong in [the historical recovery record](../../../docs/milestones/M20-ENCRYPTION-AND-RECOVERY.md).
