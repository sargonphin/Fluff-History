# Explorer integration

Fluff-History uses a classic static Shell verb for exactly one ordinary filesystem
file or directory. Windows 10 22H2 x64 presents **Restore previous version…** directly
in its context menu. Windows 11 x64 presents it under **Show more options**.
The native Fluff-History Properties page remains available on both systems.
The tested product minimum is build 19045; the technical SDK baseline remains 19041.

## Classic command and Properties

Setup owns only these per-user registry verb keys:

```text
HKCU\Software\Classes\*\shell\FluffHistory.RestorePreviousVersion
HKCU\Software\Classes\Directory\shell\FluffHistory.RestorePreviousVersion
```

`MultiSelectModel=Single` hides the command for multi-selection. `MUIVerb` points to
resource 3000 in the stable `FluffHistory.ShellLauncher.exe`; its 44 language resources
are generated from the same `Shell_CommandTitle` catalogs as Properties. The launcher
contains the product icon. Menu construction requires no product DLL or WinUI load.

The command is `"<stable launcher path>" "%1"`. There is no command interpreter.
The small native process validates one absolute file/directory path, Unicode, bounds,
ordinary attributes and reparse ancestors, without canonicalizing it into a different
item. Extended drive/UNC syntax preserves permitted long names and trailing spaces.
It writes the existing bounded token request, resolves `current-version.txt`, starts
the exact current Desktop, then exits. It does not require the Agent or open history.
Invalid invocation fails quietly. Failed activation removes its newly created request.
There are no background, virtual-namespace, URL or multi-item registrations.

The pre-XAML AppInstance owner routes the request to Timeline for that file/folder.
Requests and Properties share the existing hardened activation boundary.

The Apartment Properties DLL uses native controls and a cancellable worker. Files
show a native two-column table with at most twenty recent opaque exact versions:
historical filename and original modification date. Date-header clicks sort the
display by the numeric UTC modification time, retaining the selected exact handle.
Latest/oldest markers refer to publication order, independently of date sorting;
oldest is marked only when there are multiple versions and the full retained history
fits in the table. A single version shows latest only; latest also takes precedence
if an older broker supplies both marker bits. Counting
stops at 4096 and reports an incomplete lower bound. Browse opens Timeline for more.
The product icon and a fixed introduction precede one natural status paragraph.
There is no backup-location debug text, duplicate status or Refresh button.
Folders show scope-derived times, never independently versioned folders.

Original-location Restore confirms with Cancel as default, reloads mapping/settings
and revalidates exact authority/version through existing Windows restore services.
It uses the actual Properties HWND, normal Shell conflicts and restoration-time
mtime. Current can be restored. Deletion and password prompts are not offered in
Explorer. Preview uses the existing historical-preview service, normal Windows file
association/Open With and exact owned temporary-file cleanup. Its lease remains with
the broker until another preview replaces it or the page closes.

Preview and Restore reopen history only on a click, reload mapping/settings, verify
the same repository UUID and look up the exact selected identity. A new backup may
advance authority without invalidating a surviving selection; a removed version or
changed repository/mapping fails without substitution. The existing Windows action
checks the newly captured authority again before materialization. The displayed
list never refreshes automatically. Unavailable/recovery failures use ordinary prose,
without an expired-session or Refresh prompt.

## Broker and protocol

Broker mode precedes XAML and uses up to eight distinct AppInstance slots, separate
from the one UI owner and normal Agent. One broker owns one read-only captured
Application session while loading. A new table releases its repository reader/key
capture after loading and retains at most twenty detached version handles until
the owned pipe/page closes. It performs no scan, retention,
password/account authentication or key enrollment; encrypted open uses existing
current-user DPAPI only.

A random 128-bit token identifies the current-user pipe. Binary protocol v1 has a
16-byte header and at most 128 KiB body. Strings are counted valid UTF-16; paths
are capped at 32760 code units. Unknown operations, extra fields, malformed Unicode,
invalid tokens and oversized frames fail closed. Restore validates that the supplied
HWND belongs to the connected client process; Preview uses the same validation.

| Operation | ID | Contract |
| --- | ---: | --- |
| Open | 1 | Original path/kind layout |
| Restore | 2 | Session token, exact version token, client HWND |
| Close | 3 | Close the read-only session |
| Localized Open | 4 | Open plus captured language, at most 85 UTF-16 code units |
| Table Open | 5 | Localized Open, opting into the twenty-row structured reply |
| Preview | 6 | Session token, exact version token, client HWND; Table Open only |
| Summary / Result | 101 / 102 | Bounded detached replies |
| Table Summary | 103 | Status, session, prose/count title, up to twenty token/name/date/UTC-tick/marker rows |

Old operation layouts remain accepted so an already-loaded DLL can use a newer
broker. Legacy Summary101 remains limited to five rows and its original exact layout.
Native captions/dialogs are generated from shared `Shell_*` catalogs. The
UI thread captures its Windows resource language marker and sends it with Open5;
pool-thread locale cannot choose a different broker language. Regional number/date
formatting stays separate. Management language preference does not alter Windows
native language or a page's captured context.

## Lifetime and path safety

Repository query has a ten-second broker deadline; native connection/read deadlines
are fifteen seconds. Legacy pages keep their two-minute broker idle limit. New table
pages release the read capture immediately, retaining only detached selections and
the pipe endpoint for on-click validation. They do no timed background queries.
Page close signals
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
depending on package activation for the Properties broker and lets
an already-loaded DLL select the active installed version after an upgrade. Invalid
markers, missing executables and reparse paths fail closed. Launch does not wait for
the child process; Properties performs it on its worker. Existing AppInstance
admission still separates brokers from the single management window.

## Deployment and migration

The ordinary unpackaged per-user Inno Setup (`PrivilegesRequired=lowest`) owns
immutable versioned payloads, the stable Shell/Agent launchers, HKCU static verbs,
Agent startup and the exact x64 CLSID/Apartment Properties registration. No context
integration step writes HKLM, requests UAC, registers a sparse package or adds trust.
There is no Identity.msix or ExplorerCommand DLL in the current payload. Development
PE signing is a build-time diagnostic choice, independent of installation trust.
Unsigned public builds need no certificate or private key.

Repair reconciles only the two exact owned verb keys. Conflicting/unrecognized
values or children are preserved and reported; unrelated neighboring verbs survive.
Uninstall removes exact owned registration and inventory-bound files, retaining
settings and repository data under their existing policy. Loaded Properties DLLs
may defer deletion until their host exits normally. A mapped native image can report
access denied rather than a sharing violation; both refusals retain the exact
hash-bound cleanup obligation and allow later updates to proceed. Explorer can also
keep displaying the older loaded tab until its process exits, even after the active
application version changes. Explorer is never terminated by Setup.

After successful application activation, upgrade attempts retirement of exact known
current-user predecessor sparse identities, bound to the old owned payload inventory
and build metadata. Removal/discovery failure preserves the referenced old payload,
records bounded pending cleanup and leaves the working classic command installed.
Subsequent Setup retries. Uninstall refuses to remove a payload still referenced by
an unresolved legacy package. No similarly named or other-user packages are removed.
Old exact machine TrustedPeople certificates are left unchanged and unused; no new
elevation is introduced to remove them. Trusted Root is never touched.

The Properties broker and command are not background schedulers. Namespace providers,
virtual filesystems, Cloud Files registration, overlays, search/thumbnail providers,
tray behavior and LocalSystem services are outside this integration.
