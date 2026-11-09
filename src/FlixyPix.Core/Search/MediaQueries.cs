using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace FlixyPix.Core;

/// <param name="CountSql">Counts the same media as <paramref name="Where"/> matches, faster; only valid in the transaction the
/// query was compiled in.</param>
internal sealed record CompiledMediaQuery(string Where, (string Name, object Value)[] Parameters, string? CountSql = null)
{
    public void Deconstruct(out string where, out (string Name, object Value)[] parameters) { where = Where; parameters = Parameters; }
    internal string Count => CountSql ?? "SELECT count(*) FROM media_query q " + Where;
}

/// <summary>Executes media projection queries; filter compilation remains shared with search and matching.</summary>
internal sealed class MediaQueries(CatalogDatabase database, Func<MediaQuery, SqliteTransaction?, CompiledMediaQuery> compile, Action<SqliteTransaction> commit)
{
    internal IReadOnlyList<MediaItem> ReadAll(MediaQuery query, CancellationToken token)
     => database.Run(token, () =>
       {
           database.ThrowIfDisposed(); return ReadItems(query, null, token);
       });
    internal MediaPage QueryPage(MediaQuery query, CancellationToken cancellationToken = default)
     => database.Run(cancellationToken, () =>
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Offset < 0 || query.Limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(query), "Offset must be nonnegative and page size 1–1000.");

