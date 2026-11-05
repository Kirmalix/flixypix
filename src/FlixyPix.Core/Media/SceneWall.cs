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
    public WallPage QueryWallPage(WallQuery query, int offset=0, int limit=120, CancellationToken cancellationToken=default) =>
        wallQueries.QueryPage(query,offset,limit,cancellationToken);

    /// <summary>Compatibility bulk API, aggregated from the canonical paged query under the catalog gate.</summary>
    public IReadOnlyList<WallMark> GetWallMarks(WallQuery query, CancellationToken cancellationToken=default)
    {
        lock(gate)
        {
            ThrowIfDisposed();
            var items=new List<WallMark>();
            for(var offset=0;;offset+=1000)
            {
                var page=QueryWallPage(query,offset,1000,cancellationToken);
                items.AddRange(page.Items);
                if(items.Count>=page.TotalCount || page.Items.Count==0)return items;
            }
        }
    }
}
