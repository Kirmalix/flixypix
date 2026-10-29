using System.Globalization;
using System.Text.Json;

namespace FlixyPix.Core;

/// <summary>Rows of the start page; the original offered the first four (original "homevideosections").</summary>
public enum HomeRowKind { ContinueWatching, RecentlyAdded, WatchLater, FavoritePerformers, ForYou, Popular, Rediscover, Studios, MoreWithTag, RandomOfTheDay, RecentlyWatched, MostO }

/// <summary>Which rows the start page shows, in this order.</summary>
public sealed record HomePreferences
{
    public static readonly HomeRowKind[] DefaultRows =
        [HomeRowKind.ContinueWatching, HomeRowKind.RecentlyWatched, HomeRowKind.ForYou, HomeRowKind.RecentlyAdded, HomeRowKind.WatchLater, HomeRowKind.FavoritePerformers,
         HomeRowKind.Popular, HomeRowKind.MostO, HomeRowKind.MoreWithTag, HomeRowKind.Studios, HomeRowKind.Rediscover, HomeRowKind.RandomOfTheDay];
    public List<HomeRowKind> Rows { get; init; } = [.. DefaultRows];
    /// <summary>The app opens on the start page; the restored view stays one step back.</summary>
    public bool StartOnHome { get; init; } = true;
    /// <summary>Days that count for "Zuletzt gesehen" (original "recentlywatcheddays").</summary>
    public int RecentDays { get; init; } = 7;
    public const int MaximumRecentDays = 365;

    public static string Label(HomeRowKind kind) => kind switch
    {
        HomeRowKind.ContinueWatching => T("Continue watching"), HomeRowKind.RecentlyAdded => T("Recently added"), HomeRowKind.WatchLater => T("Watch later"),
        HomeRowKind.FavoritePerformers => T("Favorite performers"), HomeRowKind.ForYou => T("For you"), HomeRowKind.Popular => T("Popular"),
        HomeRowKind.Rediscover => T("Not watched for a long time"), HomeRowKind.Studios => T("Studios"), HomeRowKind.MoreWithTag => T("More with …"),
        HomeRowKind.RecentlyWatched => T("Recently watched"), HomeRowKind.MostO => T("Most O"), _ => T("Random pick of the day"),
    };
}

/// <summary>A medium on the start page, with why it is suggested.</summary>
public sealed record HomeCard(MediaItem Item, string? Reason = null);

/// <summary>A performer or studio with a medium whose picture stands for it.</summary>
public sealed record HomeEntity(DirectoryEntry Entry, MediaItem? Cover);

public sealed record HomeRow(HomeRowKind Kind, string Title, IReadOnlyList<HomeCard> Cards, IReadOnlyList<HomeEntity> Entities, string? Tag = null);

public sealed record HomeFeed(IReadOnlyList<HomeCard> Hero, IReadOnlyList<HomeRow> Rows);

/// <summary>The start page: rows built from indexed, limited queries, and suggestions computed locally from the tags of what
/// was watched recently (newer views weigh more), with a reason and some variety. Nothing leaves the computer.</summary>
public sealed partial class CatalogRepository
{
    private const int HomeRowSize = 20;
    /// <summary>Milliseconds each part of the last start page took (performance checks).</summary>
    public IReadOnlyDictionary<string, double> LastHomeTimings { get; private set; } = new Dictionary<string, double>();

