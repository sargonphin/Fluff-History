<div align="center">

<img src="artwork/FluffHistory.png" width="190" alt="Fluff-History folder and history clock icon">

<h1>Fluff-History</h1>

<p><strong>A modern file-history experience for Windows 10 and Windows 11</strong><br>
Automatically keep earlier versions of your files, browse them through time, and restore them from the app or directly from File Explorer</p>

<p>
  <img src="https://img.shields.io/badge/Windows%2010%20%2B%2011-x64-0078D4?logo=windows11&logoColor=white" alt="Windows 10 and Windows 11 x64">
  <img src="https://img.shields.io/badge/backup-local%20%2B%20SMB-16A34A" alt="Local and SMB backup">
  <img src="https://img.shields.io/badge/languages-44-7C3AED" alt="44 languages">
  <img src="https://img.shields.io/badge/Explorer-integrated-2563EB" alt="File Explorer integration">
</p>

<p><em>Keep your history on storage you control, with no cloud account required for backup or restore</em></p>

</div>

## 🕰️ History of your storage

Fluff-History keeps **successful versions of individual files** instead of creating monolithic disk images or pretending every backup run is a complete filesystem snapshot

<img width="1310" height="753" alt="Fluff-History Overview tab shows important information at a glance" src="https://github.com/user-attachments/assets/465e1f55-1b3c-43bf-934b-1f05ba6dee0c" />

That model gives a few useful properties

<table>
<tr><td>🛟 A successful version stays available even if a later backup cannot read the source</td></tr>
<tr><td>📚 Removing a file from protection does not automatically erase its surviving history</td></tr>
<tr><td>↩️ Restoring an older version changes the destination without rewinding or deleting newer retained history</td></tr>
<tr><td>🛡️ The exact current version is protected from retention cleanup</td></tr>
<tr><td>📍 <strong>The path is the identity.</strong> A file's history belongs to its protected path, not to a hidden filesystem ID. Renaming or moving a file starts history at the new path while retained versions remain available at the old path</td></tr>
<tr><td>⚡ No-change scans avoid rereading file contents when size and modification time still match the successful baseline</td></tr>
</table>

> [!NOTE]
> Change detection uses **file size + last-write time**, so a content change that preserves both values is not detected
>
> Directories are inferred from saved files, so empty directories are not independently versioned
>
> Fluff-History is file history, not disk imaging

More detail: [Architecture](docs/ARCHITECTURE.md) · [Configuration](docs/CONFIGURATION.md) · [Timeline & restore](docs/RESTORE.md) · [Retention](docs/RETENTION.md)

## ✨ Features

<img width="1310" height="753" alt="A modern Timeline allows browsing for files as if it was modern Explorer" src="https://github.com/user-attachments/assets/18e0a13e-a1e2-44b6-82ba-dd85004da7a2" />

<table>
<tr><td>🕒 <strong>Timeline browsing</strong> - Move through retained versions by folder and time, including files that no longer exist at the source</td></tr>
<tr><td>🔄 <strong>Flexible restore</strong> - Restore to the original location, restore somewhere else, or preview an exact historical version first</td></tr>
<tr><td>🪟 <strong>File Explorer integration</strong> - Open history from Explorer and view recent versions from a native Properties page without opening the full Fluff-History UI first</td></tr>
<tr><td>⚙️ <strong>Background backups</strong> - A lightweight per-user Agent keeps scheduled backups running after the Fluff-History UI is closed</td></tr>
<tr><td>🗂️ <strong>Choose exactly what to protect</strong> - Back up multiple folders, add independent exclusions, and keep historical identities when folders are temporarily removed from protection</td></tr>
<tr><td>💾 <strong>Local or network storage</strong> - Store history on a local disk or a normal UNC/SMB destination such as a NAS or file server</td></tr>
<tr><td>⚡ <strong>Format 4 storage engine</strong> - Fluff-History's Format 4 repository keeps the active file catalog and historical metadata compact and independently readable. Ordinary scans compare against Current instead of replaying the whole history, unchanged files avoid content reads, and changed files are streamed once while being checksummed</td></tr>
<tr><td>🚀 <strong>Optimized Timeline engine</strong> - Captured timestamps and warm navigation reuse prepared history state, while bounded range reads avoid rebuilding or rereading the full archive just to move around the Timeline</td></tr>
<tr><td>🧹 <strong>Flexible retention</strong> - Keep versions for 1, 3, 6 or 9 months, 1 or 2 years, forever, or until storage pressure requires older non-current history to be removed</td></tr>
<tr><td>🛡️ <strong>Conservative failure handling</strong> - An unreadable file, interrupted source, or uncertain network state is never silently treated as proof that your file was deleted</td></tr>
<tr><td>🌍 <strong>44 languages</strong> - Desktop and native Shell resources cover 44 language catalogs, with live application-language switching and RTL support</td></tr>
<tr><td>⬆️ <strong>Built-in update channels</strong> - Stable and Beta channels support manual or scheduled checks, backup-aware handoff, rollback through ordinary Setup, and strict installer verification</td></tr>
</table>

