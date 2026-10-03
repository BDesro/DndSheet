using System.Text.Json.Nodes;
using Hearthsheet.Core.Application;
using Hearthsheet.Core.Content;
using Hearthsheet.Core.Domain;
using Hearthsheet.Core.Serialization;
using Hearthsheet.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hearthsheet.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("hearthsheet-tests-").FullName;
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}

public static class Samples
{
    public static Character Rich()
    {
        var c = CharacterFactory.Create(new NewCharacterOptions("Arannis", "Wizard", 5, "Elf", "Sage", "Beau"));
        c.Abilities.Intelligence = 18;
        c.SkillEntry(Skill.Arcana).Proficiency = ProficiencyLevel.Expertise;
        c.Items.Add(new Item { Name = "Staff", Weight = 4, Equipped = true, Modifiers = { new Modifier { Target = "ac", Value = 1 } } });
        c.Features.Add(new Feature { Name = "Arcane Recovery", Source = FeatureSource.Class, Description = "Recover slots." });
        c.Spellcasting.Spells.Add(new Spell { Name = "Shield", Level = 1, Prepared = true });
        c.Effects.Add(new Effect { Name = "Bless", Expiry = EffectExpiry.ShortRest });
        c.Conditions.Add(Condition.Prone);
        c.Proficiencies.Add(new Proficiency { Category = ProficiencyCategory.Language, Name = "Elvish" });
        c.Resources.Add(new Resource { Name = "Custom", Maximum = 3, Current = 2, RecoveryAmount = 1 });
        c.Currency.Gold = 42;
        c.Combat.TemporaryHp = 3;
        return c;
    }
}

public class SerializationTests
{
    private readonly PortableCharacterFile _portable = new();

    [Fact]
    public void RoundTrip_PreservesState()
    {
        var original = Samples.Rich();
        var copy = CharacterJson.Deserialize(CharacterJson.Serialize(original), CharacterJson.CurrentSchemaVersion);
        Assert.Equal(CharacterJson.Serialize(original), CharacterJson.Serialize(copy));
        Assert.Equal(original.Id, copy.Id);
    }

    [Fact]
    public void ExportImport_ProducesEquivalentCharacterWithNewId()
    {
        var original = Samples.Rich();
        var imported = _portable.Import(_portable.Export(original, "1.0.0"));
        Assert.NotEqual(original.Id, imported.Id);
        imported.Id = original.Id;
        Assert.Equal(CharacterJson.Serialize(original), CharacterJson.Serialize(imported));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"format\":\"something-else\",\"schemaVersion\":1,\"character\":{}}")]
    [InlineData("{\"format\":1,\"schemaVersion\":1,\"character\":{}}")]
    [InlineData("{\"format\":\"dndsheet.character\",\"schemaVersion\":1}")]
    [InlineData("{\"format\":\"dndsheet.character\",\"schemaVersion\":0,\"character\":{}}")]
    [InlineData("{\"format\":\"dndsheet.character\",\"schemaVersion\":1,\"character\":{\"abilities\":{\"strength\":\"lots\"}}}")]
    [InlineData("{\"format\":\"dndsheet.character\",\"schemaVersion\":1,\"character\":{\"conditions\":[\"OnFire\"]}}")]
    public void Import_RejectsInvalidFiles(string text) =>
        Assert.Throws<CharacterFormatException>(() => _portable.Import(text));

    [Fact]
    public void Import_RejectsNewerSchema()
    {
        var text = _portable.Export(new Character(), "9.0.0").Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99");
        var ex = Assert.Throws<CharacterFormatException>(() => _portable.Import(text));
        Assert.Contains("newer version", ex.Message);
    }

    [Fact]
    public void Import_RejectsExcessiveNesting()
    {
        var deep = string.Concat(Enumerable.Repeat("{\"a\":", 100)) + "1" + new string('}', 100);
        Assert.Throws<CharacterFormatException>(() => _portable.Import(deep));
    }

    [Fact]
    public void Import_ClampsValuesAndRepairsStructure()
    {
        const string text = """
            {"format":"dndsheet.character","schemaVersion":1,"character":{
              "abilities":{"strength":400},
              "skills":[{"skill":"Stealth","proficiency":"Expertise"},{"skill":"Stealth"}],
              "savingThrows":[]
            }}
            """;
        var c = _portable.Import(text);
        Assert.Equal(30, c.Abilities.Strength);
        Assert.Equal(18, c.Skills.Count);
        Assert.Equal(ProficiencyLevel.Expertise, c.SkillEntry(Skill.Stealth).Proficiency);
        Assert.Equal(6, c.SavingThrows.Count);
        Assert.Equal(9, c.Spellcasting.Slots.Count);
    }
}

