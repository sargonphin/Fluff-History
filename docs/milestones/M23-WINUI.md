# M23 — WinUI management and Timeline

## Purpose and delivered behavior

Established the WinUI/Application boundary, then declarative configuration/history
pages and product UX. Later refinements added source safety, independent UI
preferences, exact cleanup/deletion and consistent native icons/actions.

## Important decisions

Configuration edits use a valid/dirty draft, explicit Save/Discard and stable source
UUIDs. Browser workers serialize one captured reader, expose bounded immutable rows
and opaque selections, and dispose before key ownership. Folder/time navigation
and multi-selection retain capture authority. Latest and historic views remain file
selection, never snapshots.

Timeline uses inferred folders, distinct file/folder collision rows, persistent
large-icons/list preference and permanently visible details/versions. Buttons and
context menus share exact Restore/Restore to/Preview/Delete services. Exact deletion
revalidates non-Current selection before and after confirmation. Cleanup has a
read-only complete preview and separate destructive confirmation.

Source safety resolves known Windows locations/aliases off the UI thread. Hard,
discouraged and automatic barriers remain distinct. Source protection time became
one scan-wide instant across independently committed batches. First ready Save
requests initial backup; later saves/restarts do not force it. Retention default
became six months; manual cleanup moved entirely to Timeline.

## Validation and evidence

Portable presentation/adapters tested draft saves, browser generations/cancellation,
selection bounds, stale exact actions, restoration metadata, initial activation,
source policy and cleanup. Installed native previews exercised browsing, restore,
icons/layout and lifecycle; screenshot/package archives were iteration evidence,
not proof of complete accessibility or all Shell conflict choices.

## Limits and current relationship

Early UI-owned scheduling and full-MSIX deployment were superseded by the background
Agent/unified Setup. Localization and page layout were subsequently extended during
pre-release preparation. Current user behavior is in [configuration](../CONFIGURATION.md),
[restore](../RESTORE.md) and [localization](../LOCALIZATION.md), not old preview flows.
