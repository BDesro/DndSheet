using Hearthsheet.Core.Serialization;
using Hearthsheet.Core.Sync;
using Hearthsheet.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hearthsheet.Tests;

public class SyncStoreTests : IDisposable
{
    private readonly TempDir _dir = new();
    private SqliteCharacterRepository NewRepository() =>
        new(_dir.File("characters.db"), CharacterMigrator.Default, NullLogger<SqliteCharacterRepository>.Instance);

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void UpgradeFromVersion1_KeepsRowsAndMarksThemUnsynced()
    {
        var c = Samples.Rich();
        using (var connection = new SqliteConnection($"Data Source={_dir.File("characters.db")};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO meta VALUES ('db_version', '1');
                CREATE TABLE characters (id TEXT PRIMARY KEY, name TEXT NOT NULL, description TEXT NOT NULL,
                    schema_version INTEGER NOT NULL, data TEXT NOT NULL, created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL);
                INSERT INTO characters VALUES ($id, 'Arannis', '', 1, $data, '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z');
                """;
            command.Parameters.AddWithValue("$id", c.Id.ToString());
            command.Parameters.AddWithValue("$data", CharacterJson.Serialize(c));
            command.ExecuteNonQuery();
        }

        var repo = NewRepository();
        Assert.Equal("Arannis", repo.Load(c.Id)!.Identity.Name);
        Assert.Null(Assert.Single(repo.GetDirty()).CloudRevision);
        Assert.Single(NewRepository().GetDirty()); // reopening at version 2 changes nothing
    }

    [Fact]
    public void Save_MarksDirtyAndCountsLocalRevisions()
    {
        var repo = NewRepository();
        var c = Samples.Rich();
        repo.Save(c);
        repo.Save(c);
        Assert.Equal(2, Assert.Single(repo.GetDirty()).LocalRevision);
        Assert.Equal(new LocalState(true, 2), repo.GetState(c.Id));
        Assert.Null(repo.GetState(Guid.NewGuid()));
    }

    [Fact]
    public void MarkPushed_ClearsDirtyOnlyIfNothingWasSavedMeanwhile()
    {
        var repo = NewRepository();
        var c = Samples.Rich();
        repo.Save(c);
        var pushed = Assert.Single(repo.GetDirty());
        repo.Save(c); // saved while the push was in flight

        repo.MarkPushed(c.Id, pushed.LocalRevision, cloudRevision: 1);
        var still = Assert.Single(repo.GetDirty());
        Assert.Equal(1, still.CloudRevision);

        repo.MarkPushed(c.Id, still.LocalRevision, cloudRevision: 2);
        Assert.Empty(repo.GetDirty());
    }

    [Fact]
    public void ApplyRemote_RespectsDirtyRows_UnlessGuardedOrForced()
    {
        var repo = NewRepository();
        var c = Samples.Rich();
        repo.Save(c);
        var remote = CharacterJson.Clone(c, CharacterMigrator.Default);
        remote.Identity.Name = "Remote";

        Assert.False(repo.ApplyRemote(remote, 5));
        Assert.False(repo.ApplyRemote(remote, 5, ifLocalRevision: 99));
        Assert.True(repo.ApplyRemote(remote, 5, ifLocalRevision: 1));
        Assert.Equal("Remote", repo.Load(c.Id)!.Identity.Name);
        Assert.Empty(repo.GetDirty());

        repo.Save(c);
        Assert.True(repo.ApplyRemote(remote, 6, force: true));
        Assert.Equal(6, repo.GetCloudRevisions()[c.Id]);
    }

    [Fact]
    public void ApplyRemote_InsertsNewRows_ButNotOnesDeletedLocally()
    {
        var repo = NewRepository();
        var c = Samples.Rich();
        Assert.True(repo.ApplyRemote(c, 1));
        Assert.Empty(repo.GetDirty());

        Assert.True(repo.Delete(c.Id));
        Assert.Equal(new PendingDelete(c.Id, 1), Assert.Single(repo.GetPendingDeletes()));
        Assert.False(repo.ApplyRemote(c, 2));
        Assert.Null(repo.Load(c.Id));

        repo.ClearPendingDelete(c.Id);
        Assert.Empty(repo.GetPendingDeletes());
    }

    [Fact]
    public void Delete_OfNeverSyncedCharacter_LeavesNothingPending()
    {
        var repo = NewRepository();
        var c = Samples.Rich();
        repo.Save(c);
        Assert.True(repo.Delete(c.Id));
        Assert.False(repo.Delete(c.Id));
        Assert.Empty(repo.GetPendingDeletes());
    }

    [Fact]
    public void ApplyRemoteDelete_SkipsDirtyRowsUnlessForced()
    {
        var repo = NewRepository();
        var c = Samples.Rich();
        repo.Save(c);
        Assert.False(repo.ApplyRemoteDelete(c.Id));
        Assert.True(repo.ApplyRemoteDelete(c.Id, force: true));
        Assert.Null(repo.Load(c.Id));
    }

    [Fact]
    public void Rebase_SetsCloudRevisionAndMarksDirty()
    {
        var repo = NewRepository();
        var c = Samples.Rich();
        repo.ApplyRemote(c, 3);
        repo.Rebase(c.Id, null);
        Assert.Null(Assert.Single(repo.GetDirty()).CloudRevision);
    }

    [Fact]
    public void ReassignAllIds_ThenResetSyncState_StartsOverUnderANewAccount()
    {
        var repo = NewRepository();
        var c = Samples.Rich();
        repo.ApplyRemote(c, 3);
        repo.SetSyncAccount("user-1");
        repo.SetLastPull(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));

        repo.ReassignAllIds();
        repo.ResetSyncState(markAllDirty: true);

        var moved = Assert.Single(repo.List());
        Assert.NotEqual(c.Id, moved.Id);
        Assert.Equal(c.Identity.Name, repo.Load(moved.Id)!.Identity.Name);
        Assert.Null(Assert.Single(repo.GetDirty()).CloudRevision);
        Assert.Equal(new SyncMeta("user-1", null), repo.GetSyncMeta());

        repo.ResetSyncState(markAllDirty: false);
        Assert.Empty(repo.GetDirty());
    }

    [Fact]
    public void SyncMeta_RoundTrips()
    {
        var repo = NewRepository();
        Assert.Equal(new SyncMeta(null, null), repo.GetSyncMeta());
        var at = new DateTimeOffset(2026, 9, 29, 10, 0, 0, 123, TimeSpan.Zero);
        repo.SetSyncAccount("user-1");
        repo.SetLastPull(at);
        Assert.Equal(new SyncMeta("user-1", at), NewRepository().GetSyncMeta());
    }
}
