using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Hearthsheet.Infrastructure.Logging;
using Hearthsheet.Infrastructure.Security;
using Hearthsheet.Infrastructure.Updates;
using Hearthsheet.Updater;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hearthsheet.Tests;

public class SemVersionTests
{
    [Theory]
    [InlineData("1.0.0", "1.0.1")]
    [InlineData("1.9.0", "1.10.0")]
    [InlineData("v1.2.3", "2.0.0")]
    public void Ordering(string lower, string higher)
    {
        Assert.True(Parse(lower) < Parse(higher));
        Assert.True(Parse(higher) > Parse(lower));
    }

    [Fact]
    public void LeadingVAndBuildMetadataAreIgnored() => Assert.Equal(Parse("1.2.3"), Parse("V1.2.3+abc"));

    private static Version Parse(string text) =>
        UpdateService.TryParseVersion(text, out var v) ? v : throw new FormatException(text);

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("1.2.3.4")]
    [InlineData("1.-2.3")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-beta")]
    [InlineData("1.2")]
    [InlineData("1..3")]
    public void RejectsInvalid(string text) => Assert.False(UpdateService.TryParseVersion(text, out _));
}

public class UpdateServiceTests : IDisposable
{
    private const string Owner = "owner";
    private const string Repo = "repo";
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private static readonly UpdateOptions Options = new() { Owner = Owner, Repository = Repo };

    private static string ReleasesJson(params (string tag, bool pre, bool draft)[] releases) =>
        "[" + string.Join(",", releases.Select((r, i) => $$"""
            {"tag_name":"{{r.tag}}","name":"{{r.tag}}","body":"notes","draft":{{(r.draft ? "true" : "false")}},"prerelease":{{(r.pre ? "true" : "false")}},
             "assets":[
               {"id":{{i * 10 + 1}},"name":"Hearthsheet-{{r.tag.TrimStart('v')}}-win-x64.zip","size":100,"url":"https://api.github.com/repos/owner/repo/releases/assets/{{i * 10 + 1}}"},
               {"id":{{i * 10 + 2}},"name":"SHA256SUMS.txt","size":100,"url":"https://api.github.com/repos/owner/repo/releases/assets/{{i * 10 + 2}}"}]}
            """)) + "]";

    private UpdateService Service(StubHandler handler, string current = "1.0.0", UpdateOptions? options = null)
    {
        var source = new GitHubReleaseSource(options ?? Options, handler);
        return new UpdateService(source, options ?? Options, Version.Parse(current), _dir.Path, NullLogger<UpdateService>.Instance);
    }

    [Fact]
    public async Task Check_FindsNewestStableRelease_IgnoringDraftsAndPreReleases()
    {
        var handler = new StubHandler(_ => Json(ReleasesJson(("v1.1.0", false, false), ("v1.3.0", false, true), ("v1.2.0-beta", true, false), ("v0.9.0", false, false))));
        var result = await Service(handler).CheckAsync();
        Assert.True(result.IsUpdateAvailable);
        Assert.Equal("1.1.0", result.Latest!.Version.ToString());
    }

    [Fact]
    public async Task Check_ReportsNoUpdateWhenCurrentIsNewest()
    {
        var handler = new StubHandler(_ => Json(ReleasesJson(("v1.0.0", false, false))));
        Assert.False((await Service(handler).CheckAsync()).IsUpdateAvailable);
    }

    [Fact]
    public async Task Check_UnconfiguredSource_Throws()
    {
        var handler = new StubHandler(_ => Json("[]"));
        await Assert.ThrowsAsync<UpdateException>(() => Service(handler, options: new UpdateOptions()).CheckAsync());
    }

