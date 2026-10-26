# Timeline and restore

Timeline assembles surviving file versions at logical paths; it does not show
complete filesystem snapshots. Empty directories and independent directory metadata
are not retained. A deleted source remains browsable while its file versions survive.
Retention can remove the last version of a path.

## Selection

Latest means latest publication. A time view selects each path's newest surviving
version with `StoredUtc <= selected UTC time`, ordered by stored time, sequence and
ordinal. Wall-clock rollback can make this differ from latest publication. The UI
displays local dates; query contracts use UTC. Scope timestamps come only from
surviving file versions, not deletions, scans or maintenance.

The virtual root contains configured folders and inactive roots with retained
history. Browsing uses one serialized captured reader and opaque exact versions.
Changing/refreshing capture invalidates old selections and continuations. Folder
and time context is preserved where possible after a deliberate refresh/removal.
File/folder rows with the same name retain distinct roles. Earlier from Latest
skips the newest timestamp; a scope with one time has no earlier neighbor.

## Restore actions

Primary Restore writes to original locations after confirmation. Restore to selects
another destination. Preview materializes the selected exact version for opening.
Item and version context menus call the same services and authority checks as buttons.
Original restore requires explicit runtime root mappings, not guessed historic paths.

Headless APIs offer FailIfExists, SkipExisting and OverwriteFile, with StopOnFailure
or ContinueIndependentFiles. Folder planning checks intrinsic historical
file/directory collisions before writes. Windows interactive restore uses one Shell
operation for the bounded displayed selection and expands selected folders at its
captured view. Ordinary target conflicts belong to Windows Shell.

Exact payload identity, ordinary type, length and seed-zero xxHash64 are validated.
Encrypted chunks must authenticate through final EOF. Windows verifies selected
content before queueing and retains safe read handles; encrypted selections use a
restricted local plaintext staging directory. Partial destination success is reported
and is not rolled back if a later file fails. Restore does not delete unrelated live
files or rewind, renumber or remove repository versions.

Original-location restore assigns restoration-time mtime, verifies it and adjusts it
if needed to differ from the successful comparison baseline. This lets the next
size/mtime scan observe the restore. Alternate restore and preview preserve the
historical file mtime. Historical creation time and supported file attributes are
applied where supported; ACLs, alternate streams and historical directory fidelity
are not part of the file-history model.

## Removal and limits

Deleting one selected version is a separate destructive action. Application validates
its exact authority, existence and non-Current status before confirmation and again
under shared admission plus the repository writer. It uses the existing exact
version-removal primitive, then refreshes Timeline. A stale selection requires a
fresh capture; it cannot authorize deleting a replacement version.

Current versions remain protected from exact deletion and cleanup. Physical payload
cleanup can defer because of sharing or permissions after logical disappearance.
An already-held payload handle has OS lifetime semantics; a captured reader alone
does not reserve every historical payload indefinitely.

Preview is not a sandbox for the opened application. Plaintext staging cleanup is
best effort; open application locks can defer it, and secure erasure of temporary
storage is not guaranteed. Cancellation is cooperative. See
[security and recovery](SECURITY-AND-RECOVERY.md) and [retention](RETENTION.md).
