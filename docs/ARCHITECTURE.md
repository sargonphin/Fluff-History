# Architecture

Fluff-History stores the successful versions of individual files. Its identity is
`(SourceRootId, normalized relative path)`. A scan is a transient attempt, a
metadata batch is a publication mechanism, and a file version is durable history.
There are no complete backup-run snapshots, manifests or restore points.

## Authority and project boundaries

| Project | Responsibility |
| --- | --- |
| Core | Frozen path identity, file/version/configuration and retention/capacity models |
| Storage | Format 4 codecs, authoritative state, immutable metadata/payloads, writer admission, encryption and physical maintenance |
| Engine | Source traversal/copy, restore planning/materialization, retention and capacity orchestration |
| Application | Desired configuration, scheduling, safeguards, browser/restore/recovery services and update state |
| Windows | Native filesystem/capacity, Shell/STA, IPC, source policy, Graph and installer handoff adapters |
| Desktop | WinUI management, Timeline, explicit recovery and live language switching |
| Agent | One normal per-user scheduling/configuration owner |
| InstallSupport | Bounded installer support and detached update handoff |

The native Explorer components never load CLR/WinUI or parse repositories. They
activate management UI or use an on-demand read-only broker. Ordinary Setup owns
the installed Agent, binaries, sparse identity and both Shell registrations.

`repository.json` identifies the product, format, encryption mode and unlock slots.
`state.json` is the sole history-authority pointer. Metadata roots bind generation,
sequence, transaction, epoch, exact predecessor and indexes. Workspaces, caches,
settings, progress, cloud objects and installer state never replace that authority.
The [format specification](REPOSITORY-FORMAT.md) gives exact encodings and limits.

## Scan and comparison

Readable eligible files compare size and last-write UTC against their latest
successful path baseline. A match causes no source content read or version. This
deliberately misses content changes preserving both values. Creation time,
attributes and display spelling belong to a successful version; merely scanning
does not refresh them. No persistent NTFS ID, inode, rename or content identity
follows a file to another path.

One source stream computes seed-zero xxHash64 while writing a destination draft.
Handle/path observations check stability around the copy; unsupported reparse,
device and EFS boundaries are not copied as ordinary files. Normal backup does not
checksum the destination again. Encrypted payloads authenticate chunks without
changing the plaintext comparison/checksum contract.

The scan samples one UTC protection time for all its new versions. Bounded batches
still commit independently, and useful committed progress survives later failure.
The default pending limits are 128 changes and 64 MiB; a larger permitted file is
published alone. This timing does not create an atomic tree snapshot.

Current comparison traverses Current, not the entire historical journal. A path
without Current can reactivate its exact newest surviving version if size/time
match. Rename evaluates the destination path independently. No-change scanning
does not mutate repository history; application status can still change.

## Deletion, exclusions and historical directories

Complete accessible coverage can prove a formerly Current path absent. Failed or
incomplete enumeration, unreadable files and unsupported boundaries preserve the
covered successful baseline. A positively observed ordinary directory replacing
an exact file disproves that file's Current membership. None of these operations
erase surviving versions.

Explicit protection/exclusion changes are separate configuration mutations and
may end Current membership before traversal. Automatic system-safety barriers
protect skipped scopes without pretending they were deleted. Independent path
exclusions and nested-root identity barriers are described in [configuration](CONFIGURATION.md).

Directories are inferred from surviving file paths; they have no independent
versions, historical timestamps or empty-directory record. History can contain a
file and descendants at the same logical name from different times. Timeline
distinguishes those rows; restore preflights the selected materialization rather
than silently renaming impossible structures.

## Publication, readers and maintenance

One retained exclusive writer handle covers each mutation session. Losing it aborts
the session. There is no timestamp-based lock stealing or transparent reacquisition.
The writer stages owned append bytes, flushes/readbacks payload/metadata prerequisites,
checks exact predecessor, and replaces state once. Exact intended authority proves
publication; exact predecessor proves nonpublication; unreadable/conflicting/lost
ownership remains uncertain. Fresh explicit reconciliation classifies an abandoned
candidate; it does not finish it speculatively.

