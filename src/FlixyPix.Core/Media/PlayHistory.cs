using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

/// <summary>O counter and playback history (settings page "Verlauf").</summary>
public sealed record HistoryPreferences
{
    /// <summary>Each playback is noted with start, watched time and watched share; off: nothing new is noted.</summary>
    public bool Enabled { get; init; } = true;
    /// <summary>Share of a video that must have been seen for the playback to count as watched.</summary>
    public double WatchedShare { get; init; } = .9;
    /// <summary>O counter in the player, the details and the start page; hidden it keeps its values.</summary>
    public bool ShowOCounter { get; init; } = true;
    public const double MinimumShare = .1, MaximumShare = 1;
}

public enum HistoryKind { Play, O }

/// <summary>One playback (seconds and share seen) or one press of the O counter.</summary>
public sealed record HistoryEntry(long Id, Guid MediaId, HistoryKind Kind, DateTimeOffset At, double Seconds, double Share);

/// <summary>Which parts of a video were seen, from the positions the player reports a few times per second. Steps forward of
/// at most a few seconds count as watched; seeks and steps back only move the position.</summary>
public sealed class PlaybackCoverage
{
    public const int Parts = 1000;
    /// <summary>Largest step between two reports that still counts as watched, at normal speed.</summary>
    public const double MaximumStep = 3;
    private readonly double duration;
    private readonly bool[] seen = new bool[Parts];
    private double? last;

    public PlaybackCoverage(double? durationSeconds) => duration = durationSeconds is > 0 and var value && double.IsFinite(value) ? value : 0;

    /// <summary>Media time watched, without pauses and skipped parts.</summary>
    public double WatchedSeconds { get; private set; }
    /// <summary>Share of the length seen at least once (0 without a known length).</summary>
    public double Share => duration > 0 ? seen.Count(part => part) / (double)Parts : 0;

    public void Observe(double seconds, double speed = 1)
    {
        if (!double.IsFinite(seconds)) return;
        if (last is { } previous && seconds > previous && seconds - previous <= MaximumStep * Math.Max(1, speed))
        {
            WatchedSeconds += seconds - previous;
            if (duration > 0)
            {
                var first = Part(previous);
                // The player stops reporting shortly before the end; a step that reaches close to it covers the rest.
                var end = duration - seconds <= MaximumStep * Math.Max(1, speed) ? Parts - 1 : Part(seconds);
                for (var part = first; part <= end; part++) seen[part] = true;
            }
        }
        last = seconds;
    }

    private int Part(double seconds) => Math.Clamp((int)(seconds / duration * Parts), 0, Parts - 1);
}

public sealed partial class CatalogRepository
{
    private const double UnixEpochJulianDay = 2440587.5;

    private void InitializeHistorySchema(SqliteTransaction transaction)
    {
        using var command = Command("""
            CREATE TABLE IF NOT EXISTS play_history(id INTEGER PRIMARY KEY, media_id TEXT NOT NULL REFERENCES media(id) ON DELETE CASCADE,
                kind INTEGER NOT NULL, at REAL NOT NULL, seconds REAL NOT NULL DEFAULT 0, share REAL NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS play_history_media ON play_history(media_id,at);
            CREATE INDEX IF NOT EXISTS play_history_at ON play_history(kind,at);
            """, transaction);
        command.ExecuteNonQuery();
    }

