# Opt-in AppFolder diagnostic

Run manually in an interactive Windows console with a configured real
Fluff-History public client ID. The registration must use the personal-account
audience, WAM broker redirect and only delegated Files.ReadWrite.AppFolder.
This tool is deliberately absent from the solution, installer and normal CI.

```powershell
dotnet run --project tools/FluffHistory.OneDriveSmoke -c Release `
  -p:ArtifactsPath=D:\FluffHistoryDiagnostics\M203\LiveTool -- `
  --run-live --client-id <real-public-client-guid> `
  --evidence D:\OwnedDiagnostics\appfolder-evidence.json
```

The explicit run flag and actual client GUID are mandatory. Do not substitute a
Microsoft sample registration. Verify the permission prompt describes the app
folder, never all user files; cancel if it does not. This checks real granted
scopes, approot, one unique synthetic file, exact readback, conditional deletion
and active-item absence. It contains no repository key or real source data.

Cleanup touches only the returned exact ID after the bytes match. Failed cleanup
is reported without enumerating/deleting unrelated app-folder files. Evidence is
limited to booleans, the scope name, sizes, timestamps and a synthetic checksum.
Account identities, URLs, tokens and service responses are not logged. This smoke
does not establish actual encrypted-repository or independent-machine recovery.

The application uses the reviewed MIT WAM dependency set. The public registration
has both broker and browser-fallback redirects configured.
The same version/hash/license guard applies to this tool and Desktop.
Live execution still requires explicit sign-in/consent;
build/fake-store tests are not live consent evidence.

On an invalid AppFolder response, the diagnostic may make one additional bounded
GET to the fixed AppFolder endpoint. It records only status and expected field/alias
shape booleans, never raw metadata, account identifiers, item IDs or token values.