        database.ThrowIfDisposed(); cancellationToken.ThrowIfCancellationRequested();
        using var transaction = database.Connection.BeginTransaction();
        var compiled = compile(query, transaction);
        var (where, parameters) = compiled;
        long count;
        using (var command = database.Command(compiled.Count, transaction, parameters))
            count = Convert.ToInt64(command.ExecuteScalar());
        var items = ReadItems(query, transaction, cancellationToken, where, parameters);
        commit(transaction);
        return new MediaPage(items, count);

    });

    /// <summary>A page without counting all hits: scrolling on through a result already knows how many there are.</summary>
    internal IReadOnlyList<MediaItem> QueryItems(MediaQuery query, CancellationToken cancellationToken = default)
     => database.Run(cancellationToken, () =>
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Offset < 0 || query.Limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(query), "Offset must be nonnegative and page size 1–1000.");
        database.ThrowIfDisposed(); cancellationToken.ThrowIfCancellationRequested();
        using var transaction = database.Connection.BeginTransaction();
        var (where, parameters) = compile(query, transaction);
        var items = ReadItems(query, transaction, cancellationToken, where, parameters);
        commit(transaction);
        return items;
    });

    /// <summary>Whether the medium stands at this place of the result; far cheaper than numbering the whole result.</summary>
    internal bool IsAt(MediaQuery query, int position, Guid id, CancellationToken cancellationToken = default)
     => database.Run(cancellationToken, () =>
    {
        database.ThrowIfDisposed();
        using var transaction = database.Connection.BeginTransaction();
        var (where, parameters) = compile(query, transaction);
        using var command = database.Command($"SELECT q.id FROM media_query q {where} ORDER BY {OrderSql(query)} LIMIT 1 OFFSET $offset", transaction, parameters);
        command.Parameters.AddWithValue("$offset", position);
        var found = command.ExecuteScalar() as string;
        commit(transaction);
        return found == id.ToString();
    });

    /// <summary>How many media match, without reading any of them; with a large catalog on a slow disk this is far cheaper than a page.</summary>
    internal long Count(MediaQuery query, CancellationToken cancellationToken = default)
     => database.Run(cancellationToken, () =>
    {
        ArgumentNullException.ThrowIfNull(query);
        database.ThrowIfDisposed(); cancellationToken.ThrowIfCancellationRequested();
        using var transaction = database.Connection.BeginTransaction();
        var compiled = compile(query, transaction);
        using var command = database.Command(compiled.Count, transaction, compiled.Parameters);
        var count = Convert.ToInt64(command.ExecuteScalar());
        commit(transaction);
        return count;
    });

    internal IReadOnlyList<MediaItem> ReadItems(MediaQuery query, SqliteTransaction? transaction, CancellationToken cancellationToken,
        string? where = null, (string Name, object Value)[]? parameters = null)
    {
        if (where is null) (where, parameters) = compile(query, transaction);
        var order = OrderSql(query);
        // Apply OFFSET/LIMIT to the compact projection before fetching large JSON documents.
        // A deep page must not pull 100000 complete records through the media table.
        var sql = transaction is null
            ? $"SELECT m.document FROM media_query q JOIN media m ON m.id=q.id {where} ORDER BY {order}"
            : $"SELECT m.document FROM (SELECT {OrderColumns(query)} FROM media_query q {(query.WatchLater == true && query.KeepIds.Count == 0 ? "INDEXED BY media_query_later " : "")}{where} ORDER BY {order} LIMIT $limit OFFSET $offset) q JOIN media m ON m.id=q.id ORDER BY {order}";
        using var command = database.Command(sql, transaction, parameters!);
        if (transaction is not null) { command.Parameters.AddWithValue("$limit", query.Limit); command.Parameters.AddWithValue("$offset", query.Offset); }

        using var reader = command.ExecuteReader();
        var items = new List<MediaItem>();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(JsonSerializer.Deserialize<MediaItem>(reader.GetString(0), CatalogRepository.Json) ?? throw new InvalidDataException("Invalid media record."));
        }
        return items;
    }

    internal (MediaPage Page, int Offset) QueryPlaylistPage(Playlist playlist, MediaQuery query, bool collectionOrder = true, Guid? selectedId = null, CancellationToken cancellationToken = default)
     => database.Run<(MediaPage Page, int Offset)>(cancellationToken, () =>
    {
        if (query.Offset < 0 || query.Limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(query));

        database.ThrowIfDisposed(); using var transaction = database.Connection.BeginTransaction();
        var (where, parameters) = compile(query, transaction);
        var sort = collectionOrder ? "p.key" : OrderSql(query) + ",p.key";
        var source = $" FROM json_each($playlist) p JOIN media_query q ON q.id=p.value {where}";
        var values = parameters.Concat(new[] { ("$playlist", (object)System.Text.Json.JsonSerializer.Serialize(playlist.MediaIds)) }).ToArray();
        long count;
        using (var command = database.Command("SELECT count(*)" + source, transaction, values)) count = Convert.ToInt64(command.ExecuteScalar());
        var offset = Math.Min(query.Offset, (int)Math.Max(0, (count - 1) / query.Limit) * query.Limit);
        if (selectedId is { } selected)
        {
            using var command = database.Command($"SELECT position FROM (SELECT q.id,row_number() OVER (ORDER BY {sort})-1 position {source}) WHERE id=$selected ORDER BY position LIMIT 1", transaction, values);
            command.Parameters.AddWithValue("$selected", selected.ToString());
            if (command.ExecuteScalar() is { } position) offset = Convert.ToInt32(position) / query.Limit * query.Limit;
        }
        using var page = database.Command($"SELECT m.document FROM (SELECT q.id,row_number() OVER (ORDER BY {sort}) position {source} ORDER BY {sort} LIMIT $limit OFFSET $offset) selected JOIN media m ON m.id=selected.id ORDER BY selected.position", transaction, values);
        page.Parameters.AddWithValue("$limit", query.Limit); page.Parameters.AddWithValue("$offset", offset);
        var items = new List<MediaItem>();
        using (var reader = page.ExecuteReader()) while (reader.Read()) items.Add(System.Text.Json.JsonSerializer.Deserialize<MediaItem>(reader.GetString(0), CatalogRepository.Json)!);
        commit(transaction); return (new(items, count), offset);

    });

    /// <summary>What the order needs, nothing more: an index holding it answers without touching the table.</summary>
    private static string OrderColumns(MediaQuery query) => "q.id,q.title,q.path" + query.Sort switch
    {
        MediaSort.AddedAt => ",q.imported", MediaSort.Rating => ",q.rating", MediaSort.Duration => ",q.duration", MediaSort.FileSize => ",q.file_size",
        MediaSort.FileDate => ",q.file_date", MediaSort.OCount => ",q.o_count", MediaSort.Resolution => ",q.lines", MediaSort.Bitrate => ",q.bit_rate",
        _ => "",
    };

    internal static string OrderSql(MediaQuery query)
    {
        // "Added" sorts by the import date, which for migrated media is the original's addedDate.
        var sort = query.Sort switch
        {
            MediaSort.Title => "q.title COLLATE EFP_ORDINAL",
            MediaSort.Path =>
         "q.path COLLATE EFP_ORDINAL",
            MediaSort.AddedAt => "q.imported",
            MediaSort.Rating => "q.rating",
            MediaSort.Duration =>
         "q.duration",
            MediaSort.FileSize => "q.file_size",
            MediaSort.FileDate => "q.file_date",
            MediaSort.OCount => "q.o_count",
            MediaSort.Resolution => "q.lines",
            MediaSort.Bitrate => "q.bit_rate",
            _ =>
         throw new ArgumentException("Invalid media sort.", nameof(query))
        };
        var direction = query.Descending ? "DESC" : "ASC";
        var secondary = query.Sort == MediaSort.Title ? "q.path COLLATE EFP_ORDINAL" : query.Sort == MediaSort.Path ? "q.title COLLATE EFP_ORDINAL" : "q.title COLLATE EFP_ORDINAL " + direction + ",q.path COLLATE EFP_ORDINAL";
        return $"{sort} {direction},{secondary} {direction},q.id {direction}";
    }
    /// <summary>Zero-based position in the same ordering as QueryPage, without loading its media documents.</summary>
    internal int? GetPosition(MediaQuery query, Guid id, CancellationToken cancellationToken = default)
     => database.Run<int?>(cancellationToken, () =>
    {

        database.ThrowIfDisposed();
        var (where, parameters) = compile(query, null);
        using var command = database.Command($"SELECT position FROM (SELECT q.id,row_number() OVER (ORDER BY {OrderSql(query)})-1 position FROM media_query q {where}) WHERE id=$selected", null, parameters);
        command.Parameters.AddWithValue("$selected", id.ToString());
        var result = command.ExecuteScalar();
        return result is null or DBNull ? null : checked((int)Convert.ToInt64(result));

    });
}
