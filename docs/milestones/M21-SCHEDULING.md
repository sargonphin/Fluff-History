# M21 — Scheduling

## Purpose and delivered behavior

Implemented one coordinator for manual and fixed-cadence scheduled attempts, with
immutable detached status and revisioned notifications suitable for UI/Agent adapters.

## Important decisions

Explicit UTC anchor and positive interval define cadence independently of completion.
Monotonic timing measures duration; wall-clock rollback is protected by a runtime
consumed high-water mark. Start chooses a future occurrence. Busy/missed points are
discarded, not replayed. Stop-future and cooperative cancel-active are separate.
No failed/cancelled/deferred attempt causes immediate automatic retry.

Each encrypted attempt opens enrolled local DPAPI and disposes the key owner after
work. Scheduler state never retains passwords, tokens or a durable run log. Later
Agent ownership changes process lifetime without changing this timing contract.

## Validation and evidence

Injected TimeProvider tests cover start/stop, due/busy boundaries, completion timing,
clock rollback/gaps, manual admission, cancellation callbacks, subscriber isolation,
disposal and coalesced progress. Native Windows diagnostics exercised the real
platform adapters separately from fake clocks.

## Limits and current relationship

Blocked native I/O can outlast cancellation. Reboot/sign-out scheduling evidence
does not establish bare-metal standby/resume; that remains a dedicated checklist.
The current per-user Agent and configuration are described in
[architecture](../ARCHITECTURE.md) and [configuration](../CONFIGURATION.md).