## 🔐 Security & recovery

Fluff-History can keep the repository unencrypted, or protect it with authenticated encryption and multiple recovery options

<table>
<tr><td>🔒 <strong>AES-256-GCM repository encryption</strong> - Metadata and file contents are authenticated. Damaged or modified encrypted data is rejected instead of being trusted silently</td></tr>
<tr><td>🗝️ <strong>Portable recovery password</strong> - The random repository master key is protected independently. PBKDF2-HMAC-SHA256 with a fresh salt and 600,000 iterations keeps password recovery portable across machines</td></tr>
<tr><td>👤 <strong>Windows local unlock</strong> - Current-user DPAPI can unlock the same repository key for convenient day-to-day access without replacing the recovery password</td></tr>
<tr><td>☁️ <strong>Experimental OneDrive recovery</strong> - Fluff-History can retrieve a tiny recovery tag from its dedicated OneDrive AppFolder. The Microsoft permission used by the app is scoped to that AppFolder, so Microsoft's authorization layer does not give Fluff-History permission to browse the rest of your OneDrive. No backup file, repository encryption key or recovery password is stored on OneDrive</td></tr>
<tr><td>🧱 <strong>Fail-closed safety</strong> - Repository publication, recovery, destructive cleanup and update installation require validated state instead of guessing through ambiguous failures</td></tr>
<tr><td>✅ <strong>Verified update path</strong> - Executable updates require a GitHub SHA-256 digest, Windows Authenticode validation, matching product metadata and an explicitly approved signer before launch</td></tr>
</table>

The full implementation details and security boundaries are documented in [Security and recovery](docs/SECURITY-AND-RECOVERY.md)

## 🪟 Built into Windows where it matters

Fluff-History uses a modern WinUI interface, while common recovery actions are also available directly from File Explorer

<img width="477" height="536" alt="Fluff-History is also available from the Properties tab" src="https://github.com/user-attachments/assets/e9315a30-19df-48f9-b464-6392194681c8" />

<table>
<tr><td>📂 <strong>Browse history</strong> directly from the File Explorer context menu</td></tr>
<tr><td>🕔 See up to <strong>20 saved versions</strong> in a sortable table, with Preview and Restore, from a file's native Properties page</td></tr>
<tr><td>↩️ Restore an exact version with normal Windows conflict handling</td></tr>
<tr><td>⚡ Keep Explorer responsive because the native Shell components do not load WinUI/.NET or parse the repository inside Explorer</td></tr>
</table>

The Explorer integration is deliberately small and isolated. History work is handled out of process by Fluff-History itself

More detail: [Explorer integration](docs/SHELL-INTEGRATION.md)

## 🧪 Tested beyond the happy path

Fluff-History has a large automated and environment-specific validation surface rather than relying only on UI smoke tests

In the **Release 1.0 Windows validation baseline**

<table>
<tr><td>✅ <strong>1,748 managed tests passed</strong>, 6 environment/platform-dependent tests were skipped and <strong>0 failed</strong> in the ordinary Windows Release suite</td></tr>
<tr><td>🪟 Native Shell validation passed <strong>325 checks</strong>, plus malformed-response, disconnect, outstanding-read, queued-worker and rapid-lifetime stress cases</td></tr>
<tr><td>🌍 All <strong>44 localization catalogs</strong> passed structural and generation guards</td></tr>
<tr><td>🌐 Validation has included multi-machine environments spanning VMs and bare-metal hosts, with real network paths and Windows/Samba SMB scenarios rather than only simulated network responses</td></tr>
<tr><td>🔐 Authentication dependency hashes and licenses, installer lifecycle rules, version policy, crypto and recovery fault stages, retention, capacity, update state, restore, and repository corruption paths have dedicated regression coverage</td></tr>
<tr><td>🧰 Separate opt-in suites exist for real SMB faults, large metadata populations, live Microsoft-account recovery, installed lifecycle and other environment-specific behavior</td></tr>
</table>

Passing tests are evidence, not a promise that every NAS, power-loss event or Windows environment behaves identically

[Testing and evidence](docs/TESTING.md) documents exactly what is exercised and what is not

## 🎛️ Everyday controls

The Fluff-History UI keeps backup configuration separate from application preferences

