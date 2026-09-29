using Hearthsheet.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace Hearthsheet.Infrastructure;

/// <summary>
/// Per-user locations. Data never lives next to the executable, so updates can replace the install
/// folder wholesale without touching characters, logs or user configuration.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string? dataDirectoryOverride = null)
    {
        Root = string.IsNullOrWhiteSpace(dataDirectoryOverride)
            ? DefaultRoot
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(dataDirectoryOverride));
        foreach (var dir in new[] { Root, Logs, Backups, Updates }) Directory.CreateDirectory(dir);
    }

    public static string DefaultRoot { get; } = ResolveDefaultRoot();

    /// <summary>
    /// Before the Hearthsheet rename the data folder was "DndSheet". Carry it over whenever the new folder has no
    /// database yet (not only when the folder is missing: anything, e.g. a failed first launch, may have created it).
    /// </summary>
    private static string ResolveDefaultRoot()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = Path.Combine(local, "Hearthsheet");
        MigrateLegacy(Path.Combine(local, "DndSheet"), root);
        return root;
    }

    public static void MigrateLegacy(string legacy, string root)
    {
        if (!File.Exists(Path.Combine(legacy, "characters.db")) || File.Exists(Path.Combine(root, "characters.db"))) return;
        try
        {
            Directory.CreateDirectory(root);
            foreach (var entry in new DirectoryInfo(legacy).EnumerateFileSystemInfos())
            {
                var target = Path.Combine(root, entry.Name);
                if (File.Exists(target) || Directory.Exists(target)) continue;
                if (entry is DirectoryInfo dir) dir.MoveTo(target); else ((FileInfo)entry).MoveTo(target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Old version still running: whatever was not moved stays in the old folder and the move retries next launch.
        }
    }

    public string Root { get; }
    public string Database => Path.Combine(Root, "characters.db");
    public string Logs => Path.Combine(Root, "logs");
    public string Backups => Path.Combine(Root, "backups");
    public string Updates => Path.Combine(Root, "updates");
    /// <summary>Optional per-user configuration overrides (e.g. DevelopmentMode) — no binary changes needed.</summary>
    /// <remarks>Always under the default root: it is read before any DataDirectory override is known.</remarks>
    public static string UserSettings => Path.Combine(DefaultRoot, "appsettings.user.json");
}

public static class DatabaseBackups
{
    /// <summary>Takes at most one backup per day and keeps the newest <paramref name="keep"/>.</summary>
    public static void RunDaily(SqliteCharacterRepository repository, string backupDirectory, int keep, ILogger logger)
    {
        var existing = new DirectoryInfo(backupDirectory).GetFiles("characters-*.db")
            .OrderByDescending(f => f.Name, StringComparer.Ordinal).ToList();
        if (existing.Count > 0 && existing[0].LastWriteTimeUtc > DateTime.UtcNow.AddDays(-1)) return;

        var path = Path.Combine(backupDirectory, $"characters-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db");
        repository.BackupTo(path);
        logger.LogInformation("Database backup written: {File}", Path.GetFileName(path));

        foreach (var old in existing.Skip(Math.Max(0, keep - 1)))
        {
            old.Delete();
            logger.LogDebug("Old backup removed: {File}", old.Name);
        }
    }
}
