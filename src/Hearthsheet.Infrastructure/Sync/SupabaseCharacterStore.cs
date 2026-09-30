using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hearthsheet.Core.Sync;

namespace Hearthsheet.Infrastructure.Sync;

/// <summary>
/// The <c>public.characters</c> table through PostgREST. Row-level security limits every call to the
/// signed-in user's rows; the server trigger owns <c>revision</c>, <c>updated_at</c> and <c>user_id</c>.
/// </summary>
public sealed class SupabaseCharacterStore(SupabaseHttp http, ICloudSession session) : ICloudCharacterStore
{
    private const string Table = "rest/v1/characters";
    private const string Columns = "select=id,name,revision,schema_version,data,deleted_at,updated_at";
    private const int PageSize = 500;

    public async Task<CloudRow?> InsertAsync(CloudUpload upload, CancellationToken ct)
    {
        var response = await SendAsync(HttpMethod.Post, $"{Table}?{Columns}", Body(upload), ct);
        return response.Status == HttpStatusCode.Conflict ? null : Rows(response).SingleOrDefault();
    }

    public async Task<CloudRow?> UpdateIfRevisionAsync(Guid id, long expectedRevision, CloudUpload upload, CancellationToken ct)
    {
        var body = Body(upload);
        body["deleted_at"] = null;
        var response = await SendAsync(HttpMethod.Patch, $"{Table}?id=eq.{id}&revision=eq.{expectedRevision}&{Columns}", body, ct);
        return Rows(response).SingleOrDefault();
    }

    public async Task<bool> TombstoneIfRevisionAsync(Guid id, long expectedRevision, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["data"] = null,
            ["deleted_at"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };
        var response = await SendAsync(HttpMethod.Patch, $"{Table}?id=eq.{id}&revision=eq.{expectedRevision}&select=id", body, ct);
        return Ids(response).Count > 0;
    }

    public async Task<CloudRow?> GetAsync(Guid id, CancellationToken ct) =>
        Rows(await SendAsync(HttpMethod.Get, $"{Table}?id=eq.{id}&{Columns}", null, ct)).SingleOrDefault();

    public async Task<IReadOnlyList<CloudRow>> ChangedSinceAsync(DateTimeOffset? since, CancellationToken ct)
    {
        var filter = since is { } s ? "&updated_at=gt." + Cursor(s) : "";
        var rows = new List<CloudRow>();
        while (true)
        {
            // Keyset paging: an offset would skip rows when another device edits one between page fetches.
            var after = rows.Count == 0 ? "" : KeysetFilter(rows[^1]);
            var page = Rows(await SendAsync(HttpMethod.Get,
                $"{Table}?{Columns}{filter}{after}&order=updated_at.asc,id.asc&limit={PageSize}", null, ct));
            rows.AddRange(page);
            if (page.Count < PageSize) return rows;
        }
    }

    private static string Cursor(DateTimeOffset value) =>
        Uri.EscapeDataString(value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", CultureInfo.InvariantCulture));

    private static string KeysetFilter(CloudRow last)
    {
        var t = Cursor(last.UpdatedAt);
        return $"&or=(updated_at.gt.{t},and(updated_at.eq.{t},id.gt.{last.Id}))";
    }

    public async Task<IReadOnlySet<Guid>> ListAllIdsAsync(CancellationToken ct)
    {
        var ids = new HashSet<Guid>();
        for (var offset = 0; ; offset += PageSize)
        {
            var page = Ids(await SendAsync(HttpMethod.Get, $"{Table}?select=id&order=id.asc&limit={PageSize}&offset={offset}", null, ct));
            ids.UnionWith(page);
            if (page.Count < PageSize) return ids;
        }
    }

    /// <summary>Sends with the user's token; after a 401 refreshes once and retries.</summary>
    private async Task<SupabaseResponse> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        var response = await http.SendAsync(method, path, body, await session.GetAccessTokenAsync(ct), ct, "return=representation");
        if (response.Status != HttpStatusCode.Unauthorized) return response;
        session.InvalidateAccessToken();
        response = await http.SendAsync(method, path, body, await session.GetAccessTokenAsync(ct), ct, "return=representation");
        if (response.Status == HttpStatusCode.Unauthorized)
            throw new CloudSignedOutException("The cloud service rejected your session. Sign in again.");
        return response;
    }

    private static JsonObject Body(CloudUpload upload) => new()
    {
        ["id"] = upload.Id.ToString(),
        ["name"] = upload.Name,
        ["description"] = upload.Description,
        ["schema_version"] = upload.SchemaVersion,
        ["data"] = JsonNode.Parse(upload.Data),
    };

    private static List<CloudRow> Rows(SupabaseResponse response) => Parse(response, e => new CloudRow(
        e.GetProperty("id").GetGuid(),
        e.GetProperty("name").GetString() ?? "",
        e.GetProperty("revision").GetInt64(),
        e.GetProperty("schema_version").GetInt32(),
        e.GetProperty("data") is { ValueKind: not JsonValueKind.Null } data ? data.GetRawText() : null,
        e.GetProperty("deleted_at") is { ValueKind: JsonValueKind.String } deleted ? deleted.GetDateTimeOffset() : null,
        e.GetProperty("updated_at").GetDateTimeOffset()));

    private static List<Guid> Ids(SupabaseResponse response) => Parse(response, e => e.GetProperty("id").GetGuid());

    private static List<T> Parse<T>(SupabaseResponse response, Func<JsonElement, T> read)
    {
        if (!response.IsSuccess) throw new CloudRequestException(response.ErrorMessage());
        try
        {
            using var document = JsonDocument.Parse(response.Body);
            return document.RootElement.EnumerateArray().Select(read).ToList();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new CloudRequestException("The cloud service returned an unexpected response.", ex);
        }
    }
}
