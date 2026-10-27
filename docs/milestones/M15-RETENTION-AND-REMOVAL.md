# M15 — Exact removal, retention and manual cleanup

## Purpose and delivered behavior

The design gate separated historical disappearance from Current membership and
payload deletion. Format 3 introduced exact noncurrent removal and durable negative
cleanup anchors. Fixed-age policy and independent manual cleanup reused that primitive.

## Important decisions

VersionRemoved removes the positive Versions key atomically while Current remains
protected. No payload is destroyed before confirmed logical publication. Exact
predecessor/intended reconciliation remains mandatory. Rebuild/compaction fold
additions/removals, reject conflicting/duplicate identities and carry negative
anchors until exact payload absence is safe.

Fixed age uses explicit UTC, elapsed days and strict StoredUtc cutoff. Complete
previews bind full authority; execution regenerates a bounded stream under retained
ownership. Manual KeepOnlyMostRecent uses publication order with one-record lookahead,
including deleted/unprotected paths, independently of automatic policy. Logical
candidate bytes are informational, not proof of free-space increase.

## Validation and evidence

Suites covered Current rejection, absent/duplicate/wrong UUID requests, publication
faults, partial cleanup, sharing/unlink behavior, malformed semantic folds, repeated
rebuild/compaction, cutoff equality, clock arithmetic, stale/incomplete preview,
cancelled multi-batch progress and no-op Forever. Structured diagnostics measured
retention and cleanup shapes separately from fixture population.

## Limits and current relationship

Numeric Format 3 is historical and unsupported; Format 4 retains its logical removal
and fold semantics. No directory sweep, new cleanup acknowledgement authority or
history-following rename was added. Current policy defaults/orchestration are in
[retention](../RETENTION.md); byte contracts are in [format](../REPOSITORY-FORMAT.md).
