# Testing and evidence

Tests exercise storage contracts and failure outcomes; they do not establish a
coverage percentage or universal filesystem compatibility. Commands and prerequisites
are in [BUILDING](BUILDING.md). Current hosted CI is the single Windows Release job
in [ci.yml](../.github/workflows/ci.yml). Windows is the authoritative validation
platform; other operating systems are not a supported-product or release gate.
The SDK is pinned exactly by `global.json` with roll-forward disabled. CI checks
`dotnet --version` before locked restore; contributor and Setup builds use the same
selection guard. SDK changes require a reviewed implicit-dependency/lock update.

## Ordinary regression

The ordinary filter is `Category!=MetadataDiagnostic`, after locked restore and
Release build. Core/Storage/Engine/Application and portable presentation tests cover
path identity, codecs, malformed controls, targeted corruption, Current comparison,
copy/deletion coverage, file/directory replacement, capture/cursor authority,
restore, exact removal, retention/capacity, reuse, scheduling, safeguards, settings,
progress, status, recovery/conversion and updates.

Injected clocks, filesystem seams, capacity probes, HTTP handlers and stage fault
hooks make boundary behavior deterministic. They are complemented by actual local
Windows handle sharing, atomic replacement, child-process ownership and native/COM
tests. A simulated service response is not a live account or SMB test.

Platform-specific test seams and skipped diagnostic cases do not establish support
or a release-validation requirement for another OS. External-client compatibility
tests require deliberately supplied legacy payloads; see their opt-in fixture gates.

## Native and packaging checks

Desktop builds run `Build-NativeShell.ps1`. Native Shell checks validate protocol,
selection, resource and COM behavior. Fault tests cover malformed replies with
refresh, broker disconnect with refresh, closure with an outstanding query, and
actual worker/page lifetime. The baseline run passed 325 native checks plus 512
blocked-pool queued closures and 128 default-pool rapid closures. The blocked-pool
test includes an explicit cancelling positive control; it exercises actual
StartWorker submissions rather than a duplicate queue implementation.

`Test-InstallerLifecycle.ps1` checks owned payload inventory, loaded-file deferral,
later exact cleanup and active-version protection. `Test-VersionPolicy.ps1` and
`Test-VersionMetadata.ps1` check identities and metadata. `Test-SetupPayload.ps1`
checks the actual published payload. `Test-BuildOutputPolicy.ps1` runs both packaging
scripts' admission statements in a disposable Git fixture, stopping before compiler
or packaging work. It checks portable versioned defaults, overrides, collision
preservation and dirty-source refusal. These static/component checks do not replace
installed clean/update/rollback/uninstall testing in an interactive Windows session.
Canonical Setup performs its existing preflights; ordinary PR CI builds Setup only
on explicit workflow_dispatch.

`Test-DistributionTerms.ps1` checks normalized agreement identity, changed text,
exact Microsoft license snapshots and policy drift. `Test-InstallerTerms.ps1`
compiles the actual shared Inno acceptance code into isolated native fixtures and
exercises interactive acceptance/cancellation, silent refusal, explicit assent,
same/changed-terms upgrades and uninstall/reinstall. Run its interactive campaign
in a disposable Windows user session, separately from a real backup installation.

## Localization and recovery

`Test-Localization.ps1` validates catalog parity, placeholder/whitespace/brand
contracts, presentation literal classification, guard regressions and generated
managed/native/installer/manifest output. The runtime localization tool checks real
PRI lookup/fallback and live resource behavior. Representative Latin, RTL and CJK
installed checks are historical evidence, not individual visual certification of
all 44 languages. Diagnostics and machine-readable contracts remain English/stable.

`Test-RecoveryDependencies.ps1` and `Test-RecoveryDependencyGuard.ps1` validate exact
authentication versions, restored archive/nuspec/license/notice identities and
negative mutation cases. Crypto/recovery suites cover wrong passwords/keys,
authentication failures, DPAPI enrollment, rotation, split-secret sidecar binding,
conditional remote deletion, uncertainty and conversion fault stages.

