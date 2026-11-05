using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class ImmediateEditingTests : IDisposable
{
    [Fact]
    public void RatingAndUndoKeepFacetsAndMinedTagStatisticsWarm()
    {
        using var db = new CatalogRepository(Database);
        var first = db.Upsert(Add(db,"first") with {Tags=["A","B"]});
        db.Upsert(Add(db,"second") with {Tags=["A","B"]});
        db.Upsert(Add(db,"third") with {Tags=["A","B"]});
        var facets = db.GetFacets();
        var combination = Assert.Single(db.GetTagCombinations());
        for (var index=0;index<20;index++)
        {
            var undo = db.ApplyImmediateEdit([first.Id],ImmediateEditFields.Rating,item=>item with {Rating=8});
            Assert.Same(facets,db.GetFacets()); Assert.Same(combination,Assert.Single(db.GetTagCombinations()));
            Assert.True(db.UndoImmediateEdit(undo));
            Assert.Same(facets,db.GetFacets()); Assert.Same(combination,Assert.Single(db.GetTagCombinations()));
        }
    }

    [Fact]
    public void TagEditAndUndoRefreshTagFacetsWhileForeignWritesInvalidateThem()
    {
        using var db = new CatalogRepository(Database);
        var first = db.Upsert(Add(db,"first") with {TagCategories=new() {["Cast"]=["Before"]}});
        db.GetFacets(); db.GetTagUsage();
        var undo = db.ApplyImmediateEdit([first.Id],ImmediateEditFields.Tags,item=>item with {Tags=["After"],TagCategories=new() {["Studio"]=["After"]}});
        Assert.Equal(["After"],db.GetFacets().Tags); Assert.Equal(1,db.GetFacets().TagCategoryCounts!["Studio"]);
        Assert.Equal("After",Assert.Single(db.GetTagUsage()).Tag);
        Assert.True(db.UndoImmediateEdit(undo));
        Assert.Equal(["Before"],db.GetFacets().Tags); Assert.Equal(1,db.GetFacets().TagCategoryCounts!["Cast"]);
        using (var other = new CatalogRepository(Database)) other.Upsert(new() {Path=Path.Combine(root,"foreign.mp4"),Tags=["Foreign"]});
        db.ApplyImmediateEdit([first.Id],ImmediateEditFields.Rating,item=>item with {Rating=6});
        Assert.Equal(2,db.GetFacets().MediaCount); Assert.Contains("Foreign",db.GetFacets().Tags);
    }

    [Fact]
    public void RolledBackBatchDoesNotPublishPartialTagCacheChanges()
    {
        using var db = new CatalogRepository(Database);
        var first = db.Upsert(Add(db,"first") with {Tags=["Before"]});
        var second = db.Upsert(Add(db,"reject") with {Tags=["Before"]});
        db.GetFacets(); db.GetTagUsage();
        using var fault = new SqliteConnection(CatalogEncryption.ConnectionString(Database,null).ToString());
        fault.Open(); CatalogRepository.AddOrdinalCollation(fault);
        using var command = fault.CreateCommand();
        command.CommandText = $"CREATE TRIGGER reject_cache_write BEFORE UPDATE ON media WHEN NEW.id='{second.Id}' BEGIN SELECT RAISE(ABORT,'synthetic cache failure'); END";
        command.ExecuteNonQuery();
        Assert.Throws<SqliteException>(() => db.ApplyImmediateEdit([first.Id,second.Id],ImmediateEditFields.Tags,item=>item with {Tags=["After"]}));
        Assert.Equal(["Before"],db.GetFacets().Tags);
        Assert.Equal(2,Assert.Single(db.GetTagUsage()).MediaCount);
        Assert.Equal(["Before"],db.GetMedia(first.Id)!.Tags);
    }
    private readonly string root = Path.Combine(Path.GetTempPath(), "flixypix-undo-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(root, "catalog.db");
    private MediaItem Add(CatalogRepository db, string name) => db.Upsert(new() { Path = Path.Combine(root, name + ".mp4"), Title = name });

    [Fact]
    public void UndoRestoresOnlyChangedFieldsAndPreservesDraftAndPlayback()
    {
        using var db = new CatalogRepository(Database); var item = Add(db, "first");
        var undo = db.ApplyImmediateEdit([item.Id], ImmediateEditFields.All, current => current with { Rating = 9 });
        Assert.Equal(ImmediateEditFields.Rating, Assert.Single(undo.Rows).Fields);
        db.Upsert(db.GetMedia(item.Id)! with { Description = "concurrent", IsFavorite = true, PlaybackPositionSeconds = 14 });
        db.SaveMetadataDrafts([new(item.Id, "pending", "", "", "", MetadataValues.From(item))]);
        Assert.True(db.UndoImmediateEdit(undo));
        var saved = db.GetMedia(item.Id)!;
        Assert.Equal(0, saved.Rating); Assert.True(saved.IsFavorite);
        Assert.Equal("concurrent", saved.Description); Assert.Equal(14, saved.PlaybackPositionSeconds);
        Assert.Equal("pending", Assert.Single(db.GetMetadataDrafts()).Title);
    }

    [Fact]
    public void TagUndoRestoresCategoriesButKeepsNewRating()
    {
        using var db = new CatalogRepository(Database); var item = Add(db, "first");
        item = db.Upsert(item with { Tags = ["A", "B"], TagCategories = new() { ["Group"] = ["A"] } });
        var undo = db.UpdateTagsWithUndo([item.Id], ["C"], ["A"]);
        db.Upsert(db.GetMedia(item.Id)! with { Rating = 8 });
        Assert.True(db.UndoImmediateEdit(undo));
        var restored = db.GetMedia(item.Id)!;
        Assert.Equal(item.Tags, restored.Tags); Assert.Equal(["A"], restored.TagCategories["Group"]); Assert.Equal(8, restored.Rating);
    }

    [Fact]
    public void RoleTagUndoKeepsOtherRolesAndUnrelatedEdits()
    {
        using var db = new CatalogRepository(Database); var item = Add(db, "first");
        item = db.Upsert(item with { Tags = ["Shared"], TagCategories = new() { ["Performers"] = ["Shared"], ["Studios"] = ["Shared"] } });
        var undo = db.UpdateCategoryTagsWithUndo([item.Id], "Performers", ["New"], ["Shared"]);
        db.Upsert(db.GetMedia(item.Id)! with { IsFavorite = true });
        Assert.True(db.UndoImmediateEdit(undo));
        var restored = db.GetMedia(item.Id)!;
        Assert.Equal(["Shared"], restored.TagCategories["Performers"]);
        Assert.Equal(["Shared"], restored.TagCategories["Studios"]);
        Assert.True(restored.IsFavorite);
    }

    [Fact]
    public void ConcurrentChangeToSecondMediumRejectsWholeUndo()
    {
        using var db = new CatalogRepository(Database); var first = Add(db, "first"); var second = Add(db, "second");
        var undo = db.ApplyImmediateEdit([first.Id, second.Id], ImmediateEditFields.Rating, current => current with { Rating = 8 });
        db.Upsert(db.GetMedia(second.Id)! with { Rating = 6 });
        Assert.False(db.UndoImmediateEdit(undo));
        Assert.Equal(8, db.GetMedia(first.Id)!.Rating); Assert.Equal(6, db.GetMedia(second.Id)!.Rating);
    }

    [Fact]
    public void FailedSecondUndoWriteRollsBackFirst()
    {
        using var db = new CatalogRepository(Database); var first = Add(db, "first"); var second = Add(db, "second");
        var undo = db.ApplyImmediateEdit([first.Id, second.Id], ImmediateEditFields.Favorite, current => current with { IsFavorite = true });
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database }.ToString());
        connection.Open(); using var command = connection.CreateCommand();
        command.CommandText = $"CREATE TRIGGER fail_undo BEFORE UPDATE ON media WHEN OLD.id='{second.Id}' BEGIN SELECT RAISE(ABORT,'synthetic failure'); END;";
        command.ExecuteNonQuery();
        Assert.Throws<SqliteException>(() => db.UndoImmediateEdit(undo));
        Assert.True(db.GetMedia(first.Id)!.IsFavorite); Assert.True(db.GetMedia(second.Id)!.IsFavorite);
    }

    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
}
