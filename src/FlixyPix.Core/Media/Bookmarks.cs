using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

public sealed partial class CatalogRepository
{
    public IReadOnlyList<Bookmark> GetBookmarks(Guid? mediaId = null)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            return ReadDocuments<Bookmark>("bookmarks").Where(x => mediaId is null || x.MediaId == mediaId)
                .OrderBy(x => x.PositionSeconds).ToArray();
        }
    }

    public Bookmark UpsertBookmark(Bookmark bookmark)
    {
        bookmark = ValidateBookmark(bookmark);
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            EnsureMediaExist([bookmark.MediaId], transaction);
            EnsurePreviewOwnership(bookmark.MediaId,bookmark.PreviewAssetId,transaction);
            SaveBookmark(bookmark, transaction);
            transaction.Commit();
            return bookmark;
        }
    }

    public void DeleteBookmark(Guid id) => DeleteDocument("bookmarks", id);

    /// <summary>Checks the bookmark and returns it with its tags cleaned like scene tags.</summary>
    private static Bookmark ValidateBookmark(Bookmark bookmark)
    {
        ArgumentNullException.ThrowIfNull(bookmark);
        if (bookmark.Id == Guid.Empty || bookmark.MediaId == Guid.Empty || bookmark.Title is null || !Enum.IsDefined(bookmark.Kind) ||
            !double.IsFinite(bookmark.PositionSeconds) || bookmark.PositionSeconds < 0)
            throw new ArgumentException("Bookmark needs a media item and a finite nonnegative position.", nameof(bookmark));
        return bookmark with { Tags = NormalizeSceneTags(bookmark.Tags ?? []) };
    }

    private void SaveBookmark(Bookmark bookmark, SqliteTransaction transaction)
    {
        if(KeepLegacyExisting("bookmarks",bookmark.Id.ToString(),JsonSerializer.Serialize(bookmark,Json),transaction))return;
        using var command = Command("INSERT INTO bookmarks(id,media_id,document) VALUES($id,$media,$json) ON CONFLICT(id) DO UPDATE SET media_id=excluded.media_id,document=excluded.document",
            transaction, ("$id", bookmark.Id.ToString()), ("$media", bookmark.MediaId.ToString()), ("$json", JsonSerializer.Serialize(bookmark, Json)));
        command.ExecuteNonQuery();
    }

    private void ValidatePlaylistEntries(Playlist playlist, SqliteTransaction transaction)
    {
        if (playlist.Entries.Count == 0) return;
        if (playlist.Entries.Any(x => x is null) || !playlist.MediaIds.SequenceEqual(playlist.Entries.Select(x => x.MediaId)))
            throw new ArgumentException("Playlist entries must match MediaIds in the same order.");
        foreach (var entry in playlist.Entries.Where(x => x.SceneId is not null))
        {
            using var command = Command("SELECT media_id FROM scenes WHERE id=$id", transaction, ("$id", entry.SceneId!.Value.ToString()));
            if (!Equals(command.ExecuteScalar(), entry.MediaId.ToString())) throw new ArgumentException("Playlist clip is missing or belongs to another media item.");
        }
    }
}
