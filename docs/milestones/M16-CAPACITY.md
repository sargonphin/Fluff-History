# M16 — Capacity retention and live backup admission

## Purpose and delivered behavior

Implemented UntilSaturation from actual caller-visible capacity/free-space observations,
then integrated admission with live source copying under the existing retained writer.

## Important decisions

Target is checked upcoming allocation plus ceil(capacity/20). Only actual free bytes
establish satisfaction. Exact Current survives; noncurrent candidates order by
StoredUtc/sequence/ordinal using bounded scratch sorting. Logical deletion totals
never substitute for post-cleanup free-space feedback.

Live admission occurs before source reads/drafts and at clean metadata-only changes.
Pending drafts/upserts/removals forbid destructive pressure retention. Confirmed
publication clears pending state before feedback. Historical reader refresh and
source reobservation prevent reactivating a version removed during admission.
Storage estimates metadata/payload/headroom rather than assuming compression savings.

## Validation and evidence

Injected and actual Windows quota/geometry cases covered ample space, impossible
target, shrinking/changing capacity, no eligible history, Current protection,
multi-batch reclamation, cancellation, disk-full and uncertain control outcomes.
Live-copy diagnostics separated upcoming allocation, already-staged bytes, metadata
growth, physical cleanup and source reobservation; partial useful progress remained.

## Limits and current relationship

Native caller/quota geometry is Windows-only; Linux tests use trusted injected
probes. Later work permits same-volume local streamed workspaces and considers
proven Ready unpublished data before history. Neither change authorizes pressure
deletion for Forever/fixed-age policy. See [retention](../RETENTION.md).
