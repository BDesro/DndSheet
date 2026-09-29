namespace Hearthsheet.Updater;

public sealed record InstallResult(bool Succeeded, string? Error);

public sealed record UpdaterOptions(int ProcessId, string SourceDirectory, string TargetDirectory, string Executable, string LogDirectory)
{
    public static UpdaterOptions? Parse(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i + 1 < args.Length; i += 2) map[args[i]] = args[i + 1];
        if (!map.TryGetValue("--pid", out var pidText) || !int.TryParse(pidText, out var pid)) return null;
        if (!map.TryGetValue("--source", out var source) || !map.TryGetValue("--target", out var target)) return null;
        var exe = map.GetValueOrDefault("--exe", "Hearthsheet.exe");
        // The executable is a bare file name inside the target folder, never a path.
        if (exe != Path.GetFileName(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        return new UpdaterOptions(pid, Path.GetFullPath(source), Path.GetFullPath(target), exe,
            map.GetValueOrDefault("--log", Path.GetTempPath()));
    }
}

/// <summary>
/// Backup → replace → verify, restoring the backup on any failure so a broken update never leaves
/// a half-written installation behind.
/// </summary>
public sealed class UpdateInstaller(UpdaterLog log)
{
    public InstallResult Install(string sourceDir, string targetDir, string executable)
    {
        if (!File.Exists(Path.Combine(sourceDir, executable)))
            return new InstallResult(false, "Staged update does not contain the application executable.");
        if (!Directory.Exists(targetDir))
            return new InstallResult(false, "Install directory does not exist.");

        var backupDir = targetDir.TrimEnd(Path.DirectorySeparatorChar) + ".backup";
        try
        {
            if (Directory.Exists(backupDir)) Directory.Delete(backupDir, recursive: true);
            CopyDirectory(targetDir, backupDir);
            log.Info("Backed up current installation");
        }
        catch (Exception ex)
        {
            // Nothing has been changed yet; abort without touching the install.
            return new InstallResult(false, "Could not back up the current installation: " + ex.Message);
        }

        try
        {
            ClearDirectory(targetDir);
            CopyDirectory(sourceDir, targetDir);
            if (!File.Exists(Path.Combine(targetDir, executable)))
                throw new IOException("Executable missing after copy.");
            Directory.Delete(backupDir, recursive: true);
            TryDelete(sourceDir);
            return new InstallResult(true, null);
        }
        catch (Exception ex)
        {
            log.Error("Install failed, restoring backup: " + ex.Message);
            try
            {
                ClearDirectory(targetDir);
                CopyDirectory(backupDir, targetDir);
                Directory.Delete(backupDir, recursive: true);
            }
            catch (Exception restoreEx)
            {
                log.Error($"Restore failed; the previous version remains at {backupDir}: {restoreEx.Message}");
            }
            return new InstallResult(false, ex.Message);
        }
    }

    private static void ClearDirectory(string dir)
    {
        foreach (var file in Directory.GetFiles(dir)) File.Delete(file);
        foreach (var sub in Directory.GetDirectories(dir)) Directory.Delete(sub, recursive: true);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (var sub in Directory.GetDirectories(source))
            CopyDirectory(sub, Path.Combine(destination, Path.GetFileName(sub)));
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public sealed class UpdaterLog : IDisposable
{
    private readonly StreamWriter? _writer;

    public UpdaterLog(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            _writer = new StreamWriter(Path.Combine(directory, "updater.log"), append: true) { AutoFlush = true };
        }
        catch (IOException)
        {
            _writer = null;
        }
    }

    public void Info(string message) => Write("INFO", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message) =>
        _writer?.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}");

    public void Dispose() => _writer?.Dispose();
}
