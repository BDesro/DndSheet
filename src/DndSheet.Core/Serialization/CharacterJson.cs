using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DndSheet.Core.Domain;

namespace DndSheet.Core.Serialization;

/// <summary>
/// The one character serialization used by both the local database and portable export files, so a
/// schema change needs exactly one migration path. Only concrete, known types are ever deserialized
/// (no type-name handling), which keeps untrusted input from choosing what gets instantiated.
/// </summary>
public static class CharacterJson
{
    /// <summary>Bump when the stored shape changes, and add an <see cref="ICharacterMigration"/> from the old version.</summary>
    public const int CurrentSchemaVersion = 1;

    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 32,
        WriteIndented = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        Converters = { new JsonStringEnumConverter() },
        // Computed properties such as SpellSlotLevel.Available are read-only and are skipped on write.
        IgnoreReadOnlyProperties = true,
    };

    private static readonly JsonSerializerOptions IndentedOptions = new(Options) { WriteIndented = true };

    public static JsonObject ToJsonObject(Character c) =>
        JsonSerializer.SerializeToNode(c, Options)!.AsObject();

    public static string Serialize(Character c, bool indented = false) =>
        JsonSerializer.Serialize(c, indented ? IndentedOptions : Options);

    /// <summary>Migrates <paramref name="node"/> from <paramref name="schemaVersion"/> to current, then materializes it.</summary>
    public static Character Deserialize(JsonObject node, int schemaVersion, CharacterMigrator migrator)
    {
        migrator.Migrate(node, schemaVersion);
        var character = node.Deserialize<Character>(Options)
                        ?? throw new CharacterFormatException("Character data is empty.");
        character.Normalize();
        return character;
    }

    public static Character Deserialize(string json, int schemaVersion, CharacterMigrator migrator)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (JsonException ex)
        {
            throw new CharacterFormatException("Character data is not valid JSON.", ex);
        }
        if (node is not JsonObject obj) throw new CharacterFormatException("Character data must be a JSON object.");
        try
        {
            return Deserialize(obj, schemaVersion, migrator);
        }
        catch (JsonException ex)
        {
            throw new CharacterFormatException($"Character data is malformed: {ex.Message}", ex);
        }
    }

    public static Character Clone(Character c, CharacterMigrator migrator) =>
        Deserialize(Serialize(c), CurrentSchemaVersion, migrator);
}

public sealed class CharacterFormatException(string message, Exception? inner = null) : Exception(message, inner);
