using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlixyPix.MediaTools;

using FlixyPix.Core;

public enum MediaJobKind { Convert, Clip, Gif, Crop, Combine, ScenePreviews, CombineSegments, ExtractAudio }
public enum MediaJobState { Pending, Running, Completed, Failed, Cancelled }
public sealed record MediaJob
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public MediaJobKind Kind { get; init; }
    public string Input { get; init; } = "";
    public List<string> Inputs { get; init; } = [];
    public List<CombineSegment> Segments { get; init; } = [];
    public string Output { get; init; } = "";
    public double StartSeconds { get; init; }
    public double DurationSeconds { get; init; }
    public Guid? MediaId { get; init; }
    public double? EndSeconds { get; init; }
    public int? PreviewCount { get; init; }
    public double? PreviewIntervalSeconds { get; init; }
    public List<Guid> ReplaceAssetIds { get; init; } = [];
    public bool ResultsApplied { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public bool MuteAudio { get; init; }
    public double CrossfadeSeconds { get; init; }
    public MediaJobState State { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; init; }
}

/// <summary>Durable, single-worker queue. The lease prevents two app instances from writing the same queue.</summary>
public sealed class MediaJobQueue : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly string path;
    private readonly MediaToolService tools;
    private readonly object gate = new();
    private readonly SemaphoreSlim runner = new(1, 1);
    private readonly FileStream lease;
    private List<MediaJob> jobs;
    private bool disposed;
    public event EventHandler? Changed;
    public event EventHandler<MediaJobProgress>? ProgressChanged;
    private sealed record Document(string Format, int Version, List<MediaJob> Jobs);

    public MediaJobQueue(string path, MediaToolService tools)
    {
        this.path = Path.GetFullPath(path); this.tools = tools;
        FileAccessPolicy.Current.EnsureWritable(this.path);
        Directory.CreateDirectory(Path.GetDirectoryName(this.path)!);
        lease = new FileStream(this.path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var document = File.Exists(this.path) ? JsonSerializer.Deserialize<Document>(File.ReadAllText(this.path), Json) : null;
            if (File.Exists(this.path) && (document is null || !ProductIdentity.IsFormat(document.Format,"flixypix-media-queue") || document.Version != 1 || document.Jobs is null)) throw new InvalidDataException("Unbekanntes Warteschlangenformat.");
            jobs = document?.Jobs ?? [];
            if (jobs.Any(j => j.Id == Guid.Empty || !Enum.IsDefined(j.Kind) || !Enum.IsDefined(j.State)) || jobs.Select(j => j.Id).Distinct().Count() != jobs.Count) throw new InvalidDataException("Ungültige Warteschlangeneinträge.");
            jobs = jobs.Select(j => j.State == MediaJobState.Running ? j with { State = MediaJobState.Pending, Error = "Vorheriger Durchlauf unterbrochen; erneuter Start erforderlich." } : j).ToList();
            Save();
        }
        catch { lease.Dispose(); throw; }
    }

    public IReadOnlyList<MediaJob> Jobs { get { lock (gate) { ObjectDisposedException.ThrowIf(disposed, this); return jobs.Select(j => j with { Inputs = j.Inputs.ToList(), Segments = j.Segments.ToList(), ReplaceAssetIds = j.ReplaceAssetIds.ToList() }).ToArray(); } } }
    public MediaJob Enqueue(MediaJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Id == Guid.Empty || !Enum.IsDefined(job.Kind) || string.IsNullOrWhiteSpace(job.Output)) throw new ArgumentException("Ein Auftrag benötigt Typ, Kennung und Ausgabedatei.", nameof(job));
        var output = Path.GetFullPath(job.Output);
        FileAccessPolicy.Current.EnsureWritable(output);
        if (job.Kind == MediaJobKind.CombineSegments && (job.Segments.Count is < 2 or > 128 || job.Segments.Any(segment =>
            !double.IsFinite(segment.StartSeconds) || segment.StartSeconds < 0 || segment.EndSeconds is { } end && (!double.IsFinite(end) || end <= segment.StartSeconds))))
            throw new ArgumentException("Ungültige Kombinationsabschnitte.", nameof(job));
        var inputs = job.Kind == MediaJobKind.Combine ? job.Inputs.Select(Path.GetFullPath).ToList() :
            job.Kind == MediaJobKind.CombineSegments ? job.Segments.Select(segment => Path.GetFullPath(segment.Input)).ToList() : [Path.GetFullPath(job.Input)];
        if (inputs.Count == 0 || inputs.Any(p => !File.Exists(p))) throw new FileNotFoundException("Eine Eingabedatei fehlt.");
        if (inputs.Contains(output, StringComparer.OrdinalIgnoreCase) || File.Exists(output) || Directory.Exists(output)) throw new IOException("Ausgabe muss neu und von der Eingabe verschieden sein.");
        if (job.Kind == MediaJobKind.ScenePreviews) ScenePreviewRequest.FromJob(job).Validate();
        if (job.Kind == MediaJobKind.ExtractAudio && !MediaToolService.AudioExtensions.Contains(Path.GetExtension(output).ToLowerInvariant())) throw new ArgumentException("Ton wird als .mp3 oder .m4a gespeichert.", nameof(job));
        if (job.CrossfadeSeconds != 0 && job.Kind != MediaJobKind.CombineSegments) throw new ArgumentException("Überblenden ist nur beim Verbinden mit Abschnitten möglich.", nameof(job));
        new CombineOptions(job.MuteAudio, job.CrossfadeSeconds).Validate(inputs.Count);
        var saved = job with { Input = inputs[0], Inputs = inputs, Segments = job.Segments.Select(segment => segment with {
         Input = Path.GetFullPath(segment.Input) }).ToList(), ReplaceAssetIds = job.ReplaceAssetIds.ToList(), Output = output,
         State = MediaJobState.Pending, Error = null, FinishedAt = null, ResultsApplied = false };
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (jobs.Any(j => j.Id == saved.Id || j.State is MediaJobState.Pending or MediaJobState.Running && j.Output.Equals(output, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Ein Auftrag für diese Ausgabe ist bereits vorhanden.");
            jobs.Add(saved); Save();
        }
        Changed?.Invoke(this, EventArgs.Empty); return saved;
    }

    public void Remove(Guid id)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (jobs.Any(j => j.Id == id && j.State == MediaJobState.Running)) throw new InvalidOperationException("Den laufenden Auftrag zuerst abbrechen.");
            jobs.RemoveAll(j => j.Id == id); Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Retry(Guid id)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var index = jobs.FindIndex(j => j.Id == id); if (index < 0) throw new KeyNotFoundException();
            if (jobs[index].State is not (MediaJobState.Failed or MediaJobState.Cancelled)) throw new InvalidOperationException("Nur fehlgeschlagene oder abgebrochene Aufträge können wiederholt werden.");
            jobs[index] = jobs[index] with { State = MediaJobState.Pending, Error = null, FinishedAt = null }; Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void MarkResultsApplied(Guid id)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var job = jobs.Single(j => j.Id == id);
            if (job.State != MediaJobState.Completed) throw new InvalidOperationException("Auftrag ist noch nicht abgeschlossen.");
            Update(job with { ResultsApplied = true });
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!await runner.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("Die Warteschlange läuft bereits.");
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                MediaJob? next;
                lock (gate) { next = jobs.FirstOrDefault(j => j.State == MediaJobState.Pending); if (next is not null) Update(next with { State = MediaJobState.Running, Error = null }); }
                if (next is null) break;
                Changed?.Invoke(this, EventArgs.Empty);
                try
                {
                    switch (next.Kind)
                    {
                        case MediaJobKind.Convert: await tools.ConvertAsync(next.Input, next.Output, cancellationToken); break;
                        case MediaJobKind.Clip: await tools.CreateClipAsync(next.Input, next.Output, next.StartSeconds, next.DurationSeconds, cancellationToken, next.MuteAudio); break;
                        case MediaJobKind.ExtractAudio: await tools.ExtractAudioAsync(next.Input, next.Output, next.StartSeconds, next.DurationSeconds, cancellationToken); break;
                        case MediaJobKind.Gif: await tools.CreateGifAsync(next.Input, next.Output, next.StartSeconds, next.DurationSeconds, cancellationToken); break;
                        case MediaJobKind.Crop: await tools.CropAsync(next.Input, next.Output, next.X, next.Y, next.Width, next.Height, cancellationToken); break;
                        case MediaJobKind.Combine: await tools.CombineAsync(next.Inputs, next.Output, cancellationToken, next.MuteAudio); break;
                        case MediaJobKind.CombineSegments: await tools.CombineSegmentsAsync(next.Segments, next.Output, cancellationToken, new(next.MuteAudio, next.CrossfadeSeconds)); break;
                        case MediaJobKind.ScenePreviews:
                            var request = ScenePreviewRequest.FromJob(next);
                            await tools.GenerateScenePreviewsAsync(request, next.Output, next.Input,
                                new Progress<int>(completed => ProgressChanged?.Invoke(this, new(next.Id, completed, request.GetPositions().Count))), cancellationToken); break;
                    }
                    lock (gate) Update(next with { State = MediaJobState.Completed, FinishedAt = DateTimeOffset.UtcNow });
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    lock (gate) Update(next with { State = MediaJobState.Cancelled, Error = "Abgebrochen", FinishedAt = DateTimeOffset.UtcNow });
                }
                catch (Exception error)
                {
                    lock (gate) Update(next with { State = MediaJobState.Failed, Error = error.Message, FinishedAt = DateTimeOffset.UtcNow });
                }
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        finally { runner.Release(); }
    }

    private void Update(MediaJob job) { jobs[jobs.FindIndex(j => j.Id == job.Id)] = job; Save(); }
    private void Save()
    {
        FileAccessPolicy.Current.EnsureWritable(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(new Document("flixypix-media-queue", 1, jobs), Json)); File.Move(temporary, path, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            if (runner.CurrentCount == 0) throw new InvalidOperationException("Warteschlange zuerst anhalten und das Ende abwarten.");
            disposed = true; lease.Dispose(); runner.Dispose();
        }
    }
}

public sealed record MediaJobProgress(Guid JobId, int Completed, int Total);
