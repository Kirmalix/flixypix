using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace FlixyPix.Core;

internal sealed record CompiledMediaQuery(string Where,(string Name,object Value)[] Parameters);

/// <summary>Executes media projection queries; filter compilation remains shared with search and matching.</summary>
internal sealed class MediaQueries(CatalogDatabase database,Func<MediaQuery,SqliteTransaction?,CompiledMediaQuery> compile,Action<SqliteTransaction> commit)
{
 internal IReadOnlyList<MediaItem> ReadAll(MediaQuery query,CancellationToken token)
 {
  lock(database.Gate) {database.ThrowIfDisposed();return ReadItems(query,null,token);}
 }
    internal MediaPage QueryPage(MediaQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Offset < 0 || query.Limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(query), "Offset must be nonnegative and page size 1–1000.");
        lock (database.Gate)
        {
            database.ThrowIfDisposed(); cancellationToken.ThrowIfCancellationRequested();
            using var transaction = database.Connection.BeginTransaction();
            var (where, parameters) = compile(query, transaction);
            long count;
            using (var command = database.Command("SELECT count(*) FROM media_query q " + where, transaction, parameters))
            {
                using var cancel = cancellationToken.Register(command.Cancel);
                count = Convert.ToInt64(command.ExecuteScalar());
            }
            var items = ReadItems(query, transaction, cancellationToken, where, parameters);
            commit(transaction);
            return new MediaPage(items, count);
        }
    }

    internal IReadOnlyList<MediaItem> ReadItems(MediaQuery query, SqliteTransaction? transaction, CancellationToken cancellationToken,
        string? where = null, (string Name, object Value)[]? parameters = null)
    {
        if (where is null) (where, parameters) = compile(query, transaction);
        var order = OrderSql(query);
        // Apply OFFSET/LIMIT to the compact projection before fetching large JSON documents.
        // A deep page must not pull 100000 complete records through the media table.
        var sql = transaction is null
            ? $"SELECT m.document FROM media_query q JOIN media m ON m.id=q.id {where} ORDER BY {order}"
            : $"SELECT m.document FROM (SELECT q.* FROM media_query q {where} ORDER BY {order} LIMIT $limit OFFSET $offset) q JOIN media m ON m.id=q.id ORDER BY {order}";
        using var command = database.Command(sql, transaction, parameters!);
        if (transaction is not null) { command.Parameters.AddWithValue("$limit", query.Limit); command.Parameters.AddWithValue("$offset", query.Offset); }
        using var cancel = cancellationToken.Register(command.Cancel);
        using var reader = command.ExecuteReader();
        var items = new List<MediaItem>();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(JsonSerializer.Deserialize<MediaItem>(reader.GetString(0), CatalogRepository.Json) ?? throw new InvalidDataException("Invalid media record."));
        }
        return items;
    }

    internal (MediaPage Page,int Offset) QueryPlaylistPage(Playlist playlist,MediaQuery query,bool collectionOrder = true,Guid? selectedId = null)
    {
        if (query.Offset < 0 || query.Limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(query));
        lock (database.Gate)
        {
            database.ThrowIfDisposed(); using var transaction = database.Connection.BeginTransaction();
            var (where, parameters) = compile(query,transaction);
            var sort = collectionOrder ? "p.key" : OrderSql(query) + ",p.key";
            var source = $" FROM json_each($playlist) p JOIN media_query q ON q.id=p.value {where}";
            var values = parameters.Concat(new[] { ("$playlist",(object)System.Text.Json.JsonSerializer.Serialize(playlist.MediaIds)) }).ToArray();
            long count;
            using(var command = database.Command("SELECT count(*)" + source,transaction,values)) count = Convert.ToInt64(command.ExecuteScalar());
            var offset = Math.Min(query.Offset,(int)Math.Max(0,(count-1)/query.Limit)*query.Limit);
            if(selectedId is { } selected)
            {
                using var command = database.Command($"SELECT position FROM (SELECT q.id,row_number() OVER (ORDER BY {sort})-1 position {source}) WHERE id=$selected ORDER BY position LIMIT 1",transaction,values);
                command.Parameters.AddWithValue("$selected",selected.ToString());
                if(command.ExecuteScalar() is { } position) offset = Convert.ToInt32(position)/query.Limit*query.Limit;
            }
            using var page = database.Command($"SELECT m.document FROM (SELECT q.id,row_number() OVER (ORDER BY {sort}) position {source} ORDER BY {sort} LIMIT $limit OFFSET $offset) selected JOIN media m ON m.id=selected.id ORDER BY selected.position",transaction,values);
            page.Parameters.AddWithValue("$limit",query.Limit); page.Parameters.AddWithValue("$offset",offset);
            var items = new List<MediaItem>();
            using(var reader=page.ExecuteReader()) while(reader.Read()) items.Add(System.Text.Json.JsonSerializer.Deserialize<MediaItem>(reader.GetString(0),CatalogRepository.Json)!);
            commit(transaction); return (new(items,count),offset);
        }
    }

    internal static string OrderSql(MediaQuery query)
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
    internal int? GetPosition(MediaQuery query, Guid id)
    {
        lock (database.Gate)
        {
            database.ThrowIfDisposed();
            var (where, parameters) = compile(query,null);
            using var command = database.Command($"SELECT position FROM (SELECT q.id,row_number() OVER (ORDER BY {OrderSql(query)})-1 position FROM media_query q {where}) WHERE id=$selected",null,parameters);
            command.Parameters.AddWithValue("$selected",id.ToString());
            var result=command.ExecuteScalar();
            return result is null or DBNull ? null : checked((int)Convert.ToInt64(result));
        }
    }
}
