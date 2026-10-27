# Updates and releases

Version 1.0 uses intentionally unsigned Setup and application binaries. Installation
is manual: download `Fluff-History-Setup.exe` from this project's public GitHub
Releases and review its origin and checksum. Windows may show an unknown-publisher or
reputation warning. A checksum detects a changed download; it is not a signing identity.

The separately self-signed sparse identity needs its exact certificate in the
computer's TrustedPeople store, with administrator approval. Its package and Explorer
registration remain per-user. That certificate is not an approved
public-update signer. The updater remains fail-closed: it may announce an unsigned
release, but will not automatically execute its unsigned installer. In-app installer
execution, including explicit manual handoff, still requires the full signer gate.
Install unsigned releases directly through ordinary Setup. Future trusted signing
needs a separate publisher/compatibility review.

## Release identity

Public release grammar is `v?MAJOR.MINOR.PATCH[-beta.NUMBER]`: ASCII nonnegative
Int32 components without leading zeros, lowercase optional `v` and `beta`. Numeric
comparison makes 1.10.0 newer than 1.9.0 and beta.2 newer than beta.1. Stable wins
over any beta of the same core. Four-part UInt16 PE/package build identity is separate;
its first three components must match the declared release. An unpublished
`development` identity can announce available releases but cannot execute an update.

Publisher assets use the exact name `Fluff-History-Setup.exe`. GitHub tag, prerelease
flag, uploaded asset identity, size, fixed download/page URLs, semantic release and
product/company/description/build metadata must agree. Missing GitHub SHA-256
makes a valid release announcement-only. Drafts, malformed identities and ambiguous
equal versions cannot authorize execution; equal/older releases never downgrade.

Stable selects the greatest valid published stable release. Beta considers stable
and beta releases together by semantic precedence. Discovery is anonymous at the
fixed GitHub Releases endpoint for `sargonphin/Fluff-History`; no user token or
arbitrary repository URL is accepted. Changing publisher repository or endpoint
requires deliberate compatibility and security review.

## Policy and ownership

Default policy is Stable/Weekly. Choices are Never, Daily, Weekly and Monthly;
intervals are 1, 7 and 30 days. Never disables automatic network checks, while
manual checking remains available. Persisted observation timestamps anchor cadence,
failed attempts have a six-hour retry floor, and future clock observations are
reanchored. There is no missed-check replay queue.

Desktop writes `update-preferences-v1.json`; Agent writes `update-state-v1.json`.
The detached helper overwrites one bounded `update-result-v1.json`. None is backup
configuration, history authority or a log of installations. Agent checks the local
result independently of network policy, including Never, because Setup can start
the replacement Agent before the old helper records its final exit.

Automatic discovery/download defers for offline, unknown or metered network state.
Execution also defers while a backup or management UI is active. Manual install
requires explicit action; cancelling an active backup requires confirmation and
waiting for actual worker exit. No process is force-killed to make installation fit.

Automatic pending is armed only for downloadable releases. The coordinator durably
consumes it **before** committing the detached handoff. Only explicit Busy or
BackupStillRunning admission deferral re-arms previously pending work. A failed
Setup/rollback followed by old-Agent restart therefore does not replay an immediate
install loop. Manual retry is available immediately; automatic retry requires a
later ordinary scheduled check. Manual-only Busy does not create automatic consent.

## Download and trust

Release JSON is bounded to 2 MiB/depth 24, 100 releases and 32 assets per release.
Installer size is capped at 512 MiB. Staging has exact ownership markers, an exclusive
writer and at most three owned version directories; unknown content is not swept.
Interrupted candidates are not accepted as completed installers.

Downloads stream with bounded buffers and deadlines, validate declared length/hash,
flush and read back. Redirects are inspected and restricted to the GitHub delivery
hosts. WinVerifyTrust validates Authenticode and the actual selected signer; policy
compares a SHA-256 certificate identity. Signed product metadata is checked separately.
No broad subject-name match or test-certificate trust silently approves a public update.

Exact installer/helper files and ordinary parent directories are held against
write/delete substitution through execution. The compiled disposable update-test
composition has explicit identity/signer prerequisites and is not enabled through
normal preferences or environment variables. It must not be used as a public package.

## Handoff and ordinary Setup

Agent retains shared mutation admission through drained shutdown. The
single-file helper validates its hash/request and binds exact processes by PID,
start time, installed path, current user and session. Ready/commit events and an
exclusive startup barrier bridge process exit to Setup's existing installation lock.
Desktop receives normal close; blocked workers are waited out rather than terminated.

Ordinary per-user x64 Inno Setup is the sole replacement/rollback owner. The helper
waits for actual installer exit and records its bounded result. Setup owns immutable
versioned payloads, sparse identity, modern Explorer command, native Properties,
Agent startup and exact uninstall cleanup. Loaded Shell images can defer deletion
without killing Explorer; later exact inventory-validated cleanup retries them.
Settings and repository data are separate from installed version directories.

Microsoft component terms are agreed before product activation. Automatic Setup
inherits only an identical previously accepted fingerprint and never supplies a
blanket assent flag. Changed/missing agreement blocks silent installation; the old
Agent recovery and consumed-pending behavior remain. Manual updates open the normal
installer so new terms can be accepted or cancelled. Legacy helpers that always run
silent may require one direct interactive Setup. See [licensing](LICENSING.md).

Current Setup includes independent Desktop and Start menu shortcut choices, both
initially selected. Public Setup/application PE files stay unsigned; only the sparse
package has the pinned self-signed identity. Local sparse trust and development
signing are disclosed and are not equivalent to public signing or Store approval.
[BUILDING](BUILDING.md) gives reproducible commands, [LICENSING](LICENSING.md) the
distribution inventory, and [TESTING](TESTING.md) the limits of installed evidence.
