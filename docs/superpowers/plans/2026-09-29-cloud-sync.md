# Cloud Sync Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Optional Supabase-backed accounts that sync characters between Hearthsheet installations, with "Stay signed in", while the app stays fully local-first.

**Architecture:** SQLite stays the source of truth and gains sync bookkeeping (dirty flag, local/cloud revisions, pending deletes). A pure `CloudSync` algorithm in Core pushes deletes and edits, then pulls remote changes, resolving conflicts with "cloud wins, local kept as conflict copy". It talks to the cloud through small interfaces implemented with plain `HttpClient` against Supabase Auth and PostgREST in Infrastructure. The WPF layer adds an account dialog, an Account menu, a status-bar indicator and the sync triggers: startup, every N minutes, manual, and on close.

**Tech Stack:** .NET 10, WPF, Microsoft.Data.Sqlite, System.Text.Json, xUnit, Supabase (Auth + Postgres + pg_cron), GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-09-29-cloud-sync-design.md`. Read it before starting any task.

## Global Constraints

- **No new NuGet packages** in any project (the Supabase SDK is explicitly rejected; plain `HttpClient` only).
- Local-first: no sync failure may block saving, loading, printing, exporting or closing.
- Sync triggers are startup, sign-in, every `Sync:IntervalMinutes` (default 5), manual "Sync now", and close (5-second cap). **Never after each save.**
- Conflict rule: the cloud version wins, and the local version is saved as `"<Name> (conflict copy, yyyy-MM-dd)"`.
- Only HTTPS to the configured Supabase host. Tokens and passwords are never logged.
- The refresh session is stored in Windows Credential Manager under `Hearthsheet/supabase-session`, only when "Stay signed in" is checked (the default).
- Tombstones are purged after 90 days. The stale-machine threshold is 80 days. The pull overlap is 2 minutes.
- The size cap is 10 MB per character document (`PortableCharacterFile.MaxFileBytes`).
- Code style follows the surrounding code: file-scoped namespaces, primary constructors where the file already uses them, XML doc summaries on public types, `LogInformation` templates, and parameterized SQL only.
- Commit messages are imperative sentence case (e.g. "Add sync bookkeeping to the local database"), ending with the `Co-Authored-By` trailer the session specifies.

## File map

| File | Status | Responsibility |
|---|---|---|
| `src/Hearthsheet.Core/Sync/SyncContracts.cs` | create | Local sync records and `ISyncLocalStore` |
| `src/Hearthsheet.Core/Sync/CloudContracts.cs` | create | `CloudRow`, `CloudUpload`, `ICloudCharacterStore`, `ICloudSession`, cloud exceptions |
| `src/Hearthsheet.Core/Sync/CloudSync.cs` | create | The sync pass, deferred changes, relinking |
| `src/Hearthsheet.Core/Serialization/PortableCharacterFile.cs` | modify | `EnforceLimits` becomes `internal` for reuse |
| `src/Hearthsheet.Infrastructure/Persistence/SqliteCharacterRepository.cs` | modify | db v2 upgrade, dirty tracking, `ISyncLocalStore` |
| `src/Hearthsheet.Infrastructure/Logging/AppLogging.cs` | modify | JWT and quoted-token redaction |
| `src/Hearthsheet.Infrastructure/Sync/SupabaseHttp.cs` | create | `SupabaseOptions`, `SupabaseResponse`, the shared HTTP client and error mapping |
| `src/Hearthsheet.Infrastructure/Sync/SupabaseAuth.cs` | create | Sign in/up/out, reset, token refresh, "Stay signed in" |
| `src/Hearthsheet.Infrastructure/Sync/SupabaseCharacterStore.cs` | create | PostgREST calls for the `characters` table |
| `src/Hearthsheet.App/Views/AccountDialog.xaml(.cs)` | create | The sign-in / create / forgot-password dialog |
| `src/Hearthsheet.App/Dialogs.cs` | modify | `SignIn`, `AccountSwitch` |
| `src/Hearthsheet.App/ViewModels/SyncViewModel.cs` | create | Status, commands, timer, running passes |
| `src/Hearthsheet.App/ViewModels/MainViewModel.cs` | modify | Owns `Sync`, applies sync results, closes characters for a relink |
| `src/Hearthsheet.App/MainWindow.xaml(.cs)` | modify | Account menu, status indicator, final sync on close |
| `src/Hearthsheet.App/Composition/AppSettings.cs`, `App.xaml.cs`, `appsettings.json` | modify | `Sync:` settings and wiring |
| `supabase/migrations/0001_cloud_sync.sql` | create | Table, trigger, RLS, purge job |
| `.github/workflows/supabase-keepalive.yml` | create | The daily keep-alive request |
| `docs/CLOUD_SYNC.md`, `docs/ARCHITECTURE.md` | create / modify | Setup checklist, manual tests, architecture |
| `tests/Hearthsheet.Tests/SyncStoreTests.cs` | create | Repository sync bookkeeping |
| `tests/Hearthsheet.Tests/CloudSyncTests.cs` | create | The sync algorithm with an in-memory cloud |
| `tests/Hearthsheet.Tests/SupabaseTests.cs` | create | Auth and store HTTP behaviour |
| `tests/Hearthsheet.Tests/UpdateTests.cs` | modify | Redaction cases (in `RedactionTests`) |

Baseline: `dotnet test Hearthsheet.slnx` passes 116 tests on `feature/cloud-sync` before Task 1.

---

### Task 1: Sync bookkeeping in the local database

**Files:**
- Create: `src/Hearthsheet.Core/Sync/SyncContracts.cs`
- Modify: `src/Hearthsheet.Infrastructure/Persistence/SqliteCharacterRepository.cs`
- Test: `tests/Hearthsheet.Tests/SyncStoreTests.cs`

**Interfaces:**
- Consumes: the existing `ICharacterRepository`, `CharacterJson`, and `SqliteCharacterRepository`'s private `Open()`, `DisplayName()` and `Describe()`.
- Produces (namespace `Hearthsheet.Core.Sync`):
  - `record LocalChange(Guid Id, long LocalRevision, long? CloudRevision, string Name, string Description, int SchemaVersion, string Data)`
  - `record LocalState(bool Dirty, long LocalRevision)`
  - `record PendingDelete(Guid Id, long CloudRevision)`
  - `record SyncMeta(string? Account, DateTimeOffset? LastPull)`
  - `interface ISyncLocalStore`, with the exact members in Step 3, implemented by `SqliteCharacterRepository`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Hearthsheet.Tests/SyncStoreTests.cs`:

```csharp
using Hearthsheet.Core.Serialization;
using Hearthsheet.Core.Sync;
using Hearthsheet.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hearthsheet.Tests;

public class SyncStoreTests : IDisposable
{
    private readonly TempDir _dir = new();
    private SqliteCharacterRepository NewRepository() =>
        new(_dir.File("characters.db"), CharacterMigrator.Default, NullLogger<SqliteCharacterRepository>.Instance);

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void UpgradeFromVersion1_KeepsRowsAndMarksThemUnsynced()
    {
        var c = Samples.Rich();
        using (var connection = new SqliteConnection($"Data Source={_dir.File("characters.db")};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO meta VALUES ('db_version', '1');
                CREATE TABLE characters (id TEXT PRIMARY KEY, name TEXT NOT NULL, description TEXT NOT NULL,
                    schema_version INTEGER NOT NULL, data TEXT NOT NULL, created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL);
                INSERT INTO characters VALUES ($id, 'Arannis', '', 1, $data, '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z');
                """;
            command.Parameters.AddWithValue("$id", c.Id.ToString());
            command.Parameters.AddWithValue("$data", CharacterJson.Serialize(c));
            command.ExecuteNonQuery();
        }

        var repo = NewRepository();
        Assert.Equal("Arannis", repo.Load(c.Id)!.Identity.Name);
        Assert.Null(Assert.Single(repo.GetDirty()).CloudRevision);
        Assert.Single(NewRepository().GetDirty()); // reopening at version 2 changes nothing
    }

    [Fact]
    public void Save_MarksDirtyAndCountsLocalRevisions()
    {
        var repo = NewRepository();
        var c = Samples.Rich();
        repo.Save(c);
        repo.Save(c);
        Assert.Equal(2, Assert.Single(repo.GetDirty()).LocalRevision);
        Assert.Equal(new LocalState(true, 2), repo.GetState(c.Id));
        Assert.Null(repo.GetState(Guid.NewGuid()));
    }

    [Fact]
    public void MarkPushed_ClearsDirtyOnlyIfNothingWasSavedMeanwhile()
    {
        var repo = NewRepository();
        var c = Samples.Rich();
        repo.Save(c);
        var pushed = Assert.Single(repo.GetDirty());
        repo.Save(c); // saved while the push was in flight

        repo.MarkPushed(c.Id, pushed.LocalRevision, cloudRevision: 1);
        var still = Assert.Single(repo.GetDirty());
        Assert.Equal(1, still.CloudRevision);

        repo.MarkPushed(c.Id, still.LocalRevision, cloudRevision: 2);
        Assert.Empty(repo.GetDirty());
    }

    [Fact]
    public void ApplyRemote_RespectsDirtyRows_UnlessGuardedOrForced()
    {
        var repo = NewRepository();
        var c = Samples.Rich();
        repo.Save(c);
        var remote = CharacterJson.Clone(c, CharacterMigrator.Default);
        remote.Identity.Name = "Remote";

        Assert.False(repo.ApplyRemote(remote, 5));
        Assert.False(repo.ApplyRemote(remote, 5, ifLocalRevision: 99));
        Assert.True(repo.ApplyRemote(remote, 5, ifLocalRevision: 1));
        Assert.Equal("Remote", repo.Load(c.Id)!.Identity.Name);
        Assert.Empty(repo.GetDirty());

        repo.Save(c);
        Assert.True(repo.ApplyRemote(remote, 6, force: true));
        Assert.Equal(6, repo.GetCloudRevisions()[c.Id]);
    }

    [Fact]
    public void ApplyRemote_InsertsNewRows_ButNotOnesDeletedLocally()
    {
        var repo = NewRepository();
        var c = Samples.Rich();
        Assert.True(repo.ApplyRemote(c, 1));
        Assert.Empty(repo.GetDirty());

        Assert.True(repo.Delete(c.Id));
        Assert.Equal(new PendingDelete(c.Id, 1), Assert.Single(repo.GetPendingDeletes()));
        Assert.False(repo.ApplyRemote(c, 2));
        Assert.Null(repo.Load(c.Id));

        repo.ClearPendingDelete(c.Id);
        Assert.Empty(repo.GetPendingDeletes());
    }

    [Fact]
    public void Delete_OfNeverSyncedCharacter_LeavesNothingPending()
    {
        var repo = NewRepository();
        var c = Samples.Rich();
        repo.Save(c);
        Assert.True(repo.Delete(c.Id));
        Assert.False(repo.Delete(c.Id));
        Assert.Empty(repo.GetPendingDeletes());
    }

    [Fact]
    public void ApplyRemoteDelete_SkipsDirtyRowsUnlessForced()
    {
        var repo = NewRepository();
        var c = Samples.Rich();
        repo.Save(c);
        Assert.False(repo.ApplyRemoteDelete(c.Id));
        Assert.True(repo.ApplyRemoteDelete(c.Id, force: true));
        Assert.Null(repo.Load(c.Id));
    }

    [Fact]
    public void Rebase_SetsCloudRevisionAndMarksDirty()
    {
        var repo = NewRepository();
        var c = Samples.Rich();
        repo.ApplyRemote(c, 3);
        repo.Rebase(c.Id, null);
        Assert.Null(Assert.Single(repo.GetDirty()).CloudRevision);
    }

    [Fact]
    public void ReassignAllIds_ThenResetSyncState_StartsOverUnderANewAccount()
    {
        var repo = NewRepository();
        var c = Samples.Rich();
        repo.ApplyRemote(c, 3);
        repo.SetSyncAccount("user-1");
        repo.SetLastPull(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));

        repo.ReassignAllIds();
        repo.ResetSyncState(markAllDirty: true);

        var moved = Assert.Single(repo.List());
        Assert.NotEqual(c.Id, moved.Id);
        Assert.Equal(c.Identity.Name, repo.Load(moved.Id)!.Identity.Name);
        Assert.Null(Assert.Single(repo.GetDirty()).CloudRevision);
        Assert.Equal(new SyncMeta("user-1", null), repo.GetSyncMeta());

        repo.ResetSyncState(markAllDirty: false);
        Assert.Empty(repo.GetDirty());
    }

    [Fact]
    public void SyncMeta_RoundTrips()
    {
        var repo = NewRepository();
        Assert.Equal(new SyncMeta(null, null), repo.GetSyncMeta());
        var at = new DateTimeOffset(2026, 9, 29, 10, 0, 0, 123, TimeSpan.Zero);
        repo.SetSyncAccount("user-1");
        repo.SetLastPull(at);
        Assert.Equal(new SyncMeta("user-1", at), NewRepository().GetSyncMeta());
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Hearthsheet.slnx --filter FullyQualifiedName~SyncStoreTests`
Expected: a build failure (`The type or namespace name 'Sync' does not exist in the namespace 'Hearthsheet.Core'`).

- [ ] **Step 3: Create the contracts**

Create `src/Hearthsheet.Core/Sync/SyncContracts.cs`:

```csharp
using Hearthsheet.Core.Domain;

namespace Hearthsheet.Core.Sync;

/// <summary>A locally changed character waiting to be pushed, as stored (<see cref="Data"/> is the JSON document).</summary>
public sealed record LocalChange(Guid Id, long LocalRevision, long? CloudRevision, string Name, string Description, int SchemaVersion, string Data);

public sealed record LocalState(bool Dirty, long LocalRevision);

/// <summary>A character deleted locally after it had been synced; the cloud row still needs a tombstone.</summary>
public sealed record PendingDelete(Guid Id, long CloudRevision);

/// <summary>The cloud account this database is linked to, and the pull cursor (server time).</summary>
public sealed record SyncMeta(string? Account, DateTimeOffset? LastPull);

/// <summary>
/// Sync bookkeeping on the local store. Each method is one atomic statement or transaction, so the UI thread's
/// saves and a background sync pass can interleave safely. <c>local_revision</c> counts saves; it guards
/// against overwriting or clearing a row that was saved again while a push was in flight.
/// </summary>
public interface ISyncLocalStore
{
    IReadOnlyList<LocalChange> GetDirty();
    LocalState? GetState(Guid id);
    /// <summary>Records the new cloud revision; clears <c>dirty</c> only if nothing was saved since <paramref name="localRevision"/>.</summary>
    void MarkPushed(Guid id, long localRevision, long cloudRevision);
    /// <summary>
    /// Upserts a pulled character as clean. Skipped when the local row is dirty (unless its local revision equals
    /// <paramref name="ifLocalRevision"/>, or <paramref name="force"/>) or has a pending delete.
    /// </summary>
    bool ApplyRemote(Character character, long cloudRevision, long? ifLocalRevision = null, bool force = false);
    /// <summary>Deletes locally without queuing a cloud delete. Skipped when the row is dirty unless <paramref name="force"/>.</summary>
    bool ApplyRemoteDelete(Guid id, bool force = false);
    /// <summary>Points the row at another cloud revision (null = never synced) and marks it dirty.</summary>
    void Rebase(Guid id, long? cloudRevision);
    IReadOnlyDictionary<Guid, long?> GetCloudRevisions();
    IReadOnlyList<PendingDelete> GetPendingDeletes();
    void ClearPendingDelete(Guid id);
    SyncMeta GetSyncMeta();
    void SetSyncAccount(string account);
    void SetLastPull(DateTimeOffset? at);
    /// <summary>Forgets all cloud state: revisions, pending deletes and the pull cursor.</summary>
    void ResetSyncState(bool markAllDirty);
    /// <summary>Gives every character a new id (used when linking to a different account).</summary>
    void ReassignAllIds();
}
```

- [ ] **Step 4: Upgrade the database to version 2**

In `SqliteCharacterRepository.cs`:
- change the class declaration to `public sealed class SqliteCharacterRepository : ICharacterRepository, ISyncLocalStore`
- add `using Hearthsheet.Core.Sync;`
- change `private const int DatabaseVersion = 1;` to `2`

Then replace the version block in `Initialize()` (from `using var version = connection.CreateCommand();` to just before `_logger.LogInformation("Database initialized");`) with:

