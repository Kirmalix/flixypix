using System.Globalization;
using System.Text;
using FlixyPix.Core;

namespace FlixyPix.MediaTools;

/// <summary>Content and look of a video contact sheet, following the original's thumbnail settings.</summary>
public sealed record ContactSheetStyle
{
    public int Columns { get; init; } = 1;
    public int Rows { get; init; } = 1;
    /// <summary>Width of the whole sheet; the height follows from the video's aspect ratio.</summary>
    public int Width { get; init; } = 480;
    /// <summary>Decode only the keyframe nearest each scene. Several times faster; the timestamp shows the keyframe's real time.</summary>
    public bool FastSeek { get; init; } = true;
    /// <summary>Gap around and between scenes in pixels.</summary>
    public int Border { get; init; }
    public string Background { get; init; } = "#000000";
    public string Foreground { get; init; } = "#FFFFFF";
    /// <summary>Font file for header and timestamps; needed only when one of them is shown.</summary>
    public string? FontFile { get; init; }
    public SheetTimestamp Timestamp { get; init; }
    /// <summary>Lines above the scenes, for example file name, size, resolution and duration; empty for none.</summary>
    public IReadOnlyList<string> Header { get; init; } = [];
    /// <summary>Percent of the duration left out at start and end.</summary>
    public int SkipPercent { get; init; }
}

public sealed partial class MediaToolService
{
    /// <summary>Seconds between scenes from which the keyframe seek is used.</summary>
    public const double FastSeekMinimumSpacing = 10;

    /// <summary>Scene times of a sheet: evenly spread over the duration without the skipped start and end.</summary>
    public static IReadOnlyList<double> SheetTimes(double durationSeconds, int count, int skipPercent)
    {
        var start = durationSeconds * skipPercent / 100d; var span = durationSeconds - 2 * start;
        return Enumerable.Range(0, count).Select(index => start + span * (index + 1) / (count + 1)).ToArray();
    }

