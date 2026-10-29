using System.Globalization;

namespace FlixyPix.Core;

/// <summary>One bar of the statistics: a label, its count and, where it makes sense, hours; <see cref="Expression"/> or
/// <see cref="MediaId"/> say where a click leads.</summary>
public sealed record StatisticsBar(string Label, long Count, double Hours = 0, SearchExpression? Expression = null, Guid? MediaId = null, string? View = null);

public sealed record LibraryStatistics
{
    public long Media { get; init; }
    public long Bytes { get; init; }
    /// <summary>Length of all videos and audio files.</summary>
    public double Hours { get; init; }
    /// <summary>Time watched according to the playback history.</summary>
    public double WatchedHours { get; init; }
    public long Plays { get; init; }
    public long OTotal { get; init; }
    /// <summary>Videos and audio files never watched (watch count, else play count 0).</summary>
    public long NeverWatched { get; init; }
    public IReadOnlyList<StatisticsBar> Kinds { get; init; } = [];
    /// <summary>Videos by the shorter side of the picture: 8K, 4K, 1440p, 1080p, 720p, SD, unknown.</summary>
    public IReadOnlyList<StatisticsBar> Resolutions { get; init; } = [];
    /// <summary>File types by extension.</summary>
    public IReadOnlyList<StatisticsBar> FileTypes { get; init; } = [];
    /// <summary>Videos by video codec (technical data of package P6), the ten most frequent; videos not yet read count as unknown.</summary>
    public IReadOnlyList<StatisticsBar> VideoCodecs { get; init; } = [];
    /// <summary>Imports per month over the last 24 months, oldest first, months without imports included.</summary>
    public IReadOnlyList<StatisticsBar> ImportsPerMonth { get; init; } = [];
    public IReadOnlyList<StatisticsBar> Performers { get; init; } = [];
    public IReadOnlyList<StatisticsBar> Studios { get; init; } = [];
    public IReadOnlyList<StatisticsBar> Tags { get; init; } = [];
    public IReadOnlyList<StatisticsBar> MostWatched { get; init; } = [];
    /// <summary>Playbacks per week of the history over the last 26 weeks, oldest first, weeks without playbacks included.</summary>
    public IReadOnlyList<StatisticsBar> PlaysPerWeek { get; init; } = [];
    /// <summary>Milliseconds each part took (performance checks).</summary>
    public IReadOnlyDictionary<string, double> Timings { get; init; } = new Dictionary<string, double>();
}

/// <summary>Library statistics computed in SQL from the query table, the tag table and the history. No medium is loaded;
/// one pass reads picture size and watch count of videos and audio straight from the stored documents.</summary>
public sealed partial class CatalogRepository
{
    public const int StatisticsTop = 10;
    private static readonly (string Label, int Lines)[] ResolutionSteps = [("8K", 4320), ("4K", 2160), ("1440p", 1440), ("1080p", 1080), ("720p", 720), ("SD", 1)];

