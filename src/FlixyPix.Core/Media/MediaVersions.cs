using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

/// <summary>Short names for technical data, as badges, filters and statistics show them.</summary>
public static class TechnicalLabels
{
    /// <summary>A readable codec name: "h264" becomes "H.264", "hevc" "HEVC"; unknown names are shown in capitals.</summary>
    public static string Codec(string? codec) => codec?.ToLowerInvariant() switch
    {
        null or "" => "",
        "h264" => "H.264", "hevc" or "h265" => "HEVC", "av1" => "AV1", "vp9" => "VP9", "vp8" => "VP8",
        "mpeg4" or "msmpeg4v3" or "msmpeg4v2" => "MPEG-4", "mpeg2video" => "MPEG-2", "mpeg1video" => "MPEG-1",
        "wmv1" or "wmv2" or "wmv3" => "WMV", "vc1" => "VC-1", "prores" => "ProRes", "mjpeg" => "MJPEG", "theora" => "Theora",
        "aac" => "AAC", "mp3" => "MP3", "opus" => "Opus", "vorbis" => "Vorbis", "flac" => "FLAC", "ac3" => "AC-3", "eac3" => "E-AC-3", "dts" => "DTS",
        var other => other.ToUpperInvariant(),
    };

    /// <summary>Resolution by the shorter side of the picture: 8K, 4K, 1440p, 1080p, 720p, SD; empty when unknown.</summary>
    public static string Resolution(int? width, int? height)
    {
        if (width is not > 0 || height is not > 0) return "";
        var lines = Math.Min(width.Value, height.Value);
        return lines >= 4320 ? "8K" : lines >= 2160 ? "4K" : lines >= 1440 ? "1440p" : lines >= 1080 ? "1080p" : lines >= 720 ? "720p" : "SD";
    }

    /// <summary>"60 fps" for 59.94 and 60; empty when unknown.</summary>
    public static string FrameRate(double? rate) => rate is > 0 and var value ? Math.Round(value).ToString(CultureInfo.CurrentCulture) + " fps" : "";

    /// <summary>A frame rate of 50 and more counts as high (badge "60 fps").</summary>
    public static bool HighFrameRate(double? rate) => rate is >= 49.5;

    /// <summary>"8.2 Mbit/s" or "640 kbit/s" from bit/s.</summary>
    public static string BitRate(long? bitsPerSecond) => bitsPerSecond switch
    {
        null or <= 0 => "",
        >= 1_000_000 => (bitsPerSecond.Value / 1_000_000d).ToString("0.#", CultureInfo.CurrentCulture) + " Mbit/s",
        _ => (bitsPerSecond.Value / 1000d).ToString("0", CultureInfo.CurrentCulture) + " kbit/s",
    };
}

/// <summary>The files of a medium: the main file and its further versions (package P6).</summary>
public static class MediaVersions
{
    /// <summary>The main file with what is stored for it.</summary>
    public static MediaFile Main(MediaItem item) => new()
    {
        Path = item.Path, FileSize = item.FileSize, FileModifiedAtUtc = item.FileModifiedAtUtc, Width = item.Width, Height = item.Height, DurationSeconds = item.DurationSeconds, Technical = item.Technical,
    };

    /// <summary>Main file first, then the further versions in their order.</summary>
    public static IReadOnlyList<MediaFile> All(MediaItem item) => [Main(item), .. item.Files];

    /// <summary>The best of the files that <paramref name="exists"/>: most lines, then highest bit rate, then largest file;
    /// the main file wins a tie. Null when none exists.</summary>
    public static MediaFile? Best(MediaItem item, Func<string, bool> exists) => All(item).Where(file => exists(file.Path))
        .Select((file, index) => (file, index))
        .OrderByDescending(entry => Lines(entry.file)).ThenByDescending(entry => entry.file.Technical?.BitRate ?? 0).ThenByDescending(entry => entry.file.FileSize ?? 0)
        .ThenBy(entry => entry.index).Select(entry => entry.file).FirstOrDefault();

