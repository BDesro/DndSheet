using Hearthsheet.Core.Domain;
using Hearthsheet.Core.Serialization;
using Hearthsheet.Core.Sync;
using Hearthsheet.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hearthsheet.Tests;

internal sealed class ManualTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>In-memory stand-in for one account's cloud table. Every write moves the shared clock by a second.</summary>
internal sealed class FakeCloud(ManualTime time) : ICloudCharacterStore
{
    public Dictionary<Guid, CloudRow> Rows { get; } = [];
    /// <summary>Ids that exist under another account: invisible, but inserting them conflicts.</summary>
    public HashSet<Guid> ForeignIds { get; } = [];
    public bool Offline { get; set; }
    public TaskCompletionSource? Gate { get; set; }

    private DateTimeOffset Tick() => time.Now += TimeSpan.FromSeconds(1);
    private void Check()
    {
        if (Offline) throw new CloudUnavailableException("offline", offline: true);
    }

    public Task<CloudRow?> InsertAsync(CloudUpload u, CancellationToken ct)
    {
        Check();
        if (Rows.ContainsKey(u.Id) || ForeignIds.Contains(u.Id)) return Task.FromResult<CloudRow?>(null);
        return Task.FromResult<CloudRow?>(Rows[u.Id] = new CloudRow(u.Id, u.Name, 1, u.SchemaVersion, u.Data, null, Tick()));
    }

    public Task<CloudRow?> UpdateIfRevisionAsync(Guid id, long expected, CloudUpload u, CancellationToken ct)
    {
        Check();
        if (!Rows.TryGetValue(id, out var row) || row.Revision != expected) return Task.FromResult<CloudRow?>(null);
        return Task.FromResult<CloudRow?>(Rows[id] = new CloudRow(id, u.Name, row.Revision + 1, u.SchemaVersion, u.Data, null, Tick()));
    }

    public Task<bool> TombstoneIfRevisionAsync(Guid id, long expected, CancellationToken ct)
    {
        Check();
        if (!Rows.TryGetValue(id, out var row) || row.Revision != expected) return Task.FromResult(false);
        DeleteRemotely(id);
        return Task.FromResult(true);
    }

    public Task<CloudRow?> GetAsync(Guid id, CancellationToken ct)
    {
        Check();
        return Task.FromResult(Rows.GetValueOrDefault(id));
    }

    public async Task<IReadOnlyList<CloudRow>> ChangedSinceAsync(DateTimeOffset? since, CancellationToken ct)
    {
        if (Gate is not null) await Gate.Task;
        Check();
        return Rows.Values.Where(r => since is null || r.UpdatedAt > since).OrderBy(r => r.UpdatedAt).ToList();
    }

    public Task<IReadOnlySet<Guid>> ListAllIdsAsync(CancellationToken ct)
    {
        Check();
        return Task.FromResult<IReadOnlySet<Guid>>(Rows.Keys.ToHashSet());
    }

    /// <summary>Another device writes a new version.</summary>
    public void EditRemotely(Guid id, Action<Character> edit)
    {
        var row = Rows[id];
        var c = CharacterJson.Deserialize(row.Data!, row.SchemaVersion, CharacterMigrator.Default);
        edit(c);
        Rows[id] = row with { Name = c.Identity.Name, Revision = row.Revision + 1, Data = CharacterJson.Serialize(c), UpdatedAt = Tick() };
    }

    public void DeleteRemotely(Guid id)
    {
        var row = Rows[id];
        Rows[id] = row with { Revision = row.Revision + 1, Data = null, DeletedAt = time.Now, UpdatedAt = Tick() };
    }
}

internal sealed class FakeSession : ICloudSession
{
    public string? UserId { get; set; } = "user-1";
    public bool Revoked { get; set; }
    public int Invalidations { get; private set; }
    public Task<string> GetAccessTokenAsync(CancellationToken ct) =>
        Revoked ? throw new CloudSignedOutException("revoked") : Task.FromResult("token");
    public void InvalidateAccessToken() => Invalidations++;
}

/// <summary>One installation: its own database, sharing the fake cloud and session.</summary>
internal sealed class Machine(TempDir dir, string name, FakeCloud cloud, FakeSession session, ManualTime time)
{
    public SqliteCharacterRepository Repo { get; } =
        new(dir.File(name + ".db"), CharacterMigrator.Default, NullLogger<SqliteCharacterRepository>.Instance);
    public CloudSync Sync => field ??= new CloudSync(Repo, Repo, cloud, session, CharacterMigrator.Default, NullLogger<CloudSync>.Instance, time);
    public Guid? OpenId { get; set; }

