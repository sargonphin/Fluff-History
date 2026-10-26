# M24 — Native Explorer integration

## Purpose and delivered behavior

Delivered a packaged native IExplorerCommand, separate native Properties page,
bounded on-demand read-only broker and single-instance management navigation.
Timeline gained equivalent item/version context menus and always-visible details.

## Important decisions

Neither DLL loads managed UI or parses repositories. Native protocol/status is
bounded; exact opaque restore handles remain in one captured Application session.
Client HWND/mapping/authority validation precedes normal Windows restore. Broker
mode runs before XAML and never schedules, scans, prompts for recovery or enrolls keys.
Page closure cancels asynchronously; it never joins repository work on Explorer's
UI thread.

Initial full-MSIX helpers registered external Properties separately. Unified Setup
later became sole owner of both entries, versioned native files and exact deferred
cleanup without restarting Explorer. Native resources later gained shared-catalog
localization and backward-compatible Open4 language capture.

## Worker-submission review and validation

A review questioned whether SubmitThreadpoolWork immediately followed by
CloseThreadpoolWork could cancel a queued callback after context/module/count
ownership transfer. The current [work-object contract](https://learn.microsoft.com/en-us/windows/win32/api/threadpoolapiset/nf-threadpoolapiset-closethreadpoolwork) and an actual
blocked-pool reproduction did not confirm that cancellation claim. Production
retains the design without a cancelling cleanup group: callbacks own the context,
DLL count and module pin through FreeLibraryWhenCallbackReturns, even after page close.

The archived 2007 thread-pool article described cancellation on close, conflicting
with the current API reference and observed supported-target behavior. It was not
used to infer a defect. Explicit pending cancellation belongs to
[WaitForThreadpoolWorkCallbacks](https://learn.microsoft.com/en-us/windows/win32/api/threadpoolapiset/nf-threadpoolapiset-waitforthreadpoolworkcallbacks).
No behavior claim for older unsupported Windows targets follows from this test.

The focused regression queues actual page StartWorker calls behind a single blocked
worker, closes the pages, verifies retained state/count before release, then verifies
complete cleanup. An explicit WaitForThreadpoolWorkCallbacks(TRUE) positive control
demonstrates real cancellation in that setup. Tests also cover the opposite rapid
default-pool race. Baseline validation repeated 267 native checks, malformed reply/
refresh, disconnect/refresh, outstanding-read closure, 512 queued closures and 128
rapid closures. No callback leak/cancellation-before-start was established.

## Limits and current relationship

Blocked native I/O can outlast cancellation inside the isolated broker. Resource
captions follow Windows native selection independently of management UI preference.
Windows 10 compatibility is not established. Full current protocol, path, lifetime
and deployment contracts are in [Shell integration](../SHELL-INTEGRATION.md).
