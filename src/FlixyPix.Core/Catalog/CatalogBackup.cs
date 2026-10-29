using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

public sealed record CatalogBackupResult(string PackagePath, int FileCount, long TotalBytes, IReadOnlyList<string> Warnings);
public sealed record CatalogRestoreResult(string DirectoryPath, string DatabasePath, int RestoredMediaFiles, IReadOnlyList<string> Warnings);
public sealed record CatalogBackupProgress(string Phase, string Name, long CompletedFiles);
/// <summary>Result of <see cref="CatalogRepository.VerifyBackupAsync"/>: media in the catalog, files and bytes checked, media files contained.</summary>
public sealed record CatalogBackupCheck(int Media, int Files, long Bytes, int MediaFiles, IReadOnlyList<string> Warnings);

public sealed partial class CatalogRepository
{
    public const int MaximumBackupEntries = 400_002;
    public const long MaximumBackupFileBytes = 8L * 1024 * 1024 * 1024 * 1024;
    public const long MaximumBackupTotalBytes = 64L * 1024 * 1024 * 1024 * 1024;
    private const string BackupManifest = "manifest.ndjson";
    /// <summary>Folder of the picture files in a package; the snapshot is "catalog.db", so it is the store of the restored catalog.</summary>
    private static readonly string BackupAssetFolder = Path.GetFileName(AssetStore.DirectoryFor("catalog.db"));
    private static readonly JsonSerializerOptions BackupJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private sealed record BackupRecord(string Type, string? Format = null, int Version = 1, string? Name = null,
        long Size = 0, string? Sha256 = null, Guid? MediaId = null, string? Kind = null, string? SourcePath = null, int FileCount = 0);

