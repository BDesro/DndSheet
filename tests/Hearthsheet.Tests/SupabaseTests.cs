using System.Net;
using System.Text;
using Hearthsheet.Core.Sync;
using Hearthsheet.Infrastructure.Sync;

namespace Hearthsheet.Tests;

internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Body, string? Bearer, string? ApiKey, string? Prefer)
{
    public string PathAndQuery => Uri.PathAndQuery;
}

internal sealed class SupabaseStub(Func<RecordedRequest, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<RecordedRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var recorded = new RecordedRequest(
            request.Method, request.RequestUri!,
            request.Content is null ? null : await request.Content.ReadAsStringAsync(ct),
            request.Headers.Authorization?.Parameter,
            request.Headers.TryGetValues("apikey", out var key) ? key.Single() : null,
            request.Headers.TryGetValues("Prefer", out var prefer) ? prefer.Single() : null);
        Requests.Add(recorded);
        return respond(recorded);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static string Session(string access, string refresh, int expiresIn = 3600) =>
        $$$"""{"access_token":"{{{access}}}","token_type":"bearer","expires_in":{{{expiresIn}}},"refresh_token":"{{{refresh}}}","user":{"id":"user-1","email":"a@b.c"}}""";
}

public class SupabaseAuthTests
{
    internal static readonly SupabaseOptions Options = new() { Url = "https://proj.supabase.co", AnonKey = "anon-key" };
    private readonly FakeSecrets _secrets = new();
    private readonly ManualTime _time = new();

    private (SupabaseAuth Auth, SupabaseStub Stub) Create(Func<RecordedRequest, HttpResponseMessage> respond)
    {
        var stub = new SupabaseStub(respond);
        return (new SupabaseAuth(new SupabaseHttp(Options, stub), _secrets, _time), stub);
    }

    private static HttpResponseMessage Ok(string body) => SupabaseStub.Json(HttpStatusCode.OK, body);

    private async Task SignedInRemembered()
    {
        var (auth, _) = Create(_ => Ok(SupabaseStub.Session("a1", "r1")));
        await auth.SignInAsync("a@b.c", "password1", remember: true, default);
    }

    [Fact]
    public async Task SignIn_Remembered_StoresSession_AndSendsOnlyTheApiKey()
    {
        var (auth, stub) = Create(_ => Ok(SupabaseStub.Session("a1", "r1")));
        await auth.SignInAsync("a@b.c", "password1", remember: true, default);

        Assert.True(auth.IsSignedIn);
        Assert.Equal("user-1", auth.UserId);
        Assert.Equal("a@b.c", auth.Email);
        var request = Assert.Single(stub.Requests);
        Assert.Equal("/auth/v1/token?grant_type=password", request.PathAndQuery);
        Assert.Equal("anon-key", request.ApiKey);
        Assert.Null(request.Bearer);
        Assert.Contains("r1", _secrets[SupabaseAuth.SecretKey]);
        Assert.Equal("a1", await auth.GetAccessTokenAsync(default));
        Assert.Single(stub.Requests); // the fresh token is reused
    }

    [Fact]
    public async Task SignIn_NotRemembered_RemovesAnyStoredSession()
    {
        _secrets[SupabaseAuth.SecretKey] = "old";
        var (auth, _) = Create(_ => Ok(SupabaseStub.Session("a1", "r1")));
        await auth.SignInAsync("a@b.c", "password1", remember: false, default);
        Assert.False(_secrets.ContainsKey(SupabaseAuth.SecretKey));
    }

    [Fact]
    public async Task SignIn_BadCredentials_ShowsTheServerMessage()
    {
        var (auth, _) = Create(_ => SupabaseStub.Json(HttpStatusCode.BadRequest,
            """{"error":"invalid_grant","error_description":"Invalid login credentials"}"""));
        var ex = await Assert.ThrowsAsync<CloudRequestException>(() => auth.SignInAsync("a@b.c", "wrong", true, default));
        Assert.Equal("Invalid login credentials", ex.Message);
        Assert.False(auth.IsSignedIn);
    }

