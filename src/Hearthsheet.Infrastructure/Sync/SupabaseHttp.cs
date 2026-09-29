using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hearthsheet.Core.Sync;

namespace Hearthsheet.Infrastructure.Sync;

/// <summary>Supabase project settings.</summary>
public sealed class SupabaseOptions
{
    /// <summary>Project URL, e.g. https://abcd.supabase.co. Public by design.</summary>
    public string Url { get; set; } = "";
    /// <summary>The anon or publishable key. Public by design: row-level security is the gatekeeper.</summary>
    public string AnonKey { get; set; } = "";
    /// <summary>Minutes between background syncs.</summary>
    public double IntervalMinutes { get; set; } = 5;

    /// <summary>True when the URL is an absolute https URL and a key is set.</summary>
    public bool IsConfigured =>
        Uri.TryCreate(Url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && !string.IsNullOrWhiteSpace(AnonKey);
}

/// <summary>A Supabase HTTP response: status and raw body.</summary>
public sealed record SupabaseResponse(HttpStatusCode Status, string Body)
{
    /// <summary>True for a 2xx status.</summary>
    public bool IsSuccess => (int)Status is >= 200 and < 300;

    /// <summary>The service's own error text (Auth and PostgREST use different field names).</summary>
    public string ErrorMessage()
    {
        try
        {
            if (JsonNode.Parse(Body) is JsonObject json)
            {
                foreach (var key in new[] { "error_description", "msg", "message", "error" })
                    if (json[key] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0) return text;
            }
        }
        catch (JsonException)
        {
        }
        return $"The cloud service returned {(int)Status}.";
    }
}

/// <summary>
/// The one HTTP client for Supabase. Only relative paths on the configured HTTPS host are accepted. Network
/// failures, timeouts and 5xx responses (including a paused free-tier project) become
/// <see cref="CloudUnavailableException"/>; everything else is returned for the caller to interpret.
/// </summary>
public sealed class SupabaseHttp
{
    private readonly HttpClient _http;
    private readonly string _apiKey;

    /// <summary>Creates the client; throws <see cref="ArgumentException"/> unless the options are configured for https.</summary>
    public SupabaseHttp(SupabaseOptions options, HttpMessageHandler? handler = null, string userAgentVersion = "1.0")
    {
        if (!options.IsConfigured)
            throw new ArgumentException("Sync:Url must be an https URL and Sync:AnonKey must be set.", nameof(options));
        _apiKey = options.AnonKey;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri(options.Url.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(20),
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Hearthsheet", userAgentVersion));
    }

    /// <param name="method">HTTP method.</param>
    /// <param name="path">Relative to the project URL, e.g. <c>auth/v1/token?grant_type=password</c>.</param>
    /// <param name="body">Optional JSON body.</param>
    /// <param name="accessToken">The user's access token; requests without a session send only the api key.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="prefer">Optional PostgREST <c>Prefer</c> header.</param>
    public async Task<SupabaseResponse> SendAsync(
        HttpMethod method, string path, JsonNode? body, string? accessToken, CancellationToken ct, string? prefer = null)
    {
        if (path.StartsWith('/') || !Uri.TryCreate(path, UriKind.Relative, out var relative))
            throw new ArgumentException("Supabase paths must be relative to the project URL.", nameof(path));

        var uri = new Uri(_http.BaseAddress!, relative);
        if (uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.Authority, _http.BaseAddress!.Authority, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Supabase paths must stay on the project host.", nameof(path));

        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Add("apikey", _apiKey);
        if (accessToken is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (prefer is not null) request.Headers.Add("Prefer", prefer);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        try
        {
            using var response = await _http.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if ((int)response.StatusCode >= 500)
                throw new CloudUnavailableException($"The cloud service returned {(int)response.StatusCode}.", offline: false);
            return new SupabaseResponse(response.StatusCode, text);
        }
        catch (HttpRequestException ex)
        {
            throw new CloudUnavailableException("The cloud service could not be reached.", offline: true, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new CloudUnavailableException("The cloud service did not respond in time.", offline: false, ex);
        }
    }
}
