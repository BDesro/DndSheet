using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Hearthsheet.Core.Application;
using Hearthsheet.Core.Domain;
using Hearthsheet.Core.Serialization;
using Microsoft.Extensions.Logging;

namespace Hearthsheet.Core.Sync;

public enum SyncOutcome { Synced, NotSignedIn, SignedOut, Offline, Unavailable, AccountMismatch, Busy }

/// <summary>
/// A remote change to the character open in the UI. The background pass never writes that row (its autosave
/// could overwrite the remote version with the stale in-memory one); <see cref="CloudSync.ApplyDeferred"/>
/// applies it on the UI thread. <see cref="CopiedLocalRevision"/> is set when a conflict copy was already made.
/// </summary>
public sealed record DeferredChange(Guid Id, Character? Remote, long Revision, long? CopiedLocalRevision = null);

public sealed record SyncResult(
    SyncOutcome Outcome, IReadOnlySet<Guid> Changed, IReadOnlySet<Guid> Deleted,
    IReadOnlyList<DeferredChange> Deferred, int ConflictCopies, IReadOnlyList<string> Warnings)
{
    public static SyncResult Of(SyncOutcome outcome) => new(outcome, new HashSet<Guid>(), new HashSet<Guid>(), [], 0, []);
    public bool HasChanges => Changed.Count + Deleted.Count + Deferred.Count + ConflictCopies > 0;
}