    [Fact]
    public async Task SignUp_WithoutASession_ExplainsEmailConfirmation()
    {
        var (auth, _) = Create(_ => Ok("""{"id":"user-1","email":"a@b.c"}"""));
        var ex = await Assert.ThrowsAsync<CloudRequestException>(() => auth.SignUpAsync("a@b.c", "password1", true, default));
        Assert.Contains("Confirm email", ex.Message);
    }

    [Fact]
    public async Task ExpiredAccessToken_IsRefreshed_AndTheRotatedTokenPersisted()
    {
        var (auth, stub) = Create(r => Ok(r.Uri.Query.Contains("refresh_token")
            ? SupabaseStub.Session("a2", "r2")
            : SupabaseStub.Session("a1", "r1")));
        await auth.SignInAsync("a@b.c", "password1", remember: true, default);
        _time.Now += TimeSpan.FromHours(1);

        Assert.Equal("a2", await auth.GetAccessTokenAsync(default));
        Assert.Contains("r1", stub.Requests[1].Body);
        Assert.Contains("r2", _secrets[SupabaseAuth.SecretKey]);
    }

    [Fact]
    public async Task Restore_LoadsTheStoredSessionWithoutTheNetwork()
    {
        await SignedInRemembered();
        var (auth, stub) = Create(_ => throw new InvalidOperationException("no network calls expected"));
        Assert.True(auth.Restore());
        Assert.Equal("user-1", auth.UserId);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public void Restore_WithNothingStored_StaysSignedOut()
    {
        var (auth, _) = Create(_ => throw new InvalidOperationException());
        Assert.False(auth.Restore());
        Assert.False(auth.IsSignedIn);
    }

    [Fact]
    public async Task RevokedRefreshToken_SignsOut_AndForgetsTheSession()
    {
        await SignedInRemembered();
        var (auth, _) = Create(_ => SupabaseStub.Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}"""));
        auth.Restore();
        await Assert.ThrowsAsync<CloudSignedOutException>(() => auth.GetAccessTokenAsync(default));
        Assert.False(auth.IsSignedIn);
        Assert.False(_secrets.ContainsKey(SupabaseAuth.SecretKey));
    }

    [Fact]
    public async Task NetworkFailure_KeepsTheSession_AndReportsOffline()
    {
        await SignedInRemembered();
        var (auth, _) = Create(_ => throw new HttpRequestException("no route"));
        auth.Restore();
        var ex = await Assert.ThrowsAsync<CloudUnavailableException>(() => auth.GetAccessTokenAsync(default));
        Assert.True(ex.Offline);
        Assert.True(auth.IsSignedIn);
        Assert.True(_secrets.ContainsKey(SupabaseAuth.SecretKey));
    }

    [Fact]
    public async Task ServerError_IsUnavailable_NotOffline()
    {
        var (auth, _) = Create(_ => SupabaseStub.Json(HttpStatusCode.ServiceUnavailable, "{}"));
        var ex = await Assert.ThrowsAsync<CloudUnavailableException>(() => auth.SignInAsync("a@b.c", "password1", true, default));
        Assert.False(ex.Offline);
    }

    [Fact]
    public async Task PasswordReset_VerifiesTheCode_ThenSetsThePassword()
    {
        var (auth, stub) = Create(r => Ok(r.PathAndQuery == "/auth/v1/verify" ? SupabaseStub.Session("a1", "r1") : "{}"));
        await auth.RequestPasswordResetAsync("a@b.c", default);
        await auth.ResetPasswordAsync("a@b.c", "123456", "newpassword1", remember: true, default);

        Assert.Equal(new[] { "/auth/v1/recover", "/auth/v1/verify", "/auth/v1/user" }, stub.Requests.Select(r => r.PathAndQuery));
        Assert.Contains("recovery", stub.Requests[1].Body);
        Assert.Contains("123456", stub.Requests[1].Body);
        Assert.Equal(HttpMethod.Put, stub.Requests[2].Method);
        Assert.Equal("a1", stub.Requests[2].Bearer);
        Assert.True(auth.IsSignedIn);
    }

    [Fact]
    public async Task SignOut_RevokesTheSession_AndForgetsIt()
    {
        var (auth, stub) = Create(r => r.PathAndQuery == "/auth/v1/logout"
            ? new HttpResponseMessage(HttpStatusCode.NoContent)
            : Ok(SupabaseStub.Session("a1", "r1")));
        await auth.SignInAsync("a@b.c", "password1", remember: true, default);
        await auth.SignOutAsync(default);

        Assert.Equal("a1", stub.Requests.Last().Bearer);
        Assert.False(auth.IsSignedIn);
        Assert.Null(auth.UserId);
        Assert.False(_secrets.ContainsKey(SupabaseAuth.SecretKey));
    }

    [Theory]
    [InlineData("http://proj.supabase.co", "key")]
    [InlineData("https://proj.supabase.co", "")]
    [InlineData("", "key")]
    public void InvalidOptions_AreRejected(string url, string key) =>
        Assert.Throws<ArgumentException>(() => new SupabaseHttp(new SupabaseOptions { Url = url, AnonKey = key }));

    [Theory]
    [InlineData("https://evil.example/x")]
    [InlineData("//evil.example/x")]
    [InlineData("/auth/v1/token")]
    [InlineData(@"\/evil.example/x")]
    public async Task OnlyRelativePathsOnTheConfiguredHost_AreAllowed(string path)
    {
        var http = new SupabaseHttp(Options, new SupabaseStub(_ => Ok("{}")));
        await Assert.ThrowsAsync<ArgumentException>(() => http.SendAsync(HttpMethod.Get, path, null, null, default));
    }

    [Fact]
    public async Task SignOut_DuringAnInFlightRefresh_IsNotUndoneByIt()
    {
        using var entered = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        var (auth, _) = Create(r =>
        {
            if (r.PathAndQuery == "/auth/v1/logout") return new HttpResponseMessage(HttpStatusCode.NoContent);
            if (!r.Uri.Query.Contains("refresh_token")) return Ok(SupabaseStub.Session("a1", "r1"));
            entered.Set();
            gate.Wait();
            return Ok(SupabaseStub.Session("a2", "r2"));
        });
        await auth.SignInAsync("a@b.c", "password1", remember: true, default);
        _time.Now += TimeSpan.FromHours(1);

        var refresh = Task.Run(() => auth.GetAccessTokenAsync(default));
        entered.Wait();
        var signOut = Task.Run(() => auth.SignOutAsync(default));
        await Task.Delay(100); // let sign-out run (or block on the lock) while the refresh is in flight
        gate.Set();
        await Task.WhenAll(refresh, signOut);

        Assert.False(auth.IsSignedIn);
        Assert.Null(auth.UserId);
        Assert.False(_secrets.ContainsKey(SupabaseAuth.SecretKey));
    }
}

public class SupabaseCharacterStoreTests
{
    private static readonly CloudUpload Upload = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "A", "Elf", 1, """{"identity":{"name":"A"}}""");

    private static string RowJson(Guid id, long revision, string? data = """{"identity":{"name":"A"}}""", string? deletedAt = null) =>
        $$"""{"id":"{{id}}","name":"A","revision":{{revision}},"schema_version":1,"data":{{data ?? "null"}},"deleted_at":{{(deletedAt is null ? "null" : $"\"{deletedAt}\"")}},"updated_at":"2026-09-29T10:00:00.123456+00:00"}""";

    private static (SupabaseCharacterStore Store, SupabaseStub Stub, FakeSession Session) Create(Func<RecordedRequest, HttpResponseMessage> respond)
    {
        var stub = new SupabaseStub(respond);
        var session = new FakeSession();
        return (new SupabaseCharacterStore(new SupabaseHttp(SupabaseAuthTests.Options, stub), session), stub, session);
    }

    private static HttpResponseMessage Ok(string body) => SupabaseStub.Json(HttpStatusCode.OK, body);

    [Fact]
    public async Task Update_FiltersOnRevision_AndReturnsTheNewRow()
    {
        var (store, stub, _) = Create(_ => Ok($"[{RowJson(Upload.Id, 4)}]"));
        var row = await store.UpdateIfRevisionAsync(Upload.Id, 3, Upload, default);

        var request = Assert.Single(stub.Requests);
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.Contains($"id=eq.{Upload.Id}", request.PathAndQuery);
        Assert.Contains("revision=eq.3", request.PathAndQuery);
        Assert.Equal("token", request.Bearer);
        Assert.Equal("anon-key", request.ApiKey);
        Assert.Equal("return=representation", request.Prefer);
        Assert.Contains("\"deleted_at\":null", request.Body);
        Assert.Contains("\"identity\":{\"name\":\"A\"}", request.Body); // data is sent as JSON, not a string
        Assert.Equal(4, row!.Revision);
        Assert.Contains("identity", row.Data);
    }

    [Fact]
    public async Task Update_RevisionMismatch_ReturnsNull()
    {
        var (store, _, _) = Create(_ => Ok("[]"));
        Assert.Null(await store.UpdateIfRevisionAsync(Upload.Id, 3, Upload, default));
    }

    [Fact]
    public async Task Insert_ExistingId_ReturnsNull()
    {
        var (store, _, _) = Create(_ => SupabaseStub.Json(HttpStatusCode.Conflict, """{"message":"duplicate key"}"""));
        Assert.Null(await store.InsertAsync(Upload, default));
    }

    [Fact]
    public async Task Tombstone_ReportsWhetherARowMatched()
    {
        var (store, stub, _) = Create(r => Ok(r.PathAndQuery.Contains("revision=eq.1") ? $$"""[{"id":"{{Upload.Id}}"}]""" : "[]"));
        Assert.True(await store.TombstoneIfRevisionAsync(Upload.Id, 1, default));
        Assert.False(await store.TombstoneIfRevisionAsync(Upload.Id, 2, default));
        Assert.Contains("\"data\":null", stub.Requests[0].Body);
    }

    [Fact]
    public async Task Get_ParsesTombstones()
    {
        var (store, _, _) = Create(_ => Ok($"[{RowJson(Upload.Id, 2, data: null, deletedAt: "2026-09-29T10:00:00+00:00")}]"));
        var row = await store.GetAsync(Upload.Id, default);
        Assert.True(row!.IsDeleted);
        Assert.Equal(2, row.Revision);
    }

    [Fact]
    public async Task ChangedSince_PagesThroughAllRows()
    {
        var page = "[" + string.Join(",", Enumerable.Range(0, 500).Select(_ => RowJson(Guid.NewGuid(), 1))) + "]";
        var (store, stub, _) = Create(r => Ok(r.PathAndQuery.Contains("offset=0") ? page : $"[{RowJson(Guid.NewGuid(), 1)}]"));

        var rows = await store.ChangedSinceAsync(new DateTimeOffset(2026, 9, 29, 9, 0, 0, TimeSpan.Zero), default);
        Assert.Equal(501, rows.Count);
        Assert.Equal(2, stub.Requests.Count);
        Assert.Contains("updated_at=gt.", stub.Requests[0].PathAndQuery);
        Assert.Contains("order=updated_at.asc", stub.Requests[0].PathAndQuery);
    }

    [Fact]
    public async Task Unauthorized_RefreshesOnce_ThenRetries()
    {
        var calls = 0;
        var (store, stub, session) = Create(_ => ++calls == 1 ? SupabaseStub.Json(HttpStatusCode.Unauthorized, "{}") : Ok("[]"));
        Assert.Null(await store.GetAsync(Upload.Id, default));
        Assert.Equal(2, stub.Requests.Count);
        Assert.Equal(1, session.Invalidations);
    }

    [Fact]
    public async Task UnauthorizedTwice_SignsOut()
    {
        var (store, _, _) = Create(_ => SupabaseStub.Json(HttpStatusCode.Unauthorized, "{}"));
        await Assert.ThrowsAsync<CloudSignedOutException>(() => store.GetAsync(Upload.Id, default));
    }

    [Fact]
    public async Task MalformedResponse_IsARequestError()
    {
        var (store, _, _) = Create(_ => Ok("{not json"));
        await Assert.ThrowsAsync<CloudRequestException>(() => store.GetAsync(Upload.Id, default));
    }
}
