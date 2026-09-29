namespace Hearthsheet.Core.Sync;

/// <summary>A character row as stored in the cloud. <see cref="Data"/> is the JSON document, null for a tombstone.</summary>
public sealed record CloudRow(Guid Id, string Name, long Revision, int SchemaVersion, string? Data, DateTimeOffset? DeletedAt, DateTimeOffset UpdatedAt)
{
    public bool IsDeleted => Data is null || DeletedAt is not null;
}

public sealed record CloudUpload(Guid Id, string Name, string Description, int SchemaVersion, string Data);

/// <summary>The signed-in user's character rows in the cloud. Revisions are assigned by the server.</summary>
public interface ICloudCharacterStore
{
    /// <returns>The stored row, or null when a row with this id already exists (it may belong to another account).</returns>
    Task<CloudRow?> InsertAsync(CloudUpload upload, CancellationToken ct);
    /// <summary>Also clears any tombstone. Returns null when the row is missing or its revision no longer matches.</summary>
    Task<CloudRow?> UpdateIfRevisionAsync(Guid id, long expectedRevision, CloudUpload upload, CancellationToken ct);
    /// <returns>False when the row is missing or its revision no longer matches.</returns>
    Task<bool> TombstoneIfRevisionAsync(Guid id, long expectedRevision, CancellationToken ct);
    Task<CloudRow?> GetAsync(Guid id, CancellationToken ct);
    /// <summary>Rows, tombstones included, updated after <paramref name="since"/> (all rows when null), oldest first.</summary>
    Task<IReadOnlyList<CloudRow>> ChangedSinceAsync(DateTimeOffset? since, CancellationToken ct);
    /// <summary>Ids of every row, tombstones included.</summary>
    Task<IReadOnlySet<Guid>> ListAllIdsAsync(CancellationToken ct);
}

public interface ICloudSession
{
    string? UserId { get; }
    /// <exception cref="CloudSignedOutException">There is no session, or the refresh token was rejected.</exception>
    Task<string> GetAccessTokenAsync(CancellationToken ct);
    /// <summary>Forces the next <see cref="GetAccessTokenAsync"/> to refresh (after a 401).</summary>
    void InvalidateAccessToken();
}

public abstract class CloudException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The service rejected the request. The message is safe to show to the user.</summary>
public sealed class CloudRequestException(string message, Exception? inner = null) : CloudException(message, inner);

/// <summary>The service could not be reached (<see cref="Offline"/>), timed out or failed (5xx, paused project).</summary>
public sealed class CloudUnavailableException(string message, bool offline, Exception? inner = null) : CloudException(message, inner)
{
    public bool Offline { get; } = offline;
}

public sealed class CloudSignedOutException(string message) : CloudException(message);
