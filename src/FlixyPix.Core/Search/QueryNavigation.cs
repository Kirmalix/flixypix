namespace FlixyPix.Core;

public sealed partial class CatalogRepository
{
    public MediaItem? GetMediaByPath(string path) => FindPath(path);
    public (MediaPage Page, int Offset) QueryPlaylistPage(Playlist playlist, MediaQuery query, bool collectionOrder = true, Guid? selectedId = null) =>
        mediaQueries.QueryPlaylistPage(playlist, query, collectionOrder, selectedId);
    public int? GetPosition(MediaQuery query, Guid id) => query.GroupVideoStacks && HasVideoStacks() ? StackPosition(query,id,default) : mediaQueries.GetPosition(query, id);
    public (MediaPage Page, int Offset) QueryPlaylistPage(Playlist playlist, MediaQuery query, bool collectionOrder, Guid? selectedId, CancellationToken cancellationToken) =>
        mediaQueries.QueryPlaylistPage(playlist, query, collectionOrder, selectedId, cancellationToken);
    public int? GetPosition(MediaQuery query, Guid id, CancellationToken cancellationToken) => query.GroupVideoStacks && HasVideoStacks() ? StackPosition(query,id,cancellationToken) : mediaQueries.GetPosition(query, id, cancellationToken);
}
