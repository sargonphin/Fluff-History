# Release 1.1.1 — Fluff-History 1.1.1.1

## Summary

Fluff-History 1.1.1 fixes a crash when selecting a cleanup option in Timeline,
including **Keep only the most recent version**. The Windows/product version is
**1.1.1.1**.

## Timeline cleanup

Selecting a cleanup option now shows the preview without closing the application.
You can switch between cleanup options, close and reopen the dialog, and review
the separate confirmation before removing versions.

Selecting an option only prepares a preview. Nothing is removed until you confirm.
**Keep only the most recent version** retains the newest saved version at each
path, including files no longer backed up. Current versions remain protected.

## Updating

Run `Fluff-History-Setup.exe` to update an existing installation. Settings and
backup history are preserved, and existing Format 4 repositories need no conversion.

Public installers and application binaries are unsigned. Windows may display an
unknown-publisher or SmartScreen warning. Install this update manually; the
built-in updater can announce the release but cannot execute an unsigned installer.

The supported platforms remain Windows 11 x64 and Windows 10 22H2 x64, build 19045.
See the [1.1 release notes](RELEASE-1_1.md) for the Windows and Explorer features in
this release family, and [updates and releases](UPDATES-AND-RELEASES.md) for update
behavior.
