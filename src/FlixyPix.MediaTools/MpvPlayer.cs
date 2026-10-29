using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace FlixyPix.MediaTools;

public sealed class MpvPlayer : IAsyncDisposable
{
    private readonly Process process;
    private readonly NamedPipeClientStream pipe;
    private readonly StreamReader reader;
    private readonly StreamWriter writer;
    private readonly SemaphoreSlim gate = new(1, 1);
    private long nextId;
    private bool disposed;
    private readonly string? inputConf;
    private Task delivery = Task.CompletedTask;
    // A cancelled StreamReader.ReadLineAsync drops the part of the line it had read, so a timed-out
    // read is left running and the next request continues with it.
    private Task<string?>? pendingLine;
    /// <summary>Arguments of a "script-message" from mpv's bindings, such as mouse input; delivered with the next request.</summary>
    public event Action<IReadOnlyList<string>>? ClientMessage;

    internal MpvPlayer(Process process, NamedPipeClientStream pipe, string? inputConf = null)
    {
        this.process = process; this.pipe = pipe; this.inputConf = inputConf;
        reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
        writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
    }

    private async Task<JsonElement> SendAsync(params object[] command)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await gate.WaitAsync(timeout.Token);
        try
        {
            var id = Interlocked.Increment(ref nextId);
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { command, request_id = id }).AsMemory(), timeout.Token);
            while (true)
            {
                pendingLine ??= reader.ReadLineAsync();
                var line = await pendingLine.WaitAsync(timeout.Token);
                pendingLine = null;
                if (line is null) throw new IOException("Die Verbindung zum Player wurde geschlossen.");
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("event", out var name) && name.GetString() == "client-message" && root.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array)
                {
                    var values = args.EnumerateArray().Select(a => a.GetString() ?? "").ToArray();
                    // Off the pipe lock, but in the order mpv sent them.
                    delivery = delivery.ContinueWith(_ => ClientMessage?.Invoke(values), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                    continue;
                }
                if (!root.TryGetProperty("request_id", out var request) || request.GetInt64() != id) continue;
                if (root.TryGetProperty("error", out var error) && error.GetString() != "success")
                    throw new InvalidOperationException("Player: " + error.GetString());
                return root.TryGetProperty("data", out var data) ? data.Clone() : default;
            }
        }
        finally { gate.Release(); }
    }

    public async Task PauseAsync(bool paused) => _ = await SendAsync("set_property", "pause", paused);
    public async Task SetMuteAsync(bool muted) => _ = await SendAsync("set_property", "mute", muted);
    /// <summary>Steps one frame forward or back; mpv pauses playback for this.</summary>
    /// <summary>Presses a key or mouse button as mpv's input does, so its bindings run (for tests of reported input).</summary>
    public async Task PressAsync(string key) => _ = await SendAsync("keypress", key);
    /// <summary>Saves the current video frame (without subtitles or on-screen display) as an image file.</summary>
    public async Task ScreenshotAsync(string path) => _ = await SendAsync("screenshot-to-file", path, "video");
    public async Task FrameStepAsync(bool forward) => _ = await SendAsync(forward ? "frame-step" : "frame-back-step");
    internal async Task WaitUntilLoadedAsync(CancellationToken token)
    {
        while (!process.HasExited)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (await GetPositionAsync() is not null && (await GetTracksAsync()).Count > 0) return;
            }
            catch (InvalidOperationException) { /* IPC can be ready before the initial file-loaded state. */ }
            try
            {
                // Still images have no playback clock; output parameters prove their frame was initialized.
                if ((await SendAsync("get_property","video-out-params")).ValueKind == JsonValueKind.Object && (await GetTracksAsync()).Count > 0) return;
            }
            catch (InvalidOperationException) { }
            await Task.Delay(20,token);
        }
        throw new IOException("Der Player wurde beendet, bevor das Medium geladen war.");
    }
    public async Task SeekAsync(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        _ = await SendAsync("seek", seconds, "absolute+exact");
    }
    /// <summary>Reads a text property or option, e.g. "hwdec", to confirm how the player was configured.</summary>
    public async Task<string?> GetTextPropertyAsync(string name)
    {
        var result = await SendAsync("get_property", name);
        // Some options (hwdec in current mpv) are string lists; they are reported comma separated.
        return result.ValueKind switch
        {
            JsonValueKind.String => result.GetString(),
            JsonValueKind.Array => string.Join(',', result.EnumerateArray().Select(entry => entry.ToString())),
            _ => null,
        };
    }

    public async Task<double?> GetPositionAsync()
    {
        var result = await SendAsync("get_property", "time-pos");
        return result.ValueKind == JsonValueKind.Number && result.TryGetDouble(out var value) ? value : null;
    }
    public async Task SetVolumeAsync(double volume)
    {
        if (!double.IsFinite(volume) || volume is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(volume));
        _ = await SendAsync("set_property", "volume", volume);
    }
    public async Task SetSpeedAsync(double speed)
    {
        if (!double.IsFinite(speed) || speed is < 0.1 or > 8) throw new ArgumentOutOfRangeException(nameof(speed));
        _ = await SendAsync("set_property", "speed", speed);
    }
    public async Task SelectAudioTrackAsync(int? id)
    {
        if (id is <= 0) throw new ArgumentOutOfRangeException(nameof(id));
        _ = await SendAsync("set_property", "aid", id.HasValue ? (object)id.Value : "no");
    }
    public async Task SelectSubtitleTrackAsync(int? id)
    {
        if (id is <= 0) throw new ArgumentOutOfRangeException(nameof(id));
        _ = await SendAsync("set_property", "sid", id.HasValue ? (object)id.Value : "no");
    }
    /// <summary>Adds a subtitle file as a track and selects it.</summary>
    public async Task AddSubtitleAsync(string path, string? title = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _ = await SendAsync("sub-add", path, "select", title ?? System.IO.Path.GetFileName(path));
    }
    /// <summary>Positive values show the subtitles later.</summary>
    public async Task SetSubtitleDelayAsync(double seconds)
    {
        if (!double.IsFinite(seconds) || Math.Abs(seconds) > 3600) throw new ArgumentOutOfRangeException(nameof(seconds));
        _ = await SendAsync("set_property", "sub-delay", seconds);
    }
    public async Task<double?> GetSubtitleDelayAsync()
    {
        var result = await SendAsync("get_property", "sub-delay");
        return result.ValueKind == JsonValueKind.Number && result.TryGetDouble(out var value) ? value : null;
    }
    public async Task<IReadOnlyList<MpvTrack>> GetTracksAsync()
    {
        var result = await SendAsync("get_property", "track-list");
        if (result.ValueKind != JsonValueKind.Array) return [];
        return result.EnumerateArray().Where(track => track.TryGetProperty("id", out _) && track.TryGetProperty("type", out _))
            .Select(track => new MpvTrack(track.GetProperty("id").GetInt32(), track.GetProperty("type").GetString() ?? "",
                track.TryGetProperty("title", out var title) ? title.GetString() : null,
                track.TryGetProperty("lang", out var lang) ? lang.GetString() : null,
                track.TryGetProperty("selected", out var selected) && selected.ValueKind == JsonValueKind.True)).ToArray();
    }
    public async Task StopAsync()
    {
        if (!disposed && !process.HasExited) _ = await SendAsync("stop");
    }

    public async Task WaitForEndAsync(CancellationToken cancellationToken = default)
    {
        while (!disposed && !process.HasExited)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var end = await SendAsync("get_property", "eof-reached");
                if (end.ValueKind == JsonValueKind.True) return;
            }
            catch (IOException) when (process.HasExited) { return; }
            catch (InvalidOperationException) when (process.HasExited) { return; }
            await Task.Delay(250, cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        try
        {
            if (!process.HasExited)
            {
                try { await SendAsync("quit"); } catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException or JsonException) { }
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await process.WaitForExitAsync(timeout.Token); } catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            }
        }
        finally
        {
            disposed = true;
            try { writer.Dispose(); } catch (IOException) { /* mpv may close its pipe before buffered writer cleanup. */ }
            finally
            {
                reader.Dispose(); pipe.Dispose(); process.Dispose(); gate.Dispose();
                if (inputConf != null) try { File.Delete(inputConf); } catch (IOException) { }
            }
        }
    }
}

public sealed record MpvTrack(int Id, string Type, string? Title, string? Language, bool Selected);
