namespace FlixyPix.Core;

/// <summary>A probe result bound to the file inspected before the asynchronous work began.</summary>
public sealed record TechnicalAnalysis(MediaFile Expected, MediaFile Values, long SourceSize, DateTime SourceModifiedUtc);

public sealed partial class CatalogRepository
{
    public bool ApplyTechnicalAnalysis(Guid mediaId, IReadOnlyList<TechnicalAnalysis> results)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            var current = ReadMediaDocument(mediaId, transaction);
            if (current is null) return false;
            MediaFile Apply(MediaFile file)
            {
                var result = results.FirstOrDefault(r => PathKey(r.Expected.Path) == PathKey(file.Path));
                if (result is null || PathKey(result.Values.Path) != PathKey(file.Path)) return file;
                var source = new FileInfo(file.Path);
                if (!source.Exists || source.Length != result.SourceSize || source.LastWriteTimeUtc != result.SourceModifiedUtc ||
                    file.FileModifiedAtUtc != result.Expected.FileModifiedAtUtc) return file;
                var expected = result.Expected; var values = result.Values;
                return file with
                {
                    Technical = file.Technical == expected.Technical ? values.Technical : file.Technical,
                    Width = file.Width == expected.Width ? values.Width : file.Width,
                    Height = file.Height == expected.Height ? values.Height : file.Height,
                    DurationSeconds = file.DurationSeconds == expected.DurationSeconds ? values.DurationSeconds : file.DurationSeconds,
                    FileSize = file.FileSize == expected.FileSize ? values.FileSize : file.FileSize,
                };
            }
            var main = Apply(MediaVersions.Main(current));
            var files = current.Files.Select(Apply).ToList();
            if (main == MediaVersions.Main(current) && files.SequenceEqual(current.Files)) return false;
            UpsertCore(current with { Technical = main.Technical, Width = main.Width, Height = main.Height,
                DurationSeconds = main.DurationSeconds, FileSize = main.FileSize, Files = files }, transaction);
            transaction.Commit();
            return true;
        }
    }

    public bool RestoreThumbnail(MediaItem expected, string path)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            var current = ReadMediaDocument(expected.Id, transaction);
            if (current is null || current.ThumbnailPath != expected.ThumbnailPath ||
                current.Extra.GetValueOrDefault("thumbnail.assetId") != expected.Extra.GetValueOrDefault("thumbnail.assetId")) return false;
            UpsertCore(current with { ThumbnailPath = path }, transaction);
            transaction.Commit();
            return true;
        }
    }
}
