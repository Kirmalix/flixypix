using System.Globalization;

namespace FlixyPix.Core;

/// <summary>Which frames of a range become pictures.</summary>
public enum StillSpacing { EveryFrame, EveryNthFrame, EverySeconds }

/// <summary>A marked range of a video to save as single pictures: every frame, every n-th frame or one picture every
/// so many seconds (<paramref name="Amount"/> is n or the seconds).</summary>
public sealed record StillRange(double StartSeconds, double EndSeconds, StillSpacing Spacing = StillSpacing.EveryFrame, double Amount = 1)
{
    /// <summary>More pictures than this in one go are refused; a wrong choice would otherwise fill a drive.</summary>
    public const int MaximumCount = 20000;
    public double LengthSeconds => EndSeconds - StartSeconds;

    /// <summary>Seconds from one picture to the next at this frame rate.</summary>
    public double Step(double framesPerSecond) => Spacing switch
    {
        StillSpacing.EverySeconds => Amount,
        StillSpacing.EveryNthFrame => Math.Max(1, Math.Round(Amount)) / framesPerSecond,
        _ => 1 / framesPerSecond,
    };

    /// <summary>How many pictures to expect; both ends of the range count.</summary>
    public int Count(double framesPerSecond) => (int)Math.Min(int.MaxValue, Math.Floor(LengthSeconds / Step(framesPerSecond) + 1e-6) + 1);

    public StillRange Validate(double framesPerSecond)
    {
        if (!double.IsFinite(StartSeconds) || !double.IsFinite(EndSeconds) || StartSeconds < 0 || EndSeconds <= StartSeconds) throw new ArgumentException(T("Set the start of the range first and then a later end."));
        if (!Enum.IsDefined(Spacing) || !double.IsFinite(Amount) || Amount <= 0 || Spacing == StillSpacing.EverySeconds && Amount < .01 || Spacing == StillSpacing.EveryNthFrame && Amount > 100000)
            throw new ArgumentOutOfRangeException(nameof(Amount));
        if (!double.IsFinite(framesPerSecond) || framesPerSecond <= 0 || framesPerSecond > 1000) throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        if (Count(framesPerSecond) > MaximumCount) throw new ArgumentException(T("That would be more than {0:N0} pictures. Choose a shorter range or a larger spacing.", MaximumCount));
        return this;
    }
}

/// <summary>A picture saved from a video, and the time in the video it shows.</summary>
public sealed record StillFile(string Path, double Seconds);

/// <summary>Pictures taken from videos: they join the library as images and remember where they come from.</summary>
public static class Stills
{
    /// <summary>Keys in <see cref="MediaItem.Extra"/>: the id of the video and the time in it (seconds, invariant).</summary>
    public const string SourceKey = "still.source", SecondsKey = "still.seconds";

    /// <summary>The video an image was taken from and the time in it; null for any other medium.</summary>
    public static (Guid VideoId, double Seconds)? SourceOf(MediaItem item) =>
        item.Extra.TryGetValue(SourceKey, out var id) && Guid.TryParse(id, out var video)
            ? (video, item.Extra.TryGetValue(SecondsKey, out var time) && double.TryParse(time, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0 ? seconds : 0)
            : null;

    /// <summary>Time with thousandths and, when the frame rate is known, the number of the frame: "1:23.480 · frame 2,502".</summary>
    public static string Position(double seconds, long? frame)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        var text = time.ToString(time.TotalHours >= 1 ? @"h\:mm\:ss\.fff" : @"m\:ss\.fff", CultureInfo.CurrentCulture);
        return frame is { } number ? T("{0} · frame {1:N0}", text, number) : text;
    }
}

public sealed partial class CatalogRepository
{
    /// <summary>Adds pictures saved from a video as images. Files the catalog already knows are left as they are.</summary>
    /// <returns>The images added.</returns>
    public IReadOnlyList<MediaItem> AddStills(Guid videoId, IReadOnlyList<StillFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var general = GetPreferences().General; var added = new List<MediaItem>();
        foreach (var file in files)
        {
            var path = Path.GetFullPath(file.Path); var info = new FileInfo(path);
            if (!info.Exists) continue;
            var item = new MediaItem
            {
                Path = path, Title = general.ImportTitle(path), Kind = MediaKind.Image, FileSize = info.Length,
                FileModifiedAtUtc = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), FileCreatedAt = MediaScanner.FileDate(info),
                Extra = new() { [Stills.SourceKey] = videoId.ToString(), [Stills.SecondsKey] = file.Seconds.ToString("0.###", CultureInfo.InvariantCulture) },
            };
            if (AddScannedItem(item) && GetMedia(item.Id) is { } saved) added.Add(saved);
        }
        return added;
    }
}
