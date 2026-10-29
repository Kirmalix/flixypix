using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

public enum BatchFileAction { Copy, Move, Rename }
public enum BatchCollisionPolicy { Cancel, Skip, UniqueName }
public enum BatchFileItemState { Ready, Skipped, Blocked }
public enum BatchFileOutcome { Completed, Skipped, Failed }
public sealed record BatchFilePlanItem(Guid MediaId,string SourcePath,string TargetPath,long Length,DateTime LastWriteTimeUtc,
    string Sha256,BatchFileItemState State,string? Note = null);
public sealed record BatchFilePlan(BatchFileAction Action,BatchCollisionPolicy CollisionPolicy,IReadOnlyList<BatchFilePlanItem> Items)
{
    public bool CanExecute => Items.Any(item => item.State == BatchFileItemState.Ready) && Items.All(item => item.State != BatchFileItemState.Blocked);
}
public sealed record BatchFileItemResult(Guid MediaId,string SourcePath,string TargetPath,BatchFileOutcome Outcome,string? Message = null);
public sealed record BatchFileResult(IReadOnlyList<BatchFileItemResult> Items,bool Cancelled);
public sealed record BatchFileProgress(int Completed,int Total,string Path);

public sealed partial class CatalogRepository
{
    /// <summary>Read-only preview. Rename templates support {name}, {ext} and {index} (four digits).</summary>
    public async Task<BatchFilePlan> PlanFileBatchAsync(IEnumerable<Guid> mediaIds,BatchFileAction action,string? destinationDirectory,
        BatchCollisionPolicy collisionPolicy = BatchCollisionPolicy.Cancel,string? renameTemplate = null,
        CancellationToken cancellationToken = default,IProgress<BatchFileProgress>? progress = null)
    {
        if (!Enum.IsDefined(action) || !Enum.IsDefined(collisionPolicy)) throw new ArgumentException("Unbekannte Dateiaktion.");
        var ids = mediaIds.Distinct().Take(100_001).ToArray();
        if (ids.Length is 0 or > 100_000) throw new ArgumentException(T("Please select 1–100,000 items."));
        string? folder = null;
        if (action != BatchFileAction.Rename)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory); folder = Path.GetFullPath(destinationDirectory); FileAccessPolicy.Current.EnsureWritable(folder); RejectBatchLinks(folder);
            if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Der Zielordner fehlt.");
        }
        if (action == BatchFileAction.Rename && string.IsNullOrWhiteSpace(renameTemplate)) throw new ArgumentException(T("A template for the new file names is missing."));
        var planned = new List<BatchFilePlanItem>(); var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < ids.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested(); var item = GetMedia(ids[index]);
            if (item == null) { planned.Add(new(ids[index],"","",0,default,"",BatchFileItemState.Skipped,T("The item is missing in the catalog."))); continue; }
            if (item.Extra.ContainsKey("archivePath")) { planned.Add(new(item.Id,item.Path,"",0,default,"",BatchFileItemState.Skipped,T("Images from archives remain read-only cache entries."))); continue; }
            var source = Path.GetFullPath(item.Path); var name = Path.GetFileName(source);
            if (action == BatchFileAction.Rename) name = renameTemplate!.Replace("{name}",Path.GetFileNameWithoutExtension(source),StringComparison.Ordinal)
                .Replace("{ext}",Path.GetExtension(source),StringComparison.Ordinal).Replace("{index}",(index + 1).ToString("D4"),StringComparison.Ordinal);
            ValidateBatchFileName(name);
            var target = Path.Combine(folder ?? Path.GetDirectoryName(source)!,name);
            try { FileAccessPolicy.Current.EnsureWritable(target); if(action!=BatchFileAction.Copy)FileAccessPolicy.Current.EnsureWritable(source); }
            catch(UnauthorizedAccessException error) { planned.Add(new(item.Id,source,target,0,default,"",BatchFileItemState.Blocked,error.Message)); continue; }
            RejectBatchLinks(source); RejectBatchLinks(target);
            if (!File.Exists(source)) { planned.Add(new(item.Id,source,target,0,default,"",BatchFileItemState.Skipped,"Quelldatei fehlt.")); continue; }
            if (source.Equals(target,StringComparison.OrdinalIgnoreCase)) { planned.Add(new(item.Id,source,target,0,default,"",BatchFileItemState.Skipped,T("Source and target are identical."))); continue; }
            string? note = null;
            if (File.Exists(target) || Directory.Exists(target) || reserved.Contains(target) || FindPath(target) is not null)
            {
                if (collisionPolicy != BatchCollisionPolicy.UniqueName)
                {
                    planned.Add(new(item.Id,source,target,0,default,"",collisionPolicy == BatchCollisionPolicy.Skip ? BatchFileItemState.Skipped : BatchFileItemState.Blocked,T("The target name is already taken."))); continue;
                }
                var original = target; var suffix = 2;
                do { target = Path.Combine(Path.GetDirectoryName(original)!,Path.GetFileNameWithoutExtension(original) + $" ({suffix++})" + Path.GetExtension(original)); }
                while (File.Exists(target) || Directory.Exists(target) || reserved.Contains(target) || FindPath(target) is not null);
                note = "Namenskollision: neuer eindeutiger Zielname.";
            }
            reserved.Add(target);
            var info = new FileInfo(source); var length = info.Length; var write = info.LastWriteTimeUtc;
            await using var input = new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read,81920,true);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input,cancellationToken));
            info.Refresh();
            if (info.Length != length || info.LastWriteTimeUtc != write) throw new IOException(T("The source was changed during the preview."));
            planned.Add(new(item.Id,source,target,length,write,hash,BatchFileItemState.Ready,note));
            progress?.Report(new(index + 1,ids.Length,source));
        }
        return new(action,collisionPolicy,planned.AsReadOnly());
    }

    /// <summary>Cancellation stops between files (or aborts an unfinished copy). Completed entries are retained and reported.</summary>
    public async Task<BatchFileResult> ExecuteFileBatchAsync(BatchFilePlan plan,CancellationToken cancellationToken = default,
        IProgress<BatchFileProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.CanExecute || !Enum.IsDefined(plan.Action) || !Enum.IsDefined(plan.CollisionPolicy)) throw new InvalidOperationException(T("The file plan is blocked or contains no executable entries."));
        if (plan.Items.Select(item => item.MediaId).Distinct().Count() != plan.Items.Count ||
            plan.Items.Where(item => item.State == BatchFileItemState.Ready).Select(item => Path.GetFullPath(item.TargetPath)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.Items.Count(item => item.State == BatchFileItemState.Ready))
            throw new ArgumentException(T("The plan contains duplicate items or targets."));
        var results = new List<BatchFileItemResult>(); var cancelled = false;
        foreach (var entry in plan.Items)
        {
            if (cancellationToken.IsCancellationRequested) { cancelled = true; break; }
            if (entry.State != BatchFileItemState.Ready)
            { results.Add(new(entry.MediaId,entry.SourcePath,entry.TargetPath,BatchFileOutcome.Skipped,entry.Note)); continue; }
            try
            {
                await ExecuteBatchItem(plan.Action,entry,cancellationToken);
                results.Add(new(entry.MediaId,entry.SourcePath,entry.TargetPath,BatchFileOutcome.Completed));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { cancelled = true; break; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or SqliteException)
            { results.Add(new(entry.MediaId,entry.SourcePath,entry.TargetPath,BatchFileOutcome.Failed,error.Message)); }
            progress?.Report(new(results.Count,plan.Items.Count,entry.SourcePath));
        }
        return new(results.AsReadOnly(),cancelled);
    }

    private async Task ExecuteBatchItem(BatchFileAction action,BatchFilePlanItem plan,CancellationToken token)
    {
        var source = Path.GetFullPath(plan.SourcePath); var target = Path.GetFullPath(plan.TargetPath);
        FileAccessPolicy.Current.EnsureWritable(target); if(action!=BatchFileAction.Copy)FileAccessPolicy.Current.EnsureWritable(source);
        RejectBatchLinks(source); RejectBatchLinks(target);
        if (source.Equals(target,StringComparison.OrdinalIgnoreCase) || File.Exists(target) || Directory.Exists(target)) throw new IOException(T("The target is now taken or identical to the source."));
        var current = GetMedia(plan.MediaId) ?? throw new InvalidOperationException(T("The item was removed from the catalog."));
        if (!Path.GetFullPath(current.Path).Equals(source,StringComparison.OrdinalIgnoreCase) || current.Extra.ContainsKey("archivePath")) throw new InvalidOperationException(T("The catalog entry changed since the preview."));
        if (FindPath(target) is not null) throw new IOException(T("The target path is now taken in the catalog."));
        var info = new FileInfo(source);
        if (!info.Exists || info.Length != plan.Length || info.LastWriteTimeUtc != plan.LastWriteTimeUtc) throw new IOException(T("The source changed since the preview."));
        // Keep a read-only source handle during copying. The original stays in place until a verified
        // destination and its catalog update both exist; this also works across volumes.
        var temporary = Path.Combine(Path.GetDirectoryName(target)!,".flixypix-file-" + Guid.NewGuid().ToString("N") + ".partial");
        var published = false; var catalogUpdated = false;
        try
        {
            await using (var input = new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read,81920,true))
            {
                var before = Convert.ToHexStringLower(await SHA256.HashDataAsync(input,token));
                if (before != plan.Sha256) throw new IOException(T("The source content changed since the preview."));
                input.Position = 0;
                await using (var output = new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,81920,true))
                {
                    var copied = await CopyBackupStream(input,output,plan.Length,token);
                    if (Convert.ToHexStringLower(copied) != plan.Sha256) throw new IOException(T("The copy does not match the preview."));
                    await output.FlushAsync(token);
                }
            }
            token.ThrowIfCancellationRequested(); FileAccessPolicy.Current.EnsureWritable(target); RejectBatchLinks(target);
            File.Move(temporary,target,false); published = true;
            File.SetLastWriteTimeUtc(target,plan.LastWriteTimeUtc);
            if (action == BatchFileAction.Copy) return;
            // Do not overwrite metadata edited while files were copying: re-read under the repository gate.
            lock (gate)
            {
                current = GetMedia(plan.MediaId) ?? throw new InvalidOperationException(T("The item was removed from the catalog."));
                if (!Path.GetFullPath(current.Path).Equals(source,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(T("The media path has changed in the meantime."));
                Upsert(current with { Path = target, ThumbnailPath = current.ThumbnailPath is { } thumbnail &&
                    Path.GetFullPath(thumbnail).Equals(source,StringComparison.OrdinalIgnoreCase) ? target : current.ThumbnailPath }); catalogUpdated = true;
                try
                {
                    // Check once more before removing the old name; a concurrent writer must not be lost.
                    using var original = new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read | FileShare.Delete);
                    if (original.Length != plan.Length || Convert.ToHexStringLower(SHA256.HashData(original)) != plan.Sha256)
                        throw new IOException(T("The source was changed before moving; both files are kept."));
                    FileAccessPolicy.Current.EnsureWritable(source); File.Delete(source);
                }
                catch
                {
                    try { Upsert(current); catalogUpdated = false; }
                    catch (Exception rollbackError) { throw new IOException(T("The new file and the catalog path are kept; the old file could not be removed. Source: {0}; target: {1}. Reassignment failed: {2}", source, target, rollbackError.Message)); }
                    throw;
                }
            }
        }
        catch
        {
            if (published && !catalogUpdated && File.Exists(target))
            {
                // Only delete our own exact copy; retain any externally changed output for manual recovery.
                try
                {
                    using var check = File.OpenRead(target);
                    if (Convert.ToHexStringLower(SHA256.HashData(check)) == plan.Sha256) { check.Dispose(); File.Delete(target); }
                }
                catch (IOException) { }
            }
            throw;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void ValidateBatchFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.EndsWith('.') || name.EndsWith(' ') || name.Length > 240)
            throw new ArgumentException(T("The template must produce a valid file name without subfolders."));
        var stem = Path.GetFileNameWithoutExtension(name).ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '0' and <= '9')
            throw new ArgumentException("Reservierter Windows-Dateiname.");
    }
    private static void RejectBatchLinks(string path) => RejectBackupLinks(path);
}