    public LibraryStatistics BuildStatistics(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var timings = new Dictionary<string, double>(); var clock = System.Diagnostics.Stopwatch.StartNew();
        void Lap(string part) { timings[part] = clock.Elapsed.TotalMilliseconds; clock.Restart(); cancellationToken.ThrowIfCancellationRequested(); }
        lock (gate)
        {
            ThrowIfDisposed();
            var result = new LibraryStatistics();
            var kinds = new List<StatisticsBar>(); long media = 0, bytes = 0; double hours = 0;
            foreach (var row in Rows("SELECT kind, count(*), coalesce(sum(file_size),0), coalesce(sum(duration),0) FROM media_query GROUP BY kind ORDER BY count(*) DESC, kind DESC"))
            {
                var kind = Convert.ToString(row[0], CultureInfo.InvariantCulture)!; var count = Convert.ToInt64(row[1]);
                var seconds = kind == nameof(MediaKind.Image) ? 0 : Convert.ToDouble(row[3], CultureInfo.InvariantCulture);
                media += count; bytes += Convert.ToInt64(row[2]); hours += seconds / 3600;
                kinds.Add(new(kind switch { nameof(MediaKind.Video) => T("Videos"), nameof(MediaKind.Image) => T("Images"), _ => T("Audio") }, count, seconds / 3600, View: kind));
            }
            Lap("kinds");

            // One pass over the documents for picture size, how often watched and the most watched.
            var resolutionCounts = new long[ResolutionSteps.Length + 1]; long never = 0;
            var watchedMost = new PriorityQueue<(string Id, string Title, long Count), (long, string)>(Comparer<(long Count, string Title)>.Create((a, b) =>
                a.Count != b.Count ? a.Count.CompareTo(b.Count) : -StringComparer.OrdinalIgnoreCase.Compare(a.Title, b.Title)));
            foreach (var row in Rows("""
                SELECT q.kind, json_extract(m.document,'$.width'), json_extract(m.document,'$.height'),
                    coalesce(json_extract(m.document,'$.watchCount'), json_extract(m.document,'$.playCount'), 0), q.id, q.title
                FROM media_query q JOIN media m ON m.id=q.id WHERE q.kind<>'Image'
                """))
            {
                var kind = (string)row[0]; var watched = Convert.ToInt64(row[3]);
                if (watched == 0) never++;
                else
                {
                    // The ten largest stay in a small heap whose root is the weakest of them.
                    var entry = ((string)row[4], (string)row[5], watched);
                    if (watchedMost.Count < StatisticsTop) watchedMost.Enqueue(entry, (watched, entry.Item2));
                    else watchedMost.EnqueueDequeue(entry, (watched, entry.Item2));
                }
                if (kind != nameof(MediaKind.Video)) continue;
                var lines = row[1] is DBNull || row[2] is DBNull ? 0 : Math.Min(Convert.ToInt64(row[1]), Convert.ToInt64(row[2]));
                var step = Array.FindIndex(ResolutionSteps, s => lines >= s.Lines);
                resolutionCounts[step < 0 ? ResolutionSteps.Length : step]++;
            }
            var resolutions = new List<StatisticsBar>();
            for (var i = 0; i < ResolutionSteps.Length; i++)
                if (resolutionCounts[i] > 0) resolutions.Add(new(ResolutionSteps[i].Label, resolutionCounts[i], Expression: Resolution(ResolutionSteps[i].Lines, i == 0 ? null : ResolutionSteps[i - 1].Lines)));
            if (resolutionCounts[^1] > 0) resolutions.Add(new(T("unknown"), resolutionCounts[^1], Expression: SearchExpression.All(IsKind(MediaKind.Video),
                SearchExpression.Where(new() { Field = SearchField.Height, Comparison = SearchComparison.IsUnknown }))));
            Lap("documents");

            var fileTypes = Rows("SELECT lower(efp_extension(path)) e, count(*), coalesce(sum(duration),0) FROM media_query GROUP BY e ORDER BY count(*) DESC, e LIMIT $limit", ("$limit", StatisticsTop))
                .Select(r => (Extension: (string)r[0], Count: Convert.ToInt64(r[1]), Seconds: Convert.ToDouble(r[2], CultureInfo.InvariantCulture)))
                .Select(r => new StatisticsBar(r.Extension.Length == 0 ? T("without extension") : r.Extension.ToUpperInvariant(), r.Count, r.Seconds / 3600,
                    r.Extension.Length == 0 ? null : SearchExpression.Where(new() { Field = SearchField.Extension, Comparison = SearchComparison.Equals, Text = r.Extension }))).ToList();
            Lap("fileTypes");

            var videoCodecs = Rows("SELECT video_codec, count(*), coalesce(sum(duration),0) FROM media_query WHERE kind='Video' GROUP BY video_codec ORDER BY video_codec IS NULL, count(*) DESC, video_codec LIMIT $limit", ("$limit", StatisticsTop))
                .Select(r => r[0] is string codec
                    ? new StatisticsBar(TechnicalLabels.Codec(codec), Convert.ToInt64(r[1]), Convert.ToDouble(r[2], CultureInfo.InvariantCulture) / 3600,
                        SearchExpression.All(IsKind(MediaKind.Video), SearchExpression.Where(new() { Field = SearchField.VideoCodec, Comparison = SearchComparison.Equals, Text = codec })))
                    : new StatisticsBar(T("not read yet"), Convert.ToInt64(r[1]), Convert.ToDouble(r[2], CultureInfo.InvariantCulture) / 3600,
                        SearchExpression.All(IsKind(MediaKind.Video), SearchExpression.Where(new() { Field = SearchField.VideoCodec, Comparison = SearchComparison.IsUnknown })))).ToList();
            Lap("codecs");

            var firstMonth = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset).AddMonths(-23);
            var perMonth = Rows("SELECT strftime('%Y-%m', imported) m, count(*) FROM media_query WHERE imported >= julianday($from) GROUP BY m", ("$from", firstMonth.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)))
                .Where(r => r[0] is string).ToDictionary(r => (string)r[0], r => Convert.ToInt64(r[1]));
            var months = Enumerable.Range(0, 24).Select(i => firstMonth.AddMonths(i)).Select(month => new StatisticsBar(
                month.ToString("MMM yyyy", CultureInfo.CurrentCulture), perMonth.GetValueOrDefault(month.ToString("yyyy-MM", CultureInfo.InvariantCulture)),
                Expression: Between(SearchField.AddedAt, month, month.AddMonths(1)))).ToList();
            Lap("months");

