# Architecture

## 1. Technology stack

**.NET 10 + WPF**, with SQLite (Microsoft.Data.Sqlite) and System.Text.Json.

| Need | Why WPF fits |
|---|---|
| Recreating a dense, paper-like sheet that is also editable | Precise layout (Grid, rounded Borders, styles) with real TextBoxes and CheckBoxes in place |
| Zoom | `LayoutTransform` scales the vector-rendered sheet with no blur and no re-layout code |
| Printing | The same page visuals go to the printer through `XpsDocumentWriter`, so there's no second print layout |
| Data binding | Two-way bindings plus `INotifyPropertyChanged` are what keeps three views in sync with one model |
| High DPI, accessibility | Per-monitor-v2 DPI via `app.manifest`; UI Automation peers for screen readers and keyboard navigation |
| Local database, packaging, updating | Ordinary .NET: SQLite in-process, xcopy-able folder, a small updater exe |

Alternatives considered:

- **WinUI 3.** Its printing and zoom story is weaker and its packaging heavier, and it would add no capability this app needs.
- **Avalonia.** Cross-platform, which isn't a requirement, and a larger dependency.
- **Electron or web UI.** Heavy runtime, weaker native printing and DPI handling.
- **WinForms.** Poor data binding and scaling.

MAUI has no real Windows advantage over WPF.

