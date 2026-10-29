using System.Numerics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

/// <summary>How close two media must look to count as duplicates: the average number of differing bits of their 64-bit
/// picture hashes.</summary>
public enum SimilarityLevel { Exact, Similar, Loose }

/// <summary>The picture hashes of one medium: one for an image, <see cref="PerceptualHash.VideoFrames"/> for a video. The
/// stamp (size and change time of the file) tells whether they still belong to the file.</summary>
public sealed record MediaFingerprint(Guid MediaId, string Stamp, IReadOnlyList<ulong> Hashes);

/// <summary>Media that look alike. <see cref="Distance"/> is the largest difference (average bits) between two of them that
/// joined the group; <see cref="Keep"/> is the version suggested for keeping.</summary>
public sealed record SimilarGroup(MediaKind Kind, IReadOnlyList<MediaItem> Items, double Distance, Guid Keep);

/// <summary>Two media the user marked as "kein Duplikat"; the smaller id comes first.</summary>
public sealed record DuplicatePair(Guid First, Guid Second)
{
    public static DuplicatePair Of(Guid a, Guid b) => a.CompareTo(b) <= 0 ? new(a, b) : new(b, a);
}

/// <summary>A perceptual hash (pHash) of a 32 × 32 greyscale picture: the 8 × 8 lowest frequencies of its DCT, each bit set
/// when the frequency lies above their median. Re-encoding, scaling and small colour changes change few bits.</summary>
public static class PerceptualHash
{
    public const int Size = 32;
    /// <summary>Stills per video, evenly between 5 % and 95 % of its duration.</summary>
    public const int VideoFrames = 16;
    private static readonly double[,] Cosines = BuildCosines();

    private static double[,] BuildCosines()
    {
        var table = new double[8, Size];
        for (var u = 0; u < 8; u++)
            for (var x = 0; x < Size; x++) table[u, x] = Math.Cos((2 * x + 1) * u * Math.PI / (2 * Size));
        return table;
    }

    /// <summary>The hash of 1,024 greyscale bytes, row by row.</summary>
    public static ulong FromGray(ReadOnlySpan<byte> pixels)
    {
        if (pixels.Length != Size * Size) throw new ArgumentException("A picture hash needs 32 × 32 greyscale pixels.", nameof(pixels));
        Span<double> rows = stackalloc double[Size * 8];
        for (var y = 0; y < Size; y++)
            for (var u = 0; u < 8; u++)
            {
                var sum = 0d;
                for (var x = 0; x < Size; x++) sum += pixels[y * Size + x] * Cosines[u, x];
                rows[y * 8 + u] = sum;
            }
        Span<double> frequencies = stackalloc double[64];
        for (var v = 0; v < 8; v++)
            for (var u = 0; u < 8; u++)
            {
                var sum = 0d;
                for (var y = 0; y < Size; y++) sum += rows[y * 8 + u] * Cosines[v, y];
                frequencies[v * 8 + u] = sum;
            }
        // The median leaves out the average brightness (DC), which says nothing about the picture.
        Span<double> sorted = stackalloc double[63];
        frequencies[1..].CopyTo(sorted); sorted.Sort();
        var median = (sorted[31] + sorted[32]) / 2;
        ulong hash = 0;
        for (var i = 0; i < 64; i++) if (frequencies[i] > median) hash |= 1UL << i;
        return hash;
    }

    public static int Distance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);

    /// <summary>The average distance of the stills at the same places; the two most different are left out, so an inserted
    /// logo or a changed intro does not decide. Null when one of them has too few stills.</summary>
    public static double? Distance(IReadOnlyList<ulong> a, IReadOnlyList<ulong> b)
    {
        if (a.Count == 1 && b.Count == 1) return Distance(a[0], b[0]);
        var count = Math.Min(a.Count, b.Count);
        if (count < VideoFrames / 2) return null;
        var distances = new int[count];
        for (var i = 0; i < count; i++) distances[i] = Distance(a[i], b[i]);
        Array.Sort(distances);
        var kept = distances.AsSpan(0, count - 2);
        var total = 0; foreach (var distance in kept) total += distance;
        return (double)total / kept.Length;
    }

    /// <summary>Largest average distance that still counts at the level.</summary>
    public static double Threshold(SimilarityLevel level) => level switch { SimilarityLevel.Exact => 3, SimilarityLevel.Similar => 7, _ => 11 };

    /// <summary>Size and last change of the file; a new stamp means the hashes have to be computed again.</summary>
    public static string Stamp(FileInfo file) => file.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" +
        file.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The places of the video stills in seconds.</summary>
    public static double[] VideoPositions(double durationSeconds) =>
        Enumerable.Range(0, VideoFrames).Select(i => durationSeconds * (0.05 + 0.9 * i / (VideoFrames - 1))).ToArray();
}

