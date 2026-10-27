# Engineering guidance

Fluff-History is a Windows per-file history application. Read the current topic
documents before changing a subsystem. [Architecture](docs/ARCHITECTURE.md) defines
the product model; [Format 4](docs/REPOSITORY-FORMAT.md) defines active storage bytes.
Current implementation and topic documents define the supported behavior.

## History and source safety

- History belongs to individual successful file versions at `(SourceRootId,
  normalized relative path)`, never backup runs, restore points or complete snapshots.
- Preserve the frozen comparator/encoding. Do not substitute host Unicode casing,
  culture rules, file IDs, inode identity, rename tracking or content identity.
- Compare size and last-write time against the successful path baseline. Matching
  values mean unchanged without source hashing. Copy changed bytes once, computing
  seed-zero xxHash64 inline; normal backup has no mandatory destination checksum reread.
- Source access/instability failure produces no version or deletion evidence. Complete
  safe coverage proves absence; explicit protection changes are a separate path.
  Removing Current membership does not erase surviving versions.
- Directories are inferred from file histories, with no independent versions or
  empty-directory backup. Preserve distinct file/folder collision identities.
- Backup never modifies source files or obtains exclusive source locks for convenience.
  Preserve permissive sharing, reparse/EFS/device boundaries and source/repository
  separation. Automatic safety barriers remain separate from user exclusions.
- A no-change scan creates no repository mutation. Bounded batches can preserve
  partial useful progress; one protection time across a scan does not imply a snapshot.

## Authority, lifetime and destructive work

- `state.json` is sole history authority. Bootstrap unlock slots, settings, caches,
  workspaces, cloud items and status never become competing history authorities.
- Retain exclusive writer ownership and exact predecessor checks through mutation.
  Lost ownership aborts; never transparently reacquire, steal a lock or blindly retry
  replacement. Exact intended/predecessor inspection decides known outcomes;
  conflicting/unreadable state remains explicitly uncertain.
- Do not adopt/truncate abandoned metadata tails, reuse unknown partial payloads or
  sweep arbitrary files. Reusable unpublished payloads require exact provenance,
  content validation and revocation/binding before ordinary publication.
- Commit exact logical version removal before payload deletion. Protect exact Current
  regardless of age. Keep cleanup obligations until safe absence; logical bytes or
  delete acknowledgements do not establish actual reclaimed capacity.
- Only UntilSaturation authorizes pressure history deletion. Preserve actual
  caller-visible free-space feedback, pessimistic allocation/headroom, clean admission
  boundaries and bounded scratch/output. Fixed-age/manual policy is independent.
- Readers/cursors/selections bind captured authority and expire explicitly. Dispose
  readers/writers before key owners. Cancellation is cooperative; blocked native I/O
  can outlast it. Never turn cancellation or transport failure into proof of absence.
- Restore changes the destination without rewinding history. Validate exact selected
  versions and intrinsic conflicts before writes. Original-location restoration uses
  mtime distinguishable from the successful baseline; preserve partial outcomes.

## Subsystem boundaries

- Core owns portable models; Storage owns codecs/authority/physical maintenance;
  Engine owns traversal/copy/restore/retention orchestration; Application owns product
  services; Windows owns native adapters; Desktop owns WinUI presentation.
- One normal per-user Agent owns scheduling and backup-settings writes. Desktop uses
  bounded typed current-user IPC with no fallback coordinator. UI closure leaves
  scheduling alive. Shared mutation admission coordinates operations; the repository
  writer remains final authority. Busy/missed cadences are discarded, never replayed.
- Native Shell DLLs load no CLR/WinUI, parse no repository and perform no blocking
  history work on Explorer threads. Preserve COM/page/module/worker ownership,
  asynchronous closure, protocol limits, client HWND binding and exact opaque handles.
- Ordinary Setup owns replacement/rollback, sparse identity, startup and both Shell
  integrations. Preserve versioned immutable payloads and inventory-bound deferred
  cleanup without force-killing Explorer or backup processes.

## Security, maintenance and presentation

- Preserve optional encryption, random master separation, mandatory password recovery
  and local user DPAPI. No plaintext secrets in settings, diagnostics or Agent IPC.
  OneDrive recovery is explicit AppFolder-only split-secret work outside Agent/Shell;
  exact conditional cleanup must never broaden to unrelated cloud data.
- Encryption conversion is fenced, exclusive, resumable physical maintenance, not
  history mutation. Preserve authenticated source/target reconciliation and legitimate
  postcommit advancement. Do not guess mixed payload modes or remove markers manually.
- Updates remain outside history authority: fixed release identity, digest,
  Authenticode, approved signer, retained staging/path ownership and safe detached
  handoff through ordinary Setup. Never enable disposable update-test composition
  through production preferences or weaken the public signing gate.
- Keep resource strings separate from diagnostics/machine contracts. English RESW is
  canonical; live language switching and regional formatting are independent. Reuse
  shared page layout and semantic presentation services rather than duplicating them.
- One overwritten last-backup display cache is not run history, scheduler input,
  Timeline revision or restore authority. Metadata compaction is physical maintenance
  with explicit budgets/gates; normal open/comparison must not replay history.

## Change and validation expectations

Keep changes focused, document behavior and limitations, and add meaningful regression
coverage for changed risks. Do not infer NAS, power-loss, live-account or bare-metal
proof from portable or simulated tests. Use explicit disposable storage/accounts for
large or disruptive diagnostics; keep fixtures outside source/release archives.
Never commit credentials, tokens, private signing keys, profile crypto material or
personal backup content.

Use locked restore and ordinary Release validation from [BUILDING](docs/BUILDING.md)
and [TESTING](docs/TESTING.md). Run affected subsystem tests; use real Windows/native,
installed, SMB or recovery campaigns where the change crosses those boundaries.
Windows is the authoritative product and release-validation platform. Do not add
other client operating systems as release gates or claim support from diagnostics.
Heavy/Stress diagnostics are opt-in, not routine test-count inflation. For prose-only
changes, check links, removed references, whitespace and the runtime scope diff.
Preserve exact third-party notices. Dependency upgrades require review of exact
artifacts/transitives/licenses and deliberate approval-data/guard updates.

Current contracts: [configuration](docs/CONFIGURATION.md), [restore](docs/RESTORE.md),
[retention](docs/RETENTION.md), [security/recovery](docs/SECURITY-AND-RECOVERY.md),
[Shell](docs/SHELL-INTEGRATION.md), [localization](docs/LOCALIZATION.md),
[updates/releases](docs/UPDATES-AND-RELEASES.md), [licensing](docs/LICENSING.md).