    [Fact]
    public async Task Check_HttpErrorsBecomeUpdateExceptions()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var ex = await Assert.ThrowsAsync<UpdateException>(() => Service(handler).CheckAsync());
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task Download_FollowsRedirectToTrustedHost()
    {
        var zip = BuildPackage();
        var sums = $"{Sha(zip)}  Hearthsheet-{RepoVersion}-win-x64.zip\n";
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/releases")) return Json(ReleasesJson(("v" + RepoVersion, false, false)));
            if (req.RequestUri.Host == "api.github.com")
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://objects.githubusercontent.com/file" + req.RequestUri.AbsolutePath.Split('/').Last());
                return redirect;
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(req.RequestUri.AbsolutePath.EndsWith("2") ? Encoding.UTF8.GetBytes(sums) : zip),
            };
        });

        var service = Service(handler, current: "0.0.1");
        var release = (await service.CheckAsync()).Latest!;
        var staged = await service.DownloadAndStageAsync(release, null);

        Assert.True(File.Exists(Path.Combine(staged.Directory, "Hearthsheet.exe")));
        Assert.Contains("objects.githubusercontent.com", handler.Requests);
    }

    [Fact]
    public async Task Download_RejectsChecksumMismatch()
    {
        var handler = PackageHandler("99.0.0", BuildPackage(), $"{new string('0', 64)}  Hearthsheet-99.0.0-win-x64.zip");
        var service = Service(handler);
        var release = (await service.CheckAsync()).Latest!;
        var ex = await Assert.ThrowsAsync<UpdateException>(() => service.DownloadAndStageAsync(release, null));
        Assert.Contains("checksum", ex.Message);
    }

    [Fact]
    public async Task Download_RejectsMissingChecksumEntry()
    {
        var handler = PackageHandler("99.0.0", BuildPackage(), $"{new string('0', 64)}  something-else.zip");
        var service = Service(handler);
        var release = (await service.CheckAsync()).Latest!;
        await Assert.ThrowsAsync<UpdateException>(() => service.DownloadAndStageAsync(release, null));
    }

    [Fact]
    public async Task Download_RejectsPackageWhoseContentsAreAnotherVersion()
    {
        var zip = BuildPackage(); // contains RepoVersion binaries, advertised as 99.0.0
        var handler = PackageHandler("99.0.0", zip, $"{Sha(zip)}  Hearthsheet-99.0.0-win-x64.zip");
        var service = Service(handler);
        var release = (await service.CheckAsync()).Latest!;
        var ex = await Assert.ThrowsAsync<UpdateException>(() => service.DownloadAndStageAsync(release, null));
        Assert.Contains("expected 99.0.0", ex.Message);
    }

    [Fact]
    public async Task Download_RejectsCorruptZip()
    {
        var zip = Encoding.UTF8.GetBytes("this is not a zip");
        var handler = PackageHandler("99.0.0", zip, $"{Sha(zip)}  Hearthsheet-99.0.0-win-x64.zip");
        var service = Service(handler);
        var release = (await service.CheckAsync()).Latest!;
        await Assert.ThrowsAsync<UpdateException>(() => service.DownloadAndStageAsync(release, null));
    }

    [Theory]
    [InlineData("http://api.github.com/x")]
    [InlineData("https://evil.example.com/x")]
    [InlineData("https://githubusercontent.com.evil.net/x")]
    public void UntrustedHosts_AreRejected(string url) =>
        Assert.Throws<UpdateException>(() => GitHubReleaseSource.EnsureTrustedHost(new Uri(url)));

    [Fact]
    public void ParseChecksums_AcceptsCommonFormats()
    {
        var hash = new string('a', 64);
        var parsed = UpdateService.ParseChecksums($"{hash}  file.zip\r\n{hash.ToUpperInvariant()} *other.zip\nnot a line\n");
        Assert.Equal(hash, parsed["file.zip"]);
        Assert.True(parsed.ContainsKey("other.zip"));
        Assert.Equal(2, parsed.Count);
    }

    private static StubHandler PackageHandler(string version, byte[] zip, string sums) => new(req =>
    {
        var path = req.RequestUri!.AbsolutePath;
        if (path.EndsWith("/releases")) return Json(ReleasesJson(("v" + version, false, false)));
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(path.EndsWith("/2") ? Encoding.UTF8.GetBytes(sums) : zip),
        };
    });

    /// <summary>The package's Hearthsheet.dll is the Updater assembly, whose ProductVersion is the repo version.</summary>
    private static readonly string RepoVersion =
        System.Diagnostics.FileVersionInfo.GetVersionInfo(typeof(UpdateInstaller).Assembly.Location).ProductVersion!;

    private static byte[] BuildPackage()
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntry("Hearthsheet.exe").Open().Dispose();
            archive.CreateEntry("Hearthsheet.Updater.exe").Open().Dispose();
            using var stream = archive.CreateEntry("Hearthsheet.dll").Open();
            stream.Write(File.ReadAllBytes(typeof(UpdateInstaller).Assembly.Location));
        }
        return ms.ToArray();
    }
    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public class InstallerTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private string MakeDir(string name, params (string file, string content)[] files)
    {
        var path = Directory.CreateDirectory(_dir.File(name)).FullName;
        foreach (var (file, content) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(path, file))!);
            File.WriteAllText(Path.Combine(path, file), content);
        }
        return path;
    }

    [Fact]
    public void Install_ReplacesInstallationAndRemovesBackup()
    {
        var target = MakeDir("app", ("Hearthsheet.exe", "old"), ("stale.dll", "old"));
        var source = MakeDir("staged", ("Hearthsheet.exe", "new"), ("sub/data.txt", "new"));
        using var log = new UpdaterLog(_dir.File("logs"));

        var result = new UpdateInstaller(log).Install(source, target, "Hearthsheet.exe");

        Assert.True(result.Succeeded);
        Assert.Equal("new", File.ReadAllText(Path.Combine(target, "Hearthsheet.exe")));
        Assert.True(File.Exists(Path.Combine(target, "sub", "data.txt")));
        Assert.False(File.Exists(Path.Combine(target, "stale.dll")));
        Assert.False(Directory.Exists(target + ".backup"));
    }

    [Fact]
    public void Install_RollsBackWhenCopyFails()
    {
        var target = MakeDir("app", ("Hearthsheet.exe", "old"), ("keep.dll", "old"));
        var source = MakeDir("staged", ("Hearthsheet.exe", "new"), ("locked.dll", "new"));
        using var log = new UpdaterLog(_dir.File("logs"));

        // Hold a file open without sharing so the copy fails midway.
        using var blocker = new FileStream(Path.Combine(source, "locked.dll"), FileMode.Open, FileAccess.Read, FileShare.None);
        var result = new UpdateInstaller(log).Install(source, target, "Hearthsheet.exe");

        Assert.False(result.Succeeded);
        Assert.Equal("old", File.ReadAllText(Path.Combine(target, "Hearthsheet.exe")));
        Assert.True(File.Exists(Path.Combine(target, "keep.dll")));
    }

    [Fact]
    public void Install_RefusesStagingWithoutExecutable()
    {
        var target = MakeDir("app", ("Hearthsheet.exe", "old"));
        var source = MakeDir("staged", ("readme.txt", "x"));
        using var log = new UpdaterLog(_dir.File("logs"));
        Assert.False(new UpdateInstaller(log).Install(source, target, "Hearthsheet.exe").Succeeded);
        Assert.Equal("old", File.ReadAllText(Path.Combine(target, "Hearthsheet.exe")));
    }

    [Theory]
    [InlineData("--pid", "1", "--source", "a", "--target", "b", "--exe", "..\\evil.exe")]
    [InlineData("--pid", "1", "--source", "a", "--target", "b", "--exe", "run.bat")]
    [InlineData("--pid", "x", "--source", "a", "--target", "b")]
    [InlineData("--source", "a", "--target", "b")]
    public void UpdaterOptions_RejectBadArguments(params string[] args) => Assert.Null(UpdaterOptions.Parse(args));
}

