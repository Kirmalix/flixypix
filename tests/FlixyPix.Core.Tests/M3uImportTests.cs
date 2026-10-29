using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class M3uImportTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "flixypix-m3u-" + Guid.NewGuid());
    public M3uImportTests() => Directory.CreateDirectory(root);
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }

    [Fact]
    public void PlaylistBecomesACollectionOfTheCatalogedEntriesInOrder()
    {
        using var catalog = new CatalogRepository(Path.Combine(root, "catalog.db"));
        var a = catalog.Upsert(new MediaItem { Path = Path.Combine(root, "media", "a.mp4"), Kind = MediaKind.Video });
        var b = catalog.Upsert(new MediaItem { Path = Path.Combine(root, "media", "b.mp3"), Kind = MediaKind.Audio });
        var list = Path.Combine(root, "Abend.m3u8");
        File.WriteAllText(list, "﻿#EXTM3U\n#EXTINF:3,B\nmedia/b.mp3\nhttp://example.invalid/stream\n" + new Uri(a.Path).AbsoluteUri + "\nmedia/fehlt.mp4\nmedia/b.mp3\n");
        Assert.Equal(4, M3uPlaylists.Read(list).Count);
        var result = catalog.ImportM3u(list);
        Assert.Equal("Abend", result.Collection!.Name);
        Assert.Equal([b.Id, a.Id], catalog.GetPlaylists().Single().MediaIds);
        Assert.Equal(Path.Combine(root, "media", "fehlt.mp4"), string.Join(" | ", result.NotInCatalog));
        File.WriteAllText(list, "#EXTM3U\nnirgends.mp4\n");
        Assert.Null(catalog.ImportM3u(list).Collection);
    }
}
