using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using DndSheet.Infrastructure.Security;

namespace DndSheet.Infrastructure.Updates;

public sealed class UpdateOptions
{
    public bool Enabled { get; set; } = true;
    public bool CheckOnStartup { get; set; } = true;
    /// <summary>GitHub owner/organization that publishes releases.</summary>
    public string Owner { get; set; } = "";
    public string Repository { get; set; } = "";
    public bool AllowPreRelease { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Owner) && !string.IsNullOrWhiteSpace(Repository);
}

public sealed record ReleaseAsset(long Id, string Name, long Size, string ApiUrl);

public sealed record ReleaseInfo(SemVersion Version, string Tag, string Name, string Notes, bool PreRelease, IReadOnlyList<ReleaseAsset> Assets);

public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Reads versioned release artifacts from the GitHub Releases API. Public repositories need no
/// credentials; for a private repository (development) an optional fine-grained PAT with read-only
/// "Contents" access is read from the OS secret store and sent only to api.github.com.
/// </summary>
public sealed class GitHubReleaseSource
{
    public const string TokenKey = "GitHubToken";
    private const string ApiHost = "api.github.com";
    private const int MaxRedirects = 5;

    private readonly HttpClient _http;
    private readonly UpdateOptions _options;
    private readonly ISecretStore? _secrets;

    /// <param name="handler">Must not auto-follow redirects; redirects are followed manually so the token is never forwarded.</param>
    public GitHubReleaseSource(UpdateOptions options, ISecretStore? secrets, HttpMessageHandler? handler = null, string userAgentVersion = "1.0")
    {
        _options = options;
        _secrets = secrets;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DndSheet", userAgentVersion));
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<IReadOnlyList<ReleaseInfo>> GetReleasesAsync(CancellationToken ct)
    {
        if (!_options.IsConfigured) throw new UpdateException("No update source is configured (Updates:Owner / Updates:Repository).");
        var url = $"https://{ApiHost}/repos/{Uri.EscapeDataString(_options.Owner)}/{Uri.EscapeDataString(_options.Repository)}/releases?per_page=20";

        using var response = await SendAsync(url, "application/vnd.github+json", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new UpdateException("The update repository was not found. If it is private, a GitHub token is required.");
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new UpdateException($"GitHub refused the request ({(int)response.StatusCode}). The token may be invalid or rate limits exceeded.");
        if (!response.IsSuccessStatusCode)
            throw new UpdateException($"GitHub returned {(int)response.StatusCode} while checking for updates.");

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        List<GitHubRelease>? releases;
        try
        {
            releases = await JsonSerializer.DeserializeAsync<List<GitHubRelease>>(stream, cancellationToken: ct);
        }
        catch (JsonException ex)
        {
            throw new UpdateException("GitHub returned an unexpected response.", ex);
        }

        var result = new List<ReleaseInfo>();
        foreach (var r in releases ?? [])
        {
            if (r.Draft || !SemVersion.TryParse(r.TagName, out var version)) continue;
            result.Add(new ReleaseInfo(version, r.TagName ?? "", r.Name ?? r.TagName ?? "", r.Body ?? "", r.PreRelease,
                (r.Assets ?? []).Select(a => new ReleaseAsset(a.Id, a.Name ?? "", a.Size, a.Url ?? "")).ToList()));
        }
        return result;
    }

    /// <summary>Downloads an asset via the API URL (works for public and private repos) into <paramref name="destination"/>.</summary>
    public async Task DownloadAssetAsync(ReleaseAsset asset, Stream destination, long maxBytes, IProgress<double>? progress, CancellationToken ct)
    {
        if (asset.Size > maxBytes) throw new UpdateException($"Update file {asset.Name} is unexpectedly large.");
        using var response = await SendAsync(asset.ApiUrl, "application/octet-stream", ct);
        if (!response.IsSuccessStatusCode)
            throw new UpdateException($"Download of {asset.Name} failed ({(int)response.StatusCode}).");

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > maxBytes) throw new UpdateException($"Update file {asset.Name} exceeded its expected size.");
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            if (asset.Size > 0) progress?.Report((double)total / asset.Size);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string url, string accept, CancellationToken ct)
    {
        var uri = new Uri(url);
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            EnsureTrustedHost(uri);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd(accept);
            // The token is attached only for the GitHub API host, never for redirect targets (asset CDN).
            if (uri.Host.Equals(ApiHost, StringComparison.OrdinalIgnoreCase) && ReadToken() is { } token)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (HttpRequestException ex)
            {
                throw new UpdateException("Could not reach GitHub. Check your internet connection.", ex);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new UpdateException("The request to GitHub timed out.", ex);
            }

            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                response.Dispose();
                continue;
            }
            return response;
        }
        throw new UpdateException("Too many redirects while contacting GitHub.");
    }

    private string? ReadToken()
    {
        try
        {
            var token = _secrets?.Read(TokenKey);
            return string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>Only HTTPS GitHub hosts are contacted, even if a response redirects elsewhere.</summary>
    public static void EnsureTrustedHost(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();
        var trusted = host is "api.github.com" or "github.com" || host.EndsWith(".githubusercontent.com", StringComparison.Ordinal);
        if (uri.Scheme != Uri.UriSchemeHttps || !trusted)
            throw new UpdateException($"Refusing to contact untrusted update host '{uri.Host}'.");
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool PreRelease { get; set; }
        [JsonPropertyName("assets")] public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
    }
}