    private static double JulianDay(DateTimeOffset when) => when.ToUnixTimeMilliseconds() / 86_400_000.0 + UnixEpochJulianDay;
    private static DateTimeOffset FromJulianDay(double day) => DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round((day - UnixEpochJulianDay) * 86_400_000));

    public void SaveHistoryPreferences(HistoryPreferences history) { ValidateHistory(history); SavePreferences(GetPreferences() with { History = history }); }

    public static void ValidateHistory(HistoryPreferences value)
    {
        if (!double.IsFinite(value.WatchedShare) || value.WatchedShare is < HistoryPreferences.MinimumShare or > HistoryPreferences.MaximumShare)
            throw new ArgumentException(T("A playback counts as watched from 10 to 100 % of its length."));
    }

    /// <summary>Notes the start of a playback of a video or audio file; null when the history is off.</summary>
    public long? BeginPlayback(Guid id, DateTimeOffset when)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (!GetPreferences().History.Enabled || GetMedia(id) is not { Kind: not MediaKind.Image }) return null;
            using var command = Command("INSERT INTO play_history(media_id,kind,at) VALUES($media,0,$at) RETURNING id", null,
                ("$media", id.ToString()), ("$at", JulianDay(when)));
            return Convert.ToInt64(command.ExecuteScalar());
        }
    }

    /// <summary>Notes how much of a playback was seen and counts it as watched from the share in the settings. A medium
    /// without a watch count yet starts from the plays before this one, so earlier playbacks stay watched.</summary>
    public MediaItem? FinishPlayback(Guid id, long? entry, double watchedSeconds, double share)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (GetMedia(id) is not { } item) return null;
            watchedSeconds = double.IsFinite(watchedSeconds) ? Math.Max(0, watchedSeconds) : 0;
            share = double.IsFinite(share) ? Math.Clamp(share, 0, 1) : 0;
            if (entry is { } row)
            {
                using var update = Command("UPDATE play_history SET seconds=$seconds, share=$share WHERE id=$id AND media_id=$media", null,
                    ("$seconds", watchedSeconds), ("$share", share), ("$id", row), ("$media", id.ToString()));
                update.ExecuteNonQuery();
            }
            var reached = share > 0 && share >= GetPreferences().History.WatchedShare;
            var before = item.WatchCount ?? Math.Max(0, (item.PlayCount ?? 1) - 1);
            var watched = before + (reached ? 1 : 0);
            return item.WatchCount == watched ? item : Upsert(item with { WatchCount = watched });
        }
    }

    /// <summary>Adds one to the O counter; with the history on, also notes when.</summary>
    public MediaItem? AddO(Guid id, DateTimeOffset when)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (GetMedia(id) is not { } item) return null;
            if (GetPreferences().History.Enabled)
            {
                using var command = Command("INSERT INTO play_history(media_id,kind,at) VALUES($media,1,$at)", null, ("$media", id.ToString()), ("$at", JulianDay(when)));
                command.ExecuteNonQuery();
            }
            return Upsert(item with { OCount = (item.OCount ?? 0) + 1 });
        }
    }

    /// <summary>Takes back the last O: one less on the counter and its history entry, if there is one.</summary>
    public MediaItem? UndoO(Guid id)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (GetMedia(id) is not { } item || item.OCount is not > 0) return GetMedia(id);
            using var command = Command("DELETE FROM play_history WHERE id=(SELECT id FROM play_history WHERE media_id=$media AND kind=1 ORDER BY at DESC, id DESC LIMIT 1)", null,
                ("$media", id.ToString()));
            command.ExecuteNonQuery();
            return Upsert(item with { OCount = item.OCount - 1 });
        }
    }

    /// <summary>Newest entries first, of one medium or of all.</summary>
    public IReadOnlyList<HistoryEntry> GetHistory(Guid? mediaId = null, int limit = 200)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var command = mediaId is { } id
                ? Command("SELECT id,media_id,kind,at,seconds,share FROM play_history WHERE media_id=$media ORDER BY at DESC, id DESC LIMIT $limit", null, ("$media", id.ToString()), ("$limit", limit))
                : Command("SELECT id,media_id,kind,at,seconds,share FROM play_history ORDER BY at DESC, id DESC LIMIT $limit", null, ("$limit", limit));
            var result = new List<HistoryEntry>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
                result.Add(new(reader.GetInt64(0), Guid.Parse(reader.GetString(1)), (HistoryKind)reader.GetInt32(2), FromJulianDay(reader.GetDouble(3)), reader.GetDouble(4), reader.GetDouble(5)));
            return result;
        }
    }

    public long CountHistory()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var command = Command("SELECT count(*) FROM play_history", null);
            return Convert.ToInt64(command.ExecuteScalar());
        }
    }

    /// <summary>Deletes the history of one medium or all of it; counters and the last playback stay. Returns the entries removed.</summary>
    public int ClearHistory(Guid? mediaId = null)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var command = mediaId is { } id
                ? Command("DELETE FROM play_history WHERE media_id=$media", null, ("$media", id.ToString()))
                : Command("DELETE FROM play_history", null);
            return command.ExecuteNonQuery();
        }
    }
}
