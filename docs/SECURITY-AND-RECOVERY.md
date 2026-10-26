# Security and recovery

Encryption is optional. This document describes the implemented boundaries, not an
external security certification. [Format 4](REPOSITORY-FORMAT.md) specifies byte
layouts, authentication contexts and finite limits.

## Encryption and key ownership

Encrypted repositories use a random 256-bit master, independent of the recovery
password and Windows account. Mandatory portable password recovery wraps that
master using PBKDF2-HMAC-SHA256 with a fresh 32-byte salt and 600,000 iterations.
The accepted password length is 1–1024 UTF-16 characters; the KDF encodes UTF-8
without normalization. Password strength still matters against offline guessing.

Domain-separated HKDF-SHA256 derives AES-256-GCM object keys. Small objects use a
fresh random 256-bit salt per envelope and a zero nonce under that independently
derived key. Metadata compresses before encryption. Payloads use a fresh salted
object key and ordered authenticated 256 KiB chunks with an authenticated final
length/count; payload bytes are never compressed.

Product/format/mode/repository identity bind encrypted objects. Password and Windows
wrappers are excluded from that immutable binding so they can change without
re-encrypting history. Decoding does not guess an encryption mode after failure.
Required controls, metadata and selected payloads fail on authentication or binding
errors. Readers/writers must be disposed before the repository key owner.

Decoded workspace blocks live in a bounded RAM LRU; encoded disk ranges remain
ciphertext in encrypted mode. History/capacity sort records and reusable descriptors
are protected. Mutable owned buffers are cleared where practical. Managed strings,
runtime/native copies, process memory and caller-owned password buffers prevent any
claim of perfect erasure.

The clear bootstrap exposes repository identity, creation time, mode and bounded
unlock metadata. Filenames, lengths/counts/access patterns, an unlocked compromised
process, local administrator/kernel access, denial of service and replay of a
complete previously valid repository are outside the confidentiality/freshness
guarantee. There is no external trusted anti-rollback counter.

## Password and Windows recovery

Low-level password open authenticates state/root and is read-only. It works without
the original Windows SID/profile or local workspace. Product creation and successful
explicit recovery enroll current-user DPAPI for later silent opening. The Windows
slot is a local convenience, not an independent off-machine recovery guarantee.
Never rely on Windows account roaming or reinstall to preserve it.

Enrollment retains exclusive writer ownership, wraps the same master, flushes and
readbacks a bootstrap candidate, checks exact predecessor and replaces only
`repository.json`. Password rotation similarly creates a fresh password wrapper of
the same master. Identity, state, file versions and content bytes stay intact.
Both wrappers are excluded from active-session immutable bootstrap comparison.
Uncertain replacement/confirmation requires fresh inspection and explicit retry;
it must not be treated as proven failure or blindly overwritten.

The repository has one current Windows-user wrapper. Enrollment on another machine replaces it; the former machine may need password/OneDrive recovery again. This is not a simultaneous per-machine offline-slot registry.

Keep the password separately from the backup. Recovery still needs an intact
repository; neither a password nor a DPAPI wrapper reconstructs lost payloads.

## Experimental Microsoft-account recovery

The current path is **OneDrive AppFolder split-secret recovery**. Older experimental
Credential Locker/PasswordVault provisioning was not proven portable and is retained
only for exact legacy cleanup. A local named-vault success is not account roaming.

Desktop explicitly signs in using MSAL WAM and the Windows account picker, with
supported MSAL browser fallback. The Windows sign-in account need not be the selected
Microsoft account. Only personal accounts and delegated `Files.ReadWrite.AppFolder`
are used. Normal `openid`, `profile` and `offline_access` sign-in scopes can also
appear; they are distinct from Graph `User.Read`. Broader granted Graph scopes are
rejected. The feature remains Experimental; the delegated permission's Preview
status recorded during implementation has not been promoted to a production claim.

OneDrive receives a tiny per-repository/per-generation JSON object containing a
fresh random recovery secret, not the repository master, recovery password, backup
payloads or file history. The local `onedrive-account-recovery.json` sidecar stores the
protected master and exact remote coordinates. Recovery needs **both halves** plus
authorized access to the same application folder; the cloud half alone cannot open
the repository. A repository copy without its sidecar cannot use this recovery path.
The sidecar is never history authority.