```csharp
        using var version = connection.CreateCommand();
        version.CommandText = "SELECT value FROM meta WHERE key = 'db_version'";
        var existing = version.ExecuteScalar() as string;
        var current = existing is null ? 1 : int.Parse(existing, CultureInfo.InvariantCulture);
        if (existing is null)
        {
            version.CommandText = "INSERT INTO meta (key, value) VALUES ('db_version', '1')";
            version.ExecuteNonQuery();
        }
        else if (current > DatabaseVersion)
        {
            throw new InvalidOperationException(
                "The character database was created by a newer version of the application. Update the application to use it.");
        }
        if (current < 2) UpgradeTo2(connection);
```

Add below `Initialize()`:

```csharp
    /// <summary>Adds cloud sync bookkeeping. Existing rows start dirty and never-synced.</summary>
    private void UpgradeTo2(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE characters ADD COLUMN cloud_revision INTEGER;
            ALTER TABLE characters ADD COLUMN dirty INTEGER NOT NULL DEFAULT 1;
            ALTER TABLE characters ADD COLUMN local_revision INTEGER NOT NULL DEFAULT 0;
            CREATE TABLE pending_deletes (id TEXT PRIMARY KEY, cloud_revision INTEGER NOT NULL);
            UPDATE meta SET value = '2' WHERE key = 'db_version';
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
        _logger.LogInformation("Database upgraded to version 2 (cloud sync)");
    }
```

- [ ] **Step 5: Track saves and deletes**

In `Save`, replace the `command.CommandText = """ ... """;` with:

```csharp
        command.CommandText = """
            INSERT INTO characters (id, name, description, schema_version, data, created_utc, updated_utc, dirty, local_revision)
            VALUES ($id, $name, $description, $version, $data, $now, $now, 1, 1)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name, description = excluded.description,
                schema_version = excluded.schema_version, data = excluded.data, updated_utc = excluded.updated_utc,
                dirty = 1, local_revision = characters.local_revision + 1
            """;
```

Replace `Delete` with:

```csharp
    public bool Delete(Guid id)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var pending = connection.CreateCommand();
        pending.Transaction = transaction;
        // A synced character still exists in the cloud: queue a tombstone for the next sync pass.
        pending.CommandText = """
            INSERT INTO pending_deletes (id, cloud_revision)
            SELECT id, cloud_revision FROM characters WHERE id = $id AND cloud_revision IS NOT NULL
            ON CONFLICT(id) DO UPDATE SET cloud_revision = excluded.cloud_revision
            """;
        pending.Parameters.AddWithValue("$id", id.ToString());
        pending.ExecuteNonQuery();

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM characters WHERE id = $id";
        command.Parameters.AddWithValue("$id", id.ToString());
        var deleted = command.ExecuteNonQuery() > 0;
        transaction.Commit();
        return deleted;
    }
```

- [ ] **Step 6: Implement `ISyncLocalStore`**

Add to `SqliteCharacterRepository`, above `BackupTo`:

```csharp
    // ---- Cloud sync bookkeeping (ISyncLocalStore) ----

    private int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command.ExecuteNonQuery();
    }

    public IReadOnlyList<LocalChange> GetDirty()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, local_revision, cloud_revision, name, description, schema_version, data FROM characters WHERE dirty = 1";
        using var reader = command.ExecuteReader();
        var result = new List<LocalChange>();
        while (reader.Read())
        {
            result.Add(new LocalChange(
                Guid.Parse(reader.GetString(0)), reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetInt64(2),
                reader.GetString(3), reader.GetString(4), reader.GetInt32(5), reader.GetString(6)));
        }
        return result;
    }

    public LocalState? GetState(Guid id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT dirty, local_revision FROM characters WHERE id = $id";
        command.Parameters.AddWithValue("$id", id.ToString());
        using var reader = command.ExecuteReader();
        return reader.Read() ? new LocalState(reader.GetInt64(0) != 0, reader.GetInt64(1)) : null;
    }

    public void MarkPushed(Guid id, long localRevision, long cloudRevision) =>
        Execute("""
            UPDATE characters SET cloud_revision = $cloud,
                dirty = CASE WHEN local_revision = $local THEN 0 ELSE 1 END
            WHERE id = $id
            """, ("$id", id.ToString()), ("$cloud", cloudRevision), ("$local", localRevision));

    public bool ApplyRemote(Character character, long cloudRevision, long? ifLocalRevision = null, bool force = false)
    {
        var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        return Execute("""
            INSERT INTO characters (id, name, description, schema_version, data, created_utc, updated_utc, cloud_revision, dirty, local_revision)
            SELECT $id, $name, $description, $version, $data, $now, $now, $cloud, 0, 0
            WHERE NOT EXISTS (SELECT 1 FROM pending_deletes WHERE id = $id)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name, description = excluded.description, schema_version = excluded.schema_version,
                data = excluded.data, updated_utc = excluded.updated_utc, cloud_revision = excluded.cloud_revision, dirty = 0
            WHERE characters.dirty = 0 OR characters.local_revision = $expected OR $force
            """,
            ("$id", character.Id.ToString()), ("$name", DisplayName(character)), ("$description", Describe(character)),
            ("$version", CharacterJson.CurrentSchemaVersion), ("$data", CharacterJson.Serialize(character)), ("$now", now),
            ("$cloud", cloudRevision), ("$expected", ifLocalRevision), ("$force", force ? 1 : 0)) > 0;
    }

    public bool ApplyRemoteDelete(Guid id, bool force = false) =>
        Execute("DELETE FROM characters WHERE id = $id AND (dirty = 0 OR $force)",
            ("$id", id.ToString()), ("$force", force ? 1 : 0)) > 0;

    public void Rebase(Guid id, long? cloudRevision) =>
        Execute("UPDATE characters SET cloud_revision = $cloud, dirty = 1 WHERE id = $id",
            ("$id", id.ToString()), ("$cloud", cloudRevision));

    public IReadOnlyDictionary<Guid, long?> GetCloudRevisions()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, cloud_revision FROM characters";
        using var reader = command.ExecuteReader();
        var result = new Dictionary<Guid, long?>();
        while (reader.Read()) result[Guid.Parse(reader.GetString(0))] = reader.IsDBNull(1) ? null : reader.GetInt64(1);
        return result;
    }

    public IReadOnlyList<PendingDelete> GetPendingDeletes()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, cloud_revision FROM pending_deletes";
        using var reader = command.ExecuteReader();
        var result = new List<PendingDelete>();
        while (reader.Read()) result.Add(new PendingDelete(Guid.Parse(reader.GetString(0)), reader.GetInt64(1)));
        return result;
    }

    public void ClearPendingDelete(Guid id) =>
        Execute("DELETE FROM pending_deletes WHERE id = $id", ("$id", id.ToString()));

    public SyncMeta GetSyncMeta()
    {
        var lastPull = ReadMeta("sync_last_pull");
        return new SyncMeta(ReadMeta("sync_account"),
            lastPull is null ? null : DateTimeOffset.Parse(lastPull, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    public void SetSyncAccount(string account) => WriteMeta("sync_account", account);

    public void SetLastPull(DateTimeOffset? at) =>
        WriteMeta("sync_last_pull", at?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));

    public void ResetSyncState(bool markAllDirty)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE characters SET cloud_revision = NULL, dirty = $dirty;
            DELETE FROM pending_deletes;
            DELETE FROM meta WHERE key = 'sync_last_pull';
            """;
        command.Parameters.AddWithValue("$dirty", markAllDirty ? 1 : 0);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public void ReassignAllIds()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var ids = new List<string>();
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT id FROM characters";
            using var reader = select.ExecuteReader();
            while (reader.Read()) ids.Add(reader.GetString(0));
        }
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE characters SET id = $new WHERE id = $old";
        var oldId = update.Parameters.Add("$old", SqliteType.Text);
        var newId = update.Parameters.Add("$new", SqliteType.Text);
        foreach (var id in ids)
        {
            oldId.Value = id;
            newId.Value = Guid.NewGuid().ToString();
            update.ExecuteNonQuery();
        }
        transaction.Commit();
        _logger.LogInformation("Reassigned ids of {Count} characters", ids.Count);
    }

    private string? ReadMeta(string key)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    private void WriteMeta(string key, string? value)
    {
        if (value is null) Execute("DELETE FROM meta WHERE key = $key", ("$key", key));
        else Execute("INSERT INTO meta (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            ("$key", key), ("$value", value));
    }
```

`Load` needs no change: it already sets `character.Id` from the row's `id` column, so `ReassignAllIds` doesn't have to rewrite the JSON.

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test Hearthsheet.slnx`
Expected: all tests pass (116 existing + 10 new).

- [ ] **Step 8: Commit**

```bash
git add src/Hearthsheet.Core/Sync/SyncContracts.cs src/Hearthsheet.Infrastructure/Persistence/SqliteCharacterRepository.cs tests/Hearthsheet.Tests/SyncStoreTests.cs
git commit -m "Add sync bookkeeping to the local database"
```

---

### Task 2: The sync algorithm (`CloudSync`)

**Files:**
- Create: `src/Hearthsheet.Core/Sync/CloudContracts.cs`
- Create: `src/Hearthsheet.Core/Sync/CloudSync.cs`
- Modify: `src/Hearthsheet.Core/Serialization/PortableCharacterFile.cs` (make `EnforceLimits` `internal`)
- Test: `tests/Hearthsheet.Tests/CloudSyncTests.cs`

**Interfaces:**
- Consumes: `ISyncLocalStore` and its records (Task 1), `ICharacterRepository`, `CharacterJson`, `CharacterMigrator`, `PortableCharacterFile.MaxFileBytes`.
- Produces (namespace `Hearthsheet.Core.Sync`):
  - `record CloudRow(Guid Id, string Name, long Revision, int SchemaVersion, string? Data, DateTimeOffset? DeletedAt, DateTimeOffset UpdatedAt)` with `bool IsDeleted`
  - `record CloudUpload(Guid Id, string Name, string Description, int SchemaVersion, string Data)`
  - `interface ICloudCharacterStore { InsertAsync, UpdateIfRevisionAsync, TombstoneIfRevisionAsync, GetAsync, ChangedSinceAsync, ListAllIdsAsync }`
  - `interface ICloudSession { string? UserId; Task<string> GetAccessTokenAsync(CancellationToken); void InvalidateAccessToken(); }`
  - `abstract class CloudException`, and its subclasses `CloudRequestException(string, Exception?)`, `CloudUnavailableException(string, bool offline, Exception?)` (with `bool Offline`) and `CloudSignedOutException(string)`
  - `enum SyncOutcome { Synced, NotSignedIn, SignedOut, Offline, Unavailable, AccountMismatch, Busy }`
  - `record DeferredChange(Guid Id, Character? Remote, long Revision, long? CopiedLocalRevision = null)`
  - `record SyncResult(SyncOutcome Outcome, IReadOnlySet<Guid> Changed, IReadOnlySet<Guid> Deleted, IReadOnlyList<DeferredChange> Deferred, int ConflictCopies, IReadOnlyList<string> Warnings)` with `static Of(SyncOutcome)` and `bool HasChanges`
  - `class CloudSync(ICharacterRepository, ISyncLocalStore, ICloudCharacterStore, ICloudSession, CharacterMigrator, ILogger<CloudSync>, TimeProvider? = null)`, with these members:
    - `string? LinkedAccount`
    - `Task<SyncResult> RunAsync(Func<Guid?> openCharacter, bool waitIfBusy, CancellationToken ct)`
    - `Character? ApplyDeferred(DeferredChange change, Character? openInMemory, bool hasUnsavedChanges)`
    - `void Relink(string userId, bool uploadExisting)`

- [ ] **Step 1: Write the cloud contracts**

These are needed so the test fakes compile. Create `src/Hearthsheet.Core/Sync/CloudContracts.cs`:

```csharp
namespace Hearthsheet.Core.Sync;

/// <summary>A character row as stored in the cloud. <see cref="Data"/> is the JSON document, null for a tombstone.</summary>
public sealed record CloudRow(Guid Id, string Name, long Revision, int SchemaVersion, string? Data, DateTimeOffset? DeletedAt, DateTimeOffset UpdatedAt)
{
    public bool IsDeleted => Data is null || DeletedAt is not null;
}

public sealed record CloudUpload(Guid Id, string Name, string Description, int SchemaVersion, string Data);

/// <summary>The signed-in user's character rows in the cloud. Revisions are assigned by the server.</summary>
public interface ICloudCharacterStore
{
    /// <returns>The stored row, or null when a row with this id already exists (it may belong to another account).</returns>
    Task<CloudRow?> InsertAsync(CloudUpload upload, CancellationToken ct);
    /// <summary>Also clears any tombstone. Returns null when the row is missing or its revision no longer matches.</summary>
    Task<CloudRow?> UpdateIfRevisionAsync(Guid id, long expectedRevision, CloudUpload upload, CancellationToken ct);
    /// <returns>False when the row is missing or its revision no longer matches.</returns>
    Task<bool> TombstoneIfRevisionAsync(Guid id, long expectedRevision, CancellationToken ct);
    Task<CloudRow?> GetAsync(Guid id, CancellationToken ct);
    /// <summary>Rows, tombstones included, updated after <paramref name="since"/> (all rows when null), oldest first.</summary>
    Task<IReadOnlyList<CloudRow>> ChangedSinceAsync(DateTimeOffset? since, CancellationToken ct);
    /// <summary>Ids of every row, tombstones included.</summary>
    Task<IReadOnlySet<Guid>> ListAllIdsAsync(CancellationToken ct);
}

public interface ICloudSession
{
    string? UserId { get; }
    /// <exception cref="CloudSignedOutException">There is no session, or the refresh token was rejected.</exception>
    Task<string> GetAccessTokenAsync(CancellationToken ct);
    /// <summary>Forces the next <see cref="GetAccessTokenAsync"/> to refresh (after a 401).</summary>
    void InvalidateAccessToken();
}

public abstract class CloudException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The service rejected the request. The message is safe to show to the user.</summary>
public sealed class CloudRequestException(string message, Exception? inner = null) : CloudException(message, inner);

/// <summary>The service could not be reached (<see cref="Offline"/>), timed out or failed (5xx, paused project).</summary>
public sealed class CloudUnavailableException(string message, bool offline, Exception? inner = null) : CloudException(message, inner)
{
    public bool Offline { get; } = offline;
}

public sealed class CloudSignedOutException(string message) : CloudException(message);
```

- [ ] **Step 2: Write the test fakes and failing tests**

Create `tests/Hearthsheet.Tests/CloudSyncTests.cs`:

```csharp
using Hearthsheet.Core.Domain;
using Hearthsheet.Core.Serialization;
using Hearthsheet.Core.Sync;
using Hearthsheet.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hearthsheet.Tests;

internal sealed class ManualTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>In-memory stand-in for one account's cloud table. Every write moves the shared clock by a second.</summary>
internal sealed class FakeCloud(ManualTime time) : ICloudCharacterStore
{
    public Dictionary<Guid, CloudRow> Rows { get; } = [];
    /// <summary>Ids that exist under another account: invisible, but inserting them conflicts.</summary>
    public HashSet<Guid> ForeignIds { get; } = [];
    public bool Offline { get; set; }
    public TaskCompletionSource? Gate { get; set; }

    private DateTimeOffset Tick() => time.Now += TimeSpan.FromSeconds(1);
    private void Check()
    {
        if (Offline) throw new CloudUnavailableException("offline", offline: true);
    }

    public Task<CloudRow?> InsertAsync(CloudUpload u, CancellationToken ct)
    {
        Check();
        if (Rows.ContainsKey(u.Id) || ForeignIds.Contains(u.Id)) return Task.FromResult<CloudRow?>(null);
        return Task.FromResult<CloudRow?>(Rows[u.Id] = new CloudRow(u.Id, u.Name, 1, u.SchemaVersion, u.Data, null, Tick()));
    }

    public Task<CloudRow?> UpdateIfRevisionAsync(Guid id, long expected, CloudUpload u, CancellationToken ct)
    {
        Check();
        if (!Rows.TryGetValue(id, out var row) || row.Revision != expected) return Task.FromResult<CloudRow?>(null);
        return Task.FromResult<CloudRow?>(Rows[id] = new CloudRow(id, u.Name, row.Revision + 1, u.SchemaVersion, u.Data, null, Tick()));
    }

