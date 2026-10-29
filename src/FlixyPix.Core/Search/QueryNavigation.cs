namespace FlixyPix.Core;

public sealed partial class CatalogRepository
{
    public MediaItem? GetMediaByPath(string path) => FindPath(path);

    public (MediaPage Page,int Offset) QueryPlaylistPage(Playlist playlist,MediaQuery query,bool collectionOrder = true,Guid? selectedId = null)
    {
        if (query.Offset < 0 || query.Limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(query));
        lock (gate)
        {
            ThrowIfDisposed(); using var transaction = connection.BeginTransaction();
            var (where, parameters) = BuildQuery(query,transaction);
            var sort = collectionOrder ? "p.key" : FullOrderSql(query) + ",p.key";
            var source = $" FROM json_each($playlist) p JOIN media_query q ON q.id=p.value {where}";
            var values = parameters.Concat(new[] { ("$playlist",(object)System.Text.Json.JsonSerializer.Serialize(playlist.MediaIds)) }).ToArray();
            long count;
            using(var command = Command("SELECT count(*)" + source,transaction,values)) count = Convert.ToInt64(command.ExecuteScalar());
            var offset = Math.Min(query.Offset,(int)Math.Max(0,(count-1)/query.Limit)*query.Limit);
            if(selectedId is { } selected)
            {
                using var command = Command($"SELECT position FROM (SELECT q.id,row_number() OVER (ORDER BY {sort})-1 position {source}) WHERE id=$selected ORDER BY position LIMIT 1",transaction,values);
                command.Parameters.AddWithValue("$selected",selected.ToString());
                if(command.ExecuteScalar() is { } position) offset = Convert.ToInt32(position)/query.Limit*query.Limit;
            }
            using var page = Command($"SELECT m.document FROM (SELECT q.id,row_number() OVER (ORDER BY {sort}) position {source} ORDER BY {sort} LIMIT $limit OFFSET $offset) selected JOIN media m ON m.id=selected.id ORDER BY selected.position",transaction,values);
            page.Parameters.AddWithValue("$limit",query.Limit); page.Parameters.AddWithValue("$offset",offset);
            var items = new List<MediaItem>();
            using(var reader=page.ExecuteReader()) while(reader.Read()) items.Add(System.Text.Json.JsonSerializer.Deserialize<MediaItem>(reader.GetString(0),Json)!);
            CommitAfterIndexing(transaction); return (new(items,count),offset);
        }
    }

    private static string FullOrderSql(MediaQuery query)
    {
        // "Added" sorts by the import date, which for migrated media is the original's addedDate.
        var sort = query.Sort switch { MediaSort.Title => "q.title COLLATE EFP_ORDINAL",MediaSort.Path =>
         "q.path COLLATE EFP_ORDINAL",MediaSort.AddedAt => "q.imported",MediaSort.Rating => "q.rating",MediaSort.Duration =>
         "q.duration",MediaSort.FileSize => "q.file_size",MediaSort.FileDate => "q.file_date",MediaSort.OCount => "q.o_count",MediaSort.Resolution => "q.lines",MediaSort.Bitrate => "q.bit_rate",_ =>
         throw new ArgumentException("Invalid media sort.",nameof(query)) };
        var direction=query.Descending?"DESC":"ASC";
        var secondary=query.Sort==MediaSort.Title?"q.path COLLATE EFP_ORDINAL":query.Sort==MediaSort.Path?"q.title COLLATE EFP_ORDINAL":"q.title COLLATE EFP_ORDINAL "+direction+",q.path COLLATE EFP_ORDINAL";
        return $"{sort} {direction},{secondary} {direction},q.id {direction}";
    }
    /// <summary>Zero-based position in the same ordering as QueryPage, without loading its media documents.</summary>
    public int? GetPosition(MediaQuery query, Guid id)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            var (where, parameters) = BuildQuery(query,null);
            using var command = Command($"SELECT position FROM (SELECT q.id,row_number() OVER (ORDER BY {FullOrderSql(query)})-1 position FROM media_query q {where}) WHERE id=$selected",null,parameters);
            command.Parameters.AddWithValue("$selected",id.ToString());
            var result=command.ExecuteScalar();
            return result is null or DBNull ? null : checked((int)Convert.ToInt64(result));
        }
    }
}
