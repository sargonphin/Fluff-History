# M22 — Safeguards, background Agent and metadata maintenance

## Purpose and delivered behavior

Added power/network start safeguards and scoped Windows worker impact, moved the
sole production coordinator/settings writer into a normal per-user Agent, delivered
unified Setup, then added safe runtime metadata maintenance and management freshness.

## Important decisions

Safeguards are advisory starts, never repository reachability/deletion evidence.
Scheduled work scopes current-thread background mode without changing whole-process
priority or sleep policy. Manual starts later bypassed only the battery preference.
No LocalSystem service or UI fallback coordinator was introduced.

Bounded current-user typed IPC controls the Agent; closing UI leaves scheduling
alive. Crash-safe exclusive-file admission spans backup, maintenance and management
operations; repository writer authority remains final. Canonical settings live
outside package state. Unified per-user x64 Setup owns versioned external files,
sparse identity, Shell entries, startup, rollback and inventory-bound cleanup.

After complete safe scan/retention, metadata policy requires exact decoded reduction
of at least 30% plus 64 MiB reclaimable, with growth/cadence gates and scratch/output/
headroom bounds. Retirement retries independently. Progress is transient/coalesced;
physical compaction does not create Timeline revision. One separately overwritten
repository/location-bound last-status display cache survives restart without becoming
history, scheduler input or restore authority.

## Validation and evidence

Tests cover unknown/battery/network decisions, worker QoS restoration, IPC limits,
revision races/settings saves, status binding and maintenance thresholds/failures.
Installed campaigns checked clean install, update/rollback, uninstall/reinstall,
loaded Shell retirement, reboot/login startup, no missed-run replay and encrypted
scheduled backup with UI closed. Separate sign-out/sign-in passed historically.
Ordinary regressions do not replace those installed controls.

The Baseline metadata assessment separated cheap prefilter from exact assessment
over 30000 paths/250000 versions. It demonstrated an optional bounded maintenance
cost, not permission to compact on every backup or startup.

## Limits and current relationship

VM standby was unavailable; bare-metal suspend/resume remains unverified. Preferred
Internet-route cost can differ from LAN/VPN SMB cost. Cancellation is cooperative.
Current ownership, settings, maintenance and deployment are documented in
[architecture](../ARCHITECTURE.md), [configuration](../CONFIGURATION.md),
[retention](../RETENTION.md) and [updates](../UPDATES-AND-RELEASES.md).