    /// <summary>The file to play: the main file, or with <paramref name="preferBest"/> the best version; a missing main file
    /// gives way to a version that exists. Null when no file exists.</summary>
    public static string? PlaybackPath(MediaItem item, bool preferBest, Func<string, bool> exists)
    {
        if (!preferBest && exists(item.Path)) return item.Path;
        return Best(item, exists)?.Path;
    }

    internal static int Lines(MediaFile file) => file.Width is > 0 && file.Height is > 0 ? Math.Min(file.Width.Value, file.Height.Value) : 0;
}

public sealed partial class CatalogRepository
{
    /// <summary>Keeps media_files in line with the versions of a saved medium. A version may be neither the main file of
    /// another medium nor a version of one, and appears only once.</summary>
    private void SyncVersionPaths(MediaItem saved, string mainKey, SqliteTransaction transaction)
    {
        var id = saved.Id.ToString();
        using (var taken = Command("SELECT 1 FROM media_files WHERE path_key=$path AND media_id<>$id", transaction, ("$path", mainKey), ("$id", id)))
            if (taken.ExecuteScalar() is not null) throw new ArgumentException(T("The file is already a version of another item."));
        var keys = new HashSet<string>(StringComparer.Ordinal) { mainKey };
        foreach (var file in saved.Files)
        {
            if (file is null || string.IsNullOrWhiteSpace(file.Path) || file.FileSize is < 0 || file.Width is <= 0 || file.Height is <= 0 ||
                file.DurationSeconds is { } duration && (!double.IsFinite(duration) || duration < 0))
                throw new ArgumentException("Invalid version file.");
            var key = PathKey(file.Path);
            if (!keys.Add(key)) throw new ArgumentException(T("A file appears twice among the versions: {0}", file.Path));
            using var main = Command("SELECT 1 FROM media WHERE path_key=$path AND id<>$id", transaction, ("$path", key), ("$id", id));
            using var version = Command("SELECT 1 FROM media_files WHERE path_key=$path AND media_id<>$id", transaction, ("$path", key), ("$id", id));
            if (main.ExecuteScalar() is not null || version.ExecuteScalar() is not null)
                throw new ArgumentException(T("The file belongs to another item: {0}", file.Path));
        }
        using (var clear = Command("DELETE FROM media_files WHERE media_id=$id", transaction, ("$id", id))) clear.ExecuteNonQuery();
        foreach (var key in keys.Skip(1))
        {
            using var insert = Command("INSERT INTO media_files(path_key,media_id) VALUES($path,$id)", transaction, ("$path", key), ("$id", id));
            insert.ExecuteNonQuery();
        }
    }

    /// <summary>The medium a further version belongs to; null when the file is none.</summary>
    private MediaItem? FindVersionOwner(string pathKey)
    {
        using var command = Command("SELECT m.document FROM media_files f JOIN media m ON m.id=f.media_id WHERE f.path_key=$path", null, ("$path", pathKey));
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<MediaItem>(json, Json) : null;
    }

