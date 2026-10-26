# Retention, cleanup and capacity

Automatic retention defaults to **SixMonths**. Available policies are Forever,
OneMonth, ThreeMonths, SixMonths, NineMonths, OneYear, TwoYears and UntilSaturation.
Configuration persists the closed policy name, independently of manual cleanup.

## Fixed age and manual cleanup

Age is measured from the file version's `StoredUtc`, not source mtime. Month-based
choices use 30, 90, 180 and 270 elapsed days; year choices use 365 and 730. Evaluation
requires explicit UTC and uses the strict rule `!IsCurrent && StoredUtc < cutoff`.
The exact Current version survives regardless of age or source readability.

Automatic fixed-age maintenance runs after a complete safe scan, with a transient
repository/policy/UTC-day throttle. Forever performs no automatic age scan or
pressure deletion. Source deletion itself removes Current membership only.

Clean Timeline is an explicit one-shot action with the six age intervals and
KeepOnlyMostRecent. The latter keeps the newest surviving publication at every
logical path, even deleted or unprotected ones; it has no cutoff. An inconsistent
older Current selection fails closed. Manual cleanup never changes automatic policy.

Preview is read-only and binds full authority and request. Incomplete/cancelled or
stale previews cannot execute. Confirmation holds no writer; execution reacquires
admission/writer ownership, validates exact initial authority and regenerates one
bounded stream. Each committed batch advances its own expected authority. Confirmed
progress survives cancellation or later failure. Restart requires a fresh preview.

Logical removal is committed before payload deletion. Durable exact cleanup anchors
remain until safe absence is proven; no arbitrary version-directory sweep is used.
Locked payloads can defer cleanup. Preview logical bytes estimate potential space,
not actual allocated bytes or guaranteed free-space increase. The UI displays sized
units and warns that actual free space can differ.

## Until space saturation

Only UntilSaturation authorizes deleting noncurrent history in response to pressure.
Given caller-visible capacity C, free bytes F and explicit upcoming allocation B,
reserve is `ceil(C / 20)` and target is checked `B + reserve`. Only observed
`F >= target` satisfies admission. A target greater than C is impossible; logical
file lengths and delete acknowledgements do not prove reclaimed space.

Candidates are ordered by stored time, sequence and ordinal using bounded local
sorting. Exact Current is protected. Default removal batches are 16; the hard
request ceiling is 4096 with a charged 4 MiB candidate bound. Space is reobserved
after cleanup. No eligible history, cancellation, work limit, impossible target and
control/publication uncertainty remain distinct outcomes.

Live copy integrates capacity checks at clean boundaries under the same writer.
Ready reusable unpublished payloads are considered before historical deletion.
Pending drafts/upserts/removals prohibit destructive retention: approve and flush
safely or stop. Forever/fixed-age perform ordinary `F >= B` checks without pressure
history deletion. Estimates deliberately take no compression credit.

Windows capacity/geometry probes use the repository filesystem and calling identity.
A local streamed metadata workspace can share the repository volume with future
scratch included in allocation accounting. UNC requires an observable local
workspace; path aliases do not prove independent volumes. Portable tests use
injected trusted probes rather than treating unsupported volume totals as quota evidence.

## Metadata maintenance

Dense metadata compaction is physical maintenance, not retention of file versions.
After a safe completed scan/retention result, runtime policy uses cheap growth/cadence
gates, exact decoded assessment, an inclusive 30% reduction threshold and a 64 MiB
reclaimable floor. Scratch/output/headroom are explicitly budgeted. Held readers
are not forced closed; retired metadata cleanup can retry separately.

Compaction/rebuild preserve surviving versions, Current and pending payload-cleanup
anchors. They do not create logical Timeline events. Source comparison and normal
open do not replay all history. See [format](REPOSITORY-FORMAT.md) and
[testing](TESTING.md) for failure and scale boundaries.
