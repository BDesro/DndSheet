using Hearthsheet.Infrastructure;

namespace Hearthsheet.Tests;

public class MigrationTests
{
    [Fact]
    public void LegacyData_MovesIntoExistingEmptyRoot()
    {
        using var dir = new TempDir();
        var legacy = Directory.CreateDirectory(dir.File("DndSheet")).FullName;
        var root = Directory.CreateDirectory(dir.File("Hearthsheet")).FullName;
        Directory.CreateDirectory(Path.Combine(root, "logs")); // a first launch may have created these
        File.WriteAllText(Path.Combine(legacy, "characters.db"), "old");
        Directory.CreateDirectory(Path.Combine(legacy, "backups"));

        AppPaths.MigrateLegacy(legacy, root);

        Assert.Equal("old", File.ReadAllText(Path.Combine(root, "characters.db")));
        Assert.True(Directory.Exists(Path.Combine(root, "backups")));
    }

    [Fact]
    public void ExistingDatabase_IsNeverOverwritten()
    {
        using var dir = new TempDir();
        var legacy = Directory.CreateDirectory(dir.File("DndSheet")).FullName;
        var root = Directory.CreateDirectory(dir.File("Hearthsheet")).FullName;
        File.WriteAllText(Path.Combine(legacy, "characters.db"), "old");
        File.WriteAllText(Path.Combine(root, "characters.db"), "new");

        AppPaths.MigrateLegacy(legacy, root);

        Assert.Equal("new", File.ReadAllText(Path.Combine(root, "characters.db")));
        Assert.True(File.Exists(Path.Combine(legacy, "characters.db")));
    }
}
