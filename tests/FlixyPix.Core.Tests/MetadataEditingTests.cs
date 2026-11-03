using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class MetadataEditingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "flixypix-edit-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(root, "catalog.db");

    [Fact]
    public void ConflictRollsBackWholeBatchAndKeepsRecoveryDrafts()
    {
        using var db = new CatalogRepository(Database);
        var first = db.Upsert(new MediaItem { Path = Path.Combine(root, "a.mp4"), Title = "A" });
        var second = db.Upsert(new MediaItem { Path = Path.Combine(root, "b.mp4"), Title = "B" });
        db.SaveMetadataDrafts([new(first.Id, "draft", "", "", "", MetadataValues.From(first))]);
        db.Upsert(second with { Description = "concurrent edit" });
        Assert.Throws<MetadataConflictException>(() => db.ApplyMetadataPatches([
            new(first.Id, MetadataValues.From(first), MetadataValues.From(first) with { Title = "changed" }),
            new(second.Id, MetadataValues.From(second), MetadataValues.From(second) with { Title = "changed" })], clearDrafts: true));
        Assert.Equal("A", db.GetMedia(first.Id)!.Title);
        Assert.Equal("concurrent edit", db.GetMedia(second.Id)!.Description);
        Assert.Single(db.GetMetadataDrafts());
    }

    [Fact]
    public void PatchesKeepImmediateEditsAndClearDraftsOnlyOnSuccess()
    {
        using var db = new CatalogRepository(Database);
        var item = db.Upsert(new MediaItem { Path = Path.Combine(root, "a.mp4"), Title = "A" });
        db.SaveMetadataDrafts([new(item.Id, "draft", "", "", "", MetadataValues.From(item))]);
        db.Upsert(item with { Rating = 9, Tags = ["new tag"], PlaybackPositionSeconds = 123 });
        db.ApplyMetadataPatches([new(item.Id, MetadataValues.From(item), MetadataValues.From(item) with { Title = "draft" })], clearDrafts: true);
        var saved = db.GetMedia(item.Id)!;
        Assert.Equal("draft", saved.Title);
        Assert.Equal(9, saved.Rating);
        Assert.Contains("new tag", saved.Tags);
        Assert.Equal(123, saved.PlaybackPositionSeconds);
        Assert.Empty(db.GetMetadataDrafts());
    }

    [Fact]
    public void DatabaseFailureOnSecondWriteRollsBackFirstWrite()
    {
        using var db = new CatalogRepository(Database);
        var first = db.Upsert(new MediaItem { Path = Path.Combine(root, "a.mp4"), Title = "A" });
        var second = db.Upsert(new MediaItem { Path = Path.Combine(root, "b.mp4"), Title = "B" });
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"CREATE TRIGGER fail_metadata BEFORE UPDATE ON media WHEN OLD.id='{second.Id}' BEGIN SELECT RAISE(ABORT,'simulated write failure'); END;";
        command.ExecuteNonQuery();
        Assert.Throws<SqliteException>(() => db.ApplyMetadataPatches([
            new(first.Id, MetadataValues.From(first), MetadataValues.From(first) with { Title = "changed" }),
            new(second.Id, MetadataValues.From(second), MetadataValues.From(second) with { Title = "changed" })]));
        Assert.Equal("A", db.GetMedia(first.Id)!.Title);
        Assert.Equal("B", db.GetMedia(second.Id)!.Title);
    }

    [Fact]
    public void SavingOneDraftKeepsOtherDraftsAndRollsBackIfDraftCleanupFails()
    {
        using var db = new CatalogRepository(Database);
        var first = db.Upsert(new MediaItem { Path = Path.Combine(root, "a.mp4"), Title = "A" });
        var second = db.Upsert(new MediaItem { Path = Path.Combine(root, "b.mp4"), Title = "B" });
        db.SaveMetadataDrafts([new(first.Id, "changed", "", "", "", MetadataValues.From(first)),
            new(second.Id, "other draft", "", "", "", MetadataValues.From(second))]);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER fail_cleanup BEFORE UPDATE ON ui_state BEGIN SELECT RAISE(ABORT,'cleanup failed'); END;";
        command.ExecuteNonQuery();
        var patch = new MetadataPatch(first.Id, MetadataValues.From(first), MetadataValues.From(first) with { Title = "changed" });
        Assert.Throws<SqliteException>(() => db.ApplyMetadataPatches([patch], clearDraftIds: [first.Id]));
        Assert.Equal("A", db.GetMedia(first.Id)!.Title);
        Assert.Equal(2, db.GetMetadataDrafts().Count);
        command.CommandText = "DROP TRIGGER fail_cleanup"; command.ExecuteNonQuery();
        db.ApplyMetadataPatches([patch], clearDraftIds: [first.Id]);
        Assert.Equal("changed", db.GetMedia(first.Id)!.Title);
        Assert.Equal(second.Id, Assert.Single(db.GetMetadataDrafts()).MediaId);
        db.ApplyMetadataPatches([], clearDraftIds: [second.Id]);
        Assert.Empty(db.GetMetadataDrafts());
        Assert.Equal("B", db.GetMedia(second.Id)!.Title);
    }

    [Fact]
    public void MissingMediaConflictIdentifiesDraftWithoutClearingIt()
    {
        using var db = new CatalogRepository(Database);
        var id = Guid.NewGuid(); var original = new MetadataValues("missing", null, null, null);
        db.SaveMetadataDrafts([new(id, "keep", "", "", "", original)]);
        var error = Assert.Throws<MetadataConflictException>(() => db.ApplyMetadataPatches([new(id, original, original)], clearDrafts: true));
        Assert.Equal(id, error.MediaId); Assert.True(error.Missing);
        Assert.Equal("keep", Assert.Single(db.GetMetadataDrafts()).Title);
    }

    [Fact]
    public void RecoveryDraftsStayEncryptedAndSurviveUnlock()
    {
        using (var db = new CatalogRepository(Database))
            db.Upsert(new MediaItem { Path = Path.Combine(root, "a.mp4"), Title = "A" });
        const string password = "synthetic-draft-test-password";
        const string privateDraft = "synthetic-private-recovery-text-0294";
        CatalogEncryption.EnableEncryption(Database, password);
        using (var key = CatalogEncryption.Unlock(Database, password))
        using (var db = new CatalogRepository(Database, key))
        {
            var item = Assert.Single(db.Search());
            db.SaveMetadataDrafts([new(item.Id, privateDraft, "", "", "", MetadataValues.From(item))]);
        }
        foreach (var file in Directory.GetFiles(root))
            Assert.True(File.ReadAllBytes(file).AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(privateDraft)) < 0, file);
        using var reopenedKey = CatalogEncryption.Unlock(Database, password);
        using var reopened = new CatalogRepository(Database, reopenedKey);
        Assert.Equal(privateDraft, Assert.Single(reopened.GetMetadataDrafts()).Title);
    }

    [Fact]
    public void DraftsSurviveReopenAndStayInTheirLibrary()
    {
        var id = Guid.NewGuid();
        using (var db = new CatalogRepository(Database))
            db.SaveMetadataDrafts([new(id, "draft", "category", "description", "invalid date retained", new("old", null, null, null))]);
        using var reopened = new CatalogRepository(Database);
        Assert.Equal("invalid date retained", Assert.Single(reopened.GetMetadataDrafts()).ReleaseDate);
        using var other = new CatalogRepository(Path.Combine(root, "other.db"));
        Assert.Empty(other.GetMetadataDrafts());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
