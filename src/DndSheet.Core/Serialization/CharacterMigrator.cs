using System.Text.Json.Nodes;

namespace DndSheet.Core.Serialization;

/// <summary>Upgrades raw character JSON from <see cref="FromVersion"/> to FromVersion + 1.</summary>
public interface ICharacterMigration
{
    int FromVersion { get; }
    void Apply(JsonObject character);
}

/// <summary>
/// Applies migrations stepwise on the JSON tree (before typed deserialization) so old shapes never
/// need to exist as C# types. There are no migrations yet: schema version 1 is the first release.
/// </summary>
public sealed class CharacterMigrator(IEnumerable<ICharacterMigration> migrations, int currentVersion = CharacterJson.CurrentSchemaVersion)
{
    private readonly Dictionary<int, ICharacterMigration> _byVersion = migrations.ToDictionary(m => m.FromVersion);

    public static CharacterMigrator Default { get; } = new([]);

    public int CurrentVersion => currentVersion;

    /// <returns>True when the data was changed.</returns>
    public bool Migrate(JsonObject character, int fromVersion)
    {
        if (fromVersion < 1)
            throw new CharacterFormatException($"Invalid character schema version {fromVersion}.");
        if (fromVersion > currentVersion)
            throw new CharacterFormatException(
                $"This character was saved by a newer version of the application (schema {fromVersion}; this version supports up to {currentVersion}). Update the application to open it.");

        for (var v = fromVersion; v < currentVersion; v++)
        {
            if (!_byVersion.TryGetValue(v, out var migration))
                throw new CharacterFormatException($"No migration available from character schema {v} to {v + 1}.");
            migration.Apply(character);
        }
        return fromVersion != currentVersion;
    }
}