public sealed partial class CatalogRepository
{
    private void EnsureFingerprintTable(SqliteTransaction? transaction)
    {
        using var command = Command("""
            CREATE TABLE IF NOT EXISTS media_fingerprints(media_id TEXT PRIMARY KEY REFERENCES media(id) ON DELETE CASCADE, stamp TEXT NOT NULL, hashes BLOB NOT NULL)
            """, transaction);
        command.ExecuteNonQuery();
    }

    /// <summary>The stored stamps of all media that have a fingerprint.</summary>
    public IReadOnlyDictionary<Guid, string> GetFingerprintStamps()
    {
        lock (gate)
        {
            ThrowIfDisposed(); EnsureFingerprintTable(null);
            var result = new Dictionary<Guid, string>();
            using var command = Command("SELECT media_id, stamp FROM media_fingerprints", null);
            using var reader = command.ExecuteReader();
            while (reader.Read()) result[Guid.Parse(reader.GetString(0))] = reader.GetString(1);
            return result;
        }
    }

    public void SaveFingerprint(MediaFingerprint fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        if (fingerprint.Hashes.Count is 0 or > 64) throw new ArgumentException("A fingerprint has 1 to 64 hashes.", nameof(fingerprint));
        var bytes = new byte[fingerprint.Hashes.Count * 8];
        for (var i = 0; i < fingerprint.Hashes.Count; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 8), fingerprint.Hashes[i]);
        lock (gate)
        {
            ThrowIfDisposed(); EnsureFingerprintTable(null);
            // A medium removed meanwhile has no row to attach to; its fingerprint is not needed any more.
            using var command = Command("""
                INSERT INTO media_fingerprints(media_id,stamp,hashes) SELECT $id,$stamp,$hashes WHERE EXISTS(SELECT 1 FROM media WHERE id=$id)
                ON CONFLICT(media_id) DO UPDATE SET stamp=excluded.stamp, hashes=excluded.hashes
                """, null, ("$id", fingerprint.MediaId.ToString()), ("$stamp", fingerprint.Stamp), ("$hashes", bytes));
            command.ExecuteNonQuery();
        }
    }

    private List<(Guid Id, MediaKind Kind, double Duration, ulong[] Hashes)> ReadFingerprints(MediaKind? kind)
    {
        EnsureFingerprintTable(null);
        var result = new List<(Guid, MediaKind, double, ulong[])>();
        using var command = Command("SELECT f.media_id, q.kind, coalesce(q.duration,0), f.hashes FROM media_fingerprints f JOIN media_query q ON q.id=f.media_id"
            + (kind is null ? "" : " WHERE q.kind=$kind"), null, ("$kind", kind?.ToString() ?? ""));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!Enum.TryParse<MediaKind>(reader.GetString(1), out var mediaKind) || mediaKind == MediaKind.Audio) continue;
            var bytes = (byte[])reader.GetValue(3);
            var hashes = new ulong[bytes.Length / 8];
            for (var i = 0; i < hashes.Length; i++) hashes[i] = BitConverter.ToUInt64(bytes, i * 8);
            result.Add((Guid.Parse(reader.GetString(0)), mediaKind, reader.GetDouble(2), hashes));
        }
        return result;
    }

    /// <summary>Groups of media that look alike, from the stored fingerprints; pairs marked "kein Duplikat" are not joined.
    /// Videos are only compared with videos of nearly the same length (2 % or 5 s), images with images.</summary>
    public IReadOnlyList<SimilarGroup> FindSimilarMedia(SimilarityLevel level, MediaKind? kind = null, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(level) || kind == MediaKind.Audio) throw new ArgumentException("Unknown similarity level or media kind.");
        List<(Guid Id, MediaKind Kind, double Duration, ulong[] Hashes)> prints; HashSet<DuplicatePair> excluded;
        lock (gate) { ThrowIfDisposed(); prints = ReadFingerprints(kind); excluded = ReadPreferences().NotDuplicates.ToHashSet(); }
        var threshold = PerceptualHash.Threshold(level);
        var parent = Enumerable.Range(0, prints.Count).ToArray();
        var reach = new double[prints.Count];
        int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        void Join(int a, int b, double distance)
        {
            if (excluded.Contains(DuplicatePair.Of(prints[a].Id, prints[b].Id))) return;
            var (x, y) = (Find(a), Find(b));
            var widest = Math.Max(distance, Math.Max(reach[x], reach[y]));
            if (x != y) parent[y] = x;
            reach[x] = widest;
        }
        // Videos: sorted by length, each compared with those of nearly the same length.
        var videos = Enumerable.Range(0, prints.Count).Where(i => prints[i].Kind == MediaKind.Video && prints[i].Duration > 0).OrderBy(i => prints[i].Duration).ToArray();
        for (var a = 0; a < videos.Length; a++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var duration = prints[videos[a]].Duration; var limit = duration + Math.Max(5, duration * 0.02);
            for (var b = a + 1; b < videos.Length && prints[videos[b]].Duration <= limit; b++)
                if (PerceptualHash.Distance(prints[videos[a]].Hashes, prints[videos[b]].Hashes) is { } distance && distance <= threshold) Join(videos[a], videos[b], distance);
        }
        // Images: every pair, in parallel. Measured on 117,104 real image hashes this takes 0.4 s at every level, where a BK
        // tree took 2 s, 31 s and 75 s: with 64 bits and radii of 7 or 11 it has to visit most of its nodes anyway.
        var images = Enumerable.Range(0, prints.Count).Where(i => prints[i].Kind == MediaKind.Image && prints[i].Hashes.Length == 1).ToArray();
        var hashes = images.Select(i => prints[i].Hashes[0]).ToArray(); var radius = (int)threshold;
        var pairs = new List<(int A, int B, int Distance)>();
        Parallel.For(0, hashes.Length, new ParallelOptions { CancellationToken = cancellationToken }, () => new List<(int, int, int)>(), (a, _, found) =>
        {
            var hash = hashes[a];
            for (var b = a + 1; b < hashes.Length; b++) if (PerceptualHash.Distance(hash, hashes[b]) is var distance && distance <= radius) found.Add((images[a], images[b], distance));
            return found;
        }, found => { lock (pairs) pairs.AddRange(found); });
        foreach (var (a, b, distance) in pairs) Join(a, b, distance);
        var groups = Enumerable.Range(0, prints.Count).GroupBy(Find).Where(g => g.Count() > 1).ToList();
        IReadOnlyDictionary<Guid, MediaItem> media;
        lock (gate) { ThrowIfDisposed(); media = ReadMediaByIds(groups.SelectMany(g => g).Select(i => prints[i].Id)); }
        var result = new List<SimilarGroup>();
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var items = group.Select(i => media.GetValueOrDefault(prints[i].Id)).OfType<MediaItem>().ToList();
            if (items.Count < 2) continue;
            result.Add(new(items[0].Kind, items, reach[group.Key], SuggestKeep(items).Id));
        }
        return result.OrderBy(g => g.Distance).ThenByDescending(g => g.Items.Count).ToList();
    }

    /// <summary>The media with the given ids in one query instead of one query each.</summary>
    private Dictionary<Guid, MediaItem> ReadMediaByIds(IEnumerable<Guid> ids)
    {
        var list = JsonSerializer.Serialize(ids.Select(id => id.ToString()).ToArray());
        using var command = Command("SELECT document FROM media WHERE id IN (SELECT value FROM json_each($ids))", null, ("$ids", list));
        using var reader = command.ExecuteReader();
        var result = new Dictionary<Guid, MediaItem>();
        while (reader.Read()) if (JsonSerializer.Deserialize<MediaItem>(reader.GetString(0), Json) is { } item) result[item.Id] = item;
        return result;
    }

    /// <summary>The version to keep: most pixels, then highest bit rate, then the larger file, then the one imported first.</summary>
    public static MediaItem SuggestKeep(IReadOnlyList<MediaItem> items) => items
        .OrderByDescending(i => (long)(i.Width ?? 0) * (i.Height ?? 0))
        .ThenByDescending(i => i.DurationSeconds is > 0 && i.FileSize is { } size ? size / i.DurationSeconds.Value : 0)
        .ThenByDescending(i => i.FileSize ?? 0).ThenBy(i => i.AddedAt).First();

    /// <summary>Remembers that these media are no duplicates of each other; later searches keep them apart.</summary>
    public void MarkNotDuplicates(IReadOnlyCollection<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var list = ids.Distinct().ToList();
        lock (gate)
        {
            ThrowIfDisposed();
            var preferences = ReadPreferences();
            var pairs = preferences.NotDuplicates.ToHashSet();
            for (var a = 0; a < list.Count; a++) for (var b = a + 1; b < list.Count; b++) pairs.Add(DuplicatePair.Of(list[a], list[b]));
            SavePreferences(preferences with { NotDuplicates = pairs.OrderBy(p => p.First).ThenBy(p => p.Second).ToList() });
        }
    }

    /// <summary>Carries what the other versions know over to the kept one before they go: tags in all categories, the best
    /// rating, favourite and watch later, plays, views and O counter, the last playback and the playback history, and
    /// description and release date where the kept one has none. Returns the kept medium as stored.</summary>
    public MediaItem MergeDuplicateMetadata(Guid keep, IReadOnlyCollection<Guid> others)
    {
        ArgumentNullException.ThrowIfNull(others);
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            var kept = ReadMediaDocument(keep, transaction) ?? throw new ArgumentException(T("The item to keep no longer exists."));
            foreach (var id in others.Where(id => id != keep).Distinct())
            {
                if (ReadMediaDocument(id, transaction) is not { } other) continue;
                using (var history = Command("UPDATE play_history SET media_id=$keep WHERE media_id=$other", transaction, ("$keep", keep.ToString()), ("$other", id.ToString())))
                    history.ExecuteNonQuery();
                var categories = kept.TagCategories.ToDictionary(p => p.Key, p => p.Value.ToList(), StringComparer.OrdinalIgnoreCase);
                foreach (var (category, tags) in other.TagCategories)
                    categories[category] = (categories.GetValueOrDefault(category) ?? []).Concat(tags).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                kept = kept with
                {
                    Tags = kept.Tags.Concat(other.Tags).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), TagCategories = categories,
                    Rating = Math.Max(kept.Rating, other.Rating), IsFavorite = kept.IsFavorite || other.IsFavorite, WatchLater = kept.WatchLater || other.WatchLater,
                    PlayCount = kept.PlayCount is null && other.PlayCount is null ? null : (kept.PlayCount ?? 0) + (other.PlayCount ?? 0),
                    ViewCount = kept.ViewCount is null && other.ViewCount is null ? null : (kept.ViewCount ?? 0) + (other.ViewCount ?? 0),
                    OCount = kept.OCount is null && other.OCount is null ? null : (kept.OCount ?? 0) + (other.OCount ?? 0),
                    WatchCount = kept.WatchCount is null && other.WatchCount is null ? null : (kept.WatchCount ?? kept.PlayCount ?? 0) + (other.WatchCount ?? other.PlayCount ?? 0),
                    LastPlayedAt = new[] { kept.LastPlayedAt, other.LastPlayedAt }.Max(),
                    Description = string.IsNullOrWhiteSpace(kept.Description) ? other.Description : kept.Description,
                    ReleaseDate = kept.ReleaseDate ?? other.ReleaseDate, Category = kept.Category ?? other.Category,
                };
            }
            kept = UpsertCore(kept, transaction);
            transaction.Commit();
            cachedMedia = null;
            return kept;
        }
    }
}