    /// <summary>Snapshot includes committed WAL contents. External media are read without modifying them.</summary>
    public async Task<CatalogBackupResult> CreateBackupAsync(string packagePath, bool includeExternalMedia,
        CancellationToken cancellationToken = default, IProgress<CatalogBackupProgress>? progress = null, bool includeCachedThumbnails = true)
    {
        var target = Path.GetFullPath(packagePath); FileAccessPolicy.Current.EnsureWritable(target); RejectBackupLinks(target);
        if (File.Exists(target) || Directory.Exists(target)) throw new IOException(T("The backup must be a new file."));
        var parent = Path.GetDirectoryName(target)!; Directory.CreateDirectory(parent);
        var work = Path.Combine(parent,".flixypix-backup-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
        var snapshot = Path.Combine(work,"catalog.db"); var partial = Path.Combine(work,"package.partial");
        var manifestFile = Path.Combine(work,"manifest.ndjson"); var warnings = new List<string>(); int count = 0; long total = 0;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Run(() =>
            {
                lock (gate)
                {
                    ThrowIfDisposed(); cancellationToken.ThrowIfCancellationRequested();
                    // An encrypted library's snapshot is written with the same key and never exists in plaintext.
                    using var destination = new SqliteConnection(CatalogEncryption.ConnectionString(snapshot, key).ToString());
                    destination.Open(); connection.BackupDatabase(destination);
                }
            },cancellationToken);
            if (key is not null) File.Copy(CatalogEncryption.KeyFilePath(DatabasePath),CatalogEncryption.KeyFilePath(snapshot));
            using (var file = new FileStream(partial,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None,81920,true))
            using (var output = key is null ? new NonClosingStream(file) : EncryptedContainer.Create(file,EncryptedContainer.BackupContent,CatalogEncryption.ReadSlots(DatabasePath),key))
            using (var zip = new ZipArchive(output,ZipArchiveMode.Create,true,Encoding.UTF8))
            await using (var manifest = new StreamWriter(new FileStream(manifestFile,FileMode.CreateNew,FileAccess.Write,FileShare.None,81920,true),new UTF8Encoding(false)))
            {
                await WriteRecord(manifest,new("header",Format:"flixypix-backup"),cancellationToken);
                await AddFile(snapshot,"catalog.db",null,"catalog",null);
                using (var db = new CatalogRepository(snapshot,key))
                {
                    foreach (var item in db.BackupMediaRows())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (includeExternalMedia) await AddOptional(item.Path,item.Id,"media");
                        if (includeCachedThumbnails && !string.IsNullOrEmpty(item.ThumbnailPath)) await AddOptional(item.ThumbnailPath,item.Id,"thumbnail");
                    }
                    // The picture files the snapshot uses, as they are (encrypted ones stay encrypted), and the store's state.
                    var store = Path.GetFullPath(assets.Directory);
                    foreach (var hash in db.ReferencedAssetHashes())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (assets.Locate(hash) is not { } stored) { warnings.Add(T("Missing file ({0}): {1}", "picture", hash)); continue; }
                        await AddFile(stored,BackupAssetFolder + "/" + Path.GetRelativePath(store,stored).Replace(Path.DirectorySeparatorChar,'/'),null,"asset",null);
                    }
                    var state = Path.Combine(store,AssetStore.StateFileName);
                    if (File.Exists(state)) await AddFile(state,BackupAssetFolder + "/" + AssetStore.StateFileName,null,"asset",null);
                }
                await WriteRecord(manifest,new("footer",Size:total,FileCount:count),cancellationToken);
                await manifest.FlushAsync(cancellationToken);
                await manifest.DisposeAsync();
                var manifestEntry = zip.CreateEntry(BackupManifest,CompressionLevel.Fastest);
                await using var manifestOutput = manifestEntry.Open(); await using var manifestInput = File.OpenRead(manifestFile);
                await manifestInput.CopyToAsync(manifestOutput,cancellationToken);

                async Task AddOptional(string path,Guid id,string kind)
                {
                    if (!File.Exists(path))
                    {
                        warnings.Add(T("Missing file ({0}): {1}", kind, path));
                        await WriteRecord(manifest,new("missing",MediaId:id,Kind:kind,SourcePath:path),cancellationToken); return;
                    }
                    if (Path.GetFullPath(path).Equals(target,StringComparison.OrdinalIgnoreCase)) throw new IOException(T("The backup must not itself be included as a media file."));
                    var extension = Path.GetExtension(path).ToLowerInvariant();
                    if (extension.Length > 16 || extension.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '.')) extension = ".bin";
                    await AddFile(path,$"{kind}/{id:N}/content{extension}",id,kind,path);
                }
                async Task AddFile(string path,string name,Guid? id,string kind,string? original)
                {
                    RejectBackupLinks(path); cancellationToken.ThrowIfCancellationRequested();
                    await using var source = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,81920,true);
                    var size = source.Length;
                    if (size > MaximumBackupFileBytes || size > MaximumBackupTotalBytes - total || count >= MaximumBackupEntries - 2)
                        throw new InvalidDataException(T("The backup exceeds the size or file limits."));
                    var entry = zip.CreateEntry(name,kind == "catalog" ? CompressionLevel.Fastest : CompressionLevel.NoCompression);
                    byte[] checksum;
                    await using (var destination = entry.Open()) checksum = await CopyBackupStream(source,destination,size,cancellationToken);
                    source.Position = 0;
                    var rechecked = await SHA256.HashDataAsync(source,cancellationToken);
                    if (!CryptographicOperations.FixedTimeEquals(checksum,rechecked))
                        throw new IOException(T("A source file was changed during the backup."));
                    count++; total += size;
                    await WriteRecord(manifest,new("file",Name:name,Size:size,Sha256:Convert.ToHexStringLower(checksum),MediaId:id,Kind:kind,SourcePath:original),cancellationToken);
                    progress?.Report(new(T("Backing up"),original ?? name,count));
                }
            }
            cancellationToken.ThrowIfCancellationRequested(); FileAccessPolicy.Current.EnsureWritable(target); RejectBackupLinks(target); File.Move(partial,target,false);
            RecordBackup(target,new FileInfo(target).Length);
            return new(target,count,total,warnings);
        }
        finally { DeleteOwnedBackupWork(work); }
    }

    /// <param name="secret">Password or recovery key of the backed-up library; required for encrypted packages.
    /// The restored library stays encrypted with the same keys.</param>
    public static async Task<CatalogRestoreResult> RestoreBackupAsync(string packagePath,string newDirectory,
        CancellationToken cancellationToken = default,IProgress<CatalogBackupProgress>? progress = null,string? secret = null)
    {
        var target = Path.GetFullPath(newDirectory); FileAccessPolicy.Current.EnsureWritable(target); RejectBackupLinks(target); RejectBackupLinks(packagePath);
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException(T("Restoring needs a new folder that does not exist yet."));
        var parent = Path.GetDirectoryName(target)!; Directory.CreateDirectory(parent);
        var work = Path.Combine(parent,".flixypix-restore-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
        return (await ReadBackupAsync(packagePath,work,target,cancellationToken,progress,secret)).Restored;
    }

    /// <summary>Checks a package completely without restoring it: every checksum, the manifest, the catalog's SQLite
    /// integrity and that all files belong to media of the catalog. Only the catalog is unpacked, into a temporary folder.</summary>
    public static async Task<CatalogBackupCheck> VerifyBackupAsync(string packagePath,CancellationToken cancellationToken = default,
        IProgress<CatalogBackupProgress>? progress = null,string? secret = null)
    {
        RejectBackupLinks(packagePath);
        var work = Path.Combine(Path.GetTempPath(),".flixypix-verify-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
        var (restored,media,files,bytes) = await ReadBackupAsync(packagePath,work,null,cancellationToken,progress,secret);
        return new(media,files,bytes,restored.RestoredMediaFiles,restored.Warnings);
    }

    /// <param name="target">Folder the restored library moves to; null only verifies and leaves nothing behind.</param>
    private static async Task<(CatalogRestoreResult Restored,int Media,int Files,long Bytes)> ReadBackupAsync(string packagePath,string work,string? target,
        CancellationToken cancellationToken,IProgress<CatalogBackupProgress>? progress,string? secret)
    {
        var verifyOnly = target is null;
        var warnings = new List<string>(); var restoredMedia = 0; var mediaCount = 0;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            CatalogKeySlots? slots = null; CatalogKey? key = null;
            Stream opened;
            if (EncryptedContainer.IsContainer(packagePath))
            {
                if (secret is null) throw new CatalogLockedException(T("The backup package is encrypted. Enter the password or recovery key."));
                slots = EncryptedContainer.ReadKeys(packagePath); key = CatalogEncryption.Unlock(slots,secret);
                opened = EncryptedContainer.OpenRead(packagePath,EncryptedContainer.BackupContent,secret);
            }
            else opened = new FileStream(packagePath,FileMode.Open,FileAccess.Read,FileShare.Read,81920,true);
            using var unlocked = key;
            await using var package = opened;
            using var zip = new ZipArchive(package,ZipArchiveMode.Read,true,Encoding.UTF8);
            if (zip.Entries.Count > MaximumBackupEntries) throw new InvalidDataException(T("Too many files in the backup."));
            var entries = new Dictionary<string,ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries)
            {
                ValidateBackupName(entry.FullName);
                if (!entries.TryAdd(entry.FullName,entry) || entry.Length > MaximumBackupFileBytes ||
                    ((entry.ExternalAttributes >> 16) & 0xf000) is not (0 or 0x8000) || (entry.ExternalAttributes & 0x400) != 0)
                    throw new InvalidDataException(T("Invalid, linked or duplicate entry in the backup."));
            }
            if (!entries.Remove(BackupManifest,out var manifestEntry) || manifestEntry.Length > 256 * 1024 * 1024)
                throw new InvalidDataException(T("The backup manifest is missing or too large."));
            var records = await ReadBackupManifest(manifestEntry,cancellationToken);
            var expected = records.Where(record => record.Type == "file").ToArray();
            if (expected.Length != entries.Count || expected.Count(record => record.Kind == "catalog" && record.Name == "catalog.db") != 1)
                throw new InvalidDataException(T("Manifest and backup content do not match."));
            long total = 0; int completed = 0;
            foreach (var record in expected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!entries.Remove(record.Name!,out var entry) || entry.Length != record.Size || record.Size > MaximumBackupTotalBytes - total)
                    throw new InvalidDataException(T("A backup size or entry is missing."));
                total += record.Size; var outputPath = Path.Combine(work,record.Name!.Replace('/',Path.DirectorySeparatorChar));
                var unpack = !verifyOnly || record.Kind == "catalog";
                if (unpack) Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                // Verifying hashes media and thumbnails without writing them anywhere.
                await using var input = entry.Open(); await using var output = unpack ? new FileStream(outputPath,FileMode.CreateNew,FileAccess.Write,FileShare.None,81920,true) : Stream.Null;
                var hash = await CopyBackupStream(input,output,record.Size,cancellationToken);
                if (!Convert.ToHexStringLower(hash).Equals(record.Sha256,StringComparison.Ordinal)) throw new InvalidDataException(T("The checksum of a file in the backup does not match."));
                completed++; progress?.Report(new(T("Check and extract"),record.Name,completed));
            }
            var fileCount = completed; var fileBytes = total;
            // Every hash is verified before SQLite opens or any path is remapped.
            var mediaFiles = expected.Where(record => record.Kind == "media").ToDictionary(record => record.MediaId!.Value);
            var thumbnails = expected.Where(record => record.Kind == "thumbnail").ToDictionary(record => record.MediaId!.Value);
            var database = Path.Combine(work,"catalog.db");
            if (slots is not null) File.WriteAllText(CatalogEncryption.KeyFilePath(database),CatalogEncryption.SerializeSlots(slots),new UTF8Encoding(false));
            using (var db = new CatalogRepository(database,key))
            {
                lock (db.gate)
                {
                    using var integrity = db.Command("PRAGMA integrity_check",null);
                    if (!string.Equals(integrity.ExecuteScalar() as string,"ok",StringComparison.Ordinal)) throw new InvalidDataException(T("The SQLite backup is damaged."));
                    using var foreign = db.Command("PRAGMA foreign_key_check",null);
                    using var reader = foreign.ExecuteReader(); if (reader.Read()) throw new InvalidDataException(T("Invalid catalog references."));
                }
                foreach (var item in db.BackupMediaRows())
                {
                    cancellationToken.ThrowIfCancellationRequested(); var updated = item; mediaCount++;
                    if (mediaFiles.Remove(item.Id,out var media)) { updated = updated with { Path = Path.Combine(target ?? work,media.Name!.Replace('/',Path.DirectorySeparatorChar)) }; restoredMedia++; }
                    if (thumbnails.Remove(item.Id,out var thumb)) updated = updated with { ThumbnailPath = Path.Combine(target ?? work,thumb.Name!.Replace('/',Path.DirectorySeparatorChar)) };
                    if (updated != item && !verifyOnly) db.Upsert(updated);
                }
                if (mediaFiles.Count > 0 || thumbnails.Count > 0) throw new InvalidDataException(T("The backup file refers to unknown items."));
                lock (db.gate) { using var checkpoint = db.Command("PRAGMA wal_checkpoint(TRUNCATE)",null); checkpoint.ExecuteNonQuery(); }
            }
            warnings.AddRange(records.Where(record => record.Type == "missing").Select(record => T("Not backed up ({0}): {1}", record.Kind, record.SourcePath)));
            cancellationToken.ThrowIfCancellationRequested();
            if (target is null) return (new("","",restoredMedia,warnings),mediaCount,fileCount,fileBytes);
            FileAccessPolicy.Current.EnsureWritable(target); RejectBackupLinks(target); Directory.Move(work,target);
            return (new(target,Path.Combine(target,"catalog.db"),restoredMedia,warnings),mediaCount,fileCount,fileBytes);
        }
        finally { DeleteOwnedBackupWork(work); }
    }

    private IEnumerable<MediaItem> BackupMediaRows()
    {
        string after = "";
        while (true)
        {
            List<(string Id,MediaItem Item)> rows = [];
            lock (gate)
            {
                using var command = Command("SELECT id,document FROM media WHERE id>$after ORDER BY id LIMIT 256",null,("$after",after));
                using var reader = command.ExecuteReader();
                while (reader.Read()) rows.Add((reader.GetString(0),JsonSerializer.Deserialize<MediaItem>(reader.GetString(1),Json) ?? throw new InvalidDataException(T("Invalid catalog entry."))));
            }
            if (rows.Count == 0) yield break;
            foreach (var row in rows) yield return row.Item;
            after = rows[^1].Id;
        }
    }

    private static async Task<List<BackupRecord>> ReadBackupManifest(ZipArchiveEntry entry,CancellationToken token)
    {
        using var reader = new StreamReader(entry.Open(),new UTF8Encoding(false,true)); var records = new List<BackupRecord>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var mapping = new HashSet<string>(StringComparer.Ordinal); long total = 0; bool footer = false;
        var first = await reader.ReadLineAsync(token);
        var header = first is null ? null : JsonSerializer.Deserialize<BackupRecord>(first,BackupJson);
        if (header?.Type != "header" || !ProductIdentity.IsFormat(header.Format!,"flixypix-backup") || header.Version != 1) throw new InvalidDataException(T("Unknown backup format."));
        string? line;
        while ((line = await reader.ReadLineAsync(token)) is not null)
        {
            if (line.Length > 65536 || footer || records.Count > MaximumBackupEntries) throw new InvalidDataException(T("Invalid backup manifest."));
            var record = JsonSerializer.Deserialize<BackupRecord>(line,BackupJson) ?? throw new InvalidDataException(T("Invalid backup record."));
            if (record.Type == "footer")
            {
                if (record.FileCount != names.Count || record.Size != total) throw new InvalidDataException(T("Incomplete backup manifest."));
                footer = true; continue;
            }
            if (record.Type is not ("file" or "missing") || record.Kind is not ("catalog" or "media" or "thumbnail" or "asset")) throw new InvalidDataException(T("Unknown backup record."));
            if (record.Kind == "asset" && (record.Type != "file" || record.MediaId is not null || record.Name?.StartsWith(BackupAssetFolder + "/", StringComparison.Ordinal) != true))
                throw new InvalidDataException(T("Invalid file assignment in the backup manifest."));
            if (record.Kind is not ("catalog" or "asset") && (record.MediaId is null || record.MediaId == Guid.Empty || !mapping.Add($"{record.Kind}:{record.MediaId}"))) throw new InvalidDataException(T("Duplicate or invalid media assignment."));
            if (record.Type == "file")
            {
                ValidateBackupName(record.Name!);
                if (!names.Add(record.Name!) || record.Size < 0 || record.Size > MaximumBackupFileBytes || record.Size > MaximumBackupTotalBytes - total ||
                    record.Sha256?.Length != 64 || record.Sha256.Any(c => !char.IsAsciiHexDigit(c)) ||
                    record.Kind == "catalog" && record.Name != "catalog.db" ||
                    record.Kind is "media" or "thumbnail" && !record.Name!.StartsWith($"{record.Kind}/{record.MediaId:N}/content",StringComparison.Ordinal))
                    throw new InvalidDataException(T("Invalid file assignment in the backup manifest."));
                total += record.Size;
            }
            records.Add(record);
        }
        if (!footer) throw new InvalidDataException(T("The end of the backup manifest is missing."));
        return records;
    }

    private static Task WriteRecord(StreamWriter writer,BackupRecord record,CancellationToken token) =>
        writer.WriteLineAsync(JsonSerializer.Serialize(record,BackupJson).AsMemory(),token);

    private static async Task<byte[]> CopyBackupStream(Stream input,Stream output,long expected,CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[81920]; long total = 0; int read;
        while ((read = await input.ReadAsync(buffer,token)) != 0)
        {
            if (read > expected - total) throw new InvalidDataException(T("Extracted data exceeds the declared size."));
            total += read; hash.AppendData(buffer.AsSpan(0,read)); await output.WriteAsync(buffer.AsMemory(0,read),token);
        }
        if (total != expected) throw new InvalidDataException(T("Incomplete backup file."));
        return hash.GetHashAndReset();
    }

    private static void ValidateBackupName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 256 || name.Contains('\\') || name.Contains(':') || name.StartsWith('/') ||
            name.Split('/').Any(part => part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.Any(c => c < 32)))
            throw new InvalidDataException(T("Unsafe path in the backup."));
    }
    private static void RejectBackupLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException(T("Linked backup paths are not supported.")); }
            catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
        }
    }
    private static void DeleteOwnedBackupWork(string work)
    {
        var resolved = Path.GetFullPath(work);
        if (Directory.Exists(resolved) && Path.GetFileName(resolved).StartsWith(".flixypix-",StringComparison.Ordinal))
        { RejectBackupLinks(resolved); Directory.Delete(resolved,true); }
    }
}