    public Task<bool> TombstoneIfRevisionAsync(Guid id, long expected, CancellationToken ct)
    {
        Check();
        if (!Rows.TryGetValue(id, out var row) || row.Revision != expected) return Task.FromResult(false);
        DeleteRemotely(id);
        return Task.FromResult(true);
    }

    public Task<CloudRow?> GetAsync(Guid id, CancellationToken ct)
    {
        Check();
        return Task.FromResult(Rows.GetValueOrDefault(id));
    }

    public async Task<IReadOnlyList<CloudRow>> ChangedSinceAsync(DateTimeOffset? since, CancellationToken ct)
    {
        if (Gate is not null) await Gate.Task;
        Check();
        return Rows.Values.Where(r => since is null || r.UpdatedAt > since).OrderBy(r => r.UpdatedAt).ToList();
    }

    public Task<IReadOnlySet<Guid>> ListAllIdsAsync(CancellationToken ct)
    {
        Check();
        return Task.FromResult<IReadOnlySet<Guid>>(Rows.Keys.ToHashSet());
    }

    /// <summary>Another device writes a new version.</summary>
    public void EditRemotely(Guid id, Action<Character> edit)
    {
        var row = Rows[id];
        var c = CharacterJson.Deserialize(row.Data!, row.SchemaVersion, CharacterMigrator.Default);
        edit(c);
        Rows[id] = row with { Name = c.Identity.Name, Revision = row.Revision + 1, Data = CharacterJson.Serialize(c), UpdatedAt = Tick() };
    }

    public void DeleteRemotely(Guid id)
    {
        var row = Rows[id];
        Rows[id] = row with { Revision = row.Revision + 1, Data = null, DeletedAt = time.Now, UpdatedAt = Tick() };
    }
}

internal sealed class FakeSession : ICloudSession
{
    public string? UserId { get; set; } = "user-1";
    public bool Revoked { get; set; }
    public int Invalidations { get; private set; }
    public Task<string> GetAccessTokenAsync(CancellationToken ct) =>
        Revoked ? throw new CloudSignedOutException("revoked") : Task.FromResult("token");
    public void InvalidateAccessToken() => Invalidations++;
}

/// <summary>One installation: its own database, sharing the fake cloud and session.</summary>
internal sealed class Machine(TempDir dir, string name, FakeCloud cloud, FakeSession session, ManualTime time)
{
    public SqliteCharacterRepository Repo { get; } =
        new(dir.File(name + ".db"), CharacterMigrator.Default, NullLogger<SqliteCharacterRepository>.Instance);
    public CloudSync Sync => field ??= new CloudSync(Repo, Repo, cloud, session, CharacterMigrator.Default, NullLogger<CloudSync>.Instance, time);
    public Guid? OpenId { get; set; }

    public Task<SyncResult> SyncAsync() => Sync.RunAsync(() => OpenId, waitIfBusy: false, CancellationToken.None);

    public Character Add(string characterName)
    {
        var c = Samples.Rich();
        c.Identity.Name = characterName;
        Repo.Save(c);
        return c;
    }

    public void Rename(Guid id, string newName)
    {
        var c = Repo.Load(id)!;
        c.Identity.Name = newName;
        Repo.Save(c);
    }

    public string? NameOf(Guid id) => Repo.Load(id)?.Identity.Name;
}

public class CloudSyncTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ManualTime _time = new();
    private readonly FakeSession _session = new();
    private readonly FakeCloud _cloud;
    private readonly Machine _a;
    private readonly Machine _b;

    public CloudSyncTests()
    {
        _cloud = new FakeCloud(_time);
        _a = new Machine(_dir, "a", _cloud, _session, _time);
        _b = new Machine(_dir, "b", _cloud, _session, _time);
    }

    public void Dispose() => _dir.Dispose();

    private async Task<Character> SyncedToBoth(string name = "Arannis")
    {
        var c = _a.Add(name);
        await _a.SyncAsync();
        await _b.SyncAsync();
        return c;
    }

    // ---- Push, pull, outcomes ----

    [Fact]
    public async Task FirstSync_UploadsLocalCharacters_AndLinksTheAccount()
    {
        var c = _a.Add("Arannis");
        var result = await _a.SyncAsync();
        Assert.Equal(SyncOutcome.Synced, result.Outcome);
        Assert.Equal("Arannis", _cloud.Rows[c.Id].Name);
        Assert.Equal("user-1", _a.Sync.LinkedAccount);
        Assert.Empty(_a.Repo.GetDirty());
    }

    [Fact]
    public async Task Pull_BringsCharactersToAnotherMachine()
    {
        var c = _a.Add("Arannis");
        await _a.SyncAsync();
        var result = await _b.SyncAsync();
        Assert.Contains(c.Id, result.Changed);
        Assert.Equal("Arannis", _b.NameOf(c.Id));
        Assert.Empty(_b.Repo.GetDirty());
    }

    [Fact]
    public async Task Edits_RoundTripBetweenMachines()
    {
        var c = await SyncedToBoth();
        _b.Rename(c.Id, "Arannis the Wise");
        await _b.SyncAsync();
        var result = await _a.SyncAsync();
        Assert.Contains(c.Id, result.Changed);
        Assert.Equal("Arannis the Wise", _a.NameOf(c.Id));
        Assert.Equal(2, _cloud.Rows[c.Id].Revision);
    }

    [Fact]
    public async Task SecondPass_WithNothingNew_ChangesNothing()
    {
        _a.Add("Arannis");
        await _a.SyncAsync();
        var result = await _a.SyncAsync();
        Assert.False(result.HasChanges);
    }

    [Fact]
    public async Task NotSignedIn_DoesNothing()
    {
        _session.UserId = null;
        _a.Add("Arannis");
        Assert.Equal(SyncOutcome.NotSignedIn, (await _a.SyncAsync()).Outcome);
        Assert.Empty(_cloud.Rows);
    }

    [Fact]
    public async Task RevokedSession_ReportsSignedOut()
    {
        _session.Revoked = true;
        Assert.Equal(SyncOutcome.SignedOut, (await _a.SyncAsync()).Outcome);
    }

    [Fact]
    public async Task Offline_ReportsOffline_AndKeepsChangesQueued()
    {
        await _a.SyncAsync();
        _cloud.Offline = true;
        _a.Add("Arannis");
        Assert.Equal(SyncOutcome.Offline, (await _a.SyncAsync()).Outcome);
        Assert.Single(_a.Repo.GetDirty());
    }

    [Fact]
    public async Task DifferentAccount_IsNotSyncedUntilRelinked()
    {
        await _a.SyncAsync();
        _session.UserId = "user-2";
        _a.Add("Arannis");
        Assert.Equal(SyncOutcome.AccountMismatch, (await _a.SyncAsync()).Outcome);
        Assert.Empty(_cloud.Rows);
    }

    [Fact]
    public async Task PassesDoNotOverlap()
    {
        _cloud.Gate = new TaskCompletionSource();
        var first = _a.SyncAsync(); // parks inside the pull
        Assert.Equal(SyncOutcome.Busy, (await _a.SyncAsync()).Outcome);
        _cloud.Gate.SetResult();
        Assert.Equal(SyncOutcome.Synced, (await first).Outcome);
    }

    // ---- Untrusted and unreadable data ----

    [Fact]
    public async Task Pull_SkipsCorruptRows_AndSyncsTheRest()
    {
        var bad = _a.Add("Bad");
        var good = _a.Add("Good");
        await _a.SyncAsync();
        _cloud.Rows[bad.Id] = _cloud.Rows[bad.Id] with { Revision = 2, Data = """{"abilities":{"strength":"lots"}}""" };

        var result = await _b.SyncAsync();
        Assert.Null(_b.Repo.Load(bad.Id));
        Assert.Equal("Good", _b.NameOf(good.Id));
        Assert.Single(result.Warnings);
    }

    [Fact]
    public async Task Pull_SkipsNewerSchemaRows_AndPicksThemUpAfterAnAppUpdate()
    {
        var future = _a.Add("Future");
        await _a.SyncAsync();
        _cloud.Rows[future.Id] = _cloud.Rows[future.Id] with { Revision = 2, SchemaVersion = CharacterJson.CurrentSchemaVersion + 1 };
        _time.Now += TimeSpan.FromHours(1);
        var later = _a.Add("Later");
        await _a.SyncAsync();

        var result = await _b.SyncAsync();
        Assert.Null(_b.Repo.Load(future.Id));
        Assert.Equal("Later", _b.NameOf(later.Id));
        Assert.Single(result.Warnings);

        // The app is updated: the same row (same updated_at) is now readable and must not have been skipped past.
        _cloud.Rows[future.Id] = _cloud.Rows[future.Id] with { SchemaVersion = CharacterJson.CurrentSchemaVersion };
        await _b.SyncAsync();
        Assert.Equal("Future", _b.NameOf(future.Id));
    }

    [Fact]
    public async Task OversizedCharacter_IsSkipped_AndOthersSync()
    {
        var big = Samples.Rich();
        big.Biography.Backstory = new string('x', 11 * 1024 * 1024);
        _a.Repo.Save(big);
        var small = _a.Add("Small");

        var result = await _a.SyncAsync();
        Assert.True(_cloud.Rows.ContainsKey(small.Id));
        Assert.False(_cloud.Rows.ContainsKey(big.Id));
        Assert.Single(result.Warnings);
        Assert.Single(_a.Repo.GetDirty());
    }

    [Fact]
    public async Task InsertOfAnIdOwnedByAnotherAccount_IsSkippedWithAWarning()
    {
        var c = _a.Add("Arannis");
        _cloud.ForeignIds.Add(c.Id);
        var result = await _a.SyncAsync();
        Assert.Single(result.Warnings);
        Assert.Single(_a.Repo.GetDirty());
    }

    // ---- Conflicts and deletes ----

    [Fact]
    public async Task Conflict_CloudWins_AndLocalIsKeptAsACopyThatUploadsNextPass()
    {
        var c = await SyncedToBoth();
        _b.Rename(c.Id, "B-name");
        await _b.SyncAsync();
        _a.Rename(c.Id, "A-name");

        var result = await _a.SyncAsync();
        Assert.Equal(1, result.ConflictCopies);
        Assert.Contains(c.Id, result.Changed);
        Assert.Equal("B-name", _a.NameOf(c.Id));
        Assert.Contains(_a.Repo.List(), s => s.Name.StartsWith("A-name (conflict copy, ", StringComparison.Ordinal));
        Assert.Single(_cloud.Rows);

        await _a.SyncAsync();
        Assert.Equal(2, _cloud.Rows.Count);
    }

    [Fact]
    public async Task Delete_PropagatesToOtherMachines()
    {
        var c = await SyncedToBoth();
        _a.Repo.Delete(c.Id);
        await _a.SyncAsync();
        Assert.True(_cloud.Rows[c.Id].IsDeleted);
        Assert.Empty(_a.Repo.GetPendingDeletes());

        var result = await _b.SyncAsync();
        Assert.Contains(c.Id, result.Deleted);
        Assert.Null(_b.Repo.Load(c.Id));
    }

    [Fact]
    public async Task Delete_LosesToANewerRemoteEdit()
    {
        var c = await SyncedToBoth();
        _b.Rename(c.Id, "B-name");
        await _b.SyncAsync();
        _a.Repo.Delete(c.Id);

        await _a.SyncAsync();
        Assert.False(_cloud.Rows[c.Id].IsDeleted);
        Assert.Equal("B-name", _a.NameOf(c.Id));
    }

    [Fact]
    public async Task Edit_BeatsARemoteDelete()
    {
        var c = await SyncedToBoth();
        _b.Repo.Delete(c.Id);
        await _b.SyncAsync();
        _a.Rename(c.Id, "A-name");

        await _a.SyncAsync(); // learns about the tombstone
        await _a.SyncAsync(); // revives the row
        Assert.False(_cloud.Rows[c.Id].IsDeleted);
        Assert.Equal("A-name", _cloud.Rows[c.Id].Name);

        await _b.SyncAsync();
        Assert.Equal("A-name", _b.NameOf(c.Id));
    }

    [Fact]
    public async Task StaleMachine_ReuploadsCharactersWhosePurgedTombstonesItMissed()
    {
        var c = await SyncedToBoth();
        _b.Repo.Delete(c.Id);
        await _b.SyncAsync();
        _cloud.Rows.Remove(c.Id); // the 90-day purge job ran
        _time.Now += TimeSpan.FromDays(81);

        await _a.SyncAsync();
        await _a.SyncAsync();
        Assert.False(_cloud.Rows[c.Id].IsDeleted); // an accepted un-delete, never data loss
    }

    // ---- The character open in the UI ----

    [Fact]
    public async Task RemoteChangeToTheOpenCharacter_IsDeferred_ThenApplied()
    {
        var c = await SyncedToBoth();
        _cloud.EditRemotely(c.Id, x => x.Identity.Name = "Remote");
        _a.OpenId = c.Id;

        var result = await _a.SyncAsync();
        var change = Assert.Single(result.Deferred);
        Assert.Equal("Remote", change.Remote!.Identity.Name);
        Assert.Equal("Arannis", _a.NameOf(c.Id));

        Assert.Null(_a.Sync.ApplyDeferred(change, _a.Repo.Load(c.Id), hasUnsavedChanges: false));
        Assert.Equal("Remote", _a.NameOf(c.Id));
        Assert.Empty(_a.Repo.GetDirty());
    }

    [Fact]
    public async Task ApplyDeferred_KeepsUnsavedEditsAsAConflictCopy()
    {
        var c = await SyncedToBoth();
        _cloud.EditRemotely(c.Id, x => x.Identity.Name = "Remote");
        _a.OpenId = c.Id;
        var change = Assert.Single((await _a.SyncAsync()).Deferred);

        var inMemory = _a.Repo.Load(c.Id)!;
        inMemory.Identity.Name = "Mine";
        var copy = _a.Sync.ApplyDeferred(change, inMemory, hasUnsavedChanges: true);

        Assert.StartsWith("Mine (conflict copy, ", copy!.Identity.Name, StringComparison.Ordinal);
        Assert.NotNull(_a.Repo.Load(copy.Id));
        Assert.Equal("Remote", _a.NameOf(c.Id));
    }

    [Fact]
    public async Task RemoteDeleteOfTheOpenCharacter_IsDeferred_ThenApplied()
    {
        var c = await SyncedToBoth();
        _b.Repo.Delete(c.Id);
        await _b.SyncAsync();
        _a.OpenId = c.Id;

        var change = Assert.Single((await _a.SyncAsync()).Deferred);
        Assert.Null(change.Remote);
        Assert.NotNull(_a.Repo.Load(c.Id));

        _a.Sync.ApplyDeferred(change, _a.Repo.Load(c.Id), hasUnsavedChanges: false);
        Assert.Null(_a.Repo.Load(c.Id));
    }

    // ---- Switching accounts ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Relink_GivesCharactersNewIds_AndUploadsOnlyWhenAsked(bool upload)
    {
        var c = _a.Add("Arannis");
        await _a.SyncAsync();
        _session.UserId = "user-2";
        _cloud.Rows.Clear(); // user-2 sees an empty table

        _a.Sync.Relink("user-2", uploadExisting: upload);
        Assert.Equal(SyncOutcome.Synced, (await _a.SyncAsync()).Outcome);

        var local = Assert.Single(_a.Repo.List());
        Assert.NotEqual(c.Id, local.Id);
        Assert.Equal(upload ? 1 : 0, _cloud.Rows.Count);
        Assert.Equal("user-2", _a.Sync.LinkedAccount);
    }
}
```

The `Machine.Sync` property uses the C# 14 `field` keyword. The codebase already uses it in `MainViewModel`.

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test Hearthsheet.slnx --filter FullyQualifiedName~CloudSyncTests`
Expected: a build failure (`The type or namespace name 'CloudSync' could not be found`).

- [ ] **Step 4: Expose the import limits**

In `src/Hearthsheet.Core/Serialization/PortableCharacterFile.cs`, change `private static void EnforceLimits(Character c)` to `internal static void EnforceLimits(Character c)`.

- [ ] **Step 5: Implement `CloudSync`**

Create `src/Hearthsheet.Core/Sync/CloudSync.cs`:

