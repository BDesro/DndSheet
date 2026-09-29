# Security policy

Please report vulnerabilities privately through GitHub's **Report a vulnerability** button on the repository's Security tab, not in a public issue.

Particularly relevant areas: the update pipeline (`src/Hearthsheet.Infrastructure/Updates`, `src/Hearthsheet.Updater`), character file import (`src/Hearthsheet.Core/Serialization`), and credential storage (`src/Hearthsheet.Infrastructure/Security`). See `docs/ARCHITECTURE.md` §9–11 for the security design.

Only the latest release receives fixes.
