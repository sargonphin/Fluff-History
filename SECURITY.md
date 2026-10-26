# Security policy

## Reporting a vulnerability

Please report suspected security vulnerabilities through the repository's
[private vulnerability reporting form](https://github.com/sargonphin/Fluff-History/security/advisories/new).
Use **Security → Report a vulnerability** rather than a public issue for sensitive
details. Non-sensitive bug reports can use ordinary GitHub issues.

Include the affected version or commit, Windows version, a description of the
impact, and safe reproduction steps using synthetic data. Include relevant error
messages after removing private paths, account details and other sensitive data.
Do not send passwords, tokens, private signing keys, repository master/recovery
secrets, Windows profile crypto material or personal backup contents.

Maintainers will review the report privately, confirm affected versions and
coordinate remediation and disclosure. No guaranteed response deadline is offered.
Please avoid publishing exploit details before that coordination.

## Security boundaries and limitations

Read [Security and recovery](https://github.com/sargonphin/Fluff-History/blob/main/docs/SECURITY-AND-RECOVERY.md) for encryption,
recovery, update trust and documented limitations. Experimental recovery behavior
and known limitations are identified there; a documented limitation may still
warrant a private report if new evidence shows a materially different impact.

Passing tests do not constitute an external security audit. Report unexpected
behavior that crosses the documented trust or data-protection boundaries.