    /// <summary>A grid of scenes with optional border, header and timestamps, made by a single FFmpeg run: one fast input seek
    /// per scene, scaling, stacking and text in one filter graph. The original started one process per scene and drew the
    /// sheet afterwards.</summary>
    public async Task<string> CreateContactSheetAsync(string input, string output, double durationSeconds, double aspectRatio, ContactSheetStyle style, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(style);
        if (style.Columns is < 1 or > 6 || style.Rows is < 1 or > 6) throw new ArgumentOutOfRangeException(nameof(style), "Das Raster erlaubt 1 bis 6 Spalten und Zeilen.");
        if (style.Width is < 160 or > 3840) throw new ArgumentOutOfRangeException(nameof(style), "Die Breite muss zwischen 160 und 3840 Pixeln liegen.");
        if (style.Border is < 0 or > 20 || style.SkipPercent is < 0 or > 20) throw new ArgumentOutOfRangeException(nameof(style));
        if (!CatalogRepository.IsColor(style.Background) || !CatalogRepository.IsColor(style.Foreground)) throw new ArgumentException("Farben bitte als #RRGGBB angeben.", nameof(style));
        var texts = style.Header.Count > 0 || style.Timestamp != SheetTimestamp.None;
        if (texts && (style.FontFile is null || !File.Exists(style.FontFile))) throw new ArgumentException("Für Kopfzeile und Zeitstempel fehlt eine Schriftdatei.", nameof(style));
        ValidateTime(durationSeconds, nameof(durationSeconds), positive: true);
        if (!double.IsFinite(aspectRatio) || aspectRatio <= 0) aspectRatio = 16d / 9;

        var count = style.Columns * style.Rows; var gap = style.Border;
        var cellWidth = Math.Max(2, (style.Width - gap * (style.Columns + 1)) / style.Columns / 2 * 2);
        var cellHeight = Math.Max(2, (int)Math.Round(cellWidth / aspectRatio / 2) * 2);
        // The sheet keeps exactly the configured width; what even cell widths leave over widens the outer border on both sides.
        var gridWidth = style.Width; var gridHeight = gap + style.Rows * (cellHeight + gap);
        var left = (gridWidth - style.Columns * cellWidth - (style.Columns - 1) * gap) / 2;
        var headerSize = Math.Clamp((int)Math.Round(gridWidth / 48d), 11, 32);
        var lineHeight = (int)Math.Round(headerSize * 1.5);
        var headerHeight = style.Header.Count == 0 ? 0 : style.Header.Count * lineHeight + headerSize;
        var stampSize = Math.Clamp((int)Math.Round(cellHeight / 9d), 9, 28);
        var background = "0x" + style.Background[1..]; var foreground = "0x" + style.Foreground[1..];
        var font = style.FontFile is null ? "" : "fontfile='" + style.FontFile.Replace('\\', '/').Replace(":", "\\:") + "'";
        var times = SheetTimes(durationSeconds, count, style.SkipPercent);
        // Hours appear only when the video has some.
        var clock = durationSeconds >= 3600 ? "%H\\\\\\:%M\\\\\\:%S" : "%M\\\\\\:%S";
        var (stampX, stampY) = style.Timestamp switch
        {
            SheetTimestamp.TopLeft => ("6", "6"), SheetTimestamp.TopRight => ("w-tw-6", "6"),
            SheetTimestamp.BottomLeft => ("6", "h-th-6"), _ => ("w-tw-6", "h-th-6"),
        };

        // Keyframes are usually up to 10 s apart: closer scenes would show the same keyframe twice, and short videos decode fast anyway.
        var fast = style.FastSeek && durationSeconds * (100 - 2 * style.SkipPercent) / 100d / (count + 1) >= FastSeekMinimumSpacing;
        string[] Seek(int index) => [.. fast ? new[] { "-skip_frame", "nokey", "-noaccurate_seek" } : [], "-ss", F(times[index])];
        var source = Input(input); var extraInputs = new List<string>(); var graph = new StringBuilder();
        for (var index = 0; index < count; index++)
        {
            if (index > 0) extraInputs.AddRange([.. Seek(index), "-i", source]);
            graph.Append(CultureInfo.InvariantCulture, $"[{index}:v]trim=end_frame=1,scale={cellWidth}:{cellHeight}:force_original_aspect_ratio=decrease,pad={cellWidth}:{cellHeight}:(ow-iw)/2:(oh-ih)/2:color=black");
            // The frame's own time plus the seek target: with a fast seek this is the keyframe actually shown.
            if (style.Timestamp != SheetTimestamp.None)
                graph.Append(CultureInfo.InvariantCulture, $",drawtext={font}:text='%{{pts\\:gmtime\\:{F(times[index])}\\:{clock}}}':fontsize={stampSize}:fontcolor=white:box=1:boxcolor=black@0.55:boxborderw={Math.Max(2, stampSize / 4)}:x={stampX}:y={stampY}");
            graph.Append(CultureInfo.InvariantCulture, $",setpts=PTS-STARTPTS,setsar=1[v{index}];");
        }
        var cells = string.Concat(Enumerable.Range(0, count).Select(index => $"[v{index}]"));
        if (count == 1) graph.Append(CultureInfo.InvariantCulture, $"{cells}pad={gridWidth}:{gridHeight}:{left}:{gap}:color={background}");
        else
        {
            var layout = string.Join('|', Enumerable.Range(0, count).Select(index => $"{index % style.Columns * (cellWidth + gap)}_{index / style.Columns * (cellHeight + gap)}"));
            graph.Append(CultureInfo.InvariantCulture, $"{cells}xstack=inputs={count}:layout={layout}:fill={background},pad={gridWidth}:{gridHeight}:{left}:{gap}:color={background}");
        }
        if (headerHeight > 0)
        {
            graph.Append(CultureInfo.InvariantCulture, $",pad={gridWidth}:{gridHeight + headerHeight}:0:{headerHeight}:color={background}");
            for (var line = 0; line < style.Header.Count; line++)
                graph.Append(CultureInfo.InvariantCulture, $",drawtext={font}:expansion=none:text='{HeaderText(style.Header[line])}':fontsize={headerSize}:fontcolor={foreground}{(line == 0 ? "" : "@0.75")}:x={gap + 6}:y={headerSize / 2 + line * lineHeight}");
        }
        graph.Append("[sheet]");
        // The graph goes into a script file: long graphs and file names stay out of the command line.
        var script = Path.Combine(Path.GetTempPath(), "flixypix-sheet-" + Guid.NewGuid().ToString("N") + ".graph");
        await File.WriteAllTextAsync(script, graph.ToString(), new UTF8Encoding(false), cancellationToken);
        try
        {
            return await RenderAsync(input, output, Seek(0), [.. extraInputs, "-/filter_complex", script, "-map", "[sheet]", "-frames:v", "1", "-update", "1"], cancellationToken);
        }
        finally { File.Delete(script); }
    }

    /// <summary>Header text inside a quoted drawtext value: the option parser needs backslash and colon escaped, and a quote
    /// would end the value, so it becomes a typographic apostrophe.</summary>
    private static string HeaderText(string text) => text.Replace('\'', '’').Replace("\\", "\\\\").Replace(":", "\\:").Replace("\r", " ").Replace("\n", " ");
}
