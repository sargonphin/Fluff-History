# M20 — Encryption, local enrollment and Microsoft-account recovery

## Purpose and delivered behavior

Added optional Format-4 authenticated encryption with mandatory portable password
recovery and local current-user DPAPI convenience. Substeps implemented explicit
enrollment and RAM decoded caching, investigated PasswordVault portability, then
replaced the experimental account path with OneDrive AppFolder split-secret recovery.

## Important decisions

A random master is separate from password/account wrappers. Metadata compresses
before encryption; payloads use ordered authenticated chunks without compression.
State remains sole history authority. Password open is read-only; enrollment
replaces only the Windows wrapper after authenticated state/root proof. Decoded
workspace data stays in a 32 MiB RAM LRU, not a persistent plaintext disk cache.

PasswordVault named-credential protection worked locally, including fresh processes
and unrelated-profile rejection. Windows prevented duplicate same-account profiles
on one installation. Independent machines confirmed the same account but did not
observe provisioning of the exact fresh credential after bounded wait/restart/manual
Windows Backup. Aggregate "Credentials: Backed up" was not a per-entry receipt;
timeout did not prove eventual provisioning impossible.

OneDrive instead stores a fresh random recovery half; a local non-authoritative
sidecar protects the master with it. Exact item/generation/binding/eTag checks,
readback before publication and conditional deletion avoid broad cloud cleanup.
WAM uses reviewed MIT-pinned Client/Broker 4.83.3 and NativeInterop 0.20.4. Agent and
Shell never authenticate or receive tokens/secrets. Password/DPAPI stay independent.

## Validation and evidence

Ordinary tests cover wrong keys/passwords, corruption, nonce/context binding, empty
payloads/chunk order/final EOF, caches, exact enrollment uncertainty and recovery
sidecar boundaries. Synthetic live WAM/AppFolder upload/read/delete passed. A separate
Windows installation first rejected the original local wrapper, then recovered the
encrypted repository through production APIs using a directly fetched cloud half,
enrolled DPAPI and reopened locally without probe network access. No account cache
or separate cloud secret was transferred. Installed English/Arabic/Japanese smoke
checked representative recovery presentation; it was not every dialog/language case.

## Limits and current relationship

OneDrive recovery remains Experimental. Wrong-account/revocation/quota/recycle-bin,
real legacy cleanup, complete installed-dialog and bare-metal campaigns remain
unclaimed. Late decryption can leave a cloud-only secret after local-half retirement
and lose exact cleanup coordinates; it cannot recover the master alone. No secure
erasure, perfect memory erasure or external anti-replay freshness is promised.
Current contracts, registration and conversion are in
[security and recovery](../SECURITY-AND-RECOVERY.md); legal approvals are in
[licensing](../LICENSING.md).
