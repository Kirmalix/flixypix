using System.Globalization;

namespace FlixyPix.Core;

public enum JumpMarkKind { Bookmark, SceneThumbnail, Scene }

/// <summary>A time point the player can jump to: a bookmark, a scene thumbnail from the original or the start of a scene.</summary>
public sealed record JumpMark(double PositionSeconds, string Title, JumpMarkKind Kind, Guid SourceId, Guid? PreviewAssetId = null, double? EndSeconds = null)
{
    public string TimeLabel => JumpMarks.FormatTime(PositionSeconds);
    public string KindLabel => Kind switch { JumpMarkKind.Bookmark => T("Bookmarks"), JumpMarkKind.SceneThumbnail => T("Scene image"), _ => T("Scene") };
}

/// <summary>Ordering and navigation of jump marks. Navigation always follows the time line, independent of the
/// display order the user arranged in the details panel.</summary>
public static class JumpMarks
{
    /// <summary>Marks closer than this after the current position count as "here" and are skipped by <see cref="Next"/>.</summary>
    public const double NextTolerance = .5;
    /// <summary>Within this time after a mark, <see cref="Previous"/> goes to the mark before it instead of restarting it, like track buttons.</summary>
    public const double PreviousRestartWindow = 1.5;
    /// <summary>A seek lands slightly before the requested time; within this margin the mark still counts as reached.</summary>
    public const double CurrentTolerance = .25;

    public static IReadOnlyList<JumpMark> Build(IEnumerable<Bookmark> bookmarks, IEnumerable<Scene> scenes, double? durationSeconds = null)
    {
        bool Playable(double seconds) => double.IsFinite(seconds) && seconds >= 0 && (durationSeconds is not > 0 || seconds <= durationSeconds.Value);
        var marks = bookmarks.Where(b => Playable(b.PositionSeconds)).Select(b => new JumpMark(b.PositionSeconds, Title(b.Title, b.Kind == BookmarkKind.Bookmark ? T("Bookmarks") : T("Scene image"), b.PositionSeconds),
                b.Kind == BookmarkKind.Bookmark ? JumpMarkKind.Bookmark : JumpMarkKind.SceneThumbnail, b.Id, b.PreviewAssetId))
            .Concat(scenes.Where(s => Playable(s.StartSeconds)).Select(s => new JumpMark(s.StartSeconds, Title(s.Title, T("Scene"), s.StartSeconds), JumpMarkKind.Scene, s.Id, s.PreviewAssetId, s.EndSeconds)));
        return marks
            .OrderBy(m => m.PositionSeconds).ThenBy(m => m.Kind).ThenBy(m => m.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(m => m.SourceId).ToArray();
    }

    public static JumpMark? Next(IReadOnlyList<JumpMark> marks, double positionSeconds) =>
        marks.Where(m => m.PositionSeconds > positionSeconds + NextTolerance).MinBy(m => m.PositionSeconds);

    public static JumpMark? Previous(IReadOnlyList<JumpMark> marks, double positionSeconds) =>
        marks.Where(m => m.PositionSeconds < positionSeconds - PreviousRestartWindow).MaxBy(m => m.PositionSeconds);

    /// <summary>The mark the playback position is in, i.e. the last one at or before it.</summary>
    public static JumpMark? Current(IReadOnlyList<JumpMark> marks, double positionSeconds) =>
        marks.Where(m => m.PositionSeconds <= positionSeconds + CurrentTolerance).MaxBy(m => m.PositionSeconds);

    /// <summary>Mark number <paramref name="number"/> (1-based) in time order, as used by the digit keys.</summary>
    public static JumpMark? Numbered(IReadOnlyList<JumpMark> marks, int number) => number >= 1 && number <= marks.Count ? marks.OrderBy(m => m.PositionSeconds).ElementAt(number - 1) : null;

    public static string FormatTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(seconds)));
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : time.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    private static string Title(string? title, string fallback, double seconds) => string.IsNullOrWhiteSpace(title) ? fallback + " " + FormatTime(seconds) : title.Trim();
}
