using System.Globalization;
using DndSheet.Core.Application;
using DndSheet.Core.Domain;
using DndSheet.Core.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DndSheet.Infrastructure.Persistence;

/// <summary>
/// Stores each character as one JSON document row in SQLite. SQLite provides atomic, crash-safe
/// writes (WAL journal) and the document shape is the same as the export format, so one set of
/// migrations serves both. Summary columns are denormalized for fast listing.
/// </summary>
public sealed class SqliteCharacterRepository : ICharacterRepository
{
    private const int DatabaseVersion = 1;

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
        if (existing is null)
        {
            version.CommandText = "INSERT INTO meta (key, value) VALUES ('db_version', $v)";
            version.Parameters.AddWithValue("$v", DatabaseVersion.ToString(CultureInfo.InvariantCulture));
            version.ExecuteNonQuery();
        }
        else if (int.Parse(existing, CultureInfo.InvariantCulture) > DatabaseVersion)
        {
            throw new InvalidOperationException(
                "The character database was created by a newer version of the application. Update the application to use it.");
        }
        // Future table-level changes go here as stepwise "if db_version == n" upgrades.
        _logger.LogInformation("Database initialized");
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
            INSERT INTO characters (id, name, description, schema_version, data, created_utc, updated_utc)
            VALUES ($id, $name, $description, $version, $data, $now, $now)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name, description = excluded.description,
                schema_version = excluded.schema_version, data = excluded.data, updated_utc = excluded.updated_utc
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
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM characters WHERE id = $id";
        command.Parameters.AddWithValue("$id", id.ToString());
        return command.ExecuteNonQuery() > 0;
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
