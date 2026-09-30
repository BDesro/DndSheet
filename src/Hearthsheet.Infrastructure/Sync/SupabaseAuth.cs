using System.ComponentModel;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hearthsheet.Core.Sync;
using Hearthsheet.Infrastructure.Security;

namespace Hearthsheet.Infrastructure.Sync;

/// <summary>
/// The signed-in Supabase Auth session. The access token lives only in memory. The refresh token (plus user id
/// and email, so an offline start knows the account) is kept in the OS secret store when "Stay signed in" is
/// chosen, and rewritten on every refresh because Supabase rotates it.
/// </summary>
public sealed class SupabaseAuth(SupabaseHttp http, ISecretStore secrets, TimeProvider? time = null) : ICloudSession
{
    /// <summary>Secret-store key holding the remembered session.</summary>
    public const string SecretKey = "supabase-session";
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(60);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _lock = new(1, 1); // serializes refreshes so token rotation can't race
    private string? _accessToken;
    private DateTimeOffset _expiresAt;
    private string? _refreshToken;
    private bool _remember;

    /// <inheritdoc />
    public string? UserId { get; private set; }
    /// <summary>The signed-in account's email, if known.</summary>
    public string? Email { get; private set; }
    /// <summary>True while a refresh token is held.</summary>
    public bool IsSignedIn => _refreshToken is not null;

    /// <summary>Loads a remembered session without the network. The first token request refreshes it.</summary>
    public bool Restore()
    {
        SavedSession? saved;
        try
        {
            saved = secrets.Read(SecretKey) is { } json ? JsonSerializer.Deserialize<SavedSession>(json) : null;
        }
        catch (Exception ex) when (ex is JsonException or Win32Exception)
        {
            saved = null;
        }
        if (saved is not { RefreshToken.Length: > 0, UserId.Length: > 0 }) return false;
        (_refreshToken, UserId, Email, _remember, _accessToken) = (saved.RefreshToken, saved.UserId, saved.Email, true, null);
        return true;
    }

    /// <summary>Signs in with email and password.</summary>
    public Task SignInAsync(string email, string password, bool remember, CancellationToken ct) =>
        StartSessionAsync("auth/v1/token?grant_type=password", new JsonObject { ["email"] = email, ["password"] = password }, remember, ct);

    /// <summary>Creates an account and signs in (requires Supabase "Confirm email" to be off).</summary>
    public Task SignUpAsync(string email, string password, bool remember, CancellationToken ct) =>
        StartSessionAsync("auth/v1/signup", new JsonObject { ["email"] = email, ["password"] = password }, remember, ct);

    /// <summary>Emails a one-time code (the recovery template shows <c>{{ .Token }}</c>).</summary>
    public async Task RequestPasswordResetAsync(string email, CancellationToken ct)
    {
        var response = await http.SendAsync(HttpMethod.Post, "auth/v1/recover", new JsonObject { ["email"] = email }, null, ct);
        if (!response.IsSuccess) throw new CloudRequestException(response.ErrorMessage());
    }

    /// <summary>
    /// Verifying the code signs the user in; the new password is then set on that session. If it can't be set,
    /// the code is already spent, so the session is forgotten rather than left half signed in.
    /// </summary>
    public async Task ResetPasswordAsync(string email, string code, string newPassword, bool remember, CancellationToken ct)
    {
        await StartSessionAsync("auth/v1/verify",
            new JsonObject { ["type"] = "recovery", ["email"] = email, ["token"] = code }, remember, ct);
        var response = await http.SendAsync(HttpMethod.Put, "auth/v1/user",
            new JsonObject { ["password"] = newPassword }, await GetAccessTokenAsync(ct), ct);
        if (response.IsSuccess) return;

        await _lock.WaitAsync(ct);
        try
        {
            Forget();
        }
        finally
        {
            _lock.Release();
        }
        throw new CloudRequestException($"{response.ErrorMessage()} Request a new code and try again.");
    }

    /// <summary>Forgets the session locally and revokes it on the server (best effort).</summary>
    public async Task SignOutAsync(CancellationToken ct)
    {
        string? token;
        await _lock.WaitAsync(ct); // waits out any in-flight refresh so it can't resurrect the session
        try
        {
            token = _accessToken;
            Forget();
        }
        finally
        {
            _lock.Release();
        }
        if (token is null) return;
        try
        {
            await http.SendAsync(HttpMethod.Post, "auth/v1/logout", null, token, ct);
        }
        catch (CloudException)
        {
            // Best effort: the local session is already gone.
        }
    }

    /// <inheritdoc />
    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_refreshToken is null) throw new CloudSignedOutException("Not signed in.");
            if (_accessToken is not null && _time.GetUtcNow() < _expiresAt - RefreshMargin) return _accessToken;

            var response = await http.SendAsync(HttpMethod.Post, "auth/v1/token?grant_type=refresh_token",
                new JsonObject { ["refresh_token"] = _refreshToken }, null, ct);
            if (response.Status is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            {
                Forget();
                throw new CloudSignedOutException("Your session has ended. Sign in to resume sync.");
            }
            if (!response.IsSuccess) throw new CloudRequestException(response.ErrorMessage());
            return Accept(response.Body);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public void InvalidateAccessToken() => _accessToken = null;

    private async Task StartSessionAsync(string path, JsonObject body, bool remember, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var response = await http.SendAsync(HttpMethod.Post, path, body, null, ct);
            if (!response.IsSuccess) throw new CloudRequestException(response.ErrorMessage());
            _remember = remember;
            Accept(response.Body);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Takes a session response; returns the new access token.</summary>
    private string Accept(string body)
    {
        string access, refresh, userId;
        string? email;
        double expiresIn;
        try
        {
            var json = JsonNode.Parse(body);
            access = json?["access_token"]?.GetValue<string>() ?? throw new CloudRequestException(
                "The sign-in service did not return a session. If you just created an account, turn off “Confirm email” in Supabase (see docs/CLOUD_SYNC.md).");
            refresh = json!["refresh_token"]?.GetValue<string>() ?? throw Unexpected(null);
            userId = json!["user"]?["id"]?.GetValue<string>() ?? throw Unexpected(null);
            email = json!["user"]?["email"]?.GetValue<string>();
            expiresIn = json!["expires_in"]?.GetValue<double>() ?? 3600;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw Unexpected(ex);
        }

        (_accessToken, _refreshToken, UserId, Email) = (access, refresh, userId, email);
        _expiresAt = _time.GetUtcNow().AddSeconds(expiresIn);
        Persist();
        return access;
    }

    private static CloudRequestException Unexpected(Exception? inner) =>
        new("The sign-in service returned an unexpected response.", inner);

    private void Persist()
    {
        try
        {
            if (_remember && _refreshToken is not null && UserId is not null)
                secrets.Write(SecretKey, JsonSerializer.Serialize(new SavedSession(_refreshToken, UserId, Email)));
            else
                secrets.Delete(SecretKey);
        }
        catch (Win32Exception)
        {
            // Failing to remember only means signing in again next launch.
        }
    }

    private void Forget()
    {
        (_accessToken, _refreshToken, UserId, Email, _remember) = (null, null, null, null, false);
        try
        {
            secrets.Delete(SecretKey);
        }
        catch (Win32Exception)
        {
        }
    }

    private sealed record SavedSession(string RefreshToken, string UserId, string? Email);
}
