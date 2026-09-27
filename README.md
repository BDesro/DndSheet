# DndSheet

A Windows desktop app for playing and managing a Dungeons & Dragons 5th Edition character.

One character model drives three views:

| View | Purpose |
|---|---|
| **Character Sheet** | The familiar three-page 5e sheet layout (main, backstory, spells). Every box is live and editable; zoom with Ctrl+wheel; prints to letter pages. |
| **Details** | The same character organized by category (Identity, Abilities & Saves, Skills, Combat, Proficiencies, Features, Equipment, Spells, Resources, Conditions & Effects, Biography) for comfortable editing. |
| **Play** | A session dashboard: damage/heal/temp HP, death saves, short and long rests, resources, spell slots, casting, conditions, effects, quick-add, and a session log. |

Characters are stored locally in SQLite, autosaved, backed up daily, and can be exported/imported as portable `.dndchar` files. The app checks GitHub Releases for updates and installs them after verifying the package checksum.

## Requirements

- Windows 10/11 (x64)
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) to run a release package
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) to build from source

## Install (double-click to launch)

**From a release:** download `DndSheet-<version>-win-x64.zip` from the [Releases page](https://github.com/BDesro/DndSheet/releases), extract it, and double-click **`Install.cmd`**. The installer:

1. Checks for the .NET 10 Desktop Runtime. If it's missing, it asks before installing it (through winget, or by opening Microsoft's download page) and re-checks. If the runtime still isn't there, it stops without changing anything.
2. Copies the app to `%LOCALAPPDATA%\Programs\DndSheet` (no admin rights needed).
3. Creates Start Menu and desktop shortcuts, then offers to launch the app.

After that, the app updates itself from new releases. Your characters live in `%LOCALAPPDATA%\DndSheet`, so reinstalling never touches them. Installer options: `-NoDesktopShortcut`, `-NoLaunch`, `-InstallDir <path>`.

**From source:** this checks for the .NET 10 SDK (and offers to install it), builds the package, and runs the same installer:

```bash
powershell -ExecutionPolicy Bypass -File scripts/install.ps1
```

## Build, run, test

```bash
dotnet build DndSheet.slnx
```

```bash
dotnet run --project src/DndSheet.App
```

```bash
dotnet test DndSheet.slnx
```

Debug builds start in **development mode**: a diagnostics console window opens next to the app, and logging is verbose. See [Configuration](#configuration).

To keep test data away from your real characters:

```bash
dotnet run --project src/DndSheet.App -- --Application:DataDirectory=%TEMP%\dndsheet-dev
```

## Packaging a release

```bash
pwsh ./scripts/publish.ps1
```

This produces `artifacts/DndSheet-<version>-win-x64.zip` and `artifacts/SHA256SUMS.txt`. Use it to check a package locally; published releases are built by GitHub Actions.

### Releasing a new version

`main` accepts changes only through pull requests with passing CI, and release tags are immutable, so a release is a version-bump PR followed by a tag:

```bash
git checkout main
git pull
git checkout -b release/v1.2.0
```

Set `<Version>1.2.0</Version>` in `Directory.Build.props`, then:

```bash
git commit -am "Release 1.2.0"
git push -u origin release/v1.2.0
gh pr create --base main --title "Release 1.2.0" --body "Changes: ..."
```

When CI passes, merge the PR, then tag the merged commit on `main`:

```bash
gh pr merge --squash --delete-branch
git checkout main
git pull
git tag -a v1.2.0 -m "DndSheet 1.2.0"
git push origin v1.2.0
```

The **Release** workflow checks that the tag matches `<Version>`, runs the tests, builds the zip and checksum, and publishes the GitHub release. Watch it with `gh run watch`, or in the repository's Actions tab. Installed copies show an "Update 1.2.0 available" button within a startup or two.

- Use [semantic versions](https://semver.org): patch for fixes, minor for features, major for breaking changes. A tag containing `-` (e.g. `v1.3.0-beta.1`) is published as a pre-release, which only users with `AllowPreRelease` see.
- If a release is broken, don't move or delete the tag (the ruleset blocks it anyway). Fix forward with the next patch version.

To install: unzip the package into a folder you own, for example `%LOCALAPPDATA%\Programs\DndSheet`, and run `DndSheet.exe`. The app never needs administrator rights.

## Configuration

Settings are layered. Later sources override earlier ones, so no binary has to change:

1. `appsettings.json` next to the exe (shipped production defaults, `DevelopmentMode: false`)
2. `appsettings.Development.json` (present only in Debug build output)
3. `%LOCALAPPDATA%\DndSheet\appsettings.user.json` (per-user overrides; see `appsettings.user.example.json`)
4. Environment variables prefixed `DNDSHEET_`, e.g. `DNDSHEET_Application__DevelopmentMode=true`
5. Command line: `--dev`, or `--Section:Key=value`

| Key | Default | Meaning |
|---|---|---|
| `Application:DevelopmentMode` | `false` | Opens a diagnostics console and logs at Debug level. It only affects diagnostics; it unlocks no extra capabilities. |
| `Application:DataDirectory` | `%LOCALAPPDATA%\DndSheet` | Where the database, logs, backups and update staging live. |
| `Application:AutosaveDelaySeconds` | `2` | Idle time after an edit before autosave. |
| `Logging:FileMinimumLevel` | `Information` | Minimum level written to the log file (Debug in development mode). |
| `Logging:RetainDays` | `14` | Log file retention. |
| `Updates:Owner` / `Updates:Repository` | *(empty)* | The GitHub repository whose releases are the update source. Update checks are off until these are set. |
| `Updates:CheckOnStartup` | `true` | Silent check at startup; a status-bar button appears when an update exists. |
| `Updates:AllowPreRelease` | `false` | Offer `-beta` style releases. |

## Where things are stored

| What | Location |
|---|---|
| Characters | `%LOCALAPPDATA%\DndSheet\characters.db` (SQLite, WAL mode) |
| Daily backups (last 10) | `%LOCALAPPDATA%\DndSheet\backups\` |
| Logs | `%LOCALAPPDATA%\DndSheet\logs\app-YYYYMMDD.log`. In the app: Help → Open log folder |
| Updater log | `%LOCALAPPDATA%\DndSheet\logs\updater.log` |
| GitHub token (optional) | Windows Credential Manager → Windows Credentials → `DndSheet/GitHubToken` |

## Keyboard shortcuts

Ctrl+N new character · Ctrl+S save · Ctrl+P print sheet · Ctrl+1/2/3 Sheet/Details/Play · Ctrl+Plus/Minus/0 zoom · Ctrl+mouse wheel zooms the sheet.

## More documentation

- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md): stack choice, layers, domain model, persistence, updates, security, testing, limitations and extension points
- [NOTICE.md](NOTICE.md): intellectual-property and licensing notes

## License

The code is under the [MIT License](LICENSE). The bundled SRD 5.1 game facts are separately licensed under CC-BY-4.0 (see [NOTICE.md](NOTICE.md)). *Dungeons & Dragons* is a trademark of Wizards of the Coast; this project is unofficial.
