# Roadmap and known limits

Fluff-History is pre-release. Future work is subject to validation and review; no
dates or completion guarantees are implied. The file-history model in
[architecture](ARCHITECTURE.md) remains the boundary: no disk imaging, global
snapshots, rename identity, backup-run restore points or Cloud Files provider.

## Release readiness

- Preserve the first-party MIT license and establish verified vulnerability reporting.
- Review redistributed binary terms, complete notices and approve production signing.
- Validate immutable releases, signing/key rotation and the updater's fixed endpoint.
- Complete installed/bare-metal recovery, suspend/resume, interaction and accessibility
  checks before release candidates. VM reboot/sign-out results do not prove physical sleep.
- Review Store certification/distribution policy and update-channel behavior separately.
  The current per-user EXE/sparse-identity installer is not a full-MSIX Store package.

The Entra recovery registration is separate from code signing and Store publisher
identity. Domain verification does not itself establish signing trust or a verified
publisher badge. Any later MSIX route needs a complete startup/update/Properties
deployment plan, not just proof that a Shell extension can be registered.

## Recovery and user experience

Experimental OneDrive recovery needs broader wrong-account/revocation/quota,
recycle-bin, real legacy cleanup and installed/bare-metal campaigns. Core independent
Windows-machine recovery has passed historically; those results do not complete all
failure/dialog checks. A late decryption crash can retire the local recovery half
before remote cleanup, losing exact coordinates to a harmless cloud-only random
secret. Consider bounded coordinate retention through completion; never compensate
with a wildcard cloud sweep.

Framework-owned ToggleSwitch captions can follow Windows language while application
labels switch live. Native Explorer language and installer base messages have their
own selection/fallback. Broader collision-dialog choices, preview applications and
accessibility need human validation. A Shell conflict dialog can display an immutable
payload UUID source name even when the restored destination name is correct.

Preview cleanup owns only its exact live lease files. Locks or application-created
content can remain; existing markers do not prove other applications' liveness or
authorize a stale sweep. Secure temporary-data erasure is not promised.

After adoption without application settings, effective repository exclusions are
conservatively retained; their old user/automatic provenance cannot be reconstructed.
An advanced editor should explain that limit rather than silently expanding coverage.

## Storage, network and performance

| Area | Current limit / follow-up |
| --- | --- |
| Publication durability | Flush/atomic replacement/process-crash evidence does not prove directory/device/server power-loss persistence |
| Blocking I/O | Synchronous source/SMB calls can outlast cancellation; future cancellation work must preserve exact uncertain outcomes |
| Network cost | Preferred Internet-route cost can differ from LAN/VPN SMB cost; advisory hints never establish reachability |
| Broader servers | Add actual authority-observer/retained-handle/fault evidence for other Windows-client NAS/redirector combinations; non-Windows clients are unsupported |
| Remote restore | Errors can leave an unconfirmed target; inspect exact evidence rather than blindly retrying/deleting |
| Cold history | Scattered records exceed the bounded handle cache; scope changes and early sparse as-of views can still scan substantial history |
| Retired metadata | Exact deleted identities remain in the retirement list and are repacked; measure growth before pruning proven-absent entries |
| Scan scope | A selected-root scan currently builds the repository-wide Current map; small selections can pay for unrelated roots |
| New paths | Absent-Current files perform individual newest-history lookups; batching is a profiling question, not permission to weaken reactivation |
| Allocation churn | Historical initial-copy and Shell diagnostics show high cumulative allocation with much smaller live samples; profile phases before optimizing |
| Range caching | Separate sparse publications can refetch ranges; any reuse must preserve predecessor/append ownership |
| Read-ahead | Bounded buffers trade fewer calls for overfetch; slower links/sparser histories need representative measurements |
| Budgets | Charged scan entries/bytes are engineering accounting, not exact CLR heap quotas |

USN/incremental discovery is deferred until profiling justifies it and must retain a
full-scan fallback with identical path/observation semantics. Delta updates, mirrors,
custom update servers and enterprise packaging are not current commitments.