    /// <summary>Video codecs in the library with the number of videos, most frequent first (filter bar).</summary>
    public IReadOnlyList<(string Codec, long Count)> GetVideoCodecs()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var command = Command("SELECT video_codec, count(*) FROM media_query WHERE video_codec IS NOT NULL GROUP BY video_codec ORDER BY count(*) DESC, video_codec", null);
            var codecs = new List<(string, long)>();
            using var reader = command.ExecuteReader();
            while (reader.Read()) codecs.Add((reader.GetString(0), reader.GetInt64(1)));
            return codecs;
        }
    }

    /// <summary>Path keys of further versions under a folder (folder sync counts them as known).</summary>
    internal IReadOnlyList<string> VersionKeysUnder(string folder)
    {
        lock(gate)
        {
        ThrowIfDisposed();
        var prefix = PathKey(folder) + System.IO.Path.DirectorySeparatorChar;
        using var command = Command("SELECT path_key FROM media_files WHERE substr(path_key,1,length($prefix))=$prefix", null, ("$prefix", prefix));
        var keys = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) keys.Add(reader.GetString(0));
        return keys;
        }
    }

    /// <summary>Makes the other media versions of <paramref name="mainId"/>: their files join its versions, tags and categories
    /// are combined, the best rating and the counters add up; bookmarks, scenes, artwork, history and collection places move
    /// to the main medium. The other media are then removed from the catalog; no file is touched.</summary>
    public MediaItem MergeVersions(Guid mainId, IReadOnlyCollection<Guid> otherIds)
    {
        ArgumentNullException.ThrowIfNull(otherIds);
        var others = otherIds.Where(id => id != mainId).Distinct().ToList();
        if (others.Count == 0) throw new ArgumentException(T("Choose at least two items to combine."));
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            var main = ReadMediaDocument(mainId, transaction) ?? throw new ArgumentException(T("The item no longer exists."));
            var merged = main;
            static long? Sum(long? a, long? b) => a is null && b is null ? null : (a ?? 0) + (b ?? 0);
            foreach (var otherId in others)
            {
                var other = ReadMediaDocument(otherId, transaction) ?? throw new ArgumentException(T("The item no longer exists."));
                if (other.Kind != main.Kind || main.Kind == MediaKind.Image) throw new ArgumentException(T("Only videos with videos or audio files with audio files can become versions of one item."));
                var categories = merged.TagCategories.ToDictionary(pair => pair.Key, pair => pair.Value.ToList(), StringComparer.OrdinalIgnoreCase);
                foreach (var (name, tags) in other.TagCategories) categories[name] = [.. (categories.GetValueOrDefault(name) ?? []).Concat(tags).Distinct(StringComparer.OrdinalIgnoreCase)];
                merged = merged with
                {
                    Files = [.. merged.Files, .. MediaVersions.All(other)],
                    Tags = [.. merged.Tags.Concat(other.Tags).Distinct(StringComparer.OrdinalIgnoreCase)],
                    TagCategories = categories,
                    Rating = Math.Max(merged.Rating, other.Rating), IsFavorite = merged.IsFavorite || other.IsFavorite, WatchLater = merged.WatchLater || other.WatchLater,
                    PlayCount = Sum(merged.PlayCount, other.PlayCount), ViewCount = Sum(merged.ViewCount, other.ViewCount), OCount = Sum(merged.OCount, other.OCount),
                    WatchCount = Sum(merged.WatchCount, other.WatchCount),
                    Category = merged.Category ?? other.Category, Description = string.IsNullOrWhiteSpace(merged.Description) ? other.Description : merged.Description,
                    ReleaseDate = merged.ReleaseDate ?? other.ReleaseDate,
                    LastPlayedAt = merged.LastPlayedAt is { } a && other.LastPlayedAt is { } b ? (a > b ? a : b) : merged.LastPlayedAt ?? other.LastPlayedAt,
                };
                var parameters = new[] { ("$main", (object)mainId.ToString()), ("$other", otherId.ToString()) };
                using (var move = Command("""
                    UPDATE bookmarks SET media_id=$main, document=json_set(document,'$.mediaId',$main) WHERE media_id=$other;
                    UPDATE scenes SET media_id=$main, document=json_set(document,'$.mediaId',$main) WHERE media_id=$other;
                    UPDATE play_history SET media_id=$main WHERE media_id=$other;
                    UPDATE media_assets SET media_id=$main, position=position+1000000, document=json_set(document,'$.mediaId',$main) WHERE media_id=$other;
                    """, transaction, parameters)) move.ExecuteNonQuery();
                foreach (var playlist in ReadDocuments<Playlist>("playlists", transaction).Where(p => p.MediaIds.Contains(otherId) || p.Entries.Any(e => e.MediaId == otherId)))
                    SavePlaylist(playlist with
                    {
                        MediaIds = playlist.MediaIds.Select(id => id == otherId ? mainId : id).Distinct().ToList(),
                        Entries = playlist.Entries.Select(e => e.MediaId == otherId ? e with { MediaId = mainId } : e).Distinct().ToList(),
                    }, transaction);
                using var delete = Command("DELETE FROM media WHERE id=$other", transaction, ("$other", otherId.ToString()));
                delete.ExecuteNonQuery();
            }
            RenumberAssets(mainId, transaction);
            var saved = UpsertCore(merged, transaction);
            transaction.Commit();
            cachedMedia = null;
            return saved;
        }
    }

    /// <summary>Artwork moved over from other media follows the main medium's own, in order.</summary>
    private void RenumberAssets(Guid mediaId, SqliteTransaction transaction)
    {
        var ids = new List<string>();
        using (var read = Command("SELECT id FROM media_assets WHERE media_id=$id ORDER BY position", transaction, ("$id", mediaId.ToString())))
        using (var reader = read.ExecuteReader()) while (reader.Read()) ids.Add(reader.GetString(0));
        for (var position = 0; position < ids.Count; position++)
        {
            using var update = Command("UPDATE media_assets SET position=$position, document=json_set(document,'$.position',$position) WHERE id=$id", transaction,
                ("$position", position), ("$id", ids[position]));
            update.ExecuteNonQuery();
        }
    }

    /// <summary>Makes a further version a medium of its own again. It takes the tags, categories and rating of the medium;
    /// counters, bookmarks and scenes stay where they are. Returns the new medium.</summary>
    public MediaItem SplitVersion(Guid mediaId, string path)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            var item = ReadMediaDocument(mediaId, transaction) ?? throw new ArgumentException(T("The item no longer exists."));
            var key = PathKey(path);
            var file = item.Files.FirstOrDefault(f => PathKey(f.Path) == key) ?? throw new ArgumentException(T("The file is no further version of this item."));
            UpsertCore(item with { Files = item.Files.Where(f => f != file).ToList() }, transaction);
            var created = UpsertCore(new MediaItem
            {
                Path = file.Path, Title = System.IO.Path.GetFileNameWithoutExtension(file.Path), Kind = item.Kind, FileSize = file.FileSize,
                Width = file.Width, Height = file.Height, DurationSeconds = file.DurationSeconds, Technical = file.Technical,
                Tags = [.. item.Tags], TagCategories = item.TagCategories.ToDictionary(pair => pair.Key, pair => pair.Value.ToList()),
                Category = item.Category, Rating = item.Rating, ReleaseDate = item.ReleaseDate,
                FileCreatedAt = File.Exists(file.Path) ? MediaScanner.FileDate(new FileInfo(file.Path)) : null,
            }, transaction);
            transaction.Commit();
            cachedMedia = null;
            return created;
        }
    }

    /// <summary>Makes a further version the main file; the former main file becomes a version.</summary>
    public MediaItem SetMainVersion(Guid mediaId, string path)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            var item = ReadMediaDocument(mediaId, transaction) ?? throw new ArgumentException(T("The item no longer exists."));
            var key = PathKey(path);
            var file = item.Files.FirstOrDefault(f => PathKey(f.Path) == key) ?? throw new ArgumentException(T("The file is no further version of this item."));
            var saved = UpsertCore(item with
            {
                Path = file.Path, FileSize = file.FileSize, FileModifiedAtUtc = file.FileModifiedAtUtc, Width = file.Width, Height = file.Height, DurationSeconds = file.DurationSeconds, Technical = file.Technical,
                Files = [MediaVersions.Main(item), .. item.Files.Where(f => f != file)],
            }, transaction);
            transaction.Commit();
            return saved;
        }
    }
}