            var directory = GetDirectory(cancellationToken);
            List<StatisticsBar> Top(TagRole role) => directory.Where(e => e.Role == role && e.MediaCount > 0).OrderByDescending(e => e.MediaCount).ThenBy(e => e.Tag, StringComparer.CurrentCultureIgnoreCase)
                .Take(StatisticsTop).Select(e => new StatisticsBar(e.Tag, e.MediaCount, Expression: e.Category.Length == 0
                    ? SearchExpression.Where(new() { Field = SearchField.Tag, Comparison = SearchComparison.Equals, Text = e.Tag }) : HasCategorizedTag(new TagKey(e.Category, e.Tag)))).ToList();
            var performers = Top(TagRole.Performer); var studios = Top(TagRole.Studio); var tags = Top(TagRole.Tag);
            Lap("directory");

            var mostWatched = watchedMost.UnorderedItems.Select(x => x.Element).OrderByDescending(x => x.Count).ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
                .Select(x => new StatisticsBar(x.Title, x.Count, MediaId: Guid.Parse(x.Id))).ToList();

            var firstWeek = now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7)).AddDays(-7 * 25);
            var firstWeekStart = new DateTimeOffset(firstWeek, now.Offset);
            var played = Rows("SELECT at FROM play_history WHERE kind=0 AND at >= $from", ("$from", JulianDay(firstWeekStart)))
                .Select(r => (int)Math.Floor((FromJulianDay(Convert.ToDouble(r[0], CultureInfo.InvariantCulture)).ToOffset(now.Offset) - firstWeekStart).TotalDays / 7))
                .Where(w => w is >= 0 and < 26).GroupBy(w => w).ToDictionary(g => g.Key, g => (long)g.Count());
            var weeks = Enumerable.Range(0, 26).Select(i => new StatisticsBar(firstWeekStart.AddDays(7 * i).ToString("d", CultureInfo.CurrentCulture), played.GetValueOrDefault(i))).ToList();
            var totals = Rows("SELECT coalesce(sum(CASE WHEN kind=0 THEN seconds END),0), count(CASE WHEN kind=0 THEN 1 END) FROM play_history").Single();
            var oTotal = Convert.ToInt64(Rows("SELECT coalesce(sum(o_count),0) FROM media_query").Single()[0]);
            Lap("history");

            return result with
            {
                Media = media, Bytes = bytes, Hours = hours, Kinds = kinds, Resolutions = resolutions, FileTypes = fileTypes, VideoCodecs = videoCodecs, ImportsPerMonth = months,
                Performers = performers, Studios = studios, Tags = tags, MostWatched = mostWatched, PlaysPerWeek = weeks, NeverWatched = never,
                WatchedHours = Convert.ToDouble(totals[0], CultureInfo.InvariantCulture) / 3600, Plays = Convert.ToInt64(totals[1]), OTotal = oTotal, Timings = timings,
            };
        }
    }

    private List<object[]> Rows(string sql, params (string Name, object Value)[] parameters)
    {
        using var command = Command(sql, null, parameters);
        using var reader = command.ExecuteReader();
        var rows = new List<object[]>();
        while (reader.Read()) { var values = new object[reader.FieldCount]; reader.GetValues(values); rows.Add(values); }
        return rows;
    }

    private static SearchExpression IsKind(MediaKind kind) => SearchExpression.Where(new() { Field = SearchField.Kind, Comparison = SearchComparison.Equals, Text = kind.ToString() });

    /// <summary>Videos whose shorter side has at least <paramref name="lines"/> and, with a next step, fewer than it.</summary>
    private static SearchExpression Resolution(int lines, int? below)
    {
        SearchExpression AtLeast(int value) => SearchExpression.All(
            SearchExpression.Where(new() { Field = SearchField.Width, Comparison = SearchComparison.GreaterOrEqual, Number = value }),
            SearchExpression.Where(new() { Field = SearchField.Height, Comparison = SearchComparison.GreaterOrEqual, Number = value }));
        return below is { } next ? SearchExpression.All(IsKind(MediaKind.Video), AtLeast(lines), SearchExpression.Not(AtLeast(next))) : SearchExpression.All(IsKind(MediaKind.Video), AtLeast(lines));
    }

    private static SearchExpression Between(SearchField field, DateTimeOffset from, DateTimeOffset until) => SearchExpression.All(
        SearchExpression.Where(new() { Field = field, Comparison = SearchComparison.GreaterOrEqual, Date = from }),
        SearchExpression.Where(new() { Field = field, Comparison = SearchComparison.LessThan, Date = until }));
}