/// <summary>
/// One sync pass: push local deletes and edits, then pull remote changes. Local SQLite stays the source of
/// truth. On a conflict the cloud version wins and the local one is kept as a conflict copy. Design:
/// docs/superpowers/specs/2026-09-29-cloud-sync-design.md §5.
/// </summary>
public sealed class CloudSync(
    ICharacterRepository repository, ISyncLocalStore local, ICloudCharacterStore cloud, ICloudSession session,
    CharacterMigrator migrator, ILogger<CloudSync> logger, TimeProvider? time = null)
{
    public static readonly TimeSpan PullOverlap = TimeSpan.FromMinutes(2);
    /// <summary>Below the 90-day tombstone retention: a machine this stale may have missed purged deletes.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(80);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string? LinkedAccount => local.GetSyncMeta().Account;

    /// <param name="openCharacter">The id open in the UI, read as the pass goes; its remote changes are deferred.</param>
    /// <param name="waitIfBusy">Wait for a running pass instead of returning <see cref="SyncOutcome.Busy"/> (used on close).</param>
    public async Task<SyncResult> RunAsync(Func<Guid?> openCharacter, bool waitIfBusy, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(waitIfBusy ? Timeout.Infinite : 0, ct)) return SyncResult.Of(SyncOutcome.Busy);
        try
        {
            return await RunPassAsync(openCharacter, ct);
        }
        catch (CloudSignedOutException ex)
        {
            logger.LogInformation("Sync stopped: {Reason}", ex.Message);
            return SyncResult.Of(SyncOutcome.SignedOut);
        }
        catch (CloudUnavailableException ex)
        {
            logger.LogWarning("Sync unavailable: {Reason}", ex.Message);
            return SyncResult.Of(ex.Offline ? SyncOutcome.Offline : SyncOutcome.Unavailable);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Applies a deferred change on the UI thread, where no save can interleave. The in-memory character is kept as
    /// a conflict copy first if it has edits the cloud doesn't. Returns that copy, if one was made.
    /// </summary>
    public Character? ApplyDeferred(DeferredChange change, Character? openInMemory, bool hasUnsavedChanges)
    {
        if (openInMemory is null || openInMemory.Id != change.Id)
        {
            // The user moved on to another character meanwhile: apply with the normal dirty guard.
            if (change.Remote is null) local.ApplyRemoteDelete(change.Id);
            else local.ApplyRemote(change.Remote, change.Revision);
            return null;
        }

        Character? copy = null;
        var savedSinceCopy = local.GetState(change.Id) is { Dirty: true } state && state.LocalRevision != change.CopiedLocalRevision;
        if (hasUnsavedChanges || savedSinceCopy)
        {
            copy = ConflictCopy(openInMemory);
            repository.Save(copy);
        }
        if (change.Remote is null) local.ApplyRemoteDelete(change.Id, force: true);
        else local.ApplyRemote(change.Remote, change.Revision, force: true);
        return copy;
    }

    /// <summary>
    /// Links this computer to a different account. Every local character gets a new id, because the old ids exist
    /// under the other account and could never be inserted into this one.
    /// </summary>
    public void Relink(string userId, bool uploadExisting)
    {
        _gate.Wait();
        try
        {
            local.ReassignAllIds();
            local.ResetSyncState(markAllDirty: uploadExisting);
            local.SetSyncAccount(userId);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SyncResult> RunPassAsync(Func<Guid?> openCharacter, CancellationToken ct)
    {
        if (session.UserId is not { } userId) return SyncResult.Of(SyncOutcome.NotSignedIn);
        await session.GetAccessTokenAsync(ct); // surfaces a revoked session or no network before anything changes

        var meta = local.GetSyncMeta();
        if (meta.Account is null)
        {
            local.ResetSyncState(markAllDirty: true);
            local.SetSyncAccount(userId);
            meta = new SyncMeta(userId, null);
            logger.LogInformation("This computer is now linked to a cloud account; uploading all characters");
        }
        else if (meta.Account != userId)
        {
            return SyncResult.Of(SyncOutcome.AccountMismatch);
        }

        var pass = new Pass();
        await PushDeletesAsync(pass, ct);
        await PushChangesAsync(openCharacter, pass, ct);
        await PullAsync(meta.LastPull, openCharacter, pass, ct);
        logger.LogInformation("Sync finished: {Changed} changed, {Deleted} deleted, {Deferred} deferred, {Copies} conflict copies, {Warnings} warnings",
            pass.Changed.Count, pass.Deleted.Count, pass.Deferred.Count, pass.ConflictCopies, pass.Warnings.Count);
        return new SyncResult(SyncOutcome.Synced, pass.Changed, pass.Deleted, pass.Deferred, pass.ConflictCopies, pass.Warnings);
    }

    private async Task PushDeletesAsync(Pass pass, CancellationToken ct)
    {
        foreach (var pending in local.GetPendingDeletes())
        {
            try
            {
                // False means the row is gone or was edited elsewhere. Either way this delete is finished:
                // a newer remote edit wins and comes back with the pull.
                await cloud.TombstoneIfRevisionAsync(pending.Id, pending.CloudRevision, ct);
                local.ClearPendingDelete(pending.Id);
            }
            catch (CloudRequestException ex)
            {
                pass.Warn(logger, $"A deleted character could not be removed from the cloud: {ex.Message}");
            }
        }
    }

    private async Task PushChangesAsync(Func<Guid?> openCharacter, Pass pass, CancellationToken ct)
    {
        foreach (var change in local.GetDirty())
        {
            if (Encoding.UTF8.GetByteCount(change.Data) > PortableCharacterFile.MaxFileBytes)
            {
                pass.Warn(logger, $"“{change.Name}” is too large to sync.");
                continue;
            }
            var upload = new CloudUpload(change.Id, change.Name, change.Description, change.SchemaVersion, change.Data);
            try
            {
                var pushed = change.CloudRevision is { } revision
                    ? await cloud.UpdateIfRevisionAsync(change.Id, revision, upload, ct)
                    : await cloud.InsertAsync(upload, ct);
                if (pushed is not null) local.MarkPushed(change.Id, change.LocalRevision, pushed.Revision);
                else await ResolveConflictAsync(change, openCharacter, pass, ct);
            }
            catch (CloudRequestException ex)
            {
                pass.Warn(logger, $"“{change.Name}” could not be uploaded: {ex.Message}");
            }
        }
    }

    private async Task ResolveConflictAsync(LocalChange change, Func<Guid?> openCharacter, Pass pass, CancellationToken ct)
    {
        var remote = await cloud.GetAsync(change.Id, ct);
        if (remote is null)
        {
            if (change.CloudRevision is null)
                pass.Warn(logger, $"“{change.Name}” could not be uploaded because its id is used by another account.");
            else
                local.Rebase(change.Id, null); // its tombstone was purged: insert it again next pass
            return;
        }
        if (remote.IsDeleted)
        {
            local.Rebase(change.Id, remote.Revision); // an edit beats a delete: the next push revives the row
            return;
        }
        if (!TryRead(remote, pass, out var remoteCharacter)) return;

        var mine = CharacterJson.Deserialize(change.Data, change.SchemaVersion, migrator);
        repository.Save(ConflictCopy(mine));
        pass.ConflictCopies++;
        logger.LogInformation("Conflict on {Id}: kept the cloud version and saved the local one as a copy", change.Id);

        if (openCharacter() == change.Id)
            pass.Deferred.Add(new DeferredChange(change.Id, remoteCharacter, remote.Revision, change.LocalRevision));
        else if (local.ApplyRemote(remoteCharacter, remote.Revision, ifLocalRevision: change.LocalRevision))
            pass.Changed.Add(change.Id);
    }

    private async Task PullAsync(DateTimeOffset? lastPull, Func<Guid?> openCharacter, Pass pass, CancellationToken ct)
    {
        var full = lastPull is null || _time.GetUtcNow() - lastPull > StaleAfter;
        var rows = await cloud.ChangedSinceAsync(full ? null : lastPull - PullOverlap, ct);
        var known = local.GetCloudRevisions();
        DateTimeOffset? heldAt = null;

        foreach (var row in rows)
        {
            if (known.TryGetValue(row.Id, out var revision) && revision == row.Revision) continue;
            var isOpen = openCharacter() == row.Id;
            if (row.IsDeleted)
            {
                if (!known.ContainsKey(row.Id)) continue;
                if (isOpen) pass.Deferred.Add(new DeferredChange(row.Id, null, row.Revision));
                else if (local.ApplyRemoteDelete(row.Id)) pass.Deleted.Add(row.Id);
                continue;
            }
            // Keep the cursor at the oldest row this version can't read yet, so an app update picks it up.
            if (row.SchemaVersion > CharacterJson.CurrentSchemaVersion && (heldAt is null || row.UpdatedAt < heldAt))
                heldAt = row.UpdatedAt;
            if (!TryRead(row, pass, out var character)) continue;
            if (isOpen) pass.Deferred.Add(new DeferredChange(row.Id, character, row.Revision));
            else if (local.ApplyRemote(character, row.Revision)) pass.Changed.Add(row.Id);
        }

        if (full)
        {
            // Rows missing entirely were tombstoned and purged while we weren't looking: upload ours again.
            var ids = await cloud.ListAllIdsAsync(ct);
            foreach (var (id, revision) in known)
                if (revision is not null && !ids.Contains(id)) local.Rebase(id, null);
        }
        local.SetLastPull(heldAt ?? (rows.Count > 0 ? rows.Max(r => r.UpdatedAt) : lastPull));
    }

    /// <summary>Cloud data is untrusted input: the same size, depth, migration, clamping and limit checks as imports.</summary>
    private bool TryRead(CloudRow row, Pass pass, [NotNullWhen(true)] out Character? character)
    {
        character = null;
        if (row.SchemaVersion > CharacterJson.CurrentSchemaVersion)
        {
            pass.Warn(logger, $"Update Hearthsheet to sync “{row.Name}”.");
            return false;
        }
        try
        {
            if (Encoding.UTF8.GetByteCount(row.Data!) > PortableCharacterFile.MaxFileBytes)
                throw new CharacterFormatException("The character is too large.");
            var parsed = CharacterJson.Deserialize(row.Data!, row.SchemaVersion, migrator);
            PortableCharacterFile.EnforceLimits(parsed);
            parsed.Id = row.Id;
            character = parsed;
            return true;
        }
        catch (Exception ex) when (ex is CharacterFormatException or InvalidOperationException)
        {
            pass.Warn(logger, $"“{row.Name}” in the cloud could not be read: {ex.Message}");
            return false;
        }
    }

    private Character ConflictCopy(Character source)
    {
        var copy = CharacterJson.Clone(source, migrator);
        copy.Id = Guid.NewGuid();
        var date = _time.GetLocalNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        copy.Identity.Name = $"{source.Identity.Name} (conflict copy, {date})";
        return copy;
    }

    private sealed class Pass
    {
        public HashSet<Guid> Changed { get; } = [];
        public HashSet<Guid> Deleted { get; } = [];
        public List<DeferredChange> Deferred { get; } = [];
        public List<string> Warnings { get; } = [];
        public int ConflictCopies { get; set; }

        public void Warn(ILogger log, string message)
        {
            log.LogWarning("Sync: {Message}", message);
            Warnings.Add(message);
        }
    }
}
