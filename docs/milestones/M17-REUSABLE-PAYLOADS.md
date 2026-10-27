# M17 — Reusable unpublished payloads

## Purpose and delivered behavior

Retained proven complete stable but unpublished copies as an optional disposable
cache, avoiding source/payload recopy on a later matching attempt.

## Important decisions

Descriptors bind exact root/path, generated payload UUID, size/mtime/checksum and
captured authority/root hash. Lookup examines at most 16 exact-bucket entries plus
an overflow sentinel, then verifies local content. Ready authorization is revoked
before exact transaction binding; reconciliation decides whether it can return to
Ready or retire. Any authority advancement conservatively invalidates provenance.

Unknown or partial orphans are not adopted. There is no cross-path deduplication,
partial-copy resume or scan checkpoint. Reuse contributes zero future payload
allocation but still charges metadata/cache headroom. Bounded proven Ready eviction
precedes authorized historical capacity deletion.

## Validation and evidence

Tests and diagnostics covered stable interrupted copies, source changes, malformed
or overflowing buckets, conflicting content, authority advancement, lost publication
responses, binding/revocation crashes, adoption without recopy and bounded capacity
cleanup. Failed cleanup preserves evidence rather than guessing ownership.

## Limits and current relationship

The cache is never needed to open/recover history and never authorizes deletion of
published/current content. Encryption later protected descriptors and keyed buckets
without changing logical provenance. See [architecture](../ARCHITECTURE.md),
[format](../REPOSITORY-FORMAT.md) and [retention](../RETENTION.md).
