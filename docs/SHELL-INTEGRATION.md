# Explorer integration

Fluff-History installs two native x64 Explorer components: a context command to
browse history and a Properties page for recent versions. They load no CLR/WinUI,
parse no repository and perform no scheduling. Current Setup owns their deployment.
The supported application minimum is Windows 11 build 22000; Windows 10 support is
not established by portable tests or by the classic Properties API's availability.

## Command and Properties

The packaged IExplorerCommand uses supported sparse-identity COM-surrogate/context
registration. It is offered for one ordinary filesystem file or folder, independent
of whether history currently exists. Invoke writes a bounded token request and
activates one pre-XAML AppInstance management owner. Runtime root mapping resolves
the logical Latest folder and exact file focus; no repository/network query runs in
the context menu's GetState path.

The Apartment Properties DLL uses native controls and a cancellable worker. Files
show at most five recent opaque exact versions; counting stops at 4096 and exposes
an incomplete lower bound/unknown oldest result. Browse opens Timeline for more.
Folders show scope-derived times, never independently versioned folders.

Original-location Restore confirms with Cancel as default, reloads mapping/settings
and revalidates exact authority/version through existing Windows restore services.
It uses the actual Properties HWND, normal Shell conflicts and restoration-time
mtime. Current can be restored. Deletion and password prompts are not offered in
Explorer; recovery-required/unavailable/expired status directs the user to management.

## Broker and protocol

Broker mode precedes XAML and uses up to eight distinct AppInstance slots, separate
from the one UI owner and normal Agent. One broker owns one read-only captured
Application session and at most five version handles. It performs no scan, retention,
password/account authentication or key enrollment; encrypted open uses existing
current-user DPAPI only.

A random 128-bit token identifies the current-user pipe. Binary protocol v1 has a
16-byte header and at most 128 KiB body. Strings are counted valid UTF-16; paths
are capped at 32760 code units. Unknown operations, extra fields, malformed Unicode,
invalid tokens and oversized frames fail closed. Restore validates that the supplied
HWND belongs to the connected client process.

| Operation | ID | Contract |
| --- | ---: | --- |
| Open | 1 | Original path/kind layout |
| Restore | 2 | Session token, exact version token, client HWND |
| Close | 3 | Close the read-only session |
| Localized Open | 4 | Open plus captured language, at most 85 UTF-16 code units |
| Summary / Result | 101 / 102 | Bounded detached replies |

Old operation layouts remain accepted so an already-loaded DLL can use a newer
broker. Native captions/dialogs are generated from shared `Shell_*` catalogs. The
UI thread captures its Windows resource language marker and sends it with Open4;
pool-thread locale cannot choose a different broker language. Regional number/date
formatting stays separate. Management language preference does not alter Windows
native language or a page's captured context.

## Lifetime and path safety

Repository query has a ten-second broker deadline; native connection/read deadlines
are fifteen seconds, with a two-minute broker idle limit. Page close signals
cancellation without joining work on the Shell thread. A blocked synchronous network
read can outlast cooperative cancellation in the isolated read-only process.
Explicit restore can retain already-completed destination writes.

StartWorker retains context, module pin and DLL object count before submission.
Closing the submitted work object releases it asynchronously after callbacks finish;
the current design does not use a cancelling cleanup group. A queued callback after
page closure releases its ownership without activating a broker. Generation checks
reject stale window notifications. Native regressions exercise blocked-pool queued
closure and default-pool rapid closure, alongside malformed replies, disconnect and
outstanding reads. Closing the work object does not cancel its queued callbacks;
explicit cancellation is a separate thread-pool operation.

Application path resolution uses explicit normalized runtime mappings and stable
root UUIDs, not OriginalPath or leaf-name similarity. Nested-root barriers remain
effective for inactive children. Resolution performs no live source traversal.
Activation arguments contain only a fixed switch and token; one exact bounded
`<token>.request` contains no key/password/repository payload coordinates. Reparse
traversal is rejected. Cleanup examines at most 64 entries and removes only exact
ordinary token files older than one day. Requests remain untrusted same-user input.

Native activation resolves Setup's bounded `current-version.txt` under the current
user's fixed installation directory and starts that version's exact Desktop EXE.
Only the broker/request switches with one validated token are accepted. This avoids
depending on sparse package activation for the classic Properties broker and lets
an already-loaded DLL select the active installed version after an upgrade. Invalid
markers, missing executables and reparse paths fail closed. Launch does not wait for
the child process; Properties performs it on its worker. Existing AppInstance
admission still separates brokers from the single management window.

## Deployment and limits

Unified per-user Setup installs immutable versioned native files, sparse identity
for the modern command, and exact HKCU x64 CLSID/Apartment Properties registration.
Update/uninstall unregister before exact inventory/hash-bound file cleanup. A loaded
DLL may defer physical retirement until its host exits normally; Explorer is never
killed or restarted. Static MSVC runtime linkage avoids a Visual Studio prerequisite.

The public composition leaves Setup and Fluff-History binaries unsigned. A dedicated
stable self-signed certificate signs only the sparse identity, whose package name is
`Sargonphin.FluffHistory`. Setup verifies the exact pinned public DER, subject and
thumbprint before installing it into LocalMachine TrustedPeople. No Trusted Root or
private key is installed. This exact package requires machine trust on Windows;
user-only trust fails with 0x800B0109. Only this certificate step requests administrator
approval. Owned trust is recorded separately and removed only after the same-publisher
product package is absent for all users; pre-existing trust and unrelated certificates
are preserved. Development-preview identity/trust is a separate composition.

If Windows refuses sparse trust or registration, Setup reports the modern context
command as unavailable and retains that fact in About diagnostics. It continues
classic native Properties registration and Agent/application installation. Data and
history never depend on sparse identity. A changed pin or unowned/unexpected package
is a fatal admission error, not a reason to import an arbitrary certificate. Rerunning
Setup retries optional integration. Existing owned preview identity is retired during
the transition, with rollback retaining its exact prior package and payload.

Archived full-MSIX development helpers required separate Properties registration;
they are not the current deployment workflow. Neither the Properties broker nor
the command is a background scheduler. Namespace providers, virtual filesystems,
Cloud Files registration, overlays, search/thumbnail providers, tray behavior and
LocalSystem services are outside this integration.