```csharp
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Hearthsheet.Core.Application;
using Hearthsheet.Core.Domain;
using Hearthsheet.Core.Serialization;
using Microsoft.Extensions.Logging;

namespace Hearthsheet.Core.Sync;

public enum SyncOutcome { Synced, NotSignedIn, SignedOut, Offline, Unavailable, AccountMismatch, Busy }

/// <summary>
/// A remote change to the character open in the UI. The background pass never writes that row (its autosave
/// could overwrite the remote version with the stale in-memory one); <see cref="CloudSync.ApplyDeferred"/>
/// applies it on the UI thread. <see cref="CopiedLocalRevision"/> is set when a conflict copy was already made.
/// </summary>
public sealed record DeferredChange(Guid Id, Character? Remote, long Revision, long? CopiedLocalRevision = null);

public sealed record SyncResult(
    SyncOutcome Outcome, IReadOnlySet<Guid> Changed, IReadOnlySet<Guid> Deleted,
    IReadOnlyList<DeferredChange> Deferred, int ConflictCopies, IReadOnlyList<string> Warnings)
{
    public static SyncResult Of(SyncOutcome outcome) => new(outcome, new HashSet<Guid>(), new HashSet<Guid>(), [], 0, []);
    public bool HasChanges => Changed.Count + Deleted.Count + Deferred.Count + ConflictCopies > 0;
}

/// <summary>
/// One sync pass: push local deletes and edits, then pull remote changes. Local SQLite stays the source of
/// truth. On a conflict the cloud version wins and the local one is kept as a conflict copy. Design:
/// docs/superpowers/specs/2026-09-29-cloud-sync-design.md §5.
/// </summary>
public sealed class CloudSync(
    ICharacterRepository repository, ISyncLocalStore local, ICloudCharacterStore cloud, ICloudSession session,
    CharacterMigrator migrator, ILogger<CloudSync> logger, TimeProvider? time = null)
{
    public static readonly TimeSpan PullOverlap = TimeSpan.FromMinutes(2);
    /// <summary>Below the 90-day tombstone retention: a machine this stale may have missed purged deletes.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(80);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string? LinkedAccount => local.GetSyncMeta().Account;

    /// <param name="openCharacter">The id open in the UI, read as the pass goes; its remote changes are deferred.</param>
    /// <param name="waitIfBusy">Wait for a running pass instead of returning <see cref="SyncOutcome.Busy"/> (used on close).</param>
    public async Task<SyncResult> RunAsync(Func<Guid?> openCharacter, bool waitIfBusy, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(waitIfBusy ? Timeout.Infinite : 0, ct)) return SyncResult.Of(SyncOutcome.Busy);
        try
        {
            return await RunPassAsync(openCharacter, ct);
        }
        catch (CloudSignedOutException ex)
        {
            logger.LogInformation("Sync stopped: {Reason}", ex.Message);
            return SyncResult.Of(SyncOutcome.SignedOut);
        }
        catch (CloudUnavailableException ex)
        {
            logger.LogWarning("Sync unavailable: {Reason}", ex.Message);
            return SyncResult.Of(ex.Offline ? SyncOutcome.Offline : SyncOutcome.Unavailable);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Applies a deferred change on the UI thread, where no save can interleave. The in-memory character is kept as
    /// a conflict copy first if it has edits the cloud doesn't. Returns that copy, if one was made.
    /// </summary>
    public Character? ApplyDeferred(DeferredChange change, Character? openInMemory, bool hasUnsavedChanges)
    {
        if (openInMemory is null || openInMemory.Id != change.Id)
        {
            // The user moved on to another character meanwhile: apply with the normal dirty guard.
            if (change.Remote is null) local.ApplyRemoteDelete(change.Id);
            else local.ApplyRemote(change.Remote, change.Revision);
            return null;
        }

        Character? copy = null;
        var savedSinceCopy = local.GetState(change.Id) is { Dirty: true } state && state.LocalRevision != change.CopiedLocalRevision;
        if (hasUnsavedChanges || savedSinceCopy)
        {
            copy = ConflictCopy(openInMemory);
            repository.Save(copy);
        }
        if (change.Remote is null) local.ApplyRemoteDelete(change.Id, force: true);
        else local.ApplyRemote(change.Remote, change.Revision, force: true);
        return copy;
    }

    /// <summary>
    /// Links this computer to a different account. Every local character gets a new id, because the old ids exist
    /// under the other account and could never be inserted into this one.
    /// </summary>
    public void Relink(string userId, bool uploadExisting)
    {
        _gate.Wait();
        try
        {
            local.ReassignAllIds();
            local.ResetSyncState(markAllDirty: uploadExisting);
            local.SetSyncAccount(userId);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SyncResult> RunPassAsync(Func<Guid?> openCharacter, CancellationToken ct)
    {
        if (session.UserId is not { } userId) return SyncResult.Of(SyncOutcome.NotSignedIn);
        await session.GetAccessTokenAsync(ct); // surfaces a revoked session or no network before anything changes

        var meta = local.GetSyncMeta();
        if (meta.Account is null)
        {
            local.ResetSyncState(markAllDirty: true);
            local.SetSyncAccount(userId);
            meta = new SyncMeta(userId, null);
            logger.LogInformation("This computer is now linked to a cloud account; uploading all characters");
        }
        else if (meta.Account != userId)
        {
            return SyncResult.Of(SyncOutcome.AccountMismatch);
        }

        var pass = new Pass();
        await PushDeletesAsync(pass, ct);
        await PushChangesAsync(openCharacter, pass, ct);
        await PullAsync(meta.LastPull, openCharacter, pass, ct);
        logger.LogInformation("Sync finished: {Changed} changed, {Deleted} deleted, {Deferred} deferred, {Copies} conflict copies, {Warnings} warnings",
            pass.Changed.Count, pass.Deleted.Count, pass.Deferred.Count, pass.ConflictCopies, pass.Warnings.Count);
        return new SyncResult(SyncOutcome.Synced, pass.Changed, pass.Deleted, pass.Deferred, pass.ConflictCopies, pass.Warnings);
    }

    private async Task PushDeletesAsync(Pass pass, CancellationToken ct)
    {
        foreach (var pending in local.GetPendingDeletes())
        {
            try
            {
                // False means the row is gone or was edited elsewhere. Either way this delete is finished:
                // a newer remote edit wins and comes back with the pull.
                await cloud.TombstoneIfRevisionAsync(pending.Id, pending.CloudRevision, ct);
                local.ClearPendingDelete(pending.Id);
            }
            catch (CloudRequestException ex)
            {
                pass.Warn(logger, $"A deleted character could not be removed from the cloud: {ex.Message}");
            }
        }
    }

    private async Task PushChangesAsync(Func<Guid?> openCharacter, Pass pass, CancellationToken ct)
    {
        foreach (var change in local.GetDirty())
        {
            if (Encoding.UTF8.GetByteCount(change.Data) > PortableCharacterFile.MaxFileBytes)
            {
                pass.Warn(logger, $"“{change.Name}” is too large to sync.");
                continue;
            }
            var upload = new CloudUpload(change.Id, change.Name, change.Description, change.SchemaVersion, change.Data);
            try
            {
                var pushed = change.CloudRevision is { } revision
                    ? await cloud.UpdateIfRevisionAsync(change.Id, revision, upload, ct)
                    : await cloud.InsertAsync(upload, ct);
                if (pushed is not null) local.MarkPushed(change.Id, change.LocalRevision, pushed.Revision);
                else await ResolveConflictAsync(change, openCharacter, pass, ct);
            }
            catch (CloudRequestException ex)
            {
                pass.Warn(logger, $"“{change.Name}” could not be uploaded: {ex.Message}");
            }
        }
    }

    private async Task ResolveConflictAsync(LocalChange change, Func<Guid?> openCharacter, Pass pass, CancellationToken ct)
    {
        var remote = await cloud.GetAsync(change.Id, ct);
        if (remote is null)
        {
            if (change.CloudRevision is null)
                pass.Warn(logger, $"“{change.Name}” could not be uploaded because its id is used by another account.");
            else
                local.Rebase(change.Id, null); // its tombstone was purged: insert it again next pass
            return;
        }
        if (remote.IsDeleted)
        {
            local.Rebase(change.Id, remote.Revision); // an edit beats a delete: the next push revives the row
            return;
        }
        if (!TryRead(remote, pass, out var remoteCharacter)) return;

        var mine = CharacterJson.Deserialize(change.Data, change.SchemaVersion, migrator);
        repository.Save(ConflictCopy(mine));
        pass.ConflictCopies++;
        logger.LogInformation("Conflict on {Id}: kept the cloud version and saved the local one as a copy", change.Id);

        if (openCharacter() == change.Id)
            pass.Deferred.Add(new DeferredChange(change.Id, remoteCharacter, remote.Revision, change.LocalRevision));
        else if (local.ApplyRemote(remoteCharacter, remote.Revision, ifLocalRevision: change.LocalRevision))
            pass.Changed.Add(change.Id);
    }

    private async Task PullAsync(DateTimeOffset? lastPull, Func<Guid?> openCharacter, Pass pass, CancellationToken ct)
    {
        var full = lastPull is null || _time.GetUtcNow() - lastPull > StaleAfter;
        var rows = await cloud.ChangedSinceAsync(full ? null : lastPull - PullOverlap, ct);
        var known = local.GetCloudRevisions();
        DateTimeOffset? heldAt = null;

        foreach (var row in rows)
        {
            if (known.TryGetValue(row.Id, out var revision) && revision == row.Revision) continue;
            var isOpen = openCharacter() == row.Id;
            if (row.IsDeleted)
            {
                if (!known.ContainsKey(row.Id)) continue;
                if (isOpen) pass.Deferred.Add(new DeferredChange(row.Id, null, row.Revision));
                else if (local.ApplyRemoteDelete(row.Id)) pass.Deleted.Add(row.Id);
                continue;
            }
            // Keep the cursor at the oldest row this version can't read yet, so an app update picks it up.
            if (row.SchemaVersion > CharacterJson.CurrentSchemaVersion && (heldAt is null || row.UpdatedAt < heldAt))
                heldAt = row.UpdatedAt;
            if (!TryRead(row, pass, out var character)) continue;
            if (isOpen) pass.Deferred.Add(new DeferredChange(row.Id, character, row.Revision));
            else if (local.ApplyRemote(character, row.Revision)) pass.Changed.Add(row.Id);
        }

        if (full)
        {
            // Rows missing entirely were tombstoned and purged while we weren't looking: upload ours again.
            var ids = await cloud.ListAllIdsAsync(ct);
            foreach (var (id, revision) in known)
                if (revision is not null && !ids.Contains(id)) local.Rebase(id, null);
        }
        local.SetLastPull(heldAt ?? (rows.Count > 0 ? rows.Max(r => r.UpdatedAt) : lastPull));
    }

    /// <summary>Cloud data is untrusted input: the same size, depth, migration, clamping and limit checks as imports.</summary>
    private bool TryRead(CloudRow row, Pass pass, [NotNullWhen(true)] out Character? character)
    {
        character = null;
        if (row.SchemaVersion > CharacterJson.CurrentSchemaVersion)
        {
            pass.Warn(logger, $"Update Hearthsheet to sync “{row.Name}”.");
            return false;
        }
        try
        {
            if (Encoding.UTF8.GetByteCount(row.Data!) > PortableCharacterFile.MaxFileBytes)
                throw new CharacterFormatException("The character is too large.");
            var parsed = CharacterJson.Deserialize(row.Data!, row.SchemaVersion, migrator);
            PortableCharacterFile.EnforceLimits(parsed);
            parsed.Id = row.Id;
            character = parsed;
            return true;
        }
        catch (Exception ex) when (ex is CharacterFormatException or InvalidOperationException)
        {
            pass.Warn(logger, $"“{row.Name}” in the cloud could not be read: {ex.Message}");
            return false;
        }
    }

    private Character ConflictCopy(Character source)
    {
        var copy = CharacterJson.Clone(source, migrator);
        copy.Id = Guid.NewGuid();
        var date = _time.GetLocalNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        copy.Identity.Name = $"{source.Identity.Name} (conflict copy, {date})";
        return copy;
    }

    private sealed class Pass
    {
        public HashSet<Guid> Changed { get; } = [];
        public HashSet<Guid> Deleted { get; } = [];
        public List<DeferredChange> Deferred { get; } = [];
        public List<string> Warnings { get; } = [];
        public int ConflictCopies { get; set; }

        public void Warn(ILogger log, string message)
        {
            log.LogWarning("Sync: {Message}", message);
            Warnings.Add(message);
        }
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test Hearthsheet.slnx`
Expected: all tests pass, including 23 cases in `CloudSyncTests`. If `Pull_SkipsNewerSchemaRows…` fails, check that `SetLastPull` uses `heldAt` before the newest `UpdatedAt`.

- [ ] **Step 7: Commit**

```bash
git add src/Hearthsheet.Core/Sync src/Hearthsheet.Core/Serialization/PortableCharacterFile.cs tests/Hearthsheet.Tests/CloudSyncTests.cs
git commit -m "Add the cloud sync algorithm"
```

---

### Task 3: Redact Supabase tokens from logs

**Files:**
- Modify: `src/Hearthsheet.Infrastructure/Logging/AppLogging.cs` (the `SecretRedactor` regex)
- Test: `tests/Hearthsheet.Tests/UpdateTests.cs` (the `RedactionTests.SecretsAreRedacted` theory)

**Interfaces:** none. This is a behaviour change to `SecretRedactor.Redact` only.

- [ ] **Step 1: Write the failing cases**

Add two `InlineData` rows to `RedactionTests.SecretsAreRedacted`:

```csharp
    [InlineData("session eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.abcdefghijklmnop")]
    [InlineData("{\"refresh_token\":\"abcdefghijklmnop\"}")]
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Hearthsheet.slnx --filter FullyQualifiedName~RedactionTests`
Expected: FAIL on the two new cases (`abcdefghijklmnop` is still present).

- [ ] **Step 3: Extend the pattern**

Replace the `[GeneratedRegex(...)]` attribute on `SecretPattern()` with:

```csharp
    [GeneratedRegex(@"(gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]*|(?i:authorization\s*[:=]).*|(?i:bearer\s+)\S+|(?i:(password|token)""?\s*[:=]\s*)\S+)")]
```

Also update the summary's first sentence to: `Last line of defense: scrubs anything shaped like a GitHub token, a JWT or an auth header from log lines.`

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Hearthsheet.slnx --filter FullyQualifiedName~RedactionTests`
Expected: PASS, including `OrdinaryMessagesAreUntouched`.

- [ ] **Step 5: Commit**

```bash
git add src/Hearthsheet.Infrastructure/Logging/AppLogging.cs tests/Hearthsheet.Tests/UpdateTests.cs
git commit -m "Redact JWTs and refresh tokens from logs"
```

---

### Task 4: Supabase HTTP client and auth session

**Files:**
- Create: `src/Hearthsheet.Infrastructure/Sync/SupabaseHttp.cs`
- Create: `src/Hearthsheet.Infrastructure/Sync/SupabaseAuth.cs`
- Test: `tests/Hearthsheet.Tests/SupabaseTests.cs`

**Interfaces:**
- Consumes: `ICloudSession` and the cloud exceptions (Task 2), `ISecretStore`, and the test types `FakeSecrets` (in `UpdateTests.cs`) and `ManualTime` (in `CloudSyncTests.cs`).
- Produces (namespace `Hearthsheet.Infrastructure.Sync`):
  - `class SupabaseOptions { string Url; string AnonKey; double IntervalMinutes = 5; bool IsConfigured }`
  - `record SupabaseResponse(HttpStatusCode Status, string Body)`, with `bool IsSuccess` and `string ErrorMessage()`
  - `class SupabaseHttp(SupabaseOptions options, HttpMessageHandler? handler = null, string userAgentVersion = "1.0")`, with `Task<SupabaseResponse> SendAsync(HttpMethod method, string path, JsonNode? body, string? accessToken, CancellationToken ct, string? prefer = null)`
  - `class SupabaseAuth(SupabaseHttp http, ISecretStore secrets, TimeProvider? time = null) : ICloudSession`, with these members:
    - `const string SecretKey = "supabase-session"`
    - `string? Email`, `bool IsSignedIn`, `bool Restore()`
    - `SignInAsync(email, password, remember, ct)`, `SignUpAsync(email, password, remember, ct)`
    - `RequestPasswordResetAsync(email, ct)`, `ResetPasswordAsync(email, code, newPassword, remember, ct)`
    - `SignOutAsync(ct)`

- [ ] **Step 1: Write the failing tests**

Create `tests/Hearthsheet.Tests/SupabaseTests.cs`:

```csharp
using System.Net;
using System.Text;
using Hearthsheet.Core.Sync;
using Hearthsheet.Infrastructure.Sync;