Readers capture authority and are not thread safe. Their cursors/actions bind that
capture; retained readers are disposed before the unlocked key owner. Retirement
can expire an epoch, and removed payloads are not silently substituted. Already-open
payload handles follow OS sharing/unlink behavior.

Metadata uses bounded independently compressed blocks; encryption follows compression.
Unencrypted payloads remain raw immutable copies. Local workspaces cache requested
encoded ranges and keep decoded blocks in bounded RAM, never a decoded disk cache.
History/capacity sorting scratch is protected in encrypted mode. Reusable unpublished
copies require exact provenance, local validation and revocation/binding before adoption;
unknown or partial orphans are never adopted and there is no cross-path deduplication.

Explicit rebuild reconstructs replaceable indexes from intact semantics. Dense
compaction validates survivors and preserves pending cleanup anchors, then retires
exact old metadata. Runtime maintenance runs only after a complete safe scan and
retention result, with headroom/budgets and a transient cadence. It requires an
inclusive 30% decoded reduction and 64 MiB reclaimable floor. Physical maintenance
does not create a logical Timeline revision. See [retention](RETENTION.md).

## Timeline and restore

Latest selects surviving versions by publication order. A selected UTC time chooses
the newest eligible surviving version per path using stored time and publication
ties. Later live deletion does not hide retained history. Retention can make a
historical view incomplete; no synthetic snapshot is reconstructed.

Timeline timestamps come only from surviving versions in scope. One captured reader
can retain one bounded sorted timestamp snapshot; scope changes discard it. Warm
navigation slices that data after epoch validation. Cold broad scopes and sparse
as-of selection can still traverse substantial history. Bounded memory is not a
constant-time performance guarantee.

Restore reads exact surviving identities and changes only the destination. Original
location needs an explicit safe runtime mapping. Windows uses Shell conflicts and
progress; integrity is verified before selected content is handed to Shell. Original
location uses a restoration-time mtime distinguishable from the comparison baseline;
alternate restore/preview preserves historical mtime. [RESTORE](RESTORE.md) gives
selection, partial-progress and plaintext-staging limits.

## Application lifetime and external services

The Agent owns one BackupCoordinator and backup-settings writer. Desktop controls it
through bounded current-user typed IPC; closing Desktop leaves scheduling active.
Manual backup, scheduled backup and maintenance use shared cross-process admission;
the repository writer is final authority. No LocalSystem service is installed.

Fixed UTC cadence discards missed/busy points rather than replaying them. Cancellation
is cooperative and can outlast blocked native I/O. Battery preference applies to
scheduled starts; manual starts retain network and explicit unknown-power safeguards.
Windows thread background mode scopes automatic work, not the entire UI process.

The Agent may overwrite one repository/location-bound last-backup display status.
It is not run history, scheduler input, a Timeline event or restore authority.
Preferences and strict update state are separate from backup configuration.

Password and current-user DPAPI are independent unlock paths. Experimental OneDrive
recovery is an explicit Desktop operation: only a random split-secret half goes to
AppFolder. Agent/Shell ordinary work never authenticates or depends on Graph.
[SECURITY-AND-RECOVERY](SECURITY-AND-RECOVERY.md) describes conversion and limitations.

Automatic updates are separate from repository/history authority. Agent discovers
fixed GitHub releases, requires a digest, Authenticode and approved signer for execution,
and hands off through a detached helper to ordinary Setup. No force termination or
backup-data migration is part of update installation. See [updates](UPDATES-AND-RELEASES.md).

## Documentation authority

README introduces the product. Current `docs/*.md` describe implementation contracts.
`docs/milestones/` contains historical decisions and evidence, not competing current
specifications. Exact upstream legal files govern their material.
