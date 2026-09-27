using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DndSheet.Core.Domain;

namespace DndSheet.Core.Serialization;

/// <summary>
/// The self-contained, versioned export format (*.dndchar). Envelope:
/// <c>{ "format": "dndsheet.character", "schemaVersion": 1, "appVersion": "1.0.0", "exportedUtc": "...", "character": { ... } }</c>
/// </summary>
public sealed class PortableCharacterFile(CharacterMigrator migrator)
{
    public const string FormatId = "dndsheet.character";
    public const string FileExtension = ".dndchar";
    public const long MaxFileBytes = 10 * 1024 * 1024;
    private const int MaxListEntries = 5000;

    public string Export(Character c, string appVersion)
    {
        var envelope = new JsonObject
        {
            ["format"] = FormatId,
            ["schemaVersion"] = CharacterJson.CurrentSchemaVersion,
            ["appVersion"] = appVersion,
            ["exportedUtc"] = DateTime.UtcNow.ToString("O"),
            ["character"] = CharacterJson.ToJsonObject(c),
        };
        return envelope.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public void ExportToFile(Character c, string appVersion, string path) =>
        AtomicFile.WriteAllText(path, Export(c, appVersion));

    public Character ImportFromFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new CharacterFormatException("The file does not exist.");
        if (info.Length > MaxFileBytes) throw new CharacterFormatException("The file is too large to be a character file.");
        return Import(File.ReadAllText(path, Encoding.UTF8));
    }

    /// <summary>
    /// Validates and imports untrusted text. The imported character always gets a fresh id so importing
    /// can never overwrite an existing character.
    /// </summary>
    public Character Import(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaxFileBytes)
            throw new CharacterFormatException("The file is too large to be a character file.");

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (JsonException)
        {
            throw new CharacterFormatException("The file is not a valid character file (invalid JSON).");
        }
        if (root is not JsonObject envelope
            || envelope["format"] is not JsonValue format
            || !format.TryGetValue<string>(out var formatId)
            || formatId != FormatId)
            throw new CharacterFormatException("The file is not a DndSheet character file.");

        var version = envelope["schemaVersion"] is JsonValue v && v.TryGetValue<int>(out var parsed) ? parsed : 0;
        if (envelope["character"] is not JsonObject characterNode)
            throw new CharacterFormatException("The file does not contain character data.");
        envelope.Remove("character");

        Character character;
        try
        {
            character = CharacterJson.Deserialize(characterNode, version, migrator);
        }
        catch (JsonException ex)
        {
            throw new CharacterFormatException($"The character data is malformed: {ex.Message}", ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new CharacterFormatException($"The character data is malformed: {ex.Message}", ex);
        }

        EnforceLimits(character);
        character.Id = Guid.NewGuid();
        return character;
    }

    private static void EnforceLimits(Character c)
    {
        int[] counts =
        [
            c.Attacks.Count, c.Proficiencies.Count, c.Features.Count, c.Items.Count, c.Resources.Count,
            c.Effects.Count, c.Spellcasting.Spells.Count,
            c.Items.Sum(i => i.Modifiers.Count), c.Effects.Sum(e => e.Modifiers.Count),
        ];
        if (counts.Any(n => n > MaxListEntries))
            throw new CharacterFormatException("The character file contains an unreasonable number of entries.");
    }
}

/// <summary>Write-to-temp-then-replace so an interrupted write never leaves a truncated file.</summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        var full = Path.GetFullPath(path);
        var temp = full + ".tmp";
        File.WriteAllText(temp, contents, new UTF8Encoding(false));
        File.Move(temp, full, overwrite: true);
    }
}