namespace Hearthsheet.Tests;

internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Body, string? Bearer, string? ApiKey, string? Prefer)
{
    public string PathAndQuery => Uri.PathAndQuery;
}

internal sealed class SupabaseStub(Func<RecordedRequest, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<RecordedRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var recorded = new RecordedRequest(
            request.Method, request.RequestUri!,
            request.Content is null ? null : await request.Content.ReadAsStringAsync(ct),
            request.Headers.Authorization?.Parameter,
            request.Headers.TryGetValues("apikey", out var key) ? key.Single() : null,
            request.Headers.TryGetValues("Prefer", out var prefer) ? prefer.Single() : null);
        Requests.Add(recorded);
        return respond(recorded);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static string Session(string access, string refresh, int expiresIn = 3600) =>
        $$"""{"access_token":"{{access}}","token_type":"bearer","expires_in":{{expiresIn}},"refresh_token":"{{refresh}}","user":{"id":"user-1","email":"a@b.c"}}""";
}

public class SupabaseAuthTests
{
    internal static readonly SupabaseOptions Options = new() { Url = "https://proj.supabase.co", AnonKey = "anon-key" };
    private readonly FakeSecrets _secrets = new();
    private readonly ManualTime _time = new();

    private (SupabaseAuth Auth, SupabaseStub Stub) Create(Func<RecordedRequest, HttpResponseMessage> respond)
    {
        var stub = new SupabaseStub(respond);
        return (new SupabaseAuth(new SupabaseHttp(Options, stub), _secrets, _time), stub);
    }

    private static HttpResponseMessage Ok(string body) => SupabaseStub.Json(HttpStatusCode.OK, body);

    private async Task SignedInRemembered()
    {
        var (auth, _) = Create(_ => Ok(SupabaseStub.Session("a1", "r1")));
        await auth.SignInAsync("a@b.c", "password1", remember: true, default);
    }

    [Fact]
    public async Task SignIn_Remembered_StoresSession_AndSendsOnlyTheApiKey()
    {
        var (auth, stub) = Create(_ => Ok(SupabaseStub.Session("a1", "r1")));
        await auth.SignInAsync("a@b.c", "password1", remember: true, default);

        Assert.True(auth.IsSignedIn);
        Assert.Equal("user-1", auth.UserId);
        Assert.Equal("a@b.c", auth.Email);
        var request = Assert.Single(stub.Requests);
        Assert.Equal("/auth/v1/token?grant_type=password", request.PathAndQuery);
        Assert.Equal("anon-key", request.ApiKey);
        Assert.Null(request.Bearer);
        Assert.Contains("r1", _secrets[SupabaseAuth.SecretKey]);
        Assert.Equal("a1", await auth.GetAccessTokenAsync(default));
        Assert.Single(stub.Requests); // the fresh token is reused
    }

    [Fact]
    public async Task SignIn_NotRemembered_RemovesAnyStoredSession()
    {
        _secrets[SupabaseAuth.SecretKey] = "old";
        var (auth, _) = Create(_ => Ok(SupabaseStub.Session("a1", "r1")));
        await auth.SignInAsync("a@b.c", "password1", remember: false, default);
        Assert.False(_secrets.ContainsKey(SupabaseAuth.SecretKey));
    }

    [Fact]
    public async Task SignIn_BadCredentials_ShowsTheServerMessage()
    {
        var (auth, _) = Create(_ => SupabaseStub.Json(HttpStatusCode.BadRequest,
            """{"error":"invalid_grant","error_description":"Invalid login credentials"}"""));
        var ex = await Assert.ThrowsAsync<CloudRequestException>(() => auth.SignInAsync("a@b.c", "wrong", true, default));
        Assert.Equal("Invalid login credentials", ex.Message);
        Assert.False(auth.IsSignedIn);
    }

    [Fact]
    public async Task SignUp_WithoutASession_ExplainsEmailConfirmation()
    {
        var (auth, _) = Create(_ => Ok("""{"id":"user-1","email":"a@b.c"}"""));
        var ex = await Assert.ThrowsAsync<CloudRequestException>(() => auth.SignUpAsync("a@b.c", "password1", true, default));
        Assert.Contains("Confirm email", ex.Message);
    }

    [Fact]
    public async Task ExpiredAccessToken_IsRefreshed_AndTheRotatedTokenPersisted()
    {
        var (auth, stub) = Create(r => Ok(r.Uri.Query.Contains("refresh_token")
            ? SupabaseStub.Session("a2", "r2")
            : SupabaseStub.Session("a1", "r1")));
        await auth.SignInAsync("a@b.c", "password1", remember: true, default);
        _time.Now += TimeSpan.FromHours(1);

        Assert.Equal("a2", await auth.GetAccessTokenAsync(default));
        Assert.Contains("r1", stub.Requests[1].Body);
        Assert.Contains("r2", _secrets[SupabaseAuth.SecretKey]);
    }

