# M13 — Source pipeline and storage efficiency

## Purpose and delivered behavior

Connected real source traversal to size/mtime comparison, one-pass inline xxHash64,
owned payload drafts and independently committed bounded metadata batches. Current
comparison and storage-efficiency measurements separated scanning, copying,
publication and maintenance costs.

## Important decisions

Read-only permissive source sharing lets applications continue normal work. Handle
and path observations detect copy instability without inventing persistent file IDs.
Unreadable/unsupported paths are non-observations; incomplete coverage cannot prove
deletion. Confirmed absence ends Current membership only. Repository/ownership/control
failure aborts mutation rather than becoming a source skip.

Owned CreateNew staging, completion/flush, batch binding and no-overwrite installation
keep unknown/partial files out of history. No-change work publishes nothing. Current
comparison avoids replaying historical metadata; absent-Current paths perform exact
newest-history lookups for possible reactivation.

## Validation and evidence

Real-source tests covered unchanged metadata, new/changed files, failure/retry,
sharing, instability, case aliases, reparse/name/size boundaries, deletion barriers,
source/destination separation, cancellation and partial useful batches. Structural
diagnostics measured bulk construction, sparse COW growth, reachable versus obsolete
metadata and compaction separately. A 25000-source initial-copy run accumulated
about 2.62 GB managed allocations while live memory was much lower; this is churn,
not a minimum RAM requirement or measured steady-state throughput.

## Limits and current relationship

Same-size/time content changes remain intentionally undetected. Synchronous source
I/O can outlast cancellation. Selected-root scans still build the repository-wide
Current map; transient charged budgets are not exact CLR heap bounds. Later capacity,
reuse, encryption and system exclusions extend the same observation/publication
contract. Current behavior is in [architecture](../ARCHITECTURE.md) and
[configuration](../CONFIGURATION.md).
