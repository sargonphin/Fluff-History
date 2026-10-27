# M19 — Network validation and Format 4 compression

## Purpose and delivered behavior

Measured real Windows/Samba metadata/authority behavior, applied narrow transport
hardening, then introduced independently compressed Format-4 metadata after an
architecture gate. Version payloads remained uncompressed.

## Important decisions

Grouped network Current/Timeline/Retention projections trade bounded 1 MiB range
windows for fewer calls; direct point/control reads remain separate. Every requested
frame retains exact validation. Failed retained network ownership queries abort;
opening a replacement lock is not transparent recovery. Local Windows uses
ReplaceFileW while the tested network path uses same-directory replacement rename
without copy/delete fallback or blind retry.

Whole-segment compression harmed random access. Independent blocks no larger than
256 KiB, validated physical locators and immutable sparse suffixes preserve bounded
point decode and committed-prefix immutability. Local workspaces fetch requested
64 KiB ranges; decoded caching later moved entirely to bounded RAM.

## Validation and evidence

Real UNC campaigns exercised authority observers, retained-handle revocation,
disconnect/reconnect and process faults, genuine quota/disk-full, source-error
deletion barriers, reuse/reconciliation and both restore directions. Application
read ranges, server SMB counters and wire/transfer projections were kept distinct.

The metadata-only Format-4 Baseline had 30000 Current paths/250000 versions. Dirty
decoded/encoded segment bytes were 2419948632 / 553625565 (22.88%); dense compaction
153054841 / 36396874 (23.78%), preserving all versions/Current. Sparse one-file
prepare/commit appended 100142 logical / 13325 physical bytes without committed
prefix rewrite. A late point observation was 0.53 ms; full cold traversals were
separate workloads. These are historical VM observations with uncontrolled caches,
not current throughput promises. Heavy/Stress evidence must distinguish physical
population from structural projection.

## Limits and current relationship

One Samba setup does not prove arbitrary NAS, Linux CIFS or device/server power-loss
durability. Read-ahead overfetch and sparse history distributions remain costs.
Compressed-block damage can affect multiple logical frames; rebuild needs intact
semantic blocks. Active byte contracts are in [format](../REPOSITORY-FORMAT.md),
with validation limits in [testing](../TESTING.md).