    [Fact]
    public async Task Restore_LoadsTheStoredSessionWithoutTheNetwork()
    {
        await SignedInRemembered();
        var (auth, stub) = Create(_ => throw new InvalidOperationException("no network calls expected"));
        Assert.True(auth.Restore());
        Assert.Equal("user-1", auth.UserId);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public void Restore_WithNothingStored_StaysSignedOut()
    {
        var (auth, _) = Create(_ => throw new InvalidOperationException());
        Assert.False(auth.Restore());
        Assert.False(auth.IsSignedIn);
    }

    [Fact]
    public async Task RevokedRefreshToken_SignsOut_AndForgetsTheSession()
    {
        await SignedInRemembered();
        var (auth, _) = Create(_ => SupabaseStub.Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}"""));
        auth.Restore();
        await Assert.ThrowsAsync<CloudSignedOutException>(() => auth.GetAccessTokenAsync(default));
        Assert.False(auth.IsSignedIn);
        Assert.False(_secrets.ContainsKey(SupabaseAuth.SecretKey));
    }

    [Fact]
    public async Task NetworkFailure_KeepsTheSession_AndReportsOffline()
    {
        await SignedInRemembered();
        var (auth, _) = Create(_ => throw new HttpRequestException("no route"));
        auth.Restore();
        var ex = await Assert.ThrowsAsync<CloudUnavailableException>(() => auth.GetAccessTokenAsync(default));
        Assert.True(ex.Offline);
        Assert.True(auth.IsSignedIn);
        Assert.True(_secrets.ContainsKey(SupabaseAuth.SecretKey));
    }

    [Fact]
    public async Task ServerError_IsUnavailable_NotOffline()
    {
        var (auth, _) = Create(_ => SupabaseStub.Json(HttpStatusCode.ServiceUnavailable, "{}"));
        var ex = await Assert.ThrowsAsync<CloudUnavailableException>(() => auth.SignInAsync("a@b.c", "password1", true, default));
        Assert.False(ex.Offline);
    }

    [Fact]
    public async Task PasswordReset_VerifiesTheCode_ThenSetsThePassword()
    {
        var (auth, stub) = Create(r => Ok(r.PathAndQuery == "/auth/v1/verify" ? SupabaseStub.Session("a1", "r1") : "{}"));
        await auth.RequestPasswordResetAsync("a@b.c", default);
        await auth.ResetPasswordAsync("a@b.c", "123456", "newpassword1", remember: true, default);

        Assert.Equal(new[] { "/auth/v1/recover", "/auth/v1/verify", "/auth/v1/user" }, stub.Requests.Select(r => r.PathAndQuery));
        Assert.Contains("recovery", stub.Requests[1].Body);
        Assert.Contains("123456", stub.Requests[1].Body);
        Assert.Equal(HttpMethod.Put, stub.Requests[2].Method);
        Assert.Equal("a1", stub.Requests[2].Bearer);
        Assert.True(auth.IsSignedIn);
    }

    [Fact]
    public async Task SignOut_RevokesTheSession_AndForgetsIt()
    {
        var (auth, stub) = Create(r => r.PathAndQuery == "/auth/v1/logout"
            ? new HttpResponseMessage(HttpStatusCode.NoContent)
            : Ok(SupabaseStub.Session("a1", "r1")));
        await auth.SignInAsync("a@b.c", "password1", remember: true, default);
        await auth.SignOutAsync(default);

        Assert.Equal("a1", stub.Requests.Last().Bearer);
        Assert.False(auth.IsSignedIn);
        Assert.Null(auth.UserId);
        Assert.False(_secrets.ContainsKey(SupabaseAuth.SecretKey));
    }

    [Theory]
    [InlineData("http://proj.supabase.co", "key")]
    [InlineData("https://proj.supabase.co", "")]
    [InlineData("", "key")]
    public void InvalidOptions_AreRejected(string url, string key) =>
        Assert.Throws<ArgumentException>(() => new SupabaseHttp(new SupabaseOptions { Url = url, AnonKey = key }));

    [Theory]
    [InlineData("https://evil.example/x")]
    [InlineData("//evil.example/x")]
    [InlineData("/auth/v1/token")]
    public async Task OnlyRelativePathsOnTheConfiguredHost_AreAllowed(string path)
    {
        var http = new SupabaseHttp(Options, new SupabaseStub(_ => Ok("{}")));
        await Assert.ThrowsAsync<ArgumentException>(() => http.SendAsync(HttpMethod.Get, path, null, null, default));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Hearthsheet.slnx --filter FullyQualifiedName~SupabaseAuthTests`
Expected: a build failure (`The type or namespace name 'Sync' does not exist in the namespace 'Hearthsheet.Infrastructure'`).

- [ ] **Step 3: Implement `SupabaseHttp`**

Create `src/Hearthsheet.Infrastructure/Sync/SupabaseHttp.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hearthsheet.Core.Sync;

namespace Hearthsheet.Infrastructure.Sync;

public sealed class SupabaseOptions
{
    /// <summary>Project URL, e.g. https://abcd.supabase.co. Public by design.</summary>
    public string Url { get; set; } = "";
    /// <summary>The anon or publishable key. Public by design: row-level security is the gatekeeper.</summary>
    public string AnonKey { get; set; } = "";
    public double IntervalMinutes { get; set; } = 5;

    public bool IsConfigured =>
        Uri.TryCreate(Url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && !string.IsNullOrWhiteSpace(AnonKey);
}

public sealed record SupabaseResponse(HttpStatusCode Status, string Body)
{
    public bool IsSuccess => (int)Status is >= 200 and < 300;

    /// <summary>The service's own error text (Auth and PostgREST use different field names).</summary>
    public string ErrorMessage()
    {
        try
        {
            if (JsonNode.Parse(Body) is JsonObject json)
            {
                foreach (var key in new[] { "error_description", "msg", "message", "error" })
                    if (json[key] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0) return text;
            }
        }
        catch (JsonException)
        {
        }
        return $"The cloud service returned {(int)Status}.";
    }
}

/// <summary>
/// The one HTTP client for Supabase. Only relative paths on the configured HTTPS host are accepted. Network
/// failures, timeouts and 5xx responses (including a paused free-tier project) become
/// <see cref="CloudUnavailableException"/>; everything else is returned for the caller to interpret.
/// </summary>
public sealed class SupabaseHttp
{
    private readonly HttpClient _http;
    private readonly string _apiKey;

    public SupabaseHttp(SupabaseOptions options, HttpMessageHandler? handler = null, string userAgentVersion = "1.0")
    {
        if (!options.IsConfigured)
            throw new ArgumentException("Sync:Url must be an https URL and Sync:AnonKey must be set.", nameof(options));
        _apiKey = options.AnonKey;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri(options.Url.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(20),
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Hearthsheet", userAgentVersion));
    }

    /// <param name="path">Relative to the project URL, e.g. <c>auth/v1/token?grant_type=password</c>.</param>
    /// <param name="accessToken">The user's access token; requests without a session send only the api key.</param>
    public async Task<SupabaseResponse> SendAsync(
        HttpMethod method, string path, JsonNode? body, string? accessToken, CancellationToken ct, string? prefer = null)
    {
        if (path.StartsWith('/') || !Uri.TryCreate(path, UriKind.Relative, out var relative))
            throw new ArgumentException("Supabase paths must be relative to the project URL.", nameof(path));

        using var request = new HttpRequestMessage(method, relative);
        request.Headers.Add("apikey", _apiKey);
        if (accessToken is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (prefer is not null) request.Headers.Add("Prefer", prefer);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        try
        {
            using var response = await _http.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if ((int)response.StatusCode >= 500)
                throw new CloudUnavailableException($"The cloud service returned {(int)response.StatusCode}.", offline: false);
            return new SupabaseResponse(response.StatusCode, text);
        }
        catch (HttpRequestException ex)
        {
            throw new CloudUnavailableException("The cloud service could not be reached.", offline: true, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new CloudUnavailableException("The cloud service did not respond in time.", offline: false, ex);
        }
    }
}
```

- [ ] **Step 4: Implement `SupabaseAuth`**

Create `src/Hearthsheet.Infrastructure/Sync/SupabaseAuth.cs`:

```csharp
using System.ComponentModel;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hearthsheet.Core.Sync;
using Hearthsheet.Infrastructure.Security;

namespace Hearthsheet.Infrastructure.Sync;

/// <summary>
/// The signed-in Supabase Auth session. The access token lives only in memory. The refresh token (plus user id
/// and email, so an offline start knows the account) is kept in the OS secret store when "Stay signed in" is
/// chosen, and rewritten on every refresh because Supabase rotates it.
/// </summary>
public sealed class SupabaseAuth(SupabaseHttp http, ISecretStore secrets, TimeProvider? time = null) : ICloudSession
{
    public const string SecretKey = "supabase-session";
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(60);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _lock = new(1, 1); // serializes refreshes so token rotation can't race
    private string? _accessToken;
    private DateTimeOffset _expiresAt;
    private string? _refreshToken;
    private bool _remember;

    public string? UserId { get; private set; }
    public string? Email { get; private set; }
    public bool IsSignedIn => _refreshToken is not null;

    /// <summary>Loads a remembered session without the network. The first token request refreshes it.</summary>
    public bool Restore()
    {
        SavedSession? saved;
        try
        {
            saved = secrets.Read(SecretKey) is { } json ? JsonSerializer.Deserialize<SavedSession>(json) : null;
        }
        catch (Exception ex) when (ex is JsonException or Win32Exception)
        {
            saved = null;
        }
        if (saved is not { RefreshToken.Length: > 0, UserId.Length: > 0 }) return false;
        (_refreshToken, UserId, Email, _remember, _accessToken) = (saved.RefreshToken, saved.UserId, saved.Email, true, null);
        return true;
    }

    public Task SignInAsync(string email, string password, bool remember, CancellationToken ct) =>
        StartSessionAsync("auth/v1/token?grant_type=password", new JsonObject { ["email"] = email, ["password"] = password }, remember, ct);

    public Task SignUpAsync(string email, string password, bool remember, CancellationToken ct) =>
        StartSessionAsync("auth/v1/signup", new JsonObject { ["email"] = email, ["password"] = password }, remember, ct);

    /// <summary>Emails a one-time code (the recovery template shows <c>{{ .Token }}</c>).</summary>
    public async Task RequestPasswordResetAsync(string email, CancellationToken ct)
    {
        var response = await http.SendAsync(HttpMethod.Post, "auth/v1/recover", new JsonObject { ["email"] = email }, null, ct);
        if (!response.IsSuccess) throw new CloudRequestException(response.ErrorMessage());
    }

    /// <summary>Verifying the code signs the user in; the new password is then set on that session.</summary>
    public async Task ResetPasswordAsync(string email, string code, string newPassword, bool remember, CancellationToken ct)
    {
        await StartSessionAsync("auth/v1/verify",
            new JsonObject { ["type"] = "recovery", ["email"] = email, ["token"] = code }, remember, ct);
        var response = await http.SendAsync(HttpMethod.Put, "auth/v1/user",
            new JsonObject { ["password"] = newPassword }, await GetAccessTokenAsync(ct), ct);
        if (!response.IsSuccess) throw new CloudRequestException(response.ErrorMessage());
    }

    public async Task SignOutAsync(CancellationToken ct)
    {
        var token = _accessToken;
        Forget();
        if (token is null) return;
        try
        {
            await http.SendAsync(HttpMethod.Post, "auth/v1/logout", null, token, ct);
        }
        catch (CloudException)
        {
            // Best effort: the local session is already gone.
        }
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_refreshToken is null) throw new CloudSignedOutException("Not signed in.");
            if (_accessToken is not null && _time.GetUtcNow() < _expiresAt - RefreshMargin) return _accessToken;

            var response = await http.SendAsync(HttpMethod.Post, "auth/v1/token?grant_type=refresh_token",
                new JsonObject { ["refresh_token"] = _refreshToken }, null, ct);
            if (response.Status is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            {
                Forget();
                throw new CloudSignedOutException("Your session has ended. Sign in to resume sync.");
            }
            if (!response.IsSuccess) throw new CloudRequestException(response.ErrorMessage());
            return Accept(response.Body);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void InvalidateAccessToken() => _accessToken = null;

    private async Task StartSessionAsync(string path, JsonObject body, bool remember, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var response = await http.SendAsync(HttpMethod.Post, path, body, null, ct);
            if (!response.IsSuccess) throw new CloudRequestException(response.ErrorMessage());
            _remember = remember;
            Accept(response.Body);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Takes a session response; returns the new access token.</summary>
    private string Accept(string body)
    {
        string access, refresh, userId;
        string? email;
        double expiresIn;
        try
        {
            var json = JsonNode.Parse(body);
            access = json?["access_token"]?.GetValue<string>() ?? throw new CloudRequestException(
                "The sign-in service did not return a session. If you just created an account, turn off “Confirm email” in Supabase (see docs/CLOUD_SYNC.md).");
            refresh = json!["refresh_token"]?.GetValue<string>() ?? throw Unexpected(null);
            userId = json!["user"]?["id"]?.GetValue<string>() ?? throw Unexpected(null);
            email = json!["user"]?["email"]?.GetValue<string>();
            expiresIn = json!["expires_in"]?.GetValue<double>() ?? 3600;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw Unexpected(ex);
        }

        (_accessToken, _refreshToken, UserId, Email) = (access, refresh, userId, email);
        _expiresAt = _time.GetUtcNow().AddSeconds(expiresIn);
        Persist();
        return access;
    }

    private static CloudRequestException Unexpected(Exception? inner) =>
        new("The sign-in service returned an unexpected response.", inner);

    private void Persist()
    {
        try
        {
            if (_remember && _refreshToken is not null && UserId is not null)
                secrets.Write(SecretKey, JsonSerializer.Serialize(new SavedSession(_refreshToken, UserId, Email)));
            else
                secrets.Delete(SecretKey);
        }
        catch (Win32Exception)
        {
            // Failing to remember only means signing in again next launch.
        }
    }

    private void Forget()
    {
        (_accessToken, _refreshToken, UserId, Email) = (null, null, null, null);
        try
        {
            secrets.Delete(SecretKey);
        }
        catch (Win32Exception)
        {
        }
    }

    private sealed record SavedSession(string RefreshToken, string UserId, string? Email);
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test Hearthsheet.slnx --filter FullyQualifiedName~SupabaseAuthTests`
Expected: PASS (18 cases).

- [ ] **Step 6: Commit**

```bash
git add src/Hearthsheet.Infrastructure/Sync tests/Hearthsheet.Tests/SupabaseTests.cs
git commit -m "Add Supabase auth with stay-signed-in sessions"
```

---

### Task 5: Supabase character store

**Files:**
- Create: `src/Hearthsheet.Infrastructure/Sync/SupabaseCharacterStore.cs`
- Test: `tests/Hearthsheet.Tests/SupabaseTests.cs` (append a class)

**Interfaces:**
- Consumes: `SupabaseHttp`, `SupabaseResponse` (Task 4), `ICloudCharacterStore`, `ICloudSession`, `CloudRow`, `CloudUpload` (Task 2), and the test types `SupabaseStub` (Task 4) and `FakeSession` (Task 2).
- Produces: `class SupabaseCharacterStore(SupabaseHttp http, ICloudSession session) : ICloudCharacterStore`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Hearthsheet.Tests/SupabaseTests.cs`:

```csharp
public class SupabaseCharacterStoreTests
{
    private static readonly CloudUpload Upload = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "A", "Elf", 1, """{"identity":{"name":"A"}}""");

    private static string RowJson(Guid id, long revision, string? data = """{"identity":{"name":"A"}}""", string? deletedAt = null) =>
        $$"""{"id":"{{id}}","name":"A","revision":{{revision}},"schema_version":1,"data":{{data ?? "null"}},"deleted_at":{{(deletedAt is null ? "null" : $"\"{deletedAt}\"")}},"updated_at":"2026-09-29T10:00:00.123456+00:00"}""";

    private static (SupabaseCharacterStore Store, SupabaseStub Stub, FakeSession Session) Create(Func<RecordedRequest, HttpResponseMessage> respond)
    {
        var stub = new SupabaseStub(respond);
        var session = new FakeSession();
        return (new SupabaseCharacterStore(new SupabaseHttp(SupabaseAuthTests.Options, stub), session), stub, session);
    }

    private static HttpResponseMessage Ok(string body) => SupabaseStub.Json(HttpStatusCode.OK, body);

    [Fact]
    public async Task Update_FiltersOnRevision_AndReturnsTheNewRow()
    {
        var (store, stub, _) = Create(_ => Ok($"[{RowJson(Upload.Id, 4)}]"));
        var row = await store.UpdateIfRevisionAsync(Upload.Id, 3, Upload, default);

        var request = Assert.Single(stub.Requests);
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.Contains($"id=eq.{Upload.Id}", request.PathAndQuery);
        Assert.Contains("revision=eq.3", request.PathAndQuery);
        Assert.Equal("token", request.Bearer);
        Assert.Equal("anon-key", request.ApiKey);
        Assert.Equal("return=representation", request.Prefer);
        Assert.Contains("\"deleted_at\":null", request.Body);
        Assert.Contains("\"identity\":{\"name\":\"A\"}", request.Body); // data is sent as JSON, not a string
        Assert.Equal(4, row!.Revision);
        Assert.Contains("identity", row.Data);
    }

    [Fact]
    public async Task Update_RevisionMismatch_ReturnsNull()
    {
        var (store, _, _) = Create(_ => Ok("[]"));
        Assert.Null(await store.UpdateIfRevisionAsync(Upload.Id, 3, Upload, default));
    }

    [Fact]
    public async Task Insert_ExistingId_ReturnsNull()
    {
        var (store, _, _) = Create(_ => SupabaseStub.Json(HttpStatusCode.Conflict, """{"message":"duplicate key"}"""));
        Assert.Null(await store.InsertAsync(Upload, default));
    }

    [Fact]
    public async Task Tombstone_ReportsWhetherARowMatched()
    {
        var (store, stub, _) = Create(r => Ok(r.PathAndQuery.Contains("revision=eq.1") ? $$"""[{"id":"{{Upload.Id}}"}]""" : "[]"));
        Assert.True(await store.TombstoneIfRevisionAsync(Upload.Id, 1, default));
        Assert.False(await store.TombstoneIfRevisionAsync(Upload.Id, 2, default));
        Assert.Contains("\"data\":null", stub.Requests[0].Body);
    }

    [Fact]
    public async Task Get_ParsesTombstones()
    {
        var (store, _, _) = Create(_ => Ok($"[{RowJson(Upload.Id, 2, data: null, deletedAt: "2026-09-29T10:00:00+00:00")}]"));
        var row = await store.GetAsync(Upload.Id, default);
        Assert.True(row!.IsDeleted);
        Assert.Equal(2, row.Revision);
    }

    [Fact]
    public async Task ChangedSince_PagesThroughAllRows()
    {
        var page = "[" + string.Join(",", Enumerable.Range(0, 500).Select(_ => RowJson(Guid.NewGuid(), 1))) + "]";
        var (store, stub, _) = Create(r => Ok(r.PathAndQuery.Contains("offset=0") ? page : $"[{RowJson(Guid.NewGuid(), 1)}]"));

        var rows = await store.ChangedSinceAsync(new DateTimeOffset(2026, 9, 29, 9, 0, 0, TimeSpan.Zero), default);
        Assert.Equal(501, rows.Count);
        Assert.Equal(2, stub.Requests.Count);
        Assert.Contains("updated_at=gt.", stub.Requests[0].PathAndQuery);
        Assert.Contains("order=updated_at.asc", stub.Requests[0].PathAndQuery);
    }

    [Fact]
    public async Task Unauthorized_RefreshesOnce_ThenRetries()
    {
        var calls = 0;
        var (store, stub, session) = Create(_ => ++calls == 1 ? SupabaseStub.Json(HttpStatusCode.Unauthorized, "{}") : Ok("[]"));
        Assert.Null(await store.GetAsync(Upload.Id, default));
        Assert.Equal(2, stub.Requests.Count);
        Assert.Equal(1, session.Invalidations);
    }

    [Fact]
    public async Task UnauthorizedTwice_SignsOut()
    {
        var (store, _, _) = Create(_ => SupabaseStub.Json(HttpStatusCode.Unauthorized, "{}"));
        await Assert.ThrowsAsync<CloudSignedOutException>(() => store.GetAsync(Upload.Id, default));
    }

    [Fact]
    public async Task MalformedResponse_IsARequestError()
    {
        var (store, _, _) = Create(_ => Ok("{not json"));
        await Assert.ThrowsAsync<CloudRequestException>(() => store.GetAsync(Upload.Id, default));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Hearthsheet.slnx --filter FullyQualifiedName~SupabaseCharacterStoreTests`
Expected: a build failure (`The type or namespace name 'SupabaseCharacterStore' could not be found`).

- [ ] **Step 3: Implement the store**

Create `src/Hearthsheet.Infrastructure/Sync/SupabaseCharacterStore.cs`:

```csharp
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hearthsheet.Core.Sync;

namespace Hearthsheet.Infrastructure.Sync;

/// <summary>
/// The <c>public.characters</c> table through PostgREST. Row-level security limits every call to the
/// signed-in user's rows; the server trigger owns <c>revision</c>, <c>updated_at</c> and <c>user_id</c>.
/// </summary>
public sealed class SupabaseCharacterStore(SupabaseHttp http, ICloudSession session) : ICloudCharacterStore
{
    private const string Table = "rest/v1/characters";
    private const string Columns = "select=id,name,revision,schema_version,data,deleted_at,updated_at";
    private const int PageSize = 500;

    public async Task<CloudRow?> InsertAsync(CloudUpload upload, CancellationToken ct)
    {
        var response = await SendAsync(HttpMethod.Post, $"{Table}?{Columns}", Body(upload), ct);
        return response.Status == HttpStatusCode.Conflict ? null : Rows(response).SingleOrDefault();
    }

    public async Task<CloudRow?> UpdateIfRevisionAsync(Guid id, long expectedRevision, CloudUpload upload, CancellationToken ct)
    {
        var body = Body(upload);
        body["deleted_at"] = null;
        var response = await SendAsync(HttpMethod.Patch, $"{Table}?id=eq.{id}&revision=eq.{expectedRevision}&{Columns}", body, ct);
        return Rows(response).SingleOrDefault();
    }

    public async Task<bool> TombstoneIfRevisionAsync(Guid id, long expectedRevision, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["data"] = null,
            ["deleted_at"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };
        var response = await SendAsync(HttpMethod.Patch, $"{Table}?id=eq.{id}&revision=eq.{expectedRevision}&select=id", body, ct);
        return Ids(response).Count > 0;
    }

    public async Task<CloudRow?> GetAsync(Guid id, CancellationToken ct) =>
        Rows(await SendAsync(HttpMethod.Get, $"{Table}?id=eq.{id}&{Columns}", null, ct)).SingleOrDefault();

    public async Task<IReadOnlyList<CloudRow>> ChangedSinceAsync(DateTimeOffset? since, CancellationToken ct)
    {
        var filter = since is { } s
            ? "&updated_at=gt." + Uri.EscapeDataString(s.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", CultureInfo.InvariantCulture))
            : "";
        var rows = new List<CloudRow>();
        for (var offset = 0; ; offset += PageSize)
        {
            var page = Rows(await SendAsync(HttpMethod.Get,
                $"{Table}?{Columns}{filter}&order=updated_at.asc,id.asc&limit={PageSize}&offset={offset}", null, ct));
            rows.AddRange(page);
            if (page.Count < PageSize) return rows;
        }
    }

    public async Task<IReadOnlySet<Guid>> ListAllIdsAsync(CancellationToken ct)
    {
        var ids = new HashSet<Guid>();
        for (var offset = 0; ; offset += PageSize)
        {
            var page = Ids(await SendAsync(HttpMethod.Get, $"{Table}?select=id&order=id.asc&limit={PageSize}&offset={offset}", null, ct));
            ids.UnionWith(page);
            if (page.Count < PageSize) return ids;
        }
    }

    /// <summary>Sends with the user's token; after a 401 refreshes once and retries.</summary>
    private async Task<SupabaseResponse> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        var response = await http.SendAsync(method, path, body, await session.GetAccessTokenAsync(ct), ct, "return=representation");
        if (response.Status != HttpStatusCode.Unauthorized) return response;
        session.InvalidateAccessToken();
        response = await http.SendAsync(method, path, body, await session.GetAccessTokenAsync(ct), ct, "return=representation");
        if (response.Status == HttpStatusCode.Unauthorized)
            throw new CloudSignedOutException("The cloud service rejected your session. Sign in again.");
        return response;
    }

    private static JsonObject Body(CloudUpload upload) => new()
    {
        ["id"] = upload.Id.ToString(),
        ["name"] = upload.Name,
        ["description"] = upload.Description,
        ["schema_version"] = upload.SchemaVersion,
        ["data"] = JsonNode.Parse(upload.Data),
    };

    private static List<CloudRow> Rows(SupabaseResponse response) => Parse(response, e => new CloudRow(
        e.GetProperty("id").GetGuid(),
        e.GetProperty("name").GetString() ?? "",
        e.GetProperty("revision").GetInt64(),
        e.GetProperty("schema_version").GetInt32(),
        e.GetProperty("data") is { ValueKind: not JsonValueKind.Null } data ? data.GetRawText() : null,
        e.GetProperty("deleted_at") is { ValueKind: JsonValueKind.String } deleted ? deleted.GetDateTimeOffset() : null,
        e.GetProperty("updated_at").GetDateTimeOffset()));

    private static List<Guid> Ids(SupabaseResponse response) => Parse(response, e => e.GetProperty("id").GetGuid());

    private static List<T> Parse<T>(SupabaseResponse response, Func<JsonElement, T> read)
    {
        if (!response.IsSuccess) throw new CloudRequestException(response.ErrorMessage());
        try
        {
            using var document = JsonDocument.Parse(response.Body);
            return document.RootElement.EnumerateArray().Select(read).ToList();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new CloudRequestException("The cloud service returned an unexpected response.", ex);
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Hearthsheet.slnx`
Expected: all tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Hearthsheet.Infrastructure/Sync/SupabaseCharacterStore.cs tests/Hearthsheet.Tests/SupabaseTests.cs
git commit -m "Add the Supabase character store"
```

---

### Task 6: Account dialog

**Files:**
- Create: `src/Hearthsheet.App/Views/AccountDialog.xaml`
- Create: `src/Hearthsheet.App/Views/AccountDialog.xaml.cs`
- Modify: `src/Hearthsheet.App/Dialogs.cs`

**Interfaces:**
- Consumes: `SupabaseAuth` (Task 4) and `CloudException` (Task 2).
- Produces: `Dialogs.SignIn(SupabaseAuth auth) : bool` (true when signed in) and `Dialogs.AccountSwitch() : bool?` (true = upload, false = keep local, null = cancel).

There are no unit tests: the test project doesn't reference the WPF app. Verification is a build plus the manual check in Task 7.

- [ ] **Step 1: Create the dialog XAML**

Create `src/Hearthsheet.App/Views/AccountDialog.xaml`:

```xml
<Window x:Class="Hearthsheet.App.Views.AccountDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Cloud sync account" Style="{StaticResource AppWindow}" Width="420" SizeToContent="Height"
        ResizeMode="NoResize" WindowStartupLocation="CenterOwner" ShowInTaskbar="False">
    <StackPanel Margin="22,18">
        <TextBlock x:Name="Heading" Style="{StaticResource SectionTitle}" Text="Sign in" />
        <TextBlock Style="{StaticResource Hint}"
                   Text="Sync your characters between computers. They always stay on this computer too, and everything keeps working offline." />
        <HeaderedContentControl Header="Email" Style="{StaticResource Field}" Margin="0,0,0,10">
            <TextBox x:Name="EmailBox" Padding="4,3" AutomationProperties.Name="Email" />
        </HeaderedContentControl>
        <HeaderedContentControl x:Name="CodeField" Header="Code from the email" Style="{StaticResource Field}" Margin="0,0,0,10">
            <TextBox x:Name="CodeBox" Padding="4,3" MaxLength="10" AutomationProperties.Name="Reset code" />
        </HeaderedContentControl>
        <HeaderedContentControl x:Name="PasswordField" Header="Password" Style="{StaticResource Field}" Margin="0,0,0,10">
            <PasswordBox x:Name="PasswordBox" Padding="4,3" AutomationProperties.Name="Password" />
        </HeaderedContentControl>
        <HeaderedContentControl x:Name="ConfirmField" Header="Confirm password" Style="{StaticResource Field}" Margin="0,0,0,10">
            <PasswordBox x:Name="ConfirmBox" Padding="4,3" AutomationProperties.Name="Confirm password" />
        </HeaderedContentControl>
        <CheckBox x:Name="RememberBox" Content="Stay signed in" IsChecked="True" Margin="0,0,0,10" />
        <TextBlock x:Name="Feedback" TextWrapping="Wrap" Margin="0,0,0,10" Foreground="{StaticResource Accent}"
                   AutomationProperties.LiveSetting="Assertive" />
        <DockPanel>
            <StackPanel DockPanel.Dock="Left" VerticalAlignment="Center">
                <TextBlock x:Name="CreateLink"><Hyperlink Click="OnShowCreate">Create account</Hyperlink></TextBlock>
                <TextBlock x:Name="ForgotLink"><Hyperlink Click="OnShowForgot">Forgot password?</Hyperlink></TextBlock>
                <TextBlock x:Name="BackLink"><Hyperlink Click="OnShowSignIn">Back to sign in</Hyperlink></TextBlock>
            </StackPanel>
            <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
                <Button x:Name="PrimaryButton" Style="{StaticResource AccentButton}" IsDefault="True" Click="OnPrimary" Margin="0,0,8,0" />
                <Button Content="Cancel" IsCancel="True" />
            </StackPanel>
        </DockPanel>
    </StackPanel>
</Window>
```

- [ ] **Step 2: Create the code-behind**

Create `src/Hearthsheet.App/Views/AccountDialog.xaml.cs`:

```csharp
using System.Windows;
using Hearthsheet.Core.Sync;
using Hearthsheet.Infrastructure.Sync;

namespace Hearthsheet.App.Views;

/// <summary>Sign in, create an account, or reset a password with an emailed code. Passwords are never stored.</summary>
public partial class AccountDialog
{
    private enum Mode { SignIn, Create, RequestCode, Reset }

    private readonly SupabaseAuth _auth;
    private Mode _mode;

    public AccountDialog(SupabaseAuth auth)
    {
        InitializeComponent();
        _auth = auth;
        Show(Mode.SignIn);
        Loaded += (_, _) => EmailBox.Focus();
    }

    private void OnShowCreate(object sender, RoutedEventArgs e) => Show(Mode.Create);
    private void OnShowForgot(object sender, RoutedEventArgs e) => Show(Mode.RequestCode);
    private void OnShowSignIn(object sender, RoutedEventArgs e) => Show(Mode.SignIn);

    private void Show(Mode mode)
    {
        _mode = mode;
        (Heading.Text, PrimaryButton.Content) = mode switch
        {
            Mode.SignIn => ("Sign in", "Sign in"),
            Mode.Create => ("Create account", "Create account"),
            Mode.RequestCode => ("Reset password", "Send code"),
            _ => ("Reset password", "Reset password"),
        };
        CodeField.Visibility = Visible(mode == Mode.Reset);
        PasswordField.Visibility = Visible(mode != Mode.RequestCode);
        PasswordField.Header = mode == Mode.Reset ? "New password" : "Password";
        ConfirmField.Visibility = Visible(mode is Mode.Create or Mode.Reset);
        RememberBox.Visibility = Visible(mode != Mode.RequestCode);
        CreateLink.Visibility = Visible(mode == Mode.SignIn);
        ForgotLink.Visibility = Visible(mode == Mode.SignIn);
        BackLink.Visibility = Visible(mode != Mode.SignIn);
        Feedback.Text = "";
    }

    private static Visibility Visible(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private async void OnPrimary(object sender, RoutedEventArgs e)
    {
        var email = EmailBox.Text.Trim();
        if (Validate(email) is { } problem)
        {
            Feedback.Text = problem;
            return;
        }
        IsEnabled = false;
        Feedback.Text = "Working…";
        try
        {
            var remember = RememberBox.IsChecked == true;
            switch (_mode)
            {
                case Mode.SignIn:
                    await _auth.SignInAsync(email, PasswordBox.Password, remember, CancellationToken.None);
                    break;
                case Mode.Create:
                    await _auth.SignUpAsync(email, PasswordBox.Password, remember, CancellationToken.None);
                    break;
                case Mode.RequestCode:
                    await _auth.RequestPasswordResetAsync(email, CancellationToken.None);
                    Show(Mode.Reset);
                    Feedback.Text = "If an account exists for that email, a code is on its way.";
                    return;
                case Mode.Reset:
                    await _auth.ResetPasswordAsync(email, CodeBox.Text.Trim(), PasswordBox.Password, remember, CancellationToken.None);
                    break;
            }
            DialogResult = true;
        }
        catch (CloudException ex)
        {
            Feedback.Text = ex.Message;
        }
        finally
        {
            PasswordBox.Clear();
            ConfirmBox.Clear();
            IsEnabled = true;
        }
    }

    private string? Validate(string email)
    {
        if (!email.Contains('@')) return "Enter your email address.";
        if (_mode == Mode.RequestCode) return null;
        if (_mode == Mode.Reset && CodeBox.Text.Trim().Length == 0) return "Enter the code from the email.";
        if (PasswordBox.Password.Length == 0) return "Enter your password.";
        if (_mode is Mode.Create or Mode.Reset)
        {
            if (PasswordBox.Password.Length < 8) return "Use at least 8 characters.";
            if (PasswordBox.Password != ConfirmBox.Password) return "The passwords don't match.";
        }
        return null;
    }
}
```

- [ ] **Step 3: Add the dialog entry points**

In `src/Hearthsheet.App/Dialogs.cs`, add `using Hearthsheet.Infrastructure.Sync;` and, after `ManageGitHubToken`:

```csharp
    /// <returns>True when the user signed in (or created an account, or reset their password).</returns>
    public bool SignIn(SupabaseAuth auth) =>
        new AccountDialog(auth) { Owner = Owner }.ShowDialog() == true;

    /// <returns>True to upload this computer's characters, false to keep them local only, null to cancel (sign out).</returns>
    public bool? AccountSwitch() =>
        Show("This computer's characters were synced with a different account.\n\n" +
             "Yes — upload them to this account\n" +
             "No — keep them on this computer only\n" +
             "Cancel — sign out\n\n" +
             "Nothing is deleted either way.",
             "Different account", MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch
        {
            MessageBoxResult.Yes => true,
            MessageBoxResult.No => false,
            _ => null,
        };
```

- [ ] **Step 4: Build**

Run: `dotnet build Hearthsheet.slnx`
Expected: Build succeeded, 0 errors, 0 new warnings.

- [ ] **Step 5: Commit**

```bash
git add src/Hearthsheet.App/Views/AccountDialog.xaml src/Hearthsheet.App/Views/AccountDialog.xaml.cs src/Hearthsheet.App/Dialogs.cs
git commit -m "Add the cloud sync account dialog"
```

---

### Task 7: Wire sync into the app

**Files:**
- Create: `src/Hearthsheet.App/ViewModels/SyncViewModel.cs`
- Modify: `src/Hearthsheet.App/Composition/AppSettings.cs`, `src/Hearthsheet.App/appsettings.json`, `src/Hearthsheet.App/App.xaml.cs`
- Modify: `src/Hearthsheet.App/ViewModels/MainViewModel.cs`
- Modify: `src/Hearthsheet.App/MainWindow.xaml`, `src/Hearthsheet.App/MainWindow.xaml.cs`

**Interfaces:**
- Consumes: everything from Tasks 1–6.
- Produces:
  - `record CloudServices(SupabaseAuth Auth, CloudSync Sync, SupabaseOptions Options)`
  - `SyncViewModel`, with:
    - properties `IsConfigured`, `IsSignedIn`, `AccountLabel`, `IsBusy`, `Status`
    - commands `SignIn`, `SignOut`, `SyncNow`
    - methods `StartAsync()`, `RunAsync(bool final = false)`, `FinalSyncAsync()`
  - `MainViewModel`, with `Sync`, `OpenCharacterId`, `ApplySyncResult(SyncResult)` and `WithCharacterClosed(Action) : bool`
  - `AppSettings.Sync : SupabaseOptions`

- [ ] **Step 1: Add the settings**

In `AppSettings.cs`, add `using Hearthsheet.Infrastructure.Sync;` and change the record to:

```csharp
public sealed record AppSettings(ApplicationSettings Application, LoggingSettings Logging, UpdateOptions Updates, SupabaseOptions Sync)
```

and in `Load`, change the `return new AppSettings(...)` to:

```csharp
        return new AppSettings(
            config.GetSection("Application").Get<ApplicationSettings>() ?? new(),
            config.GetSection("Logging").Get<LoggingSettings>() ?? new(),
            config.GetSection("Updates").Get<UpdateOptions>() ?? new(),
            config.GetSection("Sync").Get<SupabaseOptions>() ?? new());
```

Run `grep -rn "new AppSettings(" src tests`. Only `AppSettings.Load` should construct it. Fix any other hit by passing `new SupabaseOptions()`.

In `src/Hearthsheet.App/appsettings.json`, add after the `Updates` block (the values are filled in during Task 8):

```json
  "Sync": {
    "Url": "",
    "AnonKey": "",
    "IntervalMinutes": 5
  }
```

(Add the comma after the `Updates` block's closing brace.)

- [ ] **Step 2: Create `SyncViewModel`**

Create `src/Hearthsheet.App/ViewModels/SyncViewModel.cs`:

```csharp
using System.Windows.Input;
using System.Windows.Threading;
using Hearthsheet.App.Mvvm;
using Hearthsheet.Core.Domain;
using Hearthsheet.Core.Sync;
using Hearthsheet.Infrastructure.Sync;
using Microsoft.Extensions.Logging;

namespace Hearthsheet.App.ViewModels;

public sealed record CloudServices(SupabaseAuth Auth, CloudSync Sync, SupabaseOptions Options);

/// <summary>
/// Account state, the status-bar line and when sync passes run: startup, sign-in, every few minutes, "Sync now"
/// and closing. Never after individual saves. Passes run off the UI thread; results are applied back on it.
/// </summary>
public sealed class SyncViewModel : Observable
{
    private static readonly TimeSpan FinalSyncLimit = TimeSpan.FromSeconds(5);

    private readonly CloudServices? _cloud;
    private readonly MainViewModel _main;
    private readonly Dialogs _dialogs;
    private readonly ILogger _log;
    private readonly DispatcherTimer _timer = new();

    public SyncViewModel(CloudServices? cloud, MainViewModel main, Dialogs dialogs, ILogger log)
    {
        _cloud = cloud;
        _main = main;
        _dialogs = dialogs;
        _log = log;
        _timer.Interval = TimeSpan.FromMinutes(Math.Max(1, cloud?.Options.IntervalMinutes ?? 5));
        _timer.Tick += (_, _) => _ = RunAsync();
        SignIn = new RelayCommand(() => _ = SignInAsync(), () => IsConfigured && !IsSignedIn);
        SignOut = new RelayCommand(() => _ = SignOutAsync(), () => IsSignedIn);
        SyncNow = new RelayCommand(() => _ = RunAsync(), () => IsSignedIn && !IsBusy);
        Status = cloud is null ? "" : "Not signed in";
    }

    public bool IsConfigured => _cloud is not null;
    public bool IsSignedIn => _cloud?.Auth.IsSignedIn == true;
    public string AccountLabel => IsSignedIn ? $"Signed in as {_cloud!.Auth.Email}" : "Not signed in";
    public bool IsBusy { get; private set => Set(ref field, value); }
    public string Status { get; private set => Set(ref field, value); } = "";

    public ICommand SignIn { get; }
    public ICommand SignOut { get; }
    public ICommand SyncNow { get; }

    public async Task StartAsync()
    {
        if (_cloud is null || !_cloud.Auth.Restore()) return;
        OnAccountChanged();
        await RunAsync();
    }

    /// <summary>After the window's final save: one pass, capped so closing never hangs.</summary>
    public Task FinalSyncAsync() => IsSignedIn ? RunAsync(final: true) : Task.CompletedTask;

    public async Task RunAsync(bool final = false)
    {
        if (_cloud is null || !IsSignedIn || (IsBusy && !final)) return;
        if (!final) _main.Save(); // the open character's edits go out with this pass
        IsBusy = true;
        Status = "Syncing…";
        using var limit = new CancellationTokenSource();
        if (final) limit.CancelAfter(FinalSyncLimit);
        try
        {
            var sync = _cloud.Sync;
            var result = await Task.Run(() => sync.RunAsync(() => _main.OpenCharacterId, waitIfBusy: final, limit.Token), limit.Token);
            await ApplyAsync(result);
        }
        catch (OperationCanceledException)
        {
            Status = "Sync timed out — will finish next time";
        }
        catch (Exception ex)
        {
            // A bug in a background pass must never take the app down; local data is unaffected.
            _log.LogError(ex, "Sync pass failed");
            Status = "Sync failed — see the log";
        }
        finally
        {
            IsBusy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task ApplyAsync(SyncResult result)
    {
        switch (result.Outcome)
        {
            case SyncOutcome.Synced:
                _main.ApplySyncResult(result);
                Status = $"Synced {DateTime.Now:HH:mm}" + Details(result);
                break;
            case SyncOutcome.Offline:
                Status = "Offline";
                break;
            case SyncOutcome.Unavailable:
                Status = "Cloud unavailable";
                break;
            case SyncOutcome.SignedOut:
                Status = "Signed out – sign in to resume sync";
                OnAccountChanged();
                break;
            case SyncOutcome.NotSignedIn:
                Status = "Not signed in";
                break;
            case SyncOutcome.AccountMismatch:
                Status = await ConfirmAccountLinkAsync() ? "Ready to sync" : "Not signed in";
                break;
            case SyncOutcome.Busy:
                break;
        }
    }

    private static string Details(SyncResult result) =>
        (result.ConflictCopies > 0 ? $" · {result.ConflictCopies} conflict {(result.ConflictCopies == 1 ? "copy" : "copies")} kept" : "")
        + (result.Warnings.Count > 0 ? $" · {result.Warnings.Count} not synced (see log)" : "");

    private async Task SignInAsync()
    {
        if (_cloud is null || !_dialogs.SignIn(_cloud.Auth)) return;
        if (!await ConfirmAccountLinkAsync()) return;
        _log.LogInformation("Signed in to cloud sync");
        OnAccountChanged();
        await RunAsync();
    }

    /// <summary>Signing in as a different account than this computer last synced with: ask what to do.</summary>
    private async Task<bool> ConfirmAccountLinkAsync()
    {
        var userId = _cloud!.Auth.UserId!;
        var linked = _cloud.Sync.LinkedAccount;
        if (linked is null || linked == userId) return true;

        var upload = _dialogs.AccountSwitch();
        if (upload is null || !_main.WithCharacterClosed(() => _cloud.Sync.Relink(userId, upload.Value)))
        {
            await SignOutAsync();
            return false;
        }
        _log.LogInformation("Linked this computer to a different cloud account (upload existing: {Upload})", upload.Value);
        return true;
    }

    private async Task SignOutAsync()
    {
        if (_cloud is null) return;
        await _cloud.Auth.SignOutAsync(CancellationToken.None);
        _log.LogInformation("Signed out of cloud sync");
        Status = "Not signed in";
        OnAccountChanged();
    }

    private void OnAccountChanged()
    {
        if (IsSignedIn) _timer.Start();
        else _timer.Stop();
        Raise(nameof(IsSignedIn));
        Raise(nameof(AccountLabel));
        CommandManager.InvalidateRequerySuggested();
    }
}
```

- [ ] **Step 3: Extend `MainViewModel`**

In `MainViewModel.cs`:
1. Add `using Hearthsheet.Core.Sync;`.
2. Change `MainServices` to end with `ILoggerFactory Loggers, CloudServices? Cloud);`.
3. In the constructor, after `Update = new UpdateViewModel(...)`, add:
   ```csharp
           Sync = new SyncViewModel(services.Cloud, this, services.Dialogs, services.Loggers.CreateLogger<SyncViewModel>());
   ```
4. After `public UpdateViewModel Update { get; }`, add:
   ```csharp
       public SyncViewModel Sync { get; }

       // Read by the background sync pass, so it is published through a volatile reference.
       private volatile Character? _openCharacter;
       public Guid? OpenCharacterId => _openCharacter?.Id;
   ```
5. In the `Current` setter, after `field = value;`, add `_openCharacter = value?.Character;`.
6. At the end of `OnStarted()`, add `_ = Sync.StartAsync();`.
7. Add these methods after `Save()`:

```csharp
    /// <summary>
    /// Applies a finished sync pass: remote changes to the open character (deferred by the pass so its
    /// autosave can't overwrite them), then the refreshed list.
    /// </summary>
    public void ApplySyncResult(SyncResult result)
    {
        string? status = null;
        foreach (var change in result.Deferred)
        {
            var open = Current?.Character.Id == change.Id ? Current : null;
            var copy = _s.Cloud!.Sync.ApplyDeferred(change, open?.Character, open?.Session.IsDirty == true);
            if (open is null) continue;

            _autosave.Stop();
            Current = null; // drop the stale in-memory model without saving it
            if (change.Remote is not null)
            {
                Open(change.Id);
                status = copy is null
                    ? "Updated from another device"
                    : "Updated from another device — your edits were kept as a conflict copy";
            }
            else if (copy is not null)
            {
                Open(copy.Id);
                status = "Deleted on another device — your edits were kept as a new character";
            }
            else
            {
                status = "This character was deleted on another device";
            }
        }
        if (!result.HasChanges) return;
        ReloadList(Current?.Character.Id);
        if (Current is null) SelectedSummary = Characters.FirstOrDefault();
        if (status is not null) SaveStatus = status;
    }

    /// <summary>Runs an operation that rewrites character ids with no character open. False if the open one couldn't be saved.</summary>
    public bool WithCharacterClosed(Action action)
    {
        if (!Save()) return false;
        Current = null;
        SelectedSummary = null;
        try
        {
            action();
        }
        finally
        {
            ReloadList();
            SelectedSummary = Characters.FirstOrDefault();
        }
        return true;
    }
```

- [ ] **Step 4: Compose it**

In `App.xaml.cs`:
- add `using Hearthsheet.App.ViewModels;` if it's missing, plus `using Hearthsheet.Core.Sync;` and `using Hearthsheet.Infrastructure.Sync;`
- after `_log.LogDebug("Update source: ...")`, add:

```csharp
        if (!string.IsNullOrWhiteSpace(settings.Sync.Url) && !settings.Sync.IsConfigured)
            _log.LogWarning("Cloud sync is disabled: Sync:Url must be an https URL and Sync:AnonKey must be set");
```

In `Compose`, before the `return new MainViewModel(...)`, add:

```csharp
        CloudServices? cloud = null;
        if (settings.Sync.IsConfigured)
        {
            var http = new SupabaseHttp(settings.Sync, userAgentVersion: AppSettings.AppVersion);
            var auth = new SupabaseAuth(http, secrets);
            var sync = new CloudSync(repository, repository, new SupabaseCharacterStore(http, auth), auth,
                migrator, factory.CreateLogger<CloudSync>());
            cloud = new CloudServices(auth, sync, settings.Sync);
        }
```

and pass `cloud` as the last `MainServices` argument: `..., new Dialogs(), factory, cloud));`.

- [ ] **Step 5: Add the menu, status indicator and final sync**

In `MainWindow.xaml`, insert before `<MenuItem Header="_Help">`:

```xml
            <MenuItem Header="_Account" Visibility="{Binding Sync.IsConfigured, Converter={StaticResource VisibleWhen}}">
                <MenuItem Header="{Binding Sync.AccountLabel}" IsEnabled="False" />
                <Separator />
                <MenuItem Header="_Sign in…" Command="{Binding Sync.SignIn}" />
                <MenuItem Header="Sync _now" Command="{Binding Sync.SyncNow}" />
                <MenuItem Header="Sign _out" Command="{Binding Sync.SignOut}" />
            </MenuItem>
```

In the status bar, insert after the `Update … available` `<Button …/>`:

```xml
                <Button DockPanel.Dock="Right" Margin="0,0,14,0" Padding="8,1" MinHeight="22" Command="{Binding Sync.SyncNow}"
                        Content="{Binding Sync.Status}" ToolTip="Sync now" AutomationProperties.Name="Cloud sync status. Sync now"
                        Visibility="{Binding Sync.IsConfigured, Converter={StaticResource VisibleWhen}}" />
```

In `MainWindow.xaml.cs`, replace `OnClosing` with:

```csharp
    private bool _readyToClose;

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_readyToClose || Vm is not { } vm) return;
        if (!vm.OnClosing())
        {
            e.Cancel = true;
            return;
        }
        if (!vm.Sync.IsSignedIn) return;

        // One last bounded sync pass, then close for real.
        e.Cancel = true;
        IsEnabled = false;
        await vm.Sync.FinalSyncAsync();
        _readyToClose = true;
        Close();
    }
```

- [ ] **Step 6: Build and run the tests**

Run: `dotnet build Hearthsheet.slnx`, then `dotnet test Hearthsheet.slnx`
Expected: the build succeeds and all tests pass.

- [ ] **Step 7: Check the app manually without a Supabase project**

1. Run `dotnet run --project src/Hearthsheet.App -- --Application:DataDirectory=%TEMP%\hearthsheet-dev`. There should be no Account menu and no sync indicator, and nothing else should change.
2. Run `dotnet run --project src/Hearthsheet.App -- --Application:DataDirectory=%TEMP%\hearthsheet-dev --Sync:Url=https://example.invalid --Sync:AnonKey=x`.
   - The Account menu shows "Not signed in", and the indicator shows "Not signed in".
   - Account → Sign in… opens the dialog.
   - Submitting shows "The cloud service could not be reached."
   - The Create account and Forgot password links switch modes; Back returns to sign in.
   - Characters still create, edit, autosave and close normally.

- [ ] **Step 8: Commit**

```bash
git add src/Hearthsheet.App
git commit -m "Wire cloud sync into the app"
```

---

### Task 8: Supabase schema, keep-alive and documentation

**Files:**
- Create: `supabase/migrations/0001_cloud_sync.sql`
- Create: `.github/workflows/supabase-keepalive.yml`
- Create: `docs/CLOUD_SYNC.md`
- Modify: `docs/ARCHITECTURE.md`

**Interfaces:**
- Consumes: the column names used by `SupabaseCharacterStore` (Task 5): `id`, `user_id`, `name`, `description`, `schema_version`, `data`, `deleted_at`, `revision`, `updated_at`.
- Produces: the server-side contract, and the setup and manual test docs.

- [ ] **Step 1: Write the migration**

Create `supabase/migrations/0001_cloud_sync.sql`:

```sql
-- Hearthsheet cloud sync. Run once in the Supabase SQL editor (see docs/CLOUD_SYNC.md).

create table public.characters (
  id             uuid primary key,
  user_id        uuid not null default auth.uid() references auth.users on delete cascade,
  name           text not null,
  description    text not null,
  schema_version int  not null,
  data           jsonb,                        -- null once deleted
  deleted_at     timestamptz,                  -- tombstone marker
  revision       bigint not null default 1,
  updated_at     timestamptz not null default now()
);
create index characters_user_updated on public.characters (user_id, updated_at);

-- The server owns revision, updated_at and ownership; clients cannot set them.
create function public.characters_bump() returns trigger
language plpgsql set search_path = '' as $$
begin
  new.revision   := case when tg_op = 'INSERT' then 1 else old.revision + 1 end;
  new.updated_at := now();
  if tg_op = 'UPDATE' then new.user_id := old.user_id; end if;
  return new;
end $$;

create trigger characters_bump before insert or update on public.characters
  for each row execute function public.characters_bump();

alter table public.characters enable row level security;
create policy own_select on public.characters for select to authenticated using (user_id = (select auth.uid()));
create policy own_insert on public.characters for insert to authenticated with check (user_id = (select auth.uid()));
create policy own_update on public.characters for update to authenticated
  using (user_id = (select auth.uid())) with check (user_id = (select auth.uid()));
-- No delete policy: clients tombstone, and only the purge job removes rows.

grant select, insert, update on public.characters to authenticated;

-- Tombstone retention: 90 days (the app treats a machine as stale after 80).
create extension if not exists pg_cron;
select cron.schedule('purge-character-tombstones', '17 3 * * *',
  $$delete from public.characters where deleted_at < now() - interval '90 days'$$);
```

- [ ] **Step 2: Write the keep-alive workflow**

Create `.github/workflows/supabase-keepalive.yml`:

```yaml
name: Supabase keep-alive

# Free Supabase projects pause after about a week without activity. One small daily query prevents that.
on:
  schedule:
    - cron: "23 7 * * *"
  workflow_dispatch:

permissions: {}

jobs:
  ping:
    if: vars.SUPABASE_URL != ''
    runs-on: ubuntu-latest
    steps:
      - name: Query the characters table
        env:
          SUPABASE_URL: ${{ vars.SUPABASE_URL }}
          SUPABASE_ANON_KEY: ${{ vars.SUPABASE_ANON_KEY }}
        # Row-level security returns [] for the anonymous key, but the request still reaches the database.
        run: curl --fail --silent --show-error --max-time 30 "$SUPABASE_URL/rest/v1/characters?select=id&limit=1" -H "apikey: $SUPABASE_ANON_KEY"
```

- [ ] **Step 3: Write the setup guide**

Create `docs/CLOUD_SYNC.md`:

````markdown
# Cloud sync setup

Cloud sync is optional. Without `Sync:Url` and `Sync:AnonKey` the app hides the Account menu and works exactly as before.
Design: `docs/superpowers/specs/2026-09-29-cloud-sync-design.md`.

## One-time setup

1. **Create the project.** At supabase.com, create a free project and pick the region closest to your players.
2. **Create the schema.** In the SQL editor, run `supabase/migrations/0001_cloud_sync.sql`. If `create extension pg_cron` fails, enable
   **pg_cron** under Database → Extensions and run the file again.
3. **Configure authentication** (Authentication → Sign In / Providers → Email):
   - Email provider: **on**.
   - **Confirm email: off**. Sign-up is then instant and sends no email.
   - Minimum password length: **8**. If your plan offers leaked-password protection, turn it on.
4. **Set up Resend on your Cloudflare domain:**
   - Create a Resend account and choose Domains → Add domain (for example `mail.yourdomain.com`).
   - In Cloudflare DNS, add every record Resend lists, with the proxy **off** ("DNS only"). Then click Verify in Resend.
   - Create a Resend API key with sending access.
5. **Point Supabase at Resend** (Authentication → Emails → SMTP settings). Enable custom SMTP with:
   - Host `smtp.resend.com`
   - Port `465`
   - Username `resend`
   - Password: the Resend API key
   - Sender `noreply@mail.yourdomain.com`, name `Hearthsheet`
6. **Change the reset email** (Authentication → Emails → Templates → Reset password):
   - Subject: `Your Hearthsheet reset code`
   - Body:
     ```html
     <p>Your Hearthsheet password reset code is:</p>
     <h2>{{ .Token }}</h2>
     <p>Enter it in Hearthsheet. If you didn't ask for this, you can ignore this email.</p>
     ```
7. **Configure the app.** From Project Settings → API, copy the **Project URL** and the **publishable** key (`sb_publishable_…`, or the
   legacy `anon` key) into `src/Hearthsheet.App/appsettings.json` under `Sync:Url` and `Sync:AnonKey`, then commit. Both are public by design.
   **Never** put the secret or `service_role` key in the app or the repository.
8. **Keep the project awake.** In GitHub, go to Settings → Secrets and variables → Actions → **Variables** and add `SUPABASE_URL` and
   `SUPABASE_ANON_KEY`. Then run **Supabase keep-alive** once from the Actions tab. GitHub disables scheduled workflows after
   60 days without repository activity; if that happens, re-enable it from the Actions tab.

## Manual test script

Use two data folders on one PC as two "installations":

```bash
dotnet run --project src/Hearthsheet.App -- --Application:DataDirectory=%TEMP%\hs-a
```

```bash
dotnet run --project src/Hearthsheet.App -- --Application:DataDirectory=%TEMP%\hs-b
```

| # | Steps | Expected |
|---|---|---|
| 1 | A: create an account, create "Arannis". B: sign in. | B lists Arannis after sign-in. |
| 2 | A: rename, then click the sync indicator. B: click the sync indicator. | B shows the new name. |
| 3 | Disconnect the network. Edit Arannis differently in A and B. Reconnect, then sync A, then sync B. | B keeps A's version and gains "Arannis… (conflict copy, date)". The next sync uploads the copy. |
| 4 | A: delete Arannis, sync. B: sync. | Arannis disappears from B. |
| 5 | Open a character in B. Edit it in A and sync. Sync B. | B reloads it with "Updated from another device". |
| 6 | Close A and reopen it. | Still signed in, with no prompt. |
| 7 | Sign in with "Stay signed in" unchecked, then restart. | Signed out. |
| 8 | Sign out, then check Control Panel → Credential Manager. | No `Hearthsheet/supabase-session` entry. |
| 9 | Forgot password → code → new password. | The email arrives from your domain and the new password works. |
| 10 | Sign in to A as a second account. | The account-switch prompt appears, and both choices keep every local character. |

**Row-level security check** (SQL editor; replace the ids with two real user ids from Authentication → Users):

```sql
begin;
set local role authenticated;
set local request.jwt.claims = '{"sub":"<user B id>"}';
select count(*) from public.characters where user_id = '<user A id>';  -- must be 0
rollback;
```
````

- [ ] **Step 4: Update the architecture doc**

In `docs/ARCHITECTURE.md`:
- In §1, append to the dependencies paragraph: ` Cloud sync talks to Supabase with plain `HttpClient`; it adds no packages.`
- In §2's tree, add `    Sync/                  Cloud sync algorithm and cloud contracts` under `Hearthsheet.Core`, and
  `    Sync/                  Supabase auth, character store, HTTP client` under `Hearthsheet.Infrastructure`.
- In §11's table, add these rows:
  - `| Cloud account | Optional; Supabase Auth over HTTPS to one configured host; refresh session in Credential Manager only with "Stay signed in"; passwords never stored; tokens never logged (JWT redaction) |`
  - `| Cloud data | Row-level security per user; the public anon/publishable key only; pulled rows go through the import validation pipeline |`
- In §14, remove the bullet about persistence calls being synchronous. Add:
  - `- Cloud sync runs at startup, every few minutes and on close, not after each save; there are no live updates between devices.`
  - `- A free Supabase project pauses after about a week idle (a daily GitHub Actions request prevents it); while paused, sync reports "Cloud unavailable" and everything else works.`
  - `- A character deleted elsewhere more than 90 days ago can come back from a machine that was offline the whole time (tombstones are purged after 90 days).`
- Append a new section:

```markdown
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
```

- In §13, change "about 115 tests" to the current count from `dotnet test`, and add a bullet:
  `- **Cloud sync:** local bookkeeping and the db upgrade; push/pull, conflicts, deletes vs edits, deferred changes to the open character, stale machines, account relinking and unreadable rows against an in-memory cloud; Supabase auth (remember me, token rotation, revoked and offline sessions, password reset) and store requests against a stub HTTP handler.`

- [ ] **Step 5: Run the full suite one last time**

Run: `dotnet test Hearthsheet.slnx`
Expected: all tests pass. Use the reported count in §13.

- [ ] **Step 6: Commit**

```bash
git add supabase .github/workflows/supabase-keepalive.yml docs/CLOUD_SYNC.md docs/ARCHITECTURE.md
git commit -m "Add the Supabase schema, keep-alive workflow and sync docs"
```

- [ ] **Step 7: Setup and manual test (you, not an agent)**

Follow `docs/CLOUD_SYNC.md` to create the project, commit the real `Sync:Url` and `Sync:AnonKey`, and run the manual test script and the RLS check.
