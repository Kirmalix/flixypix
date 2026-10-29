using System.Text.Json;

namespace FlixyPix.Core;

public enum WallMarkKind { Scene, Bookmark }

/// <summary>One tile of the scene wall: a scene or a bookmark with its video.</summary>
public sealed record WallMark(WallMarkKind Kind, Guid Id, MediaItem Media, string Title, string? Description, double StartSeconds, double? EndSeconds, IReadOnlyList<string> Tags, Guid? PreviewAssetId)
{
    /// <summary>Tags of the mark and of its video; filters look at both.</summary>
    public IEnumerable<string> AllTags => Tags.Concat(Media.Tags);
}

/// <summary>Filter of the scene wall. Text looks at the titles and descriptions of mark and video; every chosen tag must be
/// on the mark or its video, so performers and studios (tags with a role) filter the same way.</summary>
public sealed record WallQuery(string? Text = null, IReadOnlyList<string>? Tags = null, bool Scenes = true, bool Bookmarks = true);

public sealed partial class CatalogRepository
{
    /// <summary>All scenes and bookmarks of video files with their video, newest import first, then by place in the
    /// video. One query per kind joins the video, so no media are loaded one by one.</summary>
    public IReadOnlyList<WallMark> GetWallMarks(WallQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var text = string.IsNullOrWhiteSpace(query.Text) ? null : query.Text.Trim();
        var tags = (query.Tags ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToArray();
        var marks = new List<WallMark>();
        lock (gate)
        {
            ThrowIfDisposed();
            void Read(string table, Func<string, MediaItem, WallMark> map)
            {
                using var command = Command($"SELECT x.document, m.document FROM {table} x JOIN media m ON m.id = x.media_id", null);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var media = JsonSerializer.Deserialize<MediaItem>(reader.GetString(1), Json) ?? throw new InvalidDataException("Invalid media record.");
                    if (media.Kind == MediaKind.Video) marks.Add(map(reader.GetString(0), media));
                }
            }
            if (query.Scenes) Read("scenes", (json, media) =>
            {
                var scene = JsonSerializer.Deserialize<Scene>(json, Json) ?? throw new InvalidDataException("Invalid scenes record.");
                return new(WallMarkKind.Scene, scene.Id, media, scene.Title, scene.Description, scene.StartSeconds, scene.EndSeconds, scene.Tags, scene.PreviewAssetId);
            });
            if (query.Bookmarks) Read("bookmarks", (json, media) =>
            {
                var bookmark = JsonSerializer.Deserialize<Bookmark>(json, Json) ?? throw new InvalidDataException("Invalid bookmarks record.");
                return new(WallMarkKind.Bookmark, bookmark.Id, media, bookmark.Title, bookmark.Description, bookmark.PositionSeconds, null, [], bookmark.PreviewAssetId);
            });
        }
        bool Matches(WallMark mark) =>
            (text == null || new[] { mark.Title, mark.Description, mark.Media.Title, mark.Media.Description }.Any(field => field?.Contains(text, StringComparison.CurrentCultureIgnoreCase) == true))
            && tags.All(tag => mark.AllTags.Contains(tag, StringComparer.CurrentCultureIgnoreCase));
        return marks.Where(Matches).OrderByDescending(m => m.Media.AddedAt).ThenBy(m => m.Media.Id).ThenBy(m => m.StartSeconds).ThenBy(m => m.Id).ToArray();
    }
}
