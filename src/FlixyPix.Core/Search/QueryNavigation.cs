namespace FlixyPix.Core;

public sealed partial class CatalogRepository
{
    public MediaItem? GetMediaByPath(string path) => FindPath(path);
    public (MediaPage Page,int Offset) QueryPlaylistPage(Playlist playlist,MediaQuery query,bool collectionOrder=true,Guid? selectedId=null) =>
        mediaQueries.QueryPlaylistPage(playlist,query,collectionOrder,selectedId);
    public int? GetPosition(MediaQuery query,Guid id) => mediaQueries.GetPosition(query,id);
}
