using System.Security.Cryptography;

namespace FlixyPix.Core;

public sealed record DuplicateGroup(string Sha256, long FileSize, IReadOnlyList<MediaItem> Items);
public sealed record DuplicateScanProgress(string Path, int Processed, int Total);
public sealed record DuplicateScanResult(IReadOnlyList<DuplicateGroup> Groups, IReadOnlyList<string> Errors);

/// <summary>Read-only content comparison. Only equal-size candidate files are hashed.</summary>
public static class DuplicateFinder
{
    public static async Task<DuplicateScanResult> FindAsync(IEnumerable<MediaItem> items,
        IProgress<DuplicateScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        var errors = new List<string>();
        var candidates = new List<(MediaItem Item, long Size, DateTime Modified)>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!paths.Add(CatalogRepository.PathKey(item.Path))) continue;
                var info = new FileInfo(item.Path);
                candidates.Add((item, info.Length, info.LastWriteTimeUtc));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { errors.Add($"{item.Path}: {ex.Message}"); }
        }
        var potential = candidates.GroupBy(x => x.Size).Where(x => x.Count() > 1).SelectMany(x => x).ToArray();
        var hashes = new Dictionary<(long Size, string Hash), List<MediaItem>>();
        var processed = 0;
        foreach (var (item, size, modified) in potential)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
                var current = new FileInfo(item.Path);
                if (current.Length != size || current.LastWriteTimeUtc != modified)
                    throw new IOException("File changed during duplicate scan; result discarded.");
                var key = (size, hash);
                if (!hashes.TryGetValue(key, out var group)) hashes.Add(key, group = []);
                group.Add(item);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { errors.Add($"{item.Path}: {ex.Message}"); }
            progress?.Report(new DuplicateScanProgress(item.Path, ++processed, potential.Length));
        }
        return new DuplicateScanResult(hashes.Where(x => x.Value.Count > 1)
            .Select(x => new DuplicateGroup(x.Key.Hash, x.Key.Size, x.Value.ToArray())).ToArray(), errors);
    }
}
