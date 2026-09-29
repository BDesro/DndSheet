using System.Globalization;
using Hearthsheet.Core.Application;
using Hearthsheet.Core.Domain;
using Hearthsheet.Core.Serialization;
using Hearthsheet.Core.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Hearthsheet.Infrastructure.Persistence;

/// <summary>
/// Stores each character as one JSON document row in SQLite. SQLite provides atomic, crash-safe
/// writes (WAL journal) and the document shape is the same as the export format, so one set of
/// migrations serves both. Summary columns are denormalized for fast listing.
/// </summary>
public sealed class SqliteCharacterRepository : ICharacterRepository, ISyncLocalStore
{
    private const int DatabaseVersion = 2;

    private readonly string _connectionString;
    private readonly CharacterMigrator _migrator;
    private readonly ILogger _logger;

    public SqliteCharacterRepository(string databasePath, CharacterMigrator migrator, ILogger<SqliteCharacterRepository> logger)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Pooling keeps file handles open after Dispose, which blocks backups/deletes of the file.
            Pooling = false,
        }.ToString();
        _migrator = migrator;
        _logger = logger;
        Initialize();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS characters (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                description TEXT NOT NULL,
                schema_version INTEGER NOT NULL,
                data TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();

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
        _logger.LogInformation("Database initialized");
    }

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

    public IReadOnlyList<CharacterSummary> List()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, description, updated_utc FROM characters ORDER BY name COLLATE NOCASE";
        using var reader = command.ExecuteReader();
        var result = new List<CharacterSummary>();
        while (reader.Read())
        {
            result.Add(new CharacterSummary(
                Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
                DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }
        return result;
    }

    public Character? Load(Guid id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT schema_version, data FROM characters WHERE id = $id";
        command.Parameters.AddWithValue("$id", id.ToString());
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;

        var schemaVersion = reader.GetInt32(0);
        var character = CharacterJson.Deserialize(reader.GetString(1), schemaVersion, _migrator);
        character.Id = id;
        if (schemaVersion != _migrator.CurrentVersion)
            _logger.LogInformation("Character {Id} migrated from schema {From} to {To} (saved on next write)", id, schemaVersion, _migrator.CurrentVersion);
        return character;
    }

    public void Save(Character character)
    {
        var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO characters (id, name, description, schema_version, data, created_utc, updated_utc, dirty, local_revision)
            VALUES ($id, $name, $description, $version, $data, $now, $now, 1, 1)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name, description = excluded.description,
                schema_version = excluded.schema_version, data = excluded.data, updated_utc = excluded.updated_utc,
                dirty = 1, local_revision = characters.local_revision + 1
            """;
        command.Parameters.AddWithValue("$id", character.Id.ToString());
        command.Parameters.AddWithValue("$name", DisplayName(character));
        command.Parameters.AddWithValue("$description", Describe(character));
        command.Parameters.AddWithValue("$version", CharacterJson.CurrentSchemaVersion);
        command.Parameters.AddWithValue("$data", CharacterJson.Serialize(character));
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();
        _logger.LogDebug("Saved character row {Id}", character.Id);
    }

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

    /// <summary>Consistent online copy of the database (SQLite backup API), safe while the app is running.</summary>
    public void BackupTo(string destinationPath)
    {
        using var source = Open();
        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath, Pooling = false,
        }.ToString());
        destination.Open();
        source.BackupDatabase(destination);
    }

    private static string DisplayName(Character c) =>
        string.IsNullOrWhiteSpace(c.Identity.Name) ? "Unnamed character" : c.Identity.Name;

    private static string Describe(Character c)
    {
        var parts = new[] { c.Identity.Species, $"{c.Identity.ClassName} {c.Identity.Level}".Trim() }
            .Where(p => !string.IsNullOrWhiteSpace(p));
        return string.Join(" · ", parts);
    }
}