public class RepositoryTests : IDisposable
{
    private readonly TempDir _dir = new();
    private SqliteCharacterRepository NewRepository() =>
        new(_dir.File("characters.db"), NullLogger<SqliteCharacterRepository>.Instance);

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void SaveLoadUpdateDelete()
    {
        var repo = NewRepository();
        var c = Samples.Rich();
        repo.Save(c);

        var loaded = repo.Load(c.Id)!;
        Assert.Equal(CharacterJson.Serialize(c), CharacterJson.Serialize(loaded));

        loaded.Identity.Name = "Renamed";
        loaded.TakeDamage(5);
        repo.Save(loaded);
        var summaries = repo.List();
        Assert.Single(summaries);
        Assert.Equal("Renamed", summaries[0].Name);
        Assert.Contains("Wizard 5", summaries[0].Description);
        Assert.Equal(loaded.Combat.CurrentHp, repo.Load(c.Id)!.Combat.CurrentHp);

        Assert.True(repo.Delete(c.Id));
        Assert.Null(repo.Load(c.Id));
        Assert.False(repo.Delete(c.Id));
    }

    [Fact]
    public void Data_SurvivesReopeningDatabase()
    {
        var c = Samples.Rich();
        NewRepository().Save(c);
        var reopened = NewRepository();
        Assert.Equal("Arannis", reopened.Load(c.Id)!.Identity.Name);
    }

    [Fact]
    public void MultipleCharacters_AreListedByName()
    {
        var repo = NewRepository();
        foreach (var name in new[] { "zed", "Alice", "bob" })
        {
            var c = new Character();
            c.Identity.Name = name;
            repo.Save(c);
        }
        Assert.Equal(["Alice", "bob", "zed"], repo.List().Select(s => s.Name));
    }

    [Fact]
    public void Load_RowFromNewerSchema_ThrowsFormatException()
    {
        var repo = NewRepository();
        var c = new Character();
        repo.Save(c);
        ExecuteSql($"UPDATE characters SET schema_version = {CharacterJson.CurrentSchemaVersion + 1}");

        Assert.Throws<CharacterFormatException>(() => repo.Load(c.Id));
    }

    [Fact]
    public void Load_CorruptRow_ThrowsFormatException_WithoutBreakingList()
    {
        var repo = NewRepository();
        var good = new Character();
        var bad = new Character();
        repo.Save(good);
        repo.Save(bad);
        ExecuteSql($"UPDATE characters SET data = '{{broken' WHERE id = '{bad.Id}'");

        Assert.Throws<CharacterFormatException>(() => repo.Load(bad.Id));
        Assert.NotNull(repo.Load(good.Id));
        Assert.Equal(2, repo.List().Count);
    }

    [Fact]
    public void Backup_ProducesLoadableCopy()
    {
        var repo = NewRepository();
        var c = Samples.Rich();
        repo.Save(c);
        var backupPath = _dir.File("backup.db");
        repo.BackupTo(backupPath);

        var restored = new SqliteCharacterRepository(backupPath, NullLogger<SqliteCharacterRepository>.Instance);
        Assert.Equal("Arannis", restored.Load(c.Id)!.Identity.Name);
    }

    [Fact]
    public void Library_DuplicateCreatesIndependentCopy()
    {
        var repo = NewRepository();
        var library = new CharacterLibrary(repo, new PortableCharacterFile(),
            "1.0.0", NullLogger<CharacterLibrary>.Instance);
        var original = library.Create(new NewCharacterOptions("Hero", "Fighter", 3, "Human", "Soldier"));
        var copy = library.Duplicate(original);

        copy.Abilities.Strength = 20;
        repo.Save(copy);
        Assert.Equal(2, repo.List().Count);
        Assert.Equal(10, repo.Load(original.Id)!.Abilities.Strength);
        Assert.Equal("Hero (copy)", repo.Load(copy.Id)!.Identity.Name);

        var exportPath = _dir.File("hero.dndchar");
        library.Export(original, exportPath);
        var imported = library.Import(exportPath);
        Assert.Equal(3, repo.List().Count);
        Assert.Equal("Hero", imported.Identity.Name);
    }

    private void ExecuteSql(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={_dir.File("characters.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

public class ChangeTrackerTests
{
    [Fact]
    public void Session_ReportsChangesAnywhereInTheGraph()
    {
        var c = new Character();
        using var session = new CharacterSession(c);
        var changes = 0;
        session.Changed += (_, _) => changes++;
        Assert.False(session.IsDirty);

        c.Abilities.Strength = 14;
        c.SkillEntry(Skill.Stealth).Bonus = 2;
        var item = new Item { Name = "Rope" };
        c.Items.Add(item);
        item.Quantity = 2;
        item.Modifiers.Add(new Modifier());
        c.Spellcasting.SlotLevel(3).Expended = 0; // no-op: unchanged value
        c.Conditions.Add(Condition.Prone);

        Assert.Equal(6, changes);
        Assert.True(session.IsDirty);
        session.MarkSaved();
        Assert.False(session.IsDirty);
    }

    [Fact]
    public void Session_StopsTrackingRemovedItems()
    {
        var c = new Character();
        var item = new Item();
        c.Items.Add(item);
        using var session = new CharacterSession(c);
        var changes = 0;
        session.Changed += (_, _) => changes++;

        c.Items.Remove(item);
        item.Name = "gone";
        Assert.Equal(1, changes);
    }
}
