# M12 — Storage foundation

## Purpose and delivered behavior

Established the independent per-file metadata foundation after the split from the
archival product. Root/path histories, Current membership, source configuration,
immutable semantic records, copy-on-write indexes and one authoritative state pointer
replaced complete backup-run manifests. Substeps corrected page occupancy and added
a projected Current comparison read path.

## Important decisions

The frozen case table and ordered keys prevent host Unicode/culture changes from
changing identity. Packed segments avoid one metadata file per path/invocation.
Sparse mutations rewrite affected pages/ancestors; minimum contiguous capacity-valid
page groups are balanced before immutable emission, avoiding full-page/tiny-tail
proliferation. Current comparison groups bounded key windows by physical location
without changing durable leaf values.

One retained OS writer handle, staged flush/readback, exact predecessor/intended
inspection and explicit uncertain outcomes define publication. Uncommitted tails
are never adopted or truncated. Rebuild derives indexes from committed semantics;
compaction emits a new epoch and retires only exact old identities.

## Validation and evidence

Tests exercised no-op byte/timestamp preservation, Unicode/name boundaries, malformed
metadata, bounded point/range lookup, sparse costs, rotation, rebuild/compaction,
reader retirement, child-process locks, process death around replacement, late
cancellation and real Windows sharing. A synthetic 250000-key fixture built both
indexes in 4300 bulk pages, about 70.9 MB framed metadata and two segments; a sparse
new version/current change appended eight pages. It did not populate 250000 payloads.
Reported managed live samples were not process peaks or throughput guarantees.

## Limits and current relationship

These historical numeric-2 layouts are unsupported by current clients. The active
[Format 4 specification](../REPOSITORY-FORMAT.md) incorporates surviving primitive,
comparator, root/page and publication contracts with current address/compression/
encryption changes. Historical byte counts are not current format predictions.
Flush/process-crash visibility does not prove power-loss or arbitrary SMB durability.
