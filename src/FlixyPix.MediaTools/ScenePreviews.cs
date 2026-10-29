using System.Security.Cryptography;
using System.Text.Json;
using FlixyPix.Core;

namespace FlixyPix.MediaTools;

public sealed record ScenePreviewRequest(Guid JobId, double StartSeconds, double EndSeconds, int? Count = null, double? IntervalSeconds = null)
{
    public static ScenePreviewRequest FromJob(MediaJob job) => new(job.Id, job.StartSeconds,
        job.EndSeconds ?? throw new ArgumentException("Ein Endzeitpunkt fehlt."), job.PreviewCount, job.PreviewIntervalSeconds);

    public void Validate()
    {
        if (JobId == Guid.Empty || !double.IsFinite(StartSeconds) || !double.IsFinite(EndSeconds) || StartSeconds < 0 || EndSeconds <= StartSeconds)
            throw new ArgumentException("Start und Ende müssen einen endlichen Zeitbereich bilden.");
        if (Count.HasValue == IntervalSeconds.HasValue || Count is <= 0 or > 1000 ||
            IntervalSeconds is { } interval && (!double.IsFinite(interval) || interval <= 0))
            throw new ArgumentException("Entweder 1–1000 Bilder oder ein positives Intervall angeben.");
        if (IntervalSeconds.HasValue && Math.Ceiling((EndSeconds - StartSeconds) / IntervalSeconds.Value) > 1000)
            throw new ArgumentException("Der Bereich würde mehr als 1000 Bilder erzeugen.");
    }

    public IReadOnlyList<double> GetPositions()
    {
        Validate();
        var count = Count ?? (int)Math.Ceiling((EndSeconds - StartSeconds) / IntervalSeconds!.Value);
        // Half-open interval avoids requesting an absent frame precisely at EOF.
        return Enumerable.Range(0, count).Select(index => StartSeconds + index *
            (IntervalSeconds ?? ((EndSeconds - StartSeconds) / count))).Where(time => time < EndSeconds).ToArray();
    }
}

public sealed record ScenePreviewFrame(int Index, double Seconds, string FileName, string Sha256);
public sealed record ScenePreviewManifest(string Format, int Version, ScenePreviewRequest Request, string SourceSha256, List<ScenePreviewFrame> Frames);

public sealed partial class MediaToolService
{
    private const string SceneManifestFile = "scene-previews.json";
    public async Task<ScenePreviewManifest> GenerateScenePreviewsAsync(ScenePreviewRequest request, string outputDirectory,
        string input, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        request.Validate(); var target = Path.GetFullPath(outputDirectory);
        FileAccessPolicy.Current.EnsureWritable(target);
        var source = Input(input);
        cancellationToken.ThrowIfCancellationRequested();
        var before = new FileInfo(source); var originalLength = before.Length; var originalWrite = before.LastWriteTimeUtc;
        var sourceHash = await HashFile(source, cancellationToken);
        if (Directory.Exists(target))
        {
            var existing = ReadScenePreviewManifest(target);
            if (existing.Request != request || !existing.SourceSha256.Equals(sourceHash, StringComparison.Ordinal))
                throw new IOException("Der Ausgabeordner gehört zu einem anderen Auftrag oder einer geänderten Quelle.");
            progress?.Report(existing.Frames.Count); return existing;
        }
        if (File.Exists(target)) throw new IOException("Die Ausgabe existiert bereits.");
        var probe = await ProbeAsync(source, cancellationToken);
        if (probe.VideoCodec is null || probe.DurationSeconds is not { } duration || request.EndSeconds > duration + 0.001)
            throw new ArgumentException("Der Bereich muss innerhalb eines Videos mit bekannter Dauer liegen.");
        var parent = Path.GetDirectoryName(target)!; Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, ".scene-previews-" + request.JobId.ToString("N") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var frames = new List<ScenePreviewFrame>();
            foreach (var time in request.GetPositions())
            {
                cancellationToken.ThrowIfCancellationRequested(); var name = $"frame-{frames.Count:D4}.png";
                var path = Path.Combine(temporary, name);
                await CreateThumbnailAsync(source, path, time, cancellationToken);
                frames.Add(new(frames.Count, time, name, await HashFile(path, cancellationToken)));
                progress?.Report(frames.Count);
            }
            var after = new FileInfo(source);
            if (after.Length != originalLength || after.LastWriteTimeUtc != originalWrite || await HashFile(source, cancellationToken) != sourceHash)
                throw new IOException("Die Quelle wurde während der Generierung verändert.");
            var manifest = new ScenePreviewManifest("flixypix-scene-previews", 1, request, sourceHash, frames);
            await File.WriteAllTextAsync(Path.Combine(temporary, SceneManifestFile), JsonSerializer.Serialize(manifest), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested(); FileAccessPolicy.Current.EnsureWritable(target); Directory.Move(temporary, target);
            return manifest;
        }
        finally
        {
            if (Directory.Exists(temporary))
            {
                // Delete only the files created in this unique, owned work directory.
                foreach (var file in Directory.EnumerateFiles(temporary)) File.Delete(file);
                Directory.Delete(temporary);
            }
        }
    }

    public static ScenePreviewManifest ReadScenePreviewManifest(string outputDirectory)
    {
        var directory = Path.GetFullPath(outputDirectory); var manifestPath = Path.Combine(directory, SceneManifestFile);
        if (new FileInfo(manifestPath).Length > 1024 * 1024) throw new InvalidDataException("Das Vorschau-Manifest ist zu groß.");
        var manifest = JsonSerializer.Deserialize<ScenePreviewManifest>(File.ReadAllText(manifestPath)) ?? throw new InvalidDataException("Manifest fehlt.");
        if (!ProductIdentity.IsFormat(manifest.Format,"flixypix-scene-previews") || manifest.Version != 1 || manifest.Frames is null) throw new InvalidDataException("Unbekanntes Vorschauformat.");
        manifest.Request.Validate(); var positions = manifest.Request.GetPositions();
        if (manifest.Frames.Count != positions.Count) throw new InvalidDataException("Unvollständige Szenenvorschauen.");
        for (var index = 0; index < positions.Count; index++)
        {
            var frame = manifest.Frames[index];
            if (frame.Index != index || frame.Seconds != positions[index] || frame.FileName != $"frame-{index:D4}.png") throw new InvalidDataException("Ungültige Vorschauposition.");
            var file = Path.Combine(directory, frame.FileName); var info = new FileInfo(file);
            if (!info.Exists || info.Length is 0 or > 32 * 1024 * 1024 ||
                Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))) != frame.Sha256)
                throw new InvalidDataException("Eine Szenenvorschau fehlt oder wurde verändert.");
        }
        return manifest;
    }

    private static async Task<string> HashFile(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
    }
}
