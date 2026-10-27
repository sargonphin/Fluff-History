# M18 — Timeline responsiveness

## Purpose and delivered behavior

Compared cold grouped scans, chunking, transient snapshots and derived persistence,
then selected one reader-owned active-scope sorted unique timestamp snapshot.

## Important decisions

Cold construction still validates scoped history. Warm pages binary-search/slice
captured ticks after epoch validation. Scope changes discard/rebuild; disposal frees
state. The existing bounded page selector remains active during collection so
overflow returns the exact page/continuation without a redundant second traversal.
No backup hook, persistent accelerator, format/index or history event was added.

## Validation and evidence

Tests covered authority/cursor/scope/direction/page-size binding, old capture versus
new readers, epoch expiry, cancellation, duplicates and forced overflow. A production
collector run over 500000 unique synthetic ticks retained 4000000 raw bytes, with
8325376 bytes peak explicitly referenced timestamp arrays. It was a collector/memory
experiment, not a second 500000-version repository. The forced 64-slot overflow
case traversed 50000 versions once and retained no cache.

Historical Windows VM flows showed cheap subsequent same-scope requests while broad
scope recapture remained expensive; large parent return was about 5731.894 ms and
subsequent same-scope calls 0.114–0.189 ms in that fixture. Timings used uncontrolled
OS caches and are observations, not CI limits or a bare-metal performance promise.

## Limits and current relationship

Collected ticks are capped at 1048576, with per-window dedup and exact fallback.
Sparse early as-of views and cold broad scopes can still traverse history. Memory
counters exclude garbage awaiting collection and unrelated runtime/reader state.
Current browsing/restore semantics remain in [architecture](../ARCHITECTURE.md) and
[restore](../RESTORE.md); engineering measurement categories are in [testing](../TESTING.md).