**Dependencies are kept deliberately small.** The only packages are Microsoft.Data.Sqlite, Microsoft.Extensions.Logging and Microsoft.Extensions.Configuration, plus xUnit for tests. There is no MVVM toolkit (a 40-line `RelayCommand` and the domain's `Observable` base cover it), no DI container (one composition root in `App.xaml.cs`), and no logging framework (two sinks behind the standard `ILogger`). Cloud sync talks to Supabase with plain `HttpClient`; it adds no packages.

## 2. Projects and layers

```text
src/
  Hearthsheet.Core            Domain + application layer. No UI, no I/O besides file import/export.
    Domain/                Character aggregate and its parts; gameplay operations
    Rules/                 Derived values (CharacterRules) and rest steps (RestService)
    Application/           CharacterSession (change tracking), CharacterLibrary (use cases), ICharacterRepository
    Serialization/         JSON format, schema migrations, portable .dndchar files
    Content/               SRD 5.1 facts: class table, spell-slot progressions, CharacterFactory
    Sync/                  Cloud sync algorithm and cloud contracts
  Hearthsheet.Infrastructure  Windows / I/O concerns
    Persistence/           SqliteCharacterRepository
    Logging/               ILoggerProvider with console + rolling-file sinks, secret redaction
    Security/              Windows Credential Manager secret store
    Updates/               SemVersion, GitHub release source, UpdateService
    Sync/                  Supabase auth, character store, HTTP client
    AppPaths.cs            Per-user data locations, daily database backup
  Hearthsheet.Updater         Tiny console exe that swaps the install folder after the app exits
  Hearthsheet.App             WPF presentation: views, view models, dialogs, composition root
tests/Hearthsheet.Tests       xUnit: domain, rests, persistence, migration, import/export, updates, installer
```

Dependencies point inward: App → Infrastructure → Core. Core has no reference to WPF, SQLite or the network, so all rules are unit-testable.

## 3. Domain model: one source of truth

```text
Character (aggregate root, Guid Id)
├── Identity         name, player, class, subclass, level, background, species, alignment, XP, inspiration
├── Biography        age … hair, personality/ideals/bonds/flaws, appearance, backstory, allies, treasure, notes
├── AbilityScores    six base scores
├── SavingThrows[6]  proficient + extra bonus
├── Skills[18]       None / Half / Proficient / Expertise + extra bonus
├── Combat           base AC, initiative bonus, base speed, base max HP, current/temp HP, hit die, hit dice left, death saves, stable
├── Attacks[]        name, ability (or none), proficient, bonus, damage text
├── Proficiencies[]  category (armor/weapon/tool/language/other) + name
├── Features[]       name, source (class/subclass/species/background/feat/trait/custom), description
├── Items[]          qty, weight, equipped, attunement, description, Modifiers[]
├── Currency         cp/sp/ep/gp/pp
├── Spellcasting     ability, class, concentration, Slots[1..9] (max/expended), Spells[]
├── Resources[]      current/max, recovers on (manual/short/long), optional partial amount
├── Conditions[]     the 14 standard conditions
├── ExhaustionLevel
└── Effects[]        name, duration text, expiry (manual/any rest/long rest), Modifiers[]
```

**Stored vs derived.** Only authoritative values are stored. Everything else is a pure function in `Rules/CharacterRules.cs` and is never persisted, so it can't go stale: ability modifiers, proficiency bonus, save and skill totals, effective AC/speed/max HP, initiative, passive Perception, spell DC and attack, attack bonuses and carried weight.

**Modifiers** are the single mechanism for bonuses, penalties, temporary buffs and overrides. A `Modifier` has a `Target` key (`ac`, `save.all`, `skill.stealth`, `ability.str`, …; see `StatTargets`), a `Kind` and a `Value`:

- `Bonus` adds to the value.
- `Set` replaces the base value; the highest `Set` wins, then bonuses apply.

Modifiers are active on effects, and on items that are equipped (and attuned, if the item requires attunement). This covers Bless-style bonuses, a Ring of Protection, Gauntlets of Ogre Power and ad-hoc DM rulings, without a general rules engine. Per-skill and per-save `Bonus` fields cover one-off permanent adjustments.

**Behaviour lives on the aggregate** (`Character.Play.cs`): `TakeDamage` (temp HP first, massive-damage death, failures at 0 HP), `Heal`, `SetHitPoints`, `GrantTemporaryHitPoints` (non-stacking), `RecordDeathSave`, `SpendHitDie`, `UseResource`, `ExpendSpellSlot`, `CastSpell` (lowest available slot, concentration swap, free cantrips and rituals), and conditions. These keep invariants together, for example dropping to 0 HP adds *Unconscious* and healing removes it. Property setters clamp to legal ranges, which also sanitizes imported data.

## 4. How the three views stay in sync

```text
 Details edit / Sheet edit / Play button
            │ (two-way binding or command → domain method)
            ▼
   Character model (Observable objects + ObservableCollections)
            │ PropertyChanged / CollectionChanged
            ▼
   ChangeTracker ─► CharacterSession.Changed ─┬─► CharacterViewModel.Refresh()  (derived values → all views)
                                              └─► MainViewModel autosave timer  (dirty flag, save after 2 s idle)
```

- The domain classes themselves raise change notifications, so every view binds straight to the same objects. No view has its own copy of character state.
- `ChangeTracker` walks the object graph by reflection, subscribing to nested objects and to items as they are added or removed. The domain doesn't need parent pointers.
- `CharacterViewModel` is shared by all three views. It adds only derived values and thin "row" view models (for example `SkillRow` = the stored `SkillEntry` + its computed total). Refreshes are coalesced per dispatcher pass, so a long rest that touches dozens of values refreshes once.
- Dashboard actions are `PlayViewModel` commands that call domain methods or `RestService`, then log the result. They never manipulate controls.

## 5. Rests

`RestService` runs an ordered list of `IRestStep`s. The defaults (SRD 5.1 behaviour) are:

| Step | Short rest | Long rest |
|---|---|---|
| `LongRestHitPoints` | — | HP to max, temp HP cleared, death saves reset |
| `LongRestHitDice` | — | regain half total (min 1) |
| `SpellSlotRecovery` | — | all slots |
| `ResourceRecovery` | short-rest resources | short- and long-rest resources; partial `RecoveryAmount` honoured; manual resources reported under *Needs attention* |
| `ExhaustionRecovery` | — | −1 level |
| `EffectExpiration` | effects that end on any rest | + long-rest effects and concentration |

Guards: the dead can't rest, and a long rest at 0 HP gives no benefit. Hit-die spending is interactive (in the short rest dialog, using a physical roll or an in-app roll) and goes through `Character.SpendHitDie` before the rest steps run.

To change rest rules (another edition, a variant or a homebrew rule), replace or add steps where `RestService.CreateDefault()` is composed. The UI doesn't change.

## 6. Persistence

- **SQLite, one row per character**, with the character stored as a JSON document plus denormalized `name` and `description` columns for the list. Documents fit this model: the character is always loaded and saved whole, and it's the same shape as the export file, so a single migration path serves both.
- **Crash safety.** WAL journaling plus upsert-in-one-statement makes every save atomic, so an interrupted write leaves the previous version intact. Export files are written to a temp file and then atomically renamed.
- **Backups.** One `SQLite backup API` copy per day at startup; the last 10 are kept.
- **Autosave** runs 2 s after the last change, and on switching characters, closing the app and before installing an update. Ctrl+S saves immediately.
- **Versioning.** The `meta.db_version` row covers table-level changes. Each row's `schema_version` covers the character document. A corrupt row fails only that character's load (with a message pointing at backups); the list and the other characters keep working.
- **Swapping the store.** `ICharacterRepository` (List/Load/Save/Delete) is the only persistence contract Core knows.

## 7. Versioning and migration

- App version: `<Version>` in `Directory.Build.props`, shown in the status bar and used by the updater.
- Character schema version: `CharacterJson.CurrentSchemaVersion` (currently **1**).

To change the model: bump the schema version, then add an `ICharacterMigration` with `FromVersion = n` that rewrites the raw `JsonObject` from shape *n* to *n+1*, and register it in `CharacterMigrator`. Migrations run on JSON before typed deserialization, so old shapes never need C# types. Files or rows from a *newer* schema are refused with an "update the application" message rather than being half-read. Tests exercise multi-step migration and missing-step failure with synthetic migrations.

## 8. Import / export

A `.dndchar` file is UTF-8 JSON:

```json
{ "format": "dndsheet.character", "schemaVersion": 1, "appVersion": "1.0.0",
  "exportedUtc": "…", "character": { … } }
```

Import treats the file as untrusted:

- 10 MB size cap and JSON max depth 32.
- The format marker must be present.
- Newer schemas are rejected; older ones are migrated.
- Typed deserialization only (no polymorphic type names, so the file cannot choose which types get created).
- Setters clamp every numeric field, and `Normalize()` repairs structure (exactly 6 saves, 18 skills, 9 slot levels, unique conditions).
- Per-list entry caps.
- The imported character always gets a **new id**, so importing can never overwrite an existing character.

## 9. Updates

```text
git tag vX.Y.Z → Release workflow (tests, scripts/publish.ps1, gh release create with GITHUB_TOKEN)
   → GitHub Release: Hearthsheet-X.Y.Z-win-x64.zip + SHA256SUMS.txt
   → App: UpdateService.CheckAsync   (GET api.github.com/repos/{owner}/{repo}/releases; newest non-draft, stable unless pre-release allowed)
   → user confirms; character saved
   → DownloadAndStageAsync           (download via asset API; size caps; SHA-256 must match SHA256SUMS; zip extracted with zip-slip protection;
                                      staged Hearthsheet.dll ProductVersion must equal the release version)
   → LaunchInstaller                 (copies Hearthsheet.Updater.exe out of the install folder, starts it, app exits)
   → Hearthsheet.Updater                (waits for the app's PID → backs up install folder → replaces it → on any failure restores the backup → restarts the app)
```

- No `git pull` and no developer credentials in the shipped app. For a **public** repository the update path needs no token.
- **Private repository (development).** Help → GitHub token stores a fine-grained PAT (single repository, read-only *Contents*) in **Windows Credential Manager**. It is write-only in the UI, attached only to requests to `api.github.com`, and never forwarded on redirects: redirects are followed manually and the header is dropped for the asset CDN. Only HTTPS GitHub hosts are contacted. The token is never logged; a redaction filter also scrubs token-shaped strings from every log line as a backstop.
- User data lives outside the install folder, so replacing the folder wholesale is safe.
- **Limitation.** The checksum file comes from the same release, so it protects against corruption and truncation, not against a compromised GitHub account. The next step is signing packages (Authenticode, or a minisign/ECDSA signature checked against a public key embedded in the app). `VerifyStagedPackage` is where that check belongs.

## 10. Logging, diagnostics and configuration

- The `ILogger` abstraction feeds `AppLoggerProvider`, which writes to a rolling daily file (always) and a colored console (development mode only). Line format: `[HH:mm:ss.fff] [LEVEL] [Category] message`.
- Logged events include app lifecycle, config/data paths, database init and backup, character load/save/create/delete/import/export, every gameplay action, view opens, update checks/downloads/verification, and all errors with stack traces.
- **Development mode vs build configuration** are independent. Debug builds include `appsettings.Development.json` (`DevelopmentMode: true`), and release packages exclude it (the publish script fails if it's present). A release install can still enable diagnostics per user (`appsettings.user.json`, `HEARTHSHEET_Application__DevelopmentMode=true` or `--dev`) without a different binary. Development mode only changes logging; it grants nothing else.
- **Crash handling.** UI-thread exceptions are logged and shown as a friendly message while the app keeps running; the model stays consistent because operations are small and data is autosaved. Unobserved task and AppDomain exceptions are logged. Startup failures show where the log is.

## 11. Security summary

| Concern | Handling |
|---|---|
| GitHub credentials | Optional; Credential Manager (DPAPI-protected, per user); least-privilege fine-grained PAT; sent only to api.github.com over HTTPS; never logged or displayed; not needed for public releases |
| Update packages | HTTPS + GitHub host allowlist, size caps, SHA-256 verification, zip-slip safe extraction, version check of the staged binaries, backup and rollback installer. The updater takes only a bare `.exe` file name to restart. |
| Imported files | Size and depth limits, format check, typed deserialization only, range clamping, structural repair, entry caps, fresh id |
| Database | Parameterized SQL only; per-user data folder |
| Logging | No object dumps of secrets; regex redaction backstop |
| Privilege | `asInvoker` manifest; per-user install; no admin rights ever needed |
| Cloud account | Optional; Supabase Auth over HTTPS to one configured host; refresh session in Credential Manager only with "Stay signed in"; passwords never stored; tokens never logged (JWT redaction) |
| Cloud data | Row-level security per user; the public anon/publishable key only; pulled rows go through the import validation pipeline |

## 12. UI notes

- **Character sheet strategy.** It is a structured WPF layout at exact letter-page size (816×1056 DIP). It is not a PDF background with overlaid fields. It stays editable, crisp at any zoom, accessible, bound to real data, and prints 1:1. Hover-only add/remove buttons and borderless inputs keep printouts clean.
- **Accessibility.** Every input has an `AutomationProperties.Name`. Tab templates expose their content to UI Automation (`PART_SelectedContentHost`). There are focus rings, keyboard shortcuts, and no colour-only state (toggles also change fill and border).
- **Confirmation.** Deleting a character, a long rest, applying class defaults and removing a named entry all ask first. Blank placeholder entries are removed without asking.

## 13. Testing

`dotnet test` runs 183 tests:

- **Domain rules:** modifiers, proficiency, skills including half proficiency and expertise, saves, modifier stacking and Set, item attunement, clamping.
- **Gameplay:** damage, temporary HP, massive damage, death saves in every combination, healing from 0, hit dice, resources, spell casting and upcasting, concentration.
- **Rests:** short vs long, partial and manual recovery, 0-HP rule, effect expiry, replaceable steps, class defaults.
- **Persistence:** CRUD, reopen, ordering, migration of stored rows, a corrupt row isolated, backup restore, duplicate, library import and export.
- **Import/export:** round-trip, nine kinds of invalid or corrupt file, newer schema, deep nesting, clamping and repair.
- **Updates:** SemVer precedence, release selection (drafts and pre-releases), unconfigured source, HTTP errors, token scoping across redirects, checksum mismatch and missing entry, wrong-version package, corrupt zip, host allowlist, checksum parsing.
- **Installer:** replace, rollback on mid-copy failure, refusing an invalid staging folder, argument validation.
- **Logging:** secret redaction.
- **Cloud sync:** local bookkeeping and the db upgrade; push/pull, conflicts, deletes vs edits, deferred changes to the open character, stale machines, account relinking and unreadable rows against an in-memory cloud; Supabase auth (remember me, token rotation, revoked and offline sessions, password reset) and store requests against a stub HTTP handler.

The UI was verified by driving the running app through Windows UI Automation. The installer was verified end to end by upgrading a published 1.0.0 package to 1.0.1 with the real `Hearthsheet.Updater.exe`.

## 14. Known limitations

- Single class per character: level, hit die and hit dice are single values. Multiclassing needs per-class entries and hit-dice pools, which would be a schema-2 migration.
- Conditions and exhaustion are tracked but apply no automatic mechanics (advantage and disadvantage are not modelled), because the rules differ between 2014 and 2024 editions.
- Attack damage is free text; there are no dice roller or damage-type mechanics.
- Half-caster slot progression follows SRD 5.1 (no slots at level 1). "Apply class defaults" can be ignored for 2024-style rules.
- No bundled spell, feature or item content (see NOTICE.md).
- A very long spell or feature list grows its sheet page beyond one printed page and is clipped when printing.
- The update checksum isn't a signature (see §9). Update checks are off until `Updates:Owner/Repository` are configured.
- A release package requires the .NET 10 Desktop Runtime (framework-dependent).
- No undo/redo; autosave plus daily backups are the safety net.
- Cloud sync runs at startup, every few minutes and on close, not after each save; there are no live updates between devices.
- A free Supabase project pauses after about a week idle (a daily GitHub Actions request prevents it); while paused, sync reports "Cloud unavailable" and everything else works.
- A character deleted elsewhere more than 90 days ago can come back from a machine that was offline the whole time (tombstones are purged after 90 days).

## 15. Extension points

| To add… | Touch |
|---|---|
| A new stored field | Domain class property (clamped setter) → views bind to it → if the shape changes incompatibly, bump the schema and add a migration |
| A new derived value | A function in `CharacterRules`, and a property on `CharacterViewModel` |
| A new modifier target | A constant in `StatTargets`, then pass it to `Resolve` where the value is computed |
| A rest variant | An `IRestStep`, composed in `RestService.CreateDefault()` |
| Another view | A UserControl bound to `CharacterViewModel`; it gets sync and autosave for free |
| A different store | Implement `ICharacterRepository` and compose it in `App.xaml.cs` |
| Bundled content | A data file loaded by `Content/`, respecting NOTICE.md |
| Signed updates | Verify the signature in `UpdateService.VerifyStagedPackage` / `DownloadAndStageAsync` |

## 16. Cloud sync (optional)

Local SQLite stays the source of truth; sync is opt-in by signing in (setup: `docs/CLOUD_SYNC.md`, design:
`docs/superpowers/specs/2026-09-29-cloud-sync-design.md`).

- **Local bookkeeping** (db version 2): `dirty`, `local_revision` (bumped on each save) and `cloud_revision` per row,
  a `pending_deletes` table, and `sync_account` / `sync_last_pull` in `meta`.
- **A pass** (`CloudSync`, one at a time): push tombstones for pending deletes → push dirty rows with a
  revision-conditional update → pull rows changed since the last pull (minus a 2-minute overlap). The pass never writes
  the open character; its remote changes are returned as deferred changes and applied on the UI thread.
- **Conflicts:** the cloud version wins, and the local one is saved as "Name (conflict copy, date)". An edit beats a delete.
- **Triggers:** startup, sign-in, every `Sync:IntervalMinutes` (default 5), "Sync now", and on close (5-second cap).
- **Server** (`supabase/migrations/0001_cloud_sync.sql`): one `characters` table, a trigger that owns `revision`/`updated_at`,
  per-user RLS, and a daily `pg_cron` purge of tombstones older than 90 days.
