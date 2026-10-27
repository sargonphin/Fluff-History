# M14 — History, Timeline reads and restore

## Purpose and delivered behavior

Added exact-version lookup, bounded path/scope/time queries and read-only restore.
Substeps grouped cold Timeline reads and added displayed-selection planning plus
Windows Shell restore/preview.

## Important decisions

Captured authority and opaque continuations bind each query. Directories derive from
surviving file paths; later live deletion does not hide retained versions. Latest
publication and historical time selection remain distinct under clock rollback.
File/directory collisions are exposed and preflighted for the exact selected
materialization before writes.

Restore never rewinds history or deletes unrelated destination content. Headless
policies are explicit. Windows verifies selected payloads, retains safe handles and
uses normal Shell conflict/progress behavior in one operation. Original-location
restore uses a distinguishable restoration-time mtime; alternate restore/preview
preserves historical time. Preview leases own exact files rather than sweeping temp.

## Validation and evidence

Tests covered captured paging, malformed/stale cursors, time ties/clock rollback,
ancestor conflicts across pages, integrity failures, destination/reparse boundaries,
metadata application, partial progress, cancellation and Shell callback outcomes.
The 1001-file / 32 MiB-plus-small-files Shell diagnostic observed about 1.85 GB
cumulative allocation, a roughly 23 MB managed live sample and 180 MB lifetime
process peak. It was a bounded selected plan, not a large-tree throughput guarantee.

## Limits and current relationship

Early cold/as-of traversal can still inspect substantial history. Shell conflict
dialogs can expose immutable UUID source names while writing correct destination
names. Preview application locks/extra files can defer cleanup. Later encrypted
restore intentionally stages only verified selected plaintext. Current contracts
are in [restore](../RESTORE.md) and [architecture](../ARCHITECTURE.md).
