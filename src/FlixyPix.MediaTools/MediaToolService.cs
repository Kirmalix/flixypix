using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using FlixyPix.Core;

namespace FlixyPix.MediaTools;

public sealed record MediaToolPaths(string? Ffmpeg = null, string? Ffprobe = null, string? Mpv = null, bool HeadlessPlayback = false);
/// <param name="Technical">Codecs, frame rate, bit rate, HDR and container for the catalog (package P6).</param>
public sealed record MediaProbe(double? DurationSeconds, int? Width, int? Height, string? VideoCodec, string? AudioCodec, string RawJson, TechnicalInfo? Technical = null);

public sealed partial class MediaToolService(MediaToolPaths paths)
{
    private static string Resolve(string? configured, string name)
    {
        var path = Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, "tools", name + ".exe") : configured);
        if (!File.Exists(path)) throw new InvalidOperationException($"{name} fehlt. Bitte in den Einstellungen den Pfad zur Anwendung festlegen.");
        return path;
    }

    private static string Input(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new FileNotFoundException("Die Mediendatei wurde nicht gefunden.", full);
        return full;
    }

    public async Task<MediaProbe> ProbeAsync(string input, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(Resolve(paths.Ffprobe, "ffprobe"), ["-v", "error", "-show_format", "-show_streams", "-of", "json", Input(input)], cancellationToken);
        return ParseProbe(result);
    }

    public static MediaProbe ParseProbe(string json)
    {
        using var doc = JsonDocument.Parse(json);
        double? duration = null, frameRate = null; int? width = null, height = null; string? video = null, audio = null, container = null; long? bitRate = null, videoBitRate = null; var hdr = false;
        if (doc.RootElement.TryGetProperty("format", out var format))
        {
            if (format.TryGetProperty("duration", out var d)) duration = Number(d);
            if (format.TryGetProperty("bit_rate", out var b) && Number(b) is { } rate) bitRate = (long)rate;
            if (format.TryGetProperty("format_name", out var f)) container = f.GetString();
        }
        if (doc.RootElement.TryGetProperty("streams", out var streams))
        {
            foreach (var stream in streams.EnumerateArray())
            {
                var type = stream.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                var codec = stream.TryGetProperty("codec_name", out var c) ? c.GetString() : null;
                if (type == "video" && video is null)
                {
                    video = codec;
                    if (stream.TryGetProperty("width", out var w) && w.TryGetInt32(out var wi)) width = wi;
                    if (stream.TryGetProperty("height", out var h) && h.TryGetInt32(out var hi)) height = hi;
                    frameRate = Fraction(stream, "avg_frame_rate") ?? Fraction(stream, "r_frame_rate");
                    if (stream.TryGetProperty("bit_rate", out var vb) && Number(vb) is { } streamRate) videoBitRate = (long)streamRate;
                    // PQ (HDR10, Dolby Vision) and HLG are the HDR transfers.
                    hdr = stream.TryGetProperty("color_transfer", out var transfer) && transfer.GetString() is "smpte2084" or "arib-std-b67";
                }
                if (type == "audio" && audio is null) audio = codec;
                if (duration is null && stream.TryGetProperty("duration", out var sd)) duration = Number(sd);
            }
        }
        var technical = video is null && audio is null ? null
            : new TechnicalInfo { VideoCodec = video, AudioCodec = audio, FrameRate = frameRate, BitRate = bitRate ?? videoBitRate, Hdr = hdr, Container = container };
        return new(duration, width, height, video, audio, json, technical);
    }

    /// <summary>A rate such as "30000/1001"; null for "0/0" and cover pictures (mjpeg with 90000/1).</summary>
    private static double? Fraction(JsonElement stream, string name)
    {
        if (!stream.TryGetProperty(name, out var value) || value.GetString() is not { } text) return null;
        var parts = text.Split('/');
        if (parts.Length != 2 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var top) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var bottom) || bottom <= 0) return null;
        var rate = top / bottom;
        return rate is > 0 and <= 1000 ? Math.Round(rate, 3) : null;
    }

    private static double? Number(JsonElement value) => double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) && number >= 0 ? number : null;

    public async Task<string> CreateThumbnailAsync(string input, string output, double seconds = 0, CancellationToken cancellationToken = default)
    {
        ValidateTime(seconds, nameof(seconds));
        string[] render = ["-frames:v", "1", "-vf", "scale=480:480:force_original_aspect_ratio=decrease", "-update", "1"];
        try { return await RenderAsync(input, output, ["-ss", F(seconds)], render, cancellationToken); }
        catch (NoRenderedMediaException) when (seconds > 0)
        {
            // Short transport streams may lack a keyframe after fast demuxer seeking.
            // Decode from the beginning only when that fast path produced no frame.
            return await RenderAsync(input,output,[],["-ss",F(seconds),..render],cancellationToken);
        }
    }

    /// <summary>A grid of evenly spaced frames (columns × rows), like the original's video thumbnails.
    /// Every frame uses its own fast input seek, so long videos are not decoded completely.</summary>
    public Task<string> CreateContactSheetAsync(string input, string output, double durationSeconds, int columns, int rows, int width, double aspectRatio = 16d / 9, CancellationToken cancellationToken = default) =>
        CreateContactSheetAsync(input, output, durationSeconds, aspectRatio, new ContactSheetStyle { Columns = columns, Rows = rows, Width = width, FastSeek = false }, cancellationToken);

    public Task<string> DecodeImageAsync(string input, string output, CancellationToken cancellationToken = default) =>
        RenderAsync(input, output, [], ["-frames:v", "1", "-update", "1"], cancellationToken);

    public async Task ConvertAsync(string input, string output, CancellationToken cancellationToken = default) =>
        _ = await RenderAsync(input, output, [], ["-map", "0:v:0?", "-map", "0:a:0?", "-c:v", "libx264", "-preset", "medium", "-crf", "20", "-c:a", "aac", "-movflags", "+faststart"], cancellationToken);

    public async Task CreateClipAsync(string input, string output, double startSeconds, double durationSeconds, CancellationToken cancellationToken = default, bool muteAudio = false)
    {
        ValidateTime(startSeconds, nameof(startSeconds)); ValidateTime(durationSeconds, nameof(durationSeconds), positive: true);
        string[] audio = muteAudio ? ["-an"] : ["-map", "0:a:0?", "-c:a", "aac"];
        _ = await RenderAsync(input, output, ["-ss", F(startSeconds)], ["-t", F(durationSeconds), "-map", "0:v:0?", ..audio, "-c:v", "libx264", "-crf", "20", "-movflags", "+faststart"], cancellationToken);
    }

    public async Task CreateGifAsync(string input, string output, double startSeconds, double durationSeconds, CancellationToken cancellationToken = default)
    {
        ValidateTime(startSeconds, nameof(startSeconds)); ValidateTime(durationSeconds, nameof(durationSeconds), positive: true);
        _ = await RenderAsync(input, output, ["-ss", F(startSeconds)], ["-t", F(durationSeconds), "-filter_complex", "fps=12,scale=640:-1:flags=lanczos,split[a][b];[a]palettegen[p];[b][p]paletteuse", "-loop", "0"], cancellationToken);
    }

    public async Task CropAsync(string input, string output, int x, int y, int width, int height, CancellationToken cancellationToken = default)
    {
        FileAccessPolicy.Current.EnsureWritable(output);
        if (x < 0 || y < 0 || width <= 0 || height <= 0 || width % 2 != 0 || height % 2 != 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Der Bildausschnitt benötigt positive gerade Maße und nichtnegative Koordinaten.");
        var info = await ProbeAsync(input, cancellationToken);
        if (info.Width is null || info.Height is null || (long)x + width > info.Width || (long)y + height > info.Height)
            throw new ArgumentException("Der Bildausschnitt liegt außerhalb des Videobildes.");
        _ = await RenderAsync(input, output, [], ["-vf", $"crop={width}:{height}:{x}:{y}", "-c:v", "libx264", "-crf", "20", "-c:a", "aac", "-movflags", "+faststart"], cancellationToken);
    }

    public async Task CombineAsync(IEnumerable<string> inputs, string output, CancellationToken cancellationToken = default, bool muteAudio = false)
    {
        FileAccessPolicy.Current.EnsureWritable(output);
        var sources = inputs.Select(Input).ToArray();
        if (sources.Length < 2) throw new ArgumentException("Zum Kombinieren mindestens zwei Videos wählen.", nameof(inputs));
        var target = Path.GetFullPath(output);
        FileAccessPolicy.Current.EnsureWritable(target);
        if (sources.Any(x => string.Equals(x, target, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Eingabe und Ausgabe müssen verschieden sein.", nameof(output));
        if (File.Exists(target)) throw new IOException("Die Ausgabedatei existiert bereits.");
        MediaProbe? previous = null;
        foreach (var source in sources)
        {
            var probe = await ProbeAsync(source, cancellationToken);
            if (probe.VideoCodec is null) throw new ArgumentException("Die Kombination erwartet Videodateien.", nameof(inputs));
            if (previous is not null && (previous.Width != probe.Width || previous.Height != probe.Height || previous.VideoCodec != probe.VideoCodec || !muteAudio && previous.AudioCodec != probe.AudioCodec))
                throw new ArgumentException("Zum verlustfreien Kombinieren müssen Bildmaße und Video-/Audiocodecs übereinstimmen. Unterschiedliche Dateien vorher konvertieren.", nameof(inputs));
            previous = probe;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var work = Path.Combine(Path.GetDirectoryName(target)!, ".flixypix-combine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var list = Path.Combine(work, "inputs.ffconcat"); var rendered = Path.Combine(work, "combined" + Path.GetExtension(target));
        try
        {
            await File.WriteAllLinesAsync(list, new[] { "ffconcat version 1.0" }.Concat(sources.Select(p => "file '" + p.Replace('\\', '/').Replace("'", "'\\''") + "'")), new UTF8Encoding(false), cancellationToken);
            await RunAsync(Resolve(paths.Ffmpeg, "ffmpeg"), ["-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-f", "concat", "-safe", "0", "-i", list, "-c", "copy", ..(muteAudio ? new[] { "-an" } : []), rendered], cancellationToken);
            if (!File.Exists(rendered) || new FileInfo(rendered).Length == 0) throw new IOException("Keine kombinierte Datei erzeugt.");
            FileAccessPolicy.Current.EnsureWritable(target); File.Move(rendered, target, overwrite: false);
        }
        finally
        {
            if (File.Exists(rendered)) File.Delete(rendered);
            if (File.Exists(list)) File.Delete(list);
            Directory.Delete(work, recursive: false);
        }
    }

    private async Task<string> RenderAsync(string input, string output, string[] before, string[] after, CancellationToken token)
    {
        var target = Path.GetFullPath(output);
        FileAccessPolicy.Current.EnsureWritable(target);
        var source = Input(input);
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Eingabe und Ausgabe müssen verschieden sein.", nameof(output));
        if (File.Exists(target)) throw new IOException("Die Ausgabedatei existiert bereits. Bitte einen neuen Namen wählen.");
        if (string.IsNullOrEmpty(Path.GetExtension(target))) throw new ArgumentException("Die Ausgabedatei benötigt eine Dateiendung.", nameof(output));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = Path.Combine(Path.GetDirectoryName(target)!, "." + Path.GetFileNameWithoutExtension(target) + "." + Guid.NewGuid().ToString("N") + Path.GetExtension(target));
        try
        {
            await RunAsync(Resolve(paths.Ffmpeg, "ffmpeg"), ["-hide_banner", "-loglevel", "error", "-nostdin", "-n", ..before, "-i", source, ..after, temporary], token);
            if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0) throw new NoRenderedMediaException();
            FileAccessPolicy.Current.EnsureWritable(target); File.Move(temporary, target, overwrite: false);
            return target;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>A tool ran past its time limit and was stopped. It is a file error, not a cancellation by the user, so a
    /// batch skips the file and goes on.</summary>
    public sealed class MediaToolTimeoutException(string message) : IOException(message);

    private sealed class NoRenderedMediaException : IOException
    { public NoRenderedMediaException() : base("Das Medienwerkzeug hat keine Ausgabedatei erzeugt.") { } }

    private static void ValidateTime(double value, string name, bool positive = false)
    {
        if (!double.IsFinite(value) || value < 0 || (positive && value == 0)) throw new ArgumentOutOfRangeException(name, "Zeitangaben müssen endlich und positiv beziehungsweise null sein.");
    }
    private static string F(double value) => value.ToString("0.#########", CultureInfo.InvariantCulture);

    /// <summary>Mouse buttons and wheel of the video window that are reported to FlixyPix instead of mpv's own bindings.</summary>
    public static readonly string[] ReportedMouseInputs = ["MBTN_LEFT", "MBTN_LEFT_DBL", "MBTN_RIGHT", "MBTN_MID", "MBTN_BACK", "MBTN_FORWARD", "WHEEL_UP", "WHEEL_DOWN", "Ctrl+WHEEL_UP", "Ctrl+WHEEL_DOWN", "Shift+WHEEL_UP", "Shift+WHEEL_DOWN", "Alt+WHEEL_UP", "Alt+WHEEL_DOWN"];
    public const string MouseMessage = "flixypix-mouse";

    /// <param name="reportMouse">Mouse input in the video window arrives as client messages (<see cref="MpvPlayer.ClientMessage"/>) with
    /// <see cref="MouseMessage"/> and the input name, so the app applies its own mouse settings.</param>
    public async Task<MpvPlayer> PlayAsync(string input, double startSeconds = 0, nint windowHandle = 0, CancellationToken cancellationToken = default, bool hardwareDecoding = false, bool reportMouse = false)
    {
        ValidateTime(startSeconds, nameof(startSeconds));
        var pipeName = "flixypix-" + Guid.NewGuid().ToString("N");
        var arguments = new List<string> { "--no-config", "--terminal=no", "--idle=yes", "--keep-open=yes", "--pause=yes", "--input-ipc-server=" + @"\\.\pipe\" + pipeName, "--start=" + F(startSeconds) };
        string? inputConf = null;
        if (reportMouse)
        {
            inputConf = Path.Combine(Path.GetTempPath(), "flixypix-input-" + Guid.NewGuid().ToString("N") + ".conf");
            await File.WriteAllLinesAsync(inputConf, ReportedMouseInputs.Select(key => $"{key} script-message {MouseMessage} {key}"), cancellationToken);
            arguments.Add("--input-conf=" + inputConf);
        }
        if (paths.HeadlessPlayback) arguments.AddRange(["--vo=null", "--ao=null", "--force-window=no"]);
        if (hardwareDecoding) arguments.Add("--hwdec=auto-safe");
        if (Path.GetExtension(input).ToLowerInvariant() is ".ts" or ".mts" or ".m2ts")
            arguments.Add("--hr-seek-demuxer-offset=1.5");
        if (windowHandle != 0) arguments.Add("--wid=" + unchecked((uint)windowHandle.ToInt64()).ToString(CultureInfo.InvariantCulture));
        arguments.Add("--"); arguments.Add(Input(input));
        var start = StartInfo(Resolve(paths.Mpv, "mpv"), arguments);
        var process = Process.Start(start) ?? throw new InvalidOperationException("Der Medienplayer konnte nicht gestartet werden.");
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await pipe.ConnectAsync(timeout.Token);
            var player = new MpvPlayer(process, pipe, inputConf);
            try { await player.WaitUntilLoadedAsync(timeout.Token); await player.PauseAsync(false); return player; }
            catch { await player.DisposeAsync(); throw; }
        }
        catch
        {
            pipe.Dispose();
            if (inputConf != null) try { File.Delete(inputConf); } catch (IOException) { }
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            process.Dispose(); throw;
        }
    }

    private static ProcessStartInfo StartInfo(string executable, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    internal static async Task<string> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken cancellationToken, TimeSpan? limit = null)
    {
        var info = StartInfo(executable, arguments); info.RedirectStandardOutput = true; info.RedirectStandardError = true;
        using var process = new Process { StartInfo = info };
        process.Start();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit ?? TimeSpan.FromMinutes(30));
        using var kill = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        var stdout = ReadBoundedAsync(process.StandardOutput, 8 * 1024 * 1024, timeout.Token);
        var stderr = ReadBoundedAsync(process.StandardError, 128 * 1024, timeout.Token);
        try
        {
            // The kill on time-out may end the process before the wait sees the token; both count as a time-out.
            try { await process.WaitForExitAsync(timeout.Token); } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            if (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                throw new MediaToolTimeoutException($"{Path.GetFileName(executable)} brauchte länger als {(limit ?? TimeSpan.FromMinutes(30)).TotalMinutes:0.#} Minuten und wurde beendet.");
            cancellationToken.ThrowIfCancellationRequested();
            var result = await stdout; var error = await stderr;
            if (process.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(executable)} wurde mit Fehler {process.ExitCode} beendet: {error[..Math.Min(error.Length, 1800)]}");
            return result;
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            try { await Task.WhenAll(stdout, stderr); } catch (OperationCanceledException) { }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken token)
    {
        var text = new StringBuilder(); var buffer = new char[4096]; int read;
        while ((read = await reader.ReadAsync(buffer, token)) != 0)
            if (text.Length < limit) text.Append(buffer, 0, Math.Min(read, limit - text.Length));
        return text.ToString();
    }
}