public class RedactionTests
{
    [Theory]
    [InlineData("token ghp_abcdefghijklmnopqrstuvwxyz0123 used")]
    [InlineData("pat github_pat_11ABCDEFG0123456789_abcdefghijklmnop")]
    [InlineData("Authorization: Bearer abc.def")]
    [InlineData("password=hunter2")]
    [InlineData("session eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.abcdefghijklmnop")]
    [InlineData("{\"refresh_token\":\"abcdefghijklmnop\"}")]
    public void SecretsAreRedacted(string text)
    {
        var redacted = SecretRedactor.Redact(text);
        Assert.Contains("[REDACTED]", redacted);
        Assert.DoesNotContain("hunter2", redacted);
        Assert.DoesNotContain("abcdefghijklmnop", redacted);
        Assert.DoesNotContain("abc.def", redacted);
    }

    [Fact]
    public void OrdinaryMessagesAreUntouched() =>
        Assert.Equal("Character saved: Token the Bard", SecretRedactor.Redact("Character saved: Token the Bard"));
}

internal sealed class FakeSecrets : Dictionary<string, string>, ISecretStore
{
    public string? Read(string key) => TryGetValue(key, out var v) ? v : null;
    public void Write(string key, string secret) => this[key] = secret;
    public void Delete(string key) => Remove(key);
}

internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<string> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!.Host);
        return Task.FromResult(respond(request));
    }
}