Enable verifies a fresh exact remote readback before publishing the local sidecar,
then authenticates a fresh repository open. Repair creates a new generation before
attempting exact old-generation cleanup. Lost upload responses can leave cloud-only
orphans; uncertain local publication retains potentially useful recovery material.
Disable validates expected contents and uses the observed eTag for conditional exact
deletion. Local-only disable explicitly leaves remote cleanup required. No broad
listing, guessed filename deletion or unrelated-item sweep is used.

Graph v1.0 requests have fixed endpoints, bounded metadata/content, deadlines and
bounded read retries. Writes are not blindly retried. Download redirects do not
inherit Authorization. Tokens are invocation-owned managed strings; there is no
custom persistent token cache or PII logging. WAM owns its account/cache lifecycle.
No password, token or cloud secret enters Agent IPC or Explorer. Ordinary backup,
Timeline and restore remain independent of Graph and use local enrollment.

The publisher maintains one public desktop registration; ordinary users do not need
an Entra tenant or their own registration. The compiled client ID is public metadata
(`9ffb1f5d-107e-4f78-a4e5-b2fed2958562`), not a credential. The registration uses the
consumer authority, broker redirect
`ms-appx-web://microsoft.aad.brokerplugin/9ffb1f5d-107e-4f78-a4e5-b2fed2958562`,
and desktop `http://localhost` fallback, with no client secret. A fork changing
application identity must review AppFolder continuity; a new registration is not
guaranteed access to the former application's folder.

Client/Broker 4.83.3 and NativeInterop 0.20.4 are deliberately pinned to reviewed
MIT artifacts. Upgrades require a new exact dependency/license review, not an
assumption about all later versions. See [licensing](LICENSING.md).

## Resumable encryption conversion

Changing encryption is explicit exclusive physical maintenance under shared product
admission and the repository writer. Desktop retires its reader/preview owners and
owns cancellable work; Agent never receives conversion secrets. It does not run an
orphan conversion thread after management closure.

A bounded durable marker captures source/target bootstraps, state and semantic
identity. An unsupported-mode bootstrap sentinel fences older ordinary clients
while payloads can have mixed physical representations. Only a private maintenance
context reads those exact source/target forms. Each payload is verified and replaced
using a generated owned candidate; already-converted payloads are fully verified on
resume. Dense metadata output preserves identities, versions, Current and timestamps.

Target state installs while the sentinel remains. Complete target verification
precedes authenticated Ready; exposing the target bootstrap is the commit boundary.
Recovery of Ready preserves legitimate later target-authority advancement rather
than rolling it back. Cancellation pauses, never reverses completed payload changes.
Capacity checks take no compression credit and never pressure-delete history to
make conversion fit. Insufficient space or uncertain controls require attention.

After verified decryption, the local OneDrive sidecar is retired. A late crash/pause after local-sidecar retirement can lose exact remote cleanup coordinates and leave a cloud-only random secret. It cannot unwrap the master without the missing protected local half. Never compensate with broad listing or wildcard deletion.

Remote cleanup is
best effort and can require an exact later cleanup; authentication failure cannot
undo a completed physical conversion. Secure erasure of old ciphertext/plaintext,
retired metadata, filesystem snapshots or SSD remnants is not guaranteed.

## Restore, IPC and updates

Selected encrypted restore/preview bytes intentionally leave encryption in a unique
restricted local plaintext directory. They authenticate and checksum before Shell
use; exact cleanup is best effort. Preview applications can retain copies/locks and
are outside this application's security boundary.

Agent IPC is bounded and current-user-only, rejects remote clients and contains typed
configuration/control/status, not credentials. Shell brokers are on-demand read-only
sessions with exact opaque handles and validated client window ownership. Same-user
code is not treated as a separate hostile principal.

Updates require fixed release identity, SHA-256, Authenticode and explicit selected
signer approval, with retained file/path leases through handoff. Production signer
approval is required for executable updates. See [updates and releases](UPDATES-AND-RELEASES.md).

This source tree does not include a repository-level vulnerability-reporting policy
or verified private contact. Security-sensitive reports need a verified private
reporting route. [TESTING](TESTING.md) separates ordinary regression
coverage from live recovery, network, crash and bare-metal evidence.
