using DndSheet.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace DndSheet.Infrastructure;

/// <summary>
/// Per-user locations. Data never lives next to the executable, so updates can replace the install
/// folder wholesale without touching characters, logs or user configuration.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string? dataDirectoryOverride = null)
    {
        Root = string.IsNullOrWhiteSpace(dataDirectoryOverride)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DndSheet")
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(dataDirectoryOverride));
        foreach (var dir in new[] { Root, Logs, Backups, Updates }) Directory.CreateDirectory(dir);
    }

    public string Root { get; }
    public string Database => Path.Combine(Root, "characters.db");
    public string Logs => Path.Combine(Root, "logs");
    public string Backups => Path.Combine(Root, "backups");
    public string Updates => Path.Combine(Root, "updates");
    /// <summary>Optional per-user configuration overrides (e.g. DevelopmentMode) — no binary changes needed.</summary>
    public string UserSettings => Path.Combine(Root, "appsettings.user.json");
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
