# Configuration

Configuration manages backup location, protected folders, exclusions, automatic
backups and safeguards. Application Settings separately manages appearance,
Timeline view, language and updates. Edits remain a draft until Save changes;
Discard restores the saved configuration. Page navigation preserves the draft.

## Backup location and first setup

Choose an empty folder or an existing recognized repository. Empty-folder setup
requires confirmation; unknown/nonempty data is not repurposed. A narrow local
volume-root exception ignores only recognized OS metadata during emptiness checks,
without traversing or changing it. Ordinary folders, UNC roots and uncertain roots
retain conservative admission. Source/repository overlap and unsafe aliases are
rejected.

First setup suggests the signed-in user's profile as an editable source draft,
using the same safety and overlap checks as any selected folder. Existing
configurations keep their explicit list, including an empty one. Adopting history
uses explicit root mappings rather than matching folder names.

The first valid ready configuration Save requests an initial backup, even with
automatic scheduling off. This is transient setup activation, not a durable
"first backup" marker. Restart, later saves or recovery do not force an immediate
attempt. Clearing repository files outside the configuration workflow does not
reset that activation state; use explicit reset/reconfiguration.

The location card displays encryption state and compact password/encryption controls.
Encryption is inspected from the repository, not inferred from configuration.
Conversion Pause or window closure leaves a resumable operation; do not manually
remove migration controls. Password recovery, built-in local DPAPI and explicit
Experimental OneDrive actions are explained in [security and recovery](SECURITY-AND-RECOVERY.md).

## Folders and exclusions

Source roots have stable logical UUIDs and explicit runtime paths. Removing a folder
from backup preserves its history identity; selecting the same safe folder can
reactivate it. Known child identities retain parent traversal barriers even while
inactive, preventing silent reassignment to the parent's history.

The Excluded folders list is independent of selected sources. A normalized absolute
choice outside every source stays inert and becomes effective if a broader source
is later added. A choice covering an entire explicitly selected source is rejected
with a modal notice in either edit order. Backing up a profile while excluding its
Downloads child is allowed; explicitly backing up Downloads while excluding it is not.

Choices project into the existing canonical relative source exclusions. There are
at most 256 choices and 256 KiB of paths, with the same effective per-source exclusion
limits. Covered child choices are redundant. Invalid/oversized legacy choices remain
visible for correction rather than silently truncated. Unmapped historical roots
retain their identity. There are no file, wildcard, extension or regular-expression
filters.

Explicit exclusion changes end covered Current membership during the next scan's
configuration transition, without removing retained versions. Removing an exclusion
can reactivate matching newest history without a new payload. A directory exclusion
covers descendants, not a future regular file at exactly the same logical name.

Automatic Windows safety policy remains separate from user choices. It uses resolved
known locations and validated aliases, with hard blocks for product/system metadata
and confirmation for discouraged AppData/system/program/temporary locations. Native
metadata policy handles known compatibility junctions, closed Shell metadata names,
managed/System shortcuts and specific OneDrive-managed markers. Ordinary user-created
shortcuts and dotfiles remain eligible. Reparse/EFS/device boundaries are not traversed.
Unknown failures protect prior membership rather than proving deletion.

## Schedule and safeguards

Automatic backups default on, every hour. Intervals are 10, 30, 60, 180, 360, 720
and 1440 minutes. The Agent runs at future anchored UTC cadence while the user
session is signed in; closing management UI does not stop it. Busy/missed intervals
are discarded, not queued. Manual backup uses the same one-active admission.

Known battery defers automatic starts unless allowed. Manual backup bypasses that
battery preference but retains explicit unknown-power and network safeguards.
Network-dependent work can defer for reported offline/metered/costed networks;
local-only work ignores network cost. Unknown hints are not proof of reachability
or deletion. State changes do not cancel an already-admitted backup.

Retention defaults to six months. Forever and Until space saturation have distinct
notices; only the latter authorizes pressure deletion of noncurrent history.
Manual Clean Timeline is independent and lives in Timeline. See [retention](RETENTION.md).

## Application state and status

The Agent alone writes bounded strict `settings-v2.json`; UI-only choices use
`preferences-v1.json`. Both live under `%LOCALAPPDATA%\Fluff-History\AppData`, outside
versioned binaries and package LocalState. Missing preferences use defaults;
corrupt backup settings require explicit reset without deleting the repository.
Passwords, tokens, cursors and backup-run history are not stored in these settings.

Overview shows active transient progress and one optional last-backup display cache.
That separately overwritten file binds repository UUID/location and is ignored if
missing, invalid or mismatched. It never seeds scheduling or becomes durable history.
Drive usage is a bounded observation; only the cheap drive query repeats while an
active backup is visible. Backup size is an event/page-entry estimate from directory
metadata, with entry/time bounds and no payload reads. Neither figure authorizes
capacity deletion.