    /// <summary>Counts a playback (images: a view) and notes when; editing the medium keeps the browsing caches.</summary>
    public MediaItem? RecordPlay(Guid id, DateTimeOffset when)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (GetMedia(id) is not { } item) return null;
            var played = item.Kind == MediaKind.Image
                ? item with { ViewCount = (item.ViewCount ?? 0) + 1, LastPlayedAt = when }
                : item with { PlayCount = (item.PlayCount ?? 0) + 1, LastPlayedAt = when };
            return Upsert(played);
        }
    }

    public HomeFeed BuildHomeFeed(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var preferences = GetPreferences();
        var rows = new List<HomeRow>();
        var timings = new Dictionary<string, double>(); var clock = System.Diagnostics.Stopwatch.StartNew();
        var recent = RecentlyPlayed(40, cancellationToken);
        timings["recent"] = clock.Elapsed.TotalMilliseconds;
        var watchedLately = recent.Where(m => m.LastPlayedAt > now.AddDays(-14)).Select(m => m.Id).ToHashSet();
        List<HomeCard> forYou = [];
        foreach (var kind in preferences.Home.Rows.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            clock.Restart();
            switch (kind)
            {
                case HomeRowKind.ContinueWatching:
                    Add(kind, ContinueWatching(cancellationToken).Select(m => new HomeCard(m, "bei " + Clock(m.PlaybackPositionSeconds))));
                    break;
                case HomeRowKind.RecentlyAdded:
                    Add(kind, Top(new MediaQuery { Sort = MediaSort.AddedAt, Descending = true, Limit = HomeRowSize }, cancellationToken).Select(m => new HomeCard(m)));
                    break;
                case HomeRowKind.WatchLater:
                    Add(kind, Top(new MediaQuery { WatchLater = true, Sort = MediaSort.AddedAt, Descending = true, Limit = HomeRowSize }, cancellationToken).Select(m => new HomeCard(m)));
                    break;
                case HomeRowKind.ForYou:
                    forYou = Suggestions(recent, watchedLately, now, cancellationToken);
                    Add(kind, forYou);
                    break;
                case HomeRowKind.Popular:
                    // Best rated first from the index, then by how often they were played.
                    var rated = Top(new MediaQuery { MinimumRating = 6, Sort = MediaSort.Rating, Descending = true, Limit = 200 }, cancellationToken);
                    Add(kind, rated.OrderByDescending(m => (m.PlayCount ?? 0) + (m.ViewCount ?? 0)).ThenByDescending(m => m.Rating).Take(HomeRowSize)
                        .Select(m => new HomeCard(m, (m.PlayCount ?? 0) + (m.ViewCount ?? 0) is > 0 and var plays ? T("watched {0:N0}×", plays) : null)));
                    break;
                case HomeRowKind.Rediscover:
                    var good = Top(new MediaQuery { MinimumRating = 8, Sort = MediaSort.Rating, Descending = true, Limit = 300 }, cancellationToken);
                    Add(kind, DailyShuffle(good.Where(m => m.LastPlayedAt is not { } last || last < now.AddDays(-90)), now).Take(HomeRowSize)
                        .Select(m => new HomeCard(m, m.LastPlayedAt is { } last ? "zuletzt " + last.ToLocalTime().ToString("MMMM yyyy", CultureInfo.CurrentCulture) : Tc("home feed", "never watched"))));
                    break;
                case HomeRowKind.FavoritePerformers:
                    AddEntities(kind, TagRole.Performer, cancellationToken);
                    break;
                case HomeRowKind.Studios:
                    AddEntities(kind, TagRole.Studio, cancellationToken);
                    break;
                case HomeRowKind.MoreWithTag:
                    foreach (var tag in TagsForRows(recent, 3))
                    {
                        var items = Top(new MediaQuery { RequiredTags = [tag], Sort = MediaSort.Rating, Descending = true, Limit = HomeRowSize + watchedLately.Count }, cancellationToken);
                        var cards = items.Where(m => !watchedLately.Contains(m.Id)).Take(HomeRowSize).Select(m => new HomeCard(m)).ToList();
                        if (cards.Count >= 3) rows.Add(new HomeRow(kind, T("More with ") + tag, cards, [], tag));
                    }
                    break;
                case HomeRowKind.RecentlyWatched:
                    // Everything played within the chosen days, newest first; images count as seen.
                    Add(kind, RecentlyPlayed(HomeRowSize, cancellationToken).Where(m => m.LastPlayedAt >= now.AddDays(-preferences.Home.RecentDays))
                        .Select(m => new HomeCard(m, Ago(m.LastPlayedAt!.Value, now))));
                    break;
                case HomeRowKind.MostO:
                    // Highest O counter first from its index; hidden together with the counter.
                    if (!preferences.History.ShowOCounter) break;
                    Add(kind, Top(new MediaQuery { Sort = MediaSort.OCount, Descending = true, Limit = HomeRowSize }, cancellationToken).Where(m => m.OCount > 0)
                        .Select(m => new HomeCard(m, Tn((int)Math.Min(int.MaxValue, m.OCount!.Value), "1 × O", "{0:N0} × O"))));
                    break;
                case HomeRowKind.RandomOfTheDay:
                    Add(kind, RandomOfTheDay(now, cancellationToken).Select(m => new HomeCard(m)));
                    break;
            }
            timings[kind.ToString()] = clock.Elapsed.TotalMilliseconds;
        }
        LastHomeTimings = timings;
        // Up to five pictures for the big banner: what was started, then suggestions; only media with a picture.
        var hero = rows.Where(r => r.Kind is HomeRowKind.ContinueWatching).SelectMany(r => r.Cards).Take(2).Concat(forYou)
            .Concat(rows.Where(r => r.Kind == HomeRowKind.RecentlyAdded).SelectMany(r => r.Cards))
            .Where(c => c.Item.ThumbnailPath is not null || c.Item.Kind == MediaKind.Image).DistinctBy(c => c.Item.Id).Take(5).ToList();
        return new HomeFeed(hero, rows);

        void Add(HomeRowKind kind, IEnumerable<HomeCard> cards)
        {
            var list = cards.Take(HomeRowSize).ToList();
            if (list.Count > 0) rows.Add(new HomeRow(kind, HomePreferences.Label(kind), list, []));
        }
        void AddEntities(HomeRowKind kind, TagRole role, CancellationToken token)
        {
            var entries = GetDirectory(token).Where(e => e.Role == role && e.MediaCount > 0).ToList();
            // Favourites first; without favourites the most frequent ones stand in.
            var chosen = entries.Where(e => e.IsFavorite).OrderByDescending(e => e.MediaCount)
                .Concat(entries.Where(e => !e.IsFavorite).OrderByDescending(e => e.MediaCount)).Take(16).ToList();
            if (chosen.Count == 0) return;
            var title = kind == HomeRowKind.FavoritePerformers && !chosen.Any(e => e.IsFavorite) ? T("Performers") : HomePreferences.Label(kind);
            var entities = chosen.Select(e => new HomeEntity(e, Top(new MediaQuery
            {
                Expression = HasCategorizedTag(new TagKey(e.Category, e.Tag)), Sort = MediaSort.Rating, Descending = true, Limit = 1,
            }, token).FirstOrDefault())).ToList();
            rows.Add(new HomeRow(kind, title, [], entities));
        }
    }

    /// <summary>The first media of a query without counting all hits; the start page needs no totals.</summary>
    private IReadOnlyList<MediaItem> Top(MediaQuery query, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            var (where, parameters) = BuildQuery(query, transaction);
            var items = QueryItems(query, transaction, cancellationToken, where, parameters);
            CommitAfterIndexing(transaction);
            return items;
        }
    }

    private static string Clock(double seconds) =>
        TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);

    private static string Ago(DateTimeOffset when, DateTimeOffset now) => (now.Date - when.ToOffset(now.Offset).Date).Days switch
    {
        <= 0 => "heute", 1 => "gestern", var days => Tn(days, "1 day ago", "{0} days ago"),
    };

    /// <summary>Started but not finished (1–95 %), newest playback first.</summary>
    private List<MediaItem> ContinueWatching(CancellationToken cancellationToken) =>
        MediaWhere("q.progress > 0.01 AND q.progress < 0.95", HomeRowSize, cancellationToken, "coalesce(q.last_played,0) DESC");

    private List<MediaItem> RecentlyPlayed(int count, CancellationToken cancellationToken) =>
        MediaWhere("q.last_played IS NOT NULL", count, cancellationToken, "q.last_played DESC");

    /// <summary>Media by a condition on the query table (indexed progress and last playback), without opening other documents.</summary>
    private List<MediaItem> MediaWhere(string condition, int limit, CancellationToken cancellationToken, string order)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var command = Command($"SELECT m.document FROM (SELECT q.id FROM media_query q WHERE {condition} ORDER BY {order} LIMIT {limit}) s JOIN media m ON m.id=s.id", null);
            using var cancel = cancellationToken.Register(command.Cancel);
            using var reader = command.ExecuteReader();
            var items = new List<MediaItem>();
            while (reader.Read()) items.Add(JsonSerializer.Deserialize<MediaItem>(reader.GetString(0), Json)!);
            return items;
        }
    }

    /// <summary>Media sharing tags with recently watched ones. Each recent view lends its tags a weight that halves every month;
    /// rating, favourite and "not seen yet" add a little; at most two suggestions share their strongest tag.</summary>
    private List<HomeCard> Suggestions(List<MediaItem> recent, HashSet<Guid> watchedLately, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in recent)
        {
            var age = Math.Max(0, (now - (item.LastPlayedAt ?? now)).TotalDays);
            var weight = Math.Pow(.5, age / 30);
            foreach (var tag in item.Tags) weights[tag] = weights.GetValueOrDefault(tag) + weight;
        }
        if (weights.Count == 0)
        {
            // Nothing watched yet: well rated media nobody has seen.
            var rated = Top(new MediaQuery { MinimumRating = 6, Sort = MediaSort.Rating, Descending = true, Limit = 100 }, cancellationToken);
            return rated.Where(m => (m.PlayCount ?? 0) == 0).Take(HomeRowSize).Select(m => new HomeCard(m, T("well rated, not watched yet"))).ToList();
        }
        var top = weights.OrderByDescending(p => p.Value).Take(40).Select(p => p.Key).ToList();
        var ids = new List<string>();
        lock (gate)
        {
            using var command = Command("SELECT media_id FROM media_tags WHERE category='' AND tag IN (SELECT value FROM json_each($tags)) COLLATE EFP_ORDINAL GROUP BY media_id ORDER BY count(*) DESC LIMIT 400",
                null, ("$tags", JsonSerializer.Serialize(top)));
            using var reader = command.ExecuteReader();
            while (reader.Read()) ids.Add(reader.GetString(0));
        }
        var favorites = GetPreferences();
        var scored = new List<(MediaItem Item, double Score, string Reason, string Lead)>();
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (watchedLately.Contains(Guid.Parse(id)) || GetMedia(Guid.Parse(id)) is not { } item) continue;
            var shared = item.Tags.Where(weights.ContainsKey).OrderByDescending(t => weights[t]).ToList();
            if (shared.Count == 0) continue;
            var score = shared.Sum(t => weights[t]) / Math.Sqrt(Math.Max(1, item.Tags.Count)) + item.Rating / 20.0 + (IsFavoriteCore(item, favorites) ? .2 : 0) + ((item.PlayCount ?? 0) == 0 ? .3 : 0);
            scored.Add((item, score, T("Because: {0}", string.Join(", ", shared.Take(2))), shared[0]));
        }
        var result = new List<HomeCard>(); var leads = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (item, _, reason, lead) in scored.OrderByDescending(s => s.Score))
        {
            if (leads.GetValueOrDefault(lead) >= 2) continue;
            leads[lead] = leads.GetValueOrDefault(lead) + 1;
            result.Add(new HomeCard(item, reason));
            if (result.Count == HomeRowSize) break;
        }
        return result;
    }

    /// <summary>The tags of recent views (else the most used tags) that get a row of their own.</summary>
    private List<string> TagsForRows(List<MediaItem> recent, int count)
    {
        var fromRecent = recent.SelectMany(m => m.Tags).GroupBy(t => t, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
        var frequent = new List<string>();
        if (fromRecent.Count < count)
            lock (gate)
            {
                // From the tag index; the full tag statistics are not needed for a handful of names.
                using var command = Command("SELECT tag FROM media_tags WHERE category='' GROUP BY tag COLLATE EFP_ORDINAL HAVING count(*) >= 3 ORDER BY count(*) DESC LIMIT 10", null);
                using var reader = command.ExecuteReader();
                while (reader.Read()) frequent.Add(reader.GetString(0));
            }
        return fromRecent.Concat(frequent).Distinct(StringComparer.OrdinalIgnoreCase).Take(count).ToList();
    }

    /// <summary>Random media that stay the same all day: row numbers drawn with the day as seed, in one query.</summary>
    private List<MediaItem> RandomOfTheDay(DateTimeOffset now, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            long low, high;
            using (var range = Command("SELECT min(rowid), max(rowid) FROM media", null))
            using (var reader = range.ExecuteReader()) { reader.Read(); if (reader.IsDBNull(0)) return []; low = reader.GetInt64(0); high = reader.GetInt64(1); }
            var random = new Random(DaySeed(now));
            // Row numbers can have gaps; drawing more than needed still fills the row.
            var drawn = Enumerable.Range(0, HomeRowSize * 4).Select(_ => low + (long)(random.NextDouble() * (high - low + 1))).Distinct().ToList();
            var order = drawn.Select((row, index) => (row, index)).ToDictionary(p => p.row, p => p.index);
            using var command = Command("SELECT rowid, document FROM media WHERE rowid IN (SELECT value FROM json_each($rows))", null, ("$rows", JsonSerializer.Serialize(drawn)));
            using var cancel = cancellationToken.Register(command.Cancel);
            using var result = command.ExecuteReader();
            var items = new List<(int Order, MediaItem Item)>();
            while (result.Read()) items.Add((order[result.GetInt64(0)], JsonSerializer.Deserialize<MediaItem>(result.GetString(1), Json)!));
            return items.OrderBy(p => p.Order).Select(p => p.Item).Take(HomeRowSize).ToList();
        }
    }

    private static IEnumerable<MediaItem> DailyShuffle(IEnumerable<MediaItem> items, DateTimeOffset now)
    {
        var random = new Random(DaySeed(now));
        return items.Select(m => (Item: m, Key: random.Next())).OrderBy(p => p.Key).Select(p => p.Item);
    }

    private static int DaySeed(DateTimeOffset now) => now.ToLocalTime().Year * 1000 + now.ToLocalTime().DayOfYear;
}