    public Task<SyncResult> SyncAsync() => Sync.RunAsync(() => OpenId, waitIfBusy: false, CancellationToken.None);

    public Character Add(string characterName)
    {
        var c = Samples.Rich();
        c.Identity.Name = characterName;
        Repo.Save(c);
        return c;
    }

    public void Rename(Guid id, string newName)
    {
        var c = Repo.Load(id)!;
        c.Identity.Name = newName;
        Repo.Save(c);
    }

    public string? NameOf(Guid id) => Repo.Load(id)?.Identity.Name;
}

public class CloudSyncTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ManualTime _time = new();
    private readonly FakeSession _session = new();
    private readonly FakeCloud _cloud;
    private readonly Machine _a;
    private readonly Machine _b;

    public CloudSyncTests()
    {
        _cloud = new FakeCloud(_time);
        _a = new Machine(_dir, "a", _cloud, _session, _time);
        _b = new Machine(_dir, "b", _cloud, _session, _time);
    }

    public void Dispose() => _dir.Dispose();

    private async Task<Character> SyncedToBoth(string name = "Arannis")
    {
        var c = _a.Add(name);
        await _a.SyncAsync();
        await _b.SyncAsync();
        return c;
    }

    // ---- Push, pull, outcomes ----

    [Fact]
    public async Task FirstSync_UploadsLocalCharacters_AndLinksTheAccount()
    {
        var c = _a.Add("Arannis");
        var result = await _a.SyncAsync();
        Assert.Equal(SyncOutcome.Synced, result.Outcome);
        Assert.Equal("Arannis", _cloud.Rows[c.Id].Name);
        Assert.Equal("user-1", _a.Sync.LinkedAccount);
        Assert.Empty(_a.Repo.GetDirty());
    }

    [Fact]
    public async Task Pull_BringsCharactersToAnotherMachine()
    {
        var c = _a.Add("Arannis");
        await _a.SyncAsync();
        var result = await _b.SyncAsync();
        Assert.Contains(c.Id, result.Changed);
        Assert.Equal("Arannis", _b.NameOf(c.Id));
        Assert.Empty(_b.Repo.GetDirty());
    }

    [Fact]
    public async Task Edits_RoundTripBetweenMachines()
    {
        var c = await SyncedToBoth();
        _b.Rename(c.Id, "Arannis the Wise");
        await _b.SyncAsync();
        var result = await _a.SyncAsync();
        Assert.Contains(c.Id, result.Changed);
        Assert.Equal("Arannis the Wise", _a.NameOf(c.Id));
        Assert.Equal(2, _cloud.Rows[c.Id].Revision);
    }

    [Fact]
    public async Task SecondPass_WithNothingNew_ChangesNothing()
    {
        _a.Add("Arannis");
        await _a.SyncAsync();
        var result = await _a.SyncAsync();
        Assert.False(result.HasChanges);
    }

    [Fact]
    public async Task NotSignedIn_DoesNothing()
    {
        _session.UserId = null;
        _a.Add("Arannis");
        Assert.Equal(SyncOutcome.NotSignedIn, (await _a.SyncAsync()).Outcome);
        Assert.Empty(_cloud.Rows);
    }

    [Fact]
    public async Task RevokedSession_ReportsSignedOut()
    {
        _session.Revoked = true;
        Assert.Equal(SyncOutcome.SignedOut, (await _a.SyncAsync()).Outcome);
    }

    [Fact]
    public async Task Offline_ReportsOffline_AndKeepsChangesQueued()
    {
        await _a.SyncAsync();
        _cloud.Offline = true;
        _a.Add("Arannis");
        Assert.Equal(SyncOutcome.Offline, (await _a.SyncAsync()).Outcome);
        Assert.Single(_a.Repo.GetDirty());
    }

    [Fact]
    public async Task DifferentAccount_IsNotSyncedUntilRelinked()
    {
        await _a.SyncAsync();
        _session.UserId = "user-2";
        _a.Add("Arannis");
        Assert.Equal(SyncOutcome.AccountMismatch, (await _a.SyncAsync()).Outcome);
        Assert.Empty(_cloud.Rows);
    }

    [Fact]
    public async Task PassesDoNotOverlap()
    {
        _cloud.Gate = new TaskCompletionSource();
        var first = _a.SyncAsync(); // parks inside the pull
        Assert.Equal(SyncOutcome.Busy, (await _a.SyncAsync()).Outcome);
        _cloud.Gate.SetResult();
        Assert.Equal(SyncOutcome.Synced, (await first).Outcome);
    }

    // ---- Untrusted and unreadable data ----

    [Fact]
    public async Task Pull_SkipsCorruptRows_AndSyncsTheRest()
    {
        var bad = _a.Add("Bad");
        var good = _a.Add("Good");
        await _a.SyncAsync();
        _cloud.Rows[bad.Id] = _cloud.Rows[bad.Id] with { Revision = 2, Data = """{"abilities":{"strength":"lots"}}""" };

        var result = await _b.SyncAsync();
        Assert.Null(_b.Repo.Load(bad.Id));
        Assert.Equal("Good", _b.NameOf(good.Id));
        Assert.Single(result.Warnings);
    }

    [Fact]
    public async Task Pull_SkipsNewerSchemaRows_AndPicksThemUpAfterAnAppUpdate()
    {
        var future = _a.Add("Future");
        await _a.SyncAsync();
        _cloud.Rows[future.Id] = _cloud.Rows[future.Id] with { Revision = 2, SchemaVersion = CharacterJson.CurrentSchemaVersion + 1 };
        _time.Now += TimeSpan.FromHours(1);
        var later = _a.Add("Later");
        await _a.SyncAsync();

        var result = await _b.SyncAsync();
        Assert.Null(_b.Repo.Load(future.Id));
        Assert.Equal("Later", _b.NameOf(later.Id));
        Assert.Single(result.Warnings);

        // The app is updated: the same row (same updated_at) is now readable and must not have been skipped past.
        _cloud.Rows[future.Id] = _cloud.Rows[future.Id] with { SchemaVersion = CharacterJson.CurrentSchemaVersion };
        await _b.SyncAsync();
        Assert.Equal("Future", _b.NameOf(future.Id));
    }

    [Fact]
    public async Task OversizedCharacter_IsSkipped_AndOthersSync()
    {
        var big = Samples.Rich();
        big.Biography.Backstory = new string('x', 11 * 1024 * 1024);
        _a.Repo.Save(big);
        var small = _a.Add("Small");

        var result = await _a.SyncAsync();
        Assert.True(_cloud.Rows.ContainsKey(small.Id));
        Assert.False(_cloud.Rows.ContainsKey(big.Id));
        Assert.Single(result.Warnings);
        Assert.Single(_a.Repo.GetDirty());
    }

    [Fact]
    public async Task InsertOfAnIdOwnedByAnotherAccount_IsSkippedWithAWarning()
    {
        var c = _a.Add("Arannis");
        _cloud.ForeignIds.Add(c.Id);
        var result = await _a.SyncAsync();
        Assert.Single(result.Warnings);
        Assert.Single(_a.Repo.GetDirty());
    }

    // ---- Conflicts and deletes ----

    [Fact]
    public async Task Conflict_CloudWins_AndLocalIsKeptAsACopyThatUploadsNextPass()
    {
        var c = await SyncedToBoth();
        _b.Rename(c.Id, "B-name");
        await _b.SyncAsync();
        _a.Rename(c.Id, "A-name");

        var result = await _a.SyncAsync();
        Assert.Equal(1, result.ConflictCopies);
        Assert.Contains(c.Id, result.Changed);
        Assert.Equal("B-name", _a.NameOf(c.Id));
        Assert.Contains(_a.Repo.List(), s => s.Name.StartsWith("A-name (conflict copy, ", StringComparison.Ordinal));
        Assert.Single(_cloud.Rows);

        await _a.SyncAsync();
        Assert.Equal(2, _cloud.Rows.Count);
    }

    [Fact]
    public async Task Delete_PropagatesToOtherMachines()
    {
        var c = await SyncedToBoth();
        _a.Repo.Delete(c.Id);
        await _a.SyncAsync();
        Assert.True(_cloud.Rows[c.Id].IsDeleted);
        Assert.Empty(_a.Repo.GetPendingDeletes());

        var result = await _b.SyncAsync();
        Assert.Contains(c.Id, result.Deleted);
        Assert.Null(_b.Repo.Load(c.Id));
    }

    [Fact]
    public async Task Delete_LosesToANewerRemoteEdit()
    {
        var c = await SyncedToBoth();
        _b.Rename(c.Id, "B-name");
        await _b.SyncAsync();
        _a.Repo.Delete(c.Id);

        await _a.SyncAsync();
        Assert.False(_cloud.Rows[c.Id].IsDeleted);
        Assert.Equal("B-name", _a.NameOf(c.Id));
    }

    [Fact]
    public async Task Edit_BeatsARemoteDelete()
    {
        var c = await SyncedToBoth();
        _b.Repo.Delete(c.Id);
        await _b.SyncAsync();
        _a.Rename(c.Id, "A-name");

        await _a.SyncAsync(); // learns about the tombstone
        await _a.SyncAsync(); // revives the row
        Assert.False(_cloud.Rows[c.Id].IsDeleted);
        Assert.Equal("A-name", _cloud.Rows[c.Id].Name);

        await _b.SyncAsync();
        Assert.Equal("A-name", _b.NameOf(c.Id));
    }

    [Fact]
    public async Task StaleMachine_ReuploadsCharactersWhosePurgedTombstonesItMissed()
    {
        var c = await SyncedToBoth();
        _b.Repo.Delete(c.Id);
        await _b.SyncAsync();
        _cloud.Rows.Remove(c.Id); // the 90-day purge job ran
        _time.Now += TimeSpan.FromDays(81);

        await _a.SyncAsync();
        await _a.SyncAsync();
        Assert.False(_cloud.Rows[c.Id].IsDeleted); // an accepted un-delete, never data loss
    }

    // ---- The character open in the UI ----

    [Fact]
    public async Task RemoteChangeToTheOpenCharacter_IsDeferred_ThenApplied()
    {
        var c = await SyncedToBoth();
        _cloud.EditRemotely(c.Id, x => x.Identity.Name = "Remote");
        _a.OpenId = c.Id;

        var result = await _a.SyncAsync();
        var change = Assert.Single(result.Deferred);
        Assert.Equal("Remote", change.Remote!.Identity.Name);
        Assert.Equal("Arannis", _a.NameOf(c.Id));

        Assert.Null(_a.Sync.ApplyDeferred(change, _a.Repo.Load(c.Id), hasUnsavedChanges: false));
        Assert.Equal("Remote", _a.NameOf(c.Id));
        Assert.Empty(_a.Repo.GetDirty());
    }

    [Fact]
    public async Task ApplyDeferred_KeepsUnsavedEditsAsAConflictCopy()
    {
        var c = await SyncedToBoth();
        _cloud.EditRemotely(c.Id, x => x.Identity.Name = "Remote");
        _a.OpenId = c.Id;
        var change = Assert.Single((await _a.SyncAsync()).Deferred);

        var inMemory = _a.Repo.Load(c.Id)!;
        inMemory.Identity.Name = "Mine";
        var copy = _a.Sync.ApplyDeferred(change, inMemory, hasUnsavedChanges: true);

        Assert.StartsWith("Mine (conflict copy, ", copy!.Identity.Name, StringComparison.Ordinal);
        Assert.NotNull(_a.Repo.Load(copy.Id));
        Assert.Equal("Remote", _a.NameOf(c.Id));
    }

    [Fact]
    public async Task RemoteDeleteOfTheOpenCharacter_IsDeferred_ThenApplied()
    {
        var c = await SyncedToBoth();
        _b.Repo.Delete(c.Id);
        await _b.SyncAsync();
        _a.OpenId = c.Id;

        var change = Assert.Single((await _a.SyncAsync()).Deferred);
        Assert.Null(change.Remote);
        Assert.NotNull(_a.Repo.Load(c.Id));

        _a.Sync.ApplyDeferred(change, _a.Repo.Load(c.Id), hasUnsavedChanges: false);
        Assert.Null(_a.Repo.Load(c.Id));
    }

    // ---- Switching accounts ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Relink_GivesCharactersNewIds_AndUploadsOnlyWhenAsked(bool upload)
    {
        var c = _a.Add("Arannis");
        await _a.SyncAsync();
        _session.UserId = "user-2";
        _cloud.Rows.Clear(); // user-2 sees an empty table

        _a.Sync.Relink("user-2", uploadExisting: upload);
        Assert.Equal(SyncOutcome.Synced, (await _a.SyncAsync()).Outcome);

        var local = Assert.Single(_a.Repo.List());
        Assert.NotEqual(c.Id, local.Id);
        Assert.Equal(upload ? 1 : 0, _cloud.Rows.Count);
        Assert.Equal("user-2", _a.Sync.LinkedAccount);
    }
}
