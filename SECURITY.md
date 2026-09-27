# Security policy

Please report vulnerabilities privately through GitHub's **Report a vulnerability** button on the repository's Security tab, not in a public issue.

Particularly relevant areas: the update pipeline (`src/DndSheet.Infrastructure/Updates`, `src/DndSheet.Updater`), character file import (`src/DndSheet.Core/Serialization`), and credential storage (`src/DndSheet.Infrastructure/Security`). See `docs/ARCHITECTURE.md` §9–11 for the security design.

Only the latest release receives fixes.