<table>
<tr><td>⏱️ Automatic backups default to <strong>hourly</strong>, with 10-minute through 24-hour intervals available</td></tr>
<tr><td>🔋 Battery and network-cost safeguards can defer automatic work</td></tr>
<tr><td>📁 Exclusions can remove covered files from active protection without destroying retained history</td></tr>
<tr><td>🧹 <strong>Clean Timeline</strong> provides explicit one-shot cleanup independently of automatic retention</td></tr>
<tr><td>🎨 Appearance, Timeline view, language, Stable/Beta update channel and update frequency are application preferences</td></tr>
<tr><td>🔐 Encryption conversion is explicit, exclusive and resumable instead of being a hidden repository rewrite</td></tr>
</table>

## 📦 Installation & source

The current 1.1 source targets **Windows 11 x64** and **Windows 10 22H2 x64, build 19045**. The `v1.0.0` tag remains Windows 11-only

Explorer integration offers **Restore previous version…** in the classic context menu (use **Show more options** on Windows 11), plus the native Fluff-History Properties history page on both systems

Product compatibility does not extend Microsoft's support lifecycle for your Windows edition or servicing configuration. Older Windows 10 versions, Windows Server, x86 and ARM64 are outside this compatibility target

The application uses a self-contained per-user Setup. No separately installed .NET runtime or LocalSystem service is required

Public Setup and application binaries are unsigned; Windows may show an unknown-publisher or reputation warning. Current Setup uses ordinary per-user registration without package identity or certificate installation. The experimental modern Windows 11 context entry has been retired; the classic command and Properties page remain available

Install unsigned releases manually. The built-in updater retains its approved Authenticode signer requirement and cannot execute an unsigned installer. See [updates and releases](docs/UPDATES-AND-RELEASES.md)

For source builds and development installers, see [Building Fluff-History](docs/BUILDING.md)

Release identity and updater behavior are documented in [Updates and releases](docs/UPDATES-AND-RELEASES.md)

## 📚 Documentation

<table>
<tr><td>🏗️ <strong><a href="docs/ARCHITECTURE.md">Architecture</a></strong> - Product design, storage model and subsystem boundaries</td></tr>
<tr><td>⚙️ <strong><a href="docs/CONFIGURATION.md">Configuration</a></strong> - Backup folders, exclusions, scheduling and safeguards</td></tr>
<tr><td>🕒 <strong><a href="docs/RESTORE.md">Timeline and restore</a></strong> - Browsing, preview and restore behavior</td></tr>
<tr><td>🧹 <strong><a href="docs/RETENTION.md">Retention, cleanup and capacity</a></strong> - Version lifetime and storage-pressure behavior</td></tr>
<tr><td>🔐 <strong><a href="docs/SECURITY-AND-RECOVERY.md">Security and recovery</a></strong> - Encryption, password recovery, DPAPI and OneDrive recovery</td></tr>
<tr><td>💽 <strong><a href="docs/REPOSITORY-FORMAT.md">Repository format</a></strong> - On-disk Format 4 details</td></tr>
<tr><td>🪟 <strong><a href="docs/SHELL-INTEGRATION.md">Explorer integration</a></strong> - Context menu, Properties page and native Shell boundary</td></tr>
<tr><td>🌍 <strong><a href="docs/LOCALIZATION.md">Localization</a></strong> - Language coverage and translation pipeline</td></tr>
<tr><td>⬆️ <strong><a href="docs/UPDATES-AND-RELEASES.md">Updates and releases</a></strong> - Release channels, verification and installer handoff</td></tr>
<tr><td>🛠️ <strong><a href="docs/BUILDING.md">Building</a></strong> - Build requirements and source validation</td></tr>
<tr><td>🧪 <strong><a href="docs/TESTING.md">Testing</a></strong> - Regression coverage, native checks and environment-specific evidence</td></tr>
<tr><td>🗺️ <strong><a href="docs/ROADMAP.md">Roadmap</a></strong> - Known limits and future work</td></tr>
<tr><td>⚖️ <strong><a href="docs/LICENSING.md">Licensing and provenance</a></strong> - Dependency, runtime and artwork licensing details</td></tr>
</table>

## 🤝 Contributing

Contributions are welcome once you have read [CONTRIBUTING.md](CONTRIBUTING.md) and the relevant subsystem documentation

A compact root [AGENTS.md](AGENTS.md) also records the architectural invariants that coding agents and contributors should preserve

## ⚖️ Licensing

First-party contributions are available under the [MIT License](LICENSE). For separate dependency terms, artwork provenance and exact retained notices, see [Licensing and provenance](docs/LICENSING.md) and [Third-party notices](THIRD-PARTY-NOTICES.md)
