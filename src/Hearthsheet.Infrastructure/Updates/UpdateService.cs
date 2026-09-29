using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Hearthsheet.Infrastructure.Updates;

public sealed record UpdateCheckResult(SemVersion Current, ReleaseInfo? Latest)
{
    public bool IsUpdateAvailable => Latest is not null && Latest.Version > Current;
}

public sealed record StagedUpdate(SemVersion Version, string Directory);

/// <summary>
/// Check → download → verify → stage → hand off to Hearthsheet.Updater (which replaces the install
/// folder after this process exits and rolls back on failure).
/// Release contract: each release tag vX.Y.Z carries Hearthsheet-X.Y.Z-win-x64.zip and SHA256SUMS.txt.
/// </summary>
public sealed class UpdateService(
    GitHubReleaseSource source, UpdateOptions options, SemVersion currentVersion, string updatesRoot, ILogger<UpdateService> logger)
{
    public const string AppExecutable = "Hearthsheet.exe";
    public const string UpdaterExecutable = "Hearthsheet.Updater.exe";
    public const string ChecksumAsset = "SHA256SUMS.txt";
    private const long MaxPackageBytes = 500L * 1024 * 1024;
    private const long MaxChecksumBytes = 64 * 1024;

    public SemVersion CurrentVersion => currentVersion;

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        logger.LogInformation("Checking for updates ({Owner}/{Repo}, current {Version})", options.Owner, options.Repository, currentVersion);
        var releases = await source.GetReleasesAsync(ct);
        var latest = releases
            .Where(r => options.AllowPreRelease || !r.PreRelease)
            .Where(r => FindPackage(r) is not null)
            .MaxBy(r => r.Version);
        var result = new UpdateCheckResult(currentVersion, latest);
        logger.LogInformation("Update check complete: latest {Latest}, update available: {Available}",
            latest?.Version.ToString() ?? "none", result.IsUpdateAvailable);
        return result;
    }

    public static ReleaseAsset? FindPackage(ReleaseInfo release) =>
        release.Assets.FirstOrDefault(a =>
            a.Name.Equals($"Hearthsheet-{release.Version}-win-x64.zip", StringComparison.OrdinalIgnoreCase));

    public async Task<StagedUpdate> DownloadAndStageAsync(ReleaseInfo release, IProgress<double>? progress, CancellationToken ct = default)
    {
        var package = FindPackage(release) ?? throw new UpdateException("The release does not contain a Windows package.");
        var checksumAsset = release.Assets.FirstOrDefault(a => a.Name.Equals(ChecksumAsset, StringComparison.OrdinalIgnoreCase))
                            ?? throw new UpdateException("The release has no checksum file; refusing to install an unverifiable update.");

        foreach (var old in Directory.GetDirectories(updatesRoot)) TryDeleteDirectory(old);
        var workDir = Directory.CreateDirectory(Path.Combine(updatesRoot, release.Version.ToString())).FullName;

        using var checksumStream = new MemoryStream();
        await source.DownloadAssetAsync(checksumAsset, checksumStream, MaxChecksumBytes, null, ct);
        var checksums = ParseChecksums(System.Text.Encoding.UTF8.GetString(checksumStream.ToArray()));
        if (!checksums.TryGetValue(package.Name, out var expectedHash))
            throw new UpdateException($"The checksum file does not list {package.Name}.");

        var zipPath = Path.Combine(workDir, "package.zip");
        logger.LogInformation("Downloading update {Version} ({Bytes} bytes)", release.Version, package.Size);
        await using (var file = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await source.DownloadAssetAsync(package, file, MaxPackageBytes, progress, ct);
        }

        var actualHash = await ComputeSha256Async(zipPath, ct);
        if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(zipPath);
            logger.LogError("Update checksum mismatch for {Package}", package.Name);
            throw new UpdateException("The downloaded update failed verification (checksum mismatch) and was discarded.");
        }
        logger.LogInformation("Update package verified (SHA-256)");

        var stageDir = Path.Combine(workDir, "staged");
        try
        {
            // ExtractToDirectory rejects entries that would escape the destination (zip-slip).
            ZipFile.ExtractToDirectory(zipPath, stageDir);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            throw new UpdateException("The update package is corrupt.", ex);
        }
        File.Delete(zipPath);

        VerifyStagedPackage(stageDir, release.Version);
        logger.LogInformation("Update {Version} staged at {Dir}", release.Version, stageDir);
        return new StagedUpdate(release.Version, stageDir);
    }

    /// <summary>Parses "hash  filename" lines (sha256sum / Get-FileHash output style).</summary>
    public static Dictionary<string, string> ParseChecksums(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var parts = raw.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || parts[0].Length != 64 || !parts[0].All(Uri.IsHexDigit)) continue;
            result[parts[1].TrimStart('*').Trim()] = parts[0];
        }
        return result;
    }

    public static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
    }

    /// <summary>The staged folder must contain the app and updater, and actually be the advertised version.</summary>
    public static void VerifyStagedPackage(string stageDir, SemVersion expected)
    {
        foreach (var required in new[] { AppExecutable, UpdaterExecutable, "Hearthsheet.dll" })
        {
            if (!File.Exists(Path.Combine(stageDir, required)))
                throw new UpdateException($"The update package is incomplete (missing {required}).");
        }
        var productVersion = FileVersionInfo.GetVersionInfo(Path.Combine(stageDir, "Hearthsheet.dll")).ProductVersion;
        if (!SemVersion.TryParse(productVersion, out var actual) || actual != expected)
            throw new UpdateException($"The update package reports version {productVersion}, expected {expected}.");
    }

    /// <summary>
    /// Copies the updater out of the install folder (so it can overwrite it) and starts it. The caller
    /// must exit promptly afterwards; the updater waits for <paramref name="processId"/> to end.
    /// </summary>
    public void LaunchInstaller(StagedUpdate staged, string installDirectory, int processId, string logDirectory)
    {
        var updaterFiles = Directory.GetFiles(installDirectory, "Hearthsheet.Updater*");
        if (!updaterFiles.Any(f => Path.GetFileName(f).Equals(UpdaterExecutable, StringComparison.OrdinalIgnoreCase)))
            throw new UpdateException("The updater is missing from the installation. Reinstall the application from the release page.");

        var runner = Directory.CreateDirectory(Path.Combine(updatesRoot, "runner-" + Guid.NewGuid().ToString("N"))).FullName;
        foreach (var file in updaterFiles) File.Copy(file, Path.Combine(runner, Path.GetFileName(file)));

        var start = new ProcessStartInfo(Path.Combine(runner, UpdaterExecutable)) { UseShellExecute = false };
        foreach (var arg in new[]
                 {
                     "--pid", processId.ToString(), "--source", staged.Directory, "--target", installDirectory,
                     "--exe", AppExecutable, "--log", logDirectory,
                 })
        {
            start.ArgumentList.Add(arg);
        }
        Process.Start(start);
        logger.LogInformation("Updater launched for version {Version}", staged.Version);
    }

    private void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug("Could not remove old update folder {Dir}: {Error}", Path.GetFileName(path), ex.Message);
        }
    }
}
