namespace FlixyPix.Core;

/// <summary>Timing of the hover preview on video tiles. The original offered 1–10, 15 or 20 frames, the speeds
/// Slow/Medium/Fast and an optional delay; the concrete intervals are FlixyPix values.</summary>
public static class HoverPreview
{
    public const int MaximumFrames = 20;

    /// <summary>Evenly spaced positions that skip the very start and end, where videos are often black.</summary>
    public static IReadOnlyList<double> FrameTimes(double durationSeconds, int count)
    {
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0 || count < 1) return [];
        count = Math.Min(count, MaximumFrames);
        return Enumerable.Range(1, count).Select(index => Math.Round(durationSeconds * index / (count + 1), 3)).ToArray();
    }

    public static TimeSpan Interval(HoverSpeed speed) => TimeSpan.FromMilliseconds(speed switch { HoverSpeed.Slow => 1100, HoverSpeed.Fast => 400, _ => 700 });

    /// <summary>With the delay on, a pointer that only passes over a tile does not start any work.</summary>
    public static TimeSpan StartDelay(bool delay) => TimeSpan.FromMilliseconds(delay ? 650 : 150);

    /// <summary>Cache file of one frame; the frame count is part of the name so changing it never mixes sets.</summary>
    public static string CacheFileName(Guid mediaId, int count, int index) => $"{mediaId:N}-{count}-{index}.jpg";
}
