# Release 1.1 — Fluff-History 1.1.0.15

## Summary

Fluff-History 1.1 extends the file-history application to Windows 10, simplifies
Explorer integration and improves access to saved versions from file Properties.
The Windows/product version for this source baseline is **1.1.0.15**.

## Platform support

The current source targets Windows 11 x64 and Windows 10 22H2 x64, build 19045 or
later. Older Windows 10 releases, Windows Server, x86 and ARM64 are outside this
product target. The `v1.0.0` tag remains Windows 11-only. Product compatibility does
not extend Microsoft's servicing lifecycle for a Windows edition.

## Explorer integration

**Restore previous version…** uses a classic per-user Shell command. It appears
directly in the Windows 10 context menu and under **Show more options** on Windows
11. A small native launcher opens Timeline for one selected file or folder.

The experimental compact Windows 11 menu entry and its sparse package have been
retired. Current integration needs no package identity, Explorer certificate-trust
installation or administrator approval. The native Properties page remains
available on both Windows versions.

## Properties history

File Properties includes the Fluff-History icon, a concise status message and a
table of up to 20 saved versions with **Name** and **Date modified** columns. Click
the date header to change the sort order. Latest and oldest labels stay attached
to their saved versions; a single saved version shows only **latest**.

**Preview** opens the selected historical file. **Restore** restores that exact
version with normal Windows conflict handling. **Browse all versions** opens
Timeline for the full history.

The table remains unchanged while the page is open. Preview and Restore check the
current repository and source mapping when clicked, and validate that the exact
selected version still exists. A newer backup does not substitute a different
version. A removed version, replaced repository or changed mapping fails safely.
These actions do not rewrite history. There is no timed table refresh, Refresh
button or expired-session prompt.

## Interface and usability

Timeline tiles use space more efficiently. Page scrolling and settings controls
have more consistent spacing, and Configuration uses clearer power and advanced
settings icons across Windows versions. Overview and Configuration share the
backup-size display, calculated when the UI opens and after a backup completes.
Overview also shows update availability and links to update settings. About
diagnostics always include the application version, including development builds.

## Installation and update behavior

Deployment remains a self-contained, unpackaged, per-user Inno Setup EXE, with no
separately installed .NET runtime or LocalSystem service. Setup still uses its
ordinary hidden, non-elevated PowerShell lifecycle script; the former elevated
sparse-package certificate-trust helper is no longer part of current installation.

An older Properties DLL already loaded by Explorer can remain in use until its
host exits. Setup preserves compatible legacy broker replies and defers cleanup
of exact owned, hash-validated files that are still mapped. It protects the active
payload and retries cleanup later without terminating Explorer.

Public Setup and application binaries remain unsigned. Windows may still show
unknown-publisher or SmartScreen/reputation warnings. Install unsigned releases
manually: the built-in updater retains its approved Authenticode signer requirement
and cannot execute an unsigned installer. Stable considers stable releases only;
Beta considers both stable and beta releases by version precedence.

## Compatibility and notes

The per-file history model and Format 4 remain unchanged. Existing repository
encryption, recovery, retention and restore authority rules continue to apply.
Migration retires only exact known current-user predecessor packages when ownership
is established. Uncertain references retain their old payloads. Unrelated packages
and certificates are preserved; historical machine trust is not broadly removed.

See [Explorer integration](SHELL-INTEGRATION.md), [building](BUILDING.md),
[updates and releases](UPDATES-AND-RELEASES.md) and [testing](TESTING.md) for the
maintained technical contracts and limitations.
