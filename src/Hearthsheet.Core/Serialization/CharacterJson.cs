using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Hearthsheet.Core.Domain;

namespace Hearthsheet.Core.Serialization;

/// <summary>
/// The one character serialization used by both the local database and portable export files, so a
/// schema change needs exactly one migration path. Only concrete, known types are ever deserialized
/// (no type-name handling), which keeps untrusted input from choosing what gets instantiated.
/// </summary>
public static class CharacterJson
{
    /// <summary>
    /// Bump when the stored shape changes, and migrate the raw <see cref="JsonObject"/> from the old version in
    /// <see cref="Deserialize(JsonObject, int)"/> before it is materialized. No migrations exist yet: version 1 is the first release.
    /// </summary>
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

    public static JsonObject ToJsonObject(Character c) =>
        JsonSerializer.SerializeToNode(c, Options)!.AsObject();

    public static string Serialize(Character c) => JsonSerializer.Serialize(c, Options);

    /// <summary>Materializes <paramref name="node"/>, which was saved at <paramref name="schemaVersion"/>.</summary>
    public static Character Deserialize(JsonObject node, int schemaVersion)
    {
        if (schemaVersion < 1)
            throw new CharacterFormatException($"Invalid character schema version {schemaVersion}.");
        if (schemaVersion > CurrentSchemaVersion)
            throw new CharacterFormatException(
                $"This character was saved by a newer version of the application (schema {schemaVersion}; this version supports up to {CurrentSchemaVersion}). Update the application to open it.");
        var character = node.Deserialize<Character>(Options)
                        ?? throw new CharacterFormatException("Character data is empty.");
        character.Normalize();
        return character;
    }

    public static Character Deserialize(string json, int schemaVersion)
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
            return Deserialize(obj, schemaVersion);
        }
        catch (JsonException ex)
        {
            throw new CharacterFormatException($"Character data is malformed: {ex.Message}", ex);
        }
    }

    public static Character Clone(Character c) => Deserialize(Serialize(c), CurrentSchemaVersion);
}

public sealed class CharacterFormatException(string message, Exception? inner = null) : Exception(message, inner);
