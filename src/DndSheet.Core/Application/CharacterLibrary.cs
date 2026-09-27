using DndSheet.Core.Content;
using DndSheet.Core.Domain;
using DndSheet.Core.Serialization;
using Microsoft.Extensions.Logging;

namespace DndSheet.Core.Application;

public sealed record CharacterSummary(Guid Id, string Name, string Description, DateTime UpdatedUtc);

/// <summary>Storage for characters. Implementations must make each Save atomic.</summary>
public interface ICharacterRepository
{
    IReadOnlyList<CharacterSummary> List();
    /// <exception cref="CharacterFormatException">Stored data could not be read or migrated.</exception>
    Character? Load(Guid id);
    void Save(Character character);
    bool Delete(Guid id);
}

/// <summary>Use cases for managing the set of characters (create, duplicate, import/export…).</summary>
public sealed class CharacterLibrary(
    ICharacterRepository repository, PortableCharacterFile portable, CharacterMigrator migrator,
    string appVersion, ILogger<CharacterLibrary> logger)
{
    public IReadOnlyList<CharacterSummary> List() => repository.List();

    public Character? Load(Guid id)
    {
        var c = repository.Load(id);
        logger.LogInformation("Character loaded: {Name} ({Id})", c?.Identity.Name, id);
        return c;
    }

    public void Save(Character c)
    {
        repository.Save(c);
        logger.LogInformation("Character saved: {Name}", c.Identity.Name);
    }

    public Character Create(NewCharacterOptions options)
    {
        var c = CharacterFactory.Create(options);
        repository.Save(c);
        logger.LogInformation("Character created: {Name} ({Class} {Level})", c.Identity.Name, c.Identity.ClassName, c.Identity.Level);
        return c;
    }

    public Character Duplicate(Character source)
    {
        var copy = CharacterJson.Clone(source, migrator);
        copy.Id = Guid.NewGuid();
        copy.Identity.Name = source.Identity.Name + " (copy)";
        repository.Save(copy);
        logger.LogInformation("Character duplicated: {Name}", copy.Identity.Name);
        return copy;
    }

    public bool Delete(Guid id)
    {
        var deleted = repository.Delete(id);
        logger.LogInformation("Character deleted: {Id} (found: {Found})", id, deleted);
        return deleted;
    }

    public void Export(Character c, string path)
    {
        portable.ExportToFile(c, appVersion, path);
        logger.LogInformation("Character exported: {Name} -> {File}", c.Identity.Name, Path.GetFileName(path));
    }

    /// <exception cref="CharacterFormatException">The file is invalid, corrupt or from a newer version.</exception>
    public Character Import(string path)
    {
        var c = portable.ImportFromFile(path);
        repository.Save(c);
        logger.LogInformation("Character imported: {Name} from {File}", c.Identity.Name, Path.GetFileName(path));
        return c;
    }
}
