using Hearthsheet.Core.Domain;

namespace Hearthsheet.Core.Sync;

/// <summary>A locally changed character waiting to be pushed, as stored (<see cref="Data"/> is the JSON document).</summary>
public sealed record LocalChange(Guid Id, long LocalRevision, long? CloudRevision, string Name, string Description, int SchemaVersion, string Data);

/// <summary>Local state of a character: whether it is dirty and its local revision counter.</summary>
public sealed record LocalState(bool Dirty, long LocalRevision);

/// <summary>A character deleted locally after it had been synced; the cloud row still needs a tombstone.</summary>
public sealed record PendingDelete(Guid Id, long CloudRevision);

/// <summary>The cloud account this database is linked to, and the pull cursor (server time).</summary>
public sealed record SyncMeta(string? Account, DateTimeOffset? LastPull);

/// <summary>
/// Sync bookkeeping on the local store. Each method is one atomic statement or transaction, so the UI thread's
/// saves and a background sync pass can interleave safely. <c>local_revision</c> counts saves; it guards
/// against overwriting or clearing a row that was saved again while a push was in flight.
/// </summary>
public interface ISyncLocalStore
{
    /// <summary>Returns all dirty characters (waiting to be pushed).</summary>
    IReadOnlyList<LocalChange> GetDirty();

    /// <summary>Returns the local state of a character if it exists, null otherwise.</summary>
    LocalState? GetState(Guid id);

    /// <summary>Records the new cloud revision; clears <c>dirty</c> only if nothing was saved since <paramref name="localRevision"/>.</summary>
    void MarkPushed(Guid id, long localRevision, long cloudRevision);

    /// <summary>
    /// Upserts a pulled character as clean. Skipped when the local row is dirty (unless its local revision equals
    /// <paramref name="ifLocalRevision"/>, or <paramref name="force"/>) or has a pending delete.
    /// </summary>
    bool ApplyRemote(Character character, long cloudRevision, long? ifLocalRevision = null, bool force = false);

    /// <summary>Deletes locally without queuing a cloud delete. Skipped when the row is dirty unless <paramref name="force"/>.</summary>
    bool ApplyRemoteDelete(Guid id, bool force = false);

    /// <summary>Points the row at another cloud revision (null = never synced) and marks it dirty.</summary>
    void Rebase(Guid id, long? cloudRevision);

    /// <summary>Returns the cloud revisions of all characters (null means never synced).</summary>
    IReadOnlyDictionary<Guid, long?> GetCloudRevisions();

    /// <summary>Returns all pending deletes.</summary>
    IReadOnlyList<PendingDelete> GetPendingDeletes();

    /// <summary>Clears a pending delete after it has been processed on the server.</summary>
    void ClearPendingDelete(Guid id);

    /// <summary>Returns the current sync metadata (account and pull cursor).</summary>
    SyncMeta GetSyncMeta();

    /// <summary>Sets the cloud account this database is linked to.</summary>
    void SetSyncAccount(string account);

    /// <summary>Sets the pull cursor (server time of last successful pull).</summary>
    void SetLastPull(DateTimeOffset? at);

    /// <summary>Forgets all cloud state: revisions, pending deletes and the pull cursor.</summary>
    void ResetSyncState(bool markAllDirty);

    /// <summary>Gives every character a new id (used when linking to a different account).</summary>
    void ReassignAllIds();
}
