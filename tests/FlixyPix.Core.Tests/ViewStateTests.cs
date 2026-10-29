using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class ViewStateTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "flixypix-viewstate-" + Guid.NewGuid().ToString("N"));
    public ViewStateTests() => Directory.CreateDirectory(directory);
    public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(directory, true); } catch (IOException) { } }
    private string PathFor(string name) => Path.Combine(directory, name);

    [Fact]
    public void StateSurvivesReopeningWithoutDroppingTheBrowsingCaches()
    {
        var database = PathFor("catalog.db");
        Guid first, second, playlist;
        using (var catalog = new CatalogRepository(database))
        {
            first = catalog.Upsert(new() { Path = PathFor(@"Urlaub\a.mp4"), Kind = MediaKind.Video, Tags = ["Strand"] }).Id;
            second = catalog.Upsert(new() { Path = PathFor(@"Urlaub\b.mp4"), Kind = MediaKind.Video, Tags = ["Berg"] }).Id;
            playlist = catalog.UpsertPlaylist(new() { Name = "Sommer", MediaIds = [first] }).Id;
            var facets = catalog.GetFacets();
            catalog.SaveViewState(new()
            {
                View = "Video", PlaylistId = playlist, SearchText = "urlaub", RequiredTags = ["Strand"], ExcludedTags = ["Berg"], MinimumStars = 3,
                SortOrder = 2, ViewMode = 1, TileWidth = 240, Position = 1, AnchorId = second, Selection = [first, second],
                Expression = new() { Condition = new() { Field = SearchField.Duration, Comparison = SearchComparison.GreaterThan, Number = 60 } }
            });
            Assert.Same(facets, catalog.GetFacets());
        }
        using var reopened = new CatalogRepository(database);
        var state = reopened.GetViewState()!;
        Assert.Equal(("Video", playlist, "urlaub", 3, 2, 240, second), (state.View, state.PlaylistId!.Value, state.SearchText!, state.MinimumStars, state.SortOrder, state.TileWidth, state.AnchorId!.Value));
        Assert.Equal(["Strand"], state.RequiredTags); Assert.Equal([first, second], state.Selection);
        Assert.Equal(60, state.Expression!.Condition!.Number);
        Assert.True(state.SavedAt > DateTimeOffset.UtcNow.AddMinutes(-1));
        var cleaned = reopened.Sanitize(state, out var dropped);
        Assert.Empty(dropped); Assert.Equal(state.Selection, cleaned.Selection);
    }

    [Fact]
    public void RestoringDropsWhatNoLongerExistsAndSaysWhat()
    {
        using var catalog = new CatalogRepository(PathFor("catalog.db"));
        var kept = catalog.Upsert(new() { Path = PathFor(@"Fotos\a.jpg"), Kind = MediaKind.Image, Tags = ["Natur"], Category = "Reisen" }).Id;
        var state = new ViewState
        {
            View = "Unbekannt", PlaylistId = Guid.NewGuid(), SavedSearchId = Guid.NewGuid(), Tag = "Weg", TagCategory = "Orte", Category = "Reisen",
            Folder = PathFor("Gelöscht"), RequiredTags = ["Natur", "Weg"], ExcludedTags = ["Auch weg"], MinimumStars = 9, Position = -4,
            AnchorId = Guid.NewGuid(), Selection = [kept, Guid.NewGuid()]
        };
        var cleaned = catalog.Sanitize(state, out var dropped);
        Assert.Equal(("all", (Guid?)null, (Guid?)null, (string?)null, (string?)null, "Reisen", (string?)null), (cleaned.View, cleaned.PlaylistId, cleaned.SavedSearchId, cleaned.Tag, cleaned.TagCategory, cleaned.Category, cleaned.Folder));
        Assert.Equal(["Natur"], cleaned.RequiredTags); Assert.Empty(cleaned.ExcludedTags);
        Assert.Equal([kept], cleaned.Selection); Assert.Null(cleaned.AnchorId);
        Assert.Equal((5, 0), (cleaned.MinimumStars, cleaned.Position));
        Assert.Contains("Sammlung", dropped); Assert.Contains("Tag „Auch weg“", dropped); Assert.Contains(dropped, d => d.StartsWith("Ordner"));
        // A folder above the media still exists.
        Assert.Equal(PathFor(""), catalog.Sanitize(state with { Folder = PathFor("") }, out _).Folder);
    }

    [Fact]
    public void ViewScopeKeepsOnlyTheViewAndHowItIsShown()
    {
        var state = new ViewState { View = "Image", SearchText = "x", RequiredTags = ["a"], SortOrder = 3, ViewMode = 1, TileWidth = 300, Position = 99, Selection = [Guid.NewGuid()] };
        var view = state.Restricted(RestoreScope.View);
        Assert.Equal(("Image", 3, 1, 300), (view.View, view.SortOrder, view.ViewMode, view.TileWidth));
        Assert.Null(view.SearchText); Assert.Empty(view.RequiredTags); Assert.Equal(0, view.Position); Assert.Empty(view.Selection);
        Assert.Same(state, state.Restricted(RestoreScope.All));
    }

    [Fact]
    public void HistoryGoesBackAndForwardAndScrollingDoesNotAddEntries()
    {
        var history = new ViewHistory(3);
        history.Record(new() { View = "all" });
        history.Record(new() { View = "all", Position = 40 });
        history.Record(new() { View = "Video" });
        history.Record(new() { View = "Video", RequiredTags = ["a"] });
        Assert.Equal(("Video", 0), (history.Back()!.View, history.Current!.Position));
        Assert.Equal(40, history.Back()!.Position);
        Assert.Equal("Video", history.Forward()!.View);
        Assert.True(history.CanGoForward);
        history.Record(new() { View = "Image" });
        Assert.False(history.CanGoForward);
        history.Record(new() { View = "Audio" });
        Assert.Equal("Image", history.Back()!.View);
        Assert.Equal("Video", history.Back()!.View);
        Assert.Null(history.Back());
    }
}
