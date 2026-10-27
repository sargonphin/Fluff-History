# Contributing

Use GitHub issues and pull requests for non-sensitive discussion and proposed
changes. Read [licensing](docs/LICENSING.md) before contributing or redistributing
material. This guide does not establish a separate contribution license, CLA or
vulnerability contact. Do not post secrets or private source data.

Read [architecture](docs/ARCHITECTURE.md) and the relevant current topic contract,
then [building](docs/BUILDING.md) and [testing](docs/TESTING.md). Current code and topic
documents describe the supported behavior. Exact upstream notices must remain intact.

Keep changes focused and explain the trigger, resulting behavior and validation.
Separate documentation-only work from runtime fixes when practical. Backup/history,
publication uncertainty, Current protection, path identity and recovery boundaries
are product contracts; a formatting or performance change must not silently alter
them. Never treat an access/network failure as proof of deletion or nonpublication.

Use locked dependencies. For runtime changes, run the affected regression suites
and ordinary Release checks. Native Windows/COM, installed lifecycle, live account,
SMB and large-scale changes need their relevant environment-specific evidence;
portable tests cannot substitute for it. Documentation-only changes need link,
reference and scope checks. Preserve evidence limits rather than claiming a skipped
campaign passed.

Use disposable storage/accounts for opt-in diagnostics. Keep bulk fixtures and
build output outside source/release archives; retain only useful sanitized results.
Never commit credentials, tokens, signing private keys, profile crypto material or
personal backup content. Dependency/license updates require review of exact
artifacts, transitive graph, notices and applicable guard data.