Live WAM/AppFolder testing is an explicit interactive diagnostic in
`tools/FluffHistory.OneDriveSmoke`, never a CI account requirement. Use only a
disposable repository/account and do not commit tokens, credentials, account IDs or
profile crypto material. Independent-machine recovery must obtain its cloud secret
directly, not by transferring caches/DPAPI material. Wrong-account, revoked access,
quota, recycle-bin and bare-metal cases remain separate work before Experimental
recovery promotion. Historical PasswordVault probes are research/cleanup evidence,
not the current recovery product path. Their script and native adapter are copied
beside the test assembly for default or redirected build output. Ordinary deployment
regressions launch an invalid-mode probe, rejected before native loading or account
access; they do not establish live PasswordVault behavior.

Before recovery promotion, use this sequence on disposable data:

1. On the first installation, prove password and local DPAPI independently, enable
   OneDrive, restart and verify. Passive Configuration must not sign in or poll.
2. Rotate the recovery password: new succeeds, old fails, DPAPI and the same cloud
   generation still verify. Compare representative restored payload checksums.
3. On an independent Windows installation/profile, first prove the old local wrapper
   fails. Recover by direct account retrieval without transferring account caches,
   cloud secrets or DPAPI material; enroll and fresh-open offline locally.
4. Test wrong account, revoked/cancelled sign-in, offline/quota, missing/modified
   object and rename by exact ID. Repair must prove a new generation before old cleanup.
5. Check conditional-delete 412 preservation, local-only disable notice, three-repository
   isolation, retired cloud recycle-bin generation rejection and exact legacy cleanup.
6. Pause/crash around decryption sidecar retirement and final commit. Do not claim
   every pause retains remote cleanup coordinates. Encryption on leaves cloud
   recovery off until opt-in. Do not purge unrelated/recycle-bin contents.

Retain exact build identity and sanitized scope/size/outcome evidence; redact account
picker captures. The repository has one Windows-user wrapper, so enrolling the second
machine can require recovery again on the first. This checklist is not a claim that
all its steps were completed.

## Network, faults and scale

Real SMB suites are opt-in native Windows tests. `FLUFF_HISTORY_SMB_TEST_ROOT` must
name an owned disposable UNC root; quota/disruption suites need their specific
additional environment and server controls. They validate retained handle revocation,
authority observer visibility, interruption/reconciliation, actual disk-full/quota,
source-failure deletion barriers and both restore directions. Run only the selected
campaign on storage that may be destroyed. Passing local NTFS or injected probes
does not establish network behavior.

Historical Windows/Samba evidence supports that specific server/client setup.
It does not establish every NAS, redirector version or power-loss
guarantee. Source/native network calls can block beyond cooperative cancellation;
disruptive child watchdogs are diagnostic containment, not proof of nonpublication.

Large metadata diagnostics require explicit `FLUFF_HISTORY_DIAGNOSTICS=1`, the
selected fixture/filter and disposable-root settings. The runtime-maintenance
fixture additionally requires explicit `FLUFF_HISTORY_METADATA_DIAGNOSTIC_ROOT`, rejects source,
release/build/persistent storage and aliases into them, and checks at least 16 GiB
available. Never run all large diagnostics just to obtain a test count. Fixture
construction, measured operation, transfer and cleanup are separate costs.

| Engineering tier | Current paths | Surviving versions | Meaning |
| --- | ---: | ---: | --- |
| Baseline | 30000 | 250000 | Physical metadata diagnostic shape |
| Heavy | 80000 | 800000 | Explicit larger diagnostic gate |
| Stress | 250000 | 5000000 | Normally structural projection, not a physical population |

These are workload definitions, not supported throughput promises. Path lengths,
file sizes, change/time distribution, dirty metadata, storage and latency matter.
Current scanning scales with Current; cold Timeline, retention and compaction can
scale with history. Application range requests, SMB commands, wire bytes, logical
bytes, allocated space, live heap, lifetime peaks and cumulative allocations are
different metrics.

Historical Format-4 Baseline evidence measured dirty metadata at 2419948632 decoded
bytes / 553625565 encoded segment bytes (22.88%), and compact output at 153054841 /
36396874 bytes (23.78%). One-file publication appended 100142 logical / 13325
physical bytes without rewriting a committed prefix. Those metadata-only Windows
VM observations exclude payloads/controls/allocation and use uncontrolled caches;
they are neither CI timing thresholds nor bare-metal promises. A separate collector test
measured a 500000-tick collector retaining 4000000 raw timestamp bytes; that was a
collector experiment, not a 500000-version repository throughput result.

Further servers, hardware sleep/resume,
security review, accessibility and publisher distribution checks remain in the
[roadmap](ROADMAP.md). There is no claim that a passing ordinary suite proves them.
