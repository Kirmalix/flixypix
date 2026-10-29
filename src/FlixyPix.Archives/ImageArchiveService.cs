using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using FlixyPix.Core;

namespace FlixyPix.Archives;

/// <summary>A virtual image identified by immutable archive bytes and its exact entry name.</summary>
public sealed record ArchiveImageEntry(string ArchivePath, string EntryName, long UncompressedSize)
{
    public string ArchiveSha256 { get; init; } = "";
}

/// <summary>Reads ZIP/ZIP64 and single-volume RAR images. Extracted files are disposable cache artifacts.</summary>
public sealed partial class ImageArchiveService
{
    public const long MaximumImageSize = 128L * 1024 * 1024;
    public const long MaximumTotalSize = 2L * 1024 * 1024 * 1024;
    public const int MaximumEntryCount = 10_000;
    private static IReadOnlySet<string> Extensions => MediaFormats.ImageExtensions;
    private static readonly UTF8Encoding ZipEncoding = new(false, true);
    private readonly string _cache;

    public ImageArchiveService(string cacheDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        _cache = Path.GetFullPath(cacheDirectory);
        FileAccessPolicy.Current.EnsureWritable(_cache);
    }

    public IReadOnlyList<ArchiveImageEntry> Inspect(string archive)
    {
        using var file = OpenSource(archive);
        if (IsRar(file)) return InspectRar(file,archive);
        ValidateDirectory(file);
        file.Position = 0;
        var hash = Convert.ToHexStringLower(SHA256.HashData(file));
        file.Position = 0;
        using var zip = new ZipArchive(file, ZipArchiveMode.Read, true, ZipEncoding);
        return zip.Entries.Where(e => !e.FullName.EndsWith('/') && Extensions.Contains(Path.GetExtension(e.FullName)))
            .Select(e => new ArchiveImageEntry(Path.GetFullPath(archive), e.FullName, e.Length) { ArchiveSha256 = hash }).ToArray();
    }

    public async Task<string> ExtractAsync(ArchiveImageEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!Extensions.Contains(Path.GetExtension(entry.EntryName))) throw new InvalidDataException("Unsupported image extension.");
        cancellationToken.ThrowIfCancellationRequested();
        await using var source = OpenSource(entry.ArchivePath);
        if (IsRar(source)) return await ExtractRarAsync(source,entry,cancellationToken);
        ValidateName(entry.EntryName);
        ValidateDirectory(source);
        source.Position = 0;
        var archiveHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(source, cancellationToken));
        if (entry.ArchiveSha256.Length > 0 && !StringComparer.Ordinal.Equals(entry.ArchiveSha256, archiveHash))
            throw new InvalidDataException("The ZIP changed after inspection. Inspect it again.");
        source.Position = 0;
        using var zip = new ZipArchive(source, ZipArchiveMode.Read, true, ZipEncoding);
        var member = zip.GetEntry(entry.EntryName) ?? throw new InvalidDataException("Image entry no longer exists.");
        if (member.Length != entry.UncompressedSize || member.Length > MaximumImageSize)
            throw new InvalidDataException("Image length does not match inspected entry.");
        await using var input = member.Open();
        return await ExtractMemberAsync(source,input,entry,archiveHash,cancellationToken);
    }

    private async Task<string> ExtractMemberAsync(FileStream source, Stream input, ArchiveImageEntry entry, string archiveHash, CancellationToken cancellationToken)
    {
        var entryHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(entry.EntryName)));
        var directory = EnsureCacheDirectory(archiveHash);
        var target = Path.Combine(directory, entryHash + Path.GetExtension(entry.EntryName).ToLowerInvariant());
        FileAccessPolicy.Current.EnsureWritable(target);
        RejectReparse(target);
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".partial");
        try
        {
            byte[] expectedHash;
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[81920];
                long written = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    written += read;
                    if (written > entry.UncompressedSize || written > MaximumImageSize)
                        throw new InvalidDataException("Expanded image exceeded its declared size.");
                    hash.AppendData(buffer.AsSpan(0, read));
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                if (written != entry.UncompressedSize) throw new InvalidDataException("Truncated image entry.");
                expectedHash = hash.GetHashAndReset();
            }
            cancellationToken.ThrowIfCancellationRequested();
            await input.DisposeAsync();
            // Rehash source to detect an external writer even on platforms without Windows share locks.
            source.Position = 0;
            var finalArchiveHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(source, cancellationToken));
            if (finalArchiveHash != archiveHash) throw new InvalidDataException("ZIP changed while extracting.");
            RejectReparse(target);
            try { File.Move(temporary, target, false); }
            catch (IOException) when (File.Exists(target))
            {
                RejectReparse(target);
                await using var existing = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                if (existing.Length != entry.UncompressedSize)
                    throw new InvalidDataException("Cached image differs from the immutable ZIP content.");
                var actualHash = await SHA256.HashDataAsync(existing, cancellationToken);
                if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
                    throw new InvalidDataException("Cached image differs from the immutable ZIP content.");
            }
            return target;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static FileStream OpenSource(string archive)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archive);
        RejectReparse(archive);
        var stream = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        if (stream.Length > MaximumTotalSize || stream.Length < 8)
        { stream.Dispose(); throw new InvalidDataException("Archive source size is outside supported bounds."); }
        return stream;
    }

    private string EnsureCacheDirectory(string archiveHash)
    {
        FileAccessPolicy.Current.EnsureWritable(_cache);
        RejectReparse(_cache);
        Directory.CreateDirectory(_cache);
        RejectReparse(_cache);
        var directory = Path.Combine(_cache, archiveHash);
        RejectReparse(directory);
        Directory.CreateDirectory(directory);
        RejectReparse(directory);
        return directory;
    }

    private static void RejectReparse(string path)
    {
        // Inspect all existing ancestors, including a dangling symlink where GetAttributes succeeds.
        for (var current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Symbolic links and reparse points are not supported here."); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith('/') || name.StartsWith('\\') || name.Contains('\\') || name.Contains(':') ||
            name.Any(c => c < 32 || c == '\u007f') || name.Split('/').Any(p => p is "." or ".." || p.EndsWith(' ') || p.EndsWith('.')))
            throw new InvalidDataException("Unsafe ZIP entry name.");
    }

    private static void ValidateDirectory(FileStream stream)
    {
        // Parse bounded central-directory metadata before entry streams open, including ZIP64 size fields.
        var tailSize = (int)Math.Min(stream.Length, 65557);
        var tail = new byte[tailSize];
        stream.Position = stream.Length - tailSize;
        stream.ReadExactly(tail);
        var eocd = -1;
        for (var i = tail.Length - 22; i >= 0; i--)
            if (U32(tail, i) == 0x06054b50 && i + 22 + U16(tail, i + 20) == tail.Length) { eocd = i; break; }
        if (eocd < 0) throw new InvalidDataException("Missing ZIP directory.");
        long count = U16(tail, eocd + 10), centralSize = U32(tail, eocd + 12), centralOffset = U32(tail, eocd + 16);
        var eocdOffset = stream.Length - tailSize + eocd; var centralEnd = eocdOffset;
        if (U16(tail, eocd + 4) != 0 || U16(tail, eocd + 6) != 0 || U16(tail, eocd + 8) != count)
            throw new InvalidDataException("Multi-volume ZIP is unsupported.");
        if (count == ushort.MaxValue || centralSize == uint.MaxValue || centralOffset == uint.MaxValue ||
            eocd >= 20 && U32(tail,eocd - 20) == 0x07064b50)
        {
            if (eocd < 20 || U32(tail,eocd - 20) != 0x07064b50 || U32(tail,eocd - 16) != 0 || U32(tail,eocd - 4) != 1)
                throw new InvalidDataException("Missing or multi-volume ZIP64 locator.");
            var zip64Offset = BoundedU64(tail,eocd - 12,stream.Length - 56);
            var zip64 = new byte[56]; stream.Position = zip64Offset; stream.ReadExactly(zip64);
            var zip64Length = BoundedU64(zip64,4,1024 * 1024);
            if (U32(zip64,0) != 0x06064b50 || zip64Length < 44 || zip64Offset + 12 + zip64Length != eocdOffset - 20 ||
                U32(zip64,16) != 0 || U32(zip64,20) != 0 || U64(zip64,24) != U64(zip64,32))
                throw new InvalidDataException("Invalid or multi-volume ZIP64 directory.");
            var zip64Count = BoundedU64(zip64,32,MaximumEntryCount);
            var zip64Size = BoundedU64(zip64,40,16 * 1024 * 1024);
            var zip64Central = BoundedU64(zip64,48,stream.Length);
            if (count != ushort.MaxValue && count != zip64Count || centralSize != uint.MaxValue && centralSize != zip64Size ||
                centralOffset != uint.MaxValue && centralOffset != zip64Central)
                throw new InvalidDataException("ZIP64 directory disagrees with the end record.");
            count = zip64Count; centralSize = zip64Size; centralOffset = zip64Central; centralEnd = zip64Offset;
        }
        if (count > MaximumEntryCount || centralSize > 16 * 1024 * 1024 || centralOffset + centralSize != centralEnd)
            throw new InvalidDataException("Oversized or inconsistent ZIP directory.");
        var directory = new byte[(int)centralSize];
        stream.Position = centralOffset;
        stream.ReadExactly(directory);
        int cursor = 0;
        long total = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < count; index++)
        {
            if (cursor + 46 > directory.Length || U32(directory, cursor) != 0x02014b50)
                throw new InvalidDataException("Malformed ZIP directory entry.");
            int flags = U16(directory, cursor + 8), method = U16(directory, cursor + 10);
            long compressed = U32(directory, cursor + 20), expanded = U32(directory, cursor + 24);
            int nameLength = U16(directory, cursor + 28), extraLength = U16(directory, cursor + 30), commentLength = U16(directory, cursor + 32);
            var attributes = U32(directory, cursor + 38);
            long localOffset = U32(directory, cursor + 42);
            var next = cursor + 46 + nameLength + extraLength + commentLength;
            if (next > directory.Length) throw new InvalidDataException("Truncated ZIP directory entry.");
            var disk = (long)U16(directory,cursor + 34);
            if (compressed == uint.MaxValue || expanded == uint.MaxValue || localOffset == uint.MaxValue || disk == ushort.MaxValue)
            {
                var extraStart = cursor + 46 + nameLength; var extraEnd = extraStart + extraLength; var found = false;
                while (extraStart + 4 <= extraEnd)
                {
                    var kind = U16(directory,extraStart); var length = U16(directory,extraStart + 2); extraStart += 4;
                    if (extraStart + length > extraEnd) throw new InvalidDataException("Malformed ZIP64 extra field.");
                    if (kind == 1)
                    {
                        if (found) throw new InvalidDataException("Duplicate ZIP64 size fields."); found = true;
                        var position = extraStart; var end = extraStart + length;
                        if (expanded == uint.MaxValue) expanded = ReadZip64Extra(directory,ref position,end);
                        if (compressed == uint.MaxValue) compressed = ReadZip64Extra(directory,ref position,end);
                        if (localOffset == uint.MaxValue) localOffset = ReadZip64Extra(directory,ref position,end);
                        if (disk == ushort.MaxValue)
                        { if (position + 4 > end) throw new InvalidDataException("Missing ZIP64 disk field."); disk = U32(directory,position); }
                    }
                    extraStart += length;
                }
                if (!found || extraStart != extraEnd) throw new InvalidDataException("Missing ZIP64 size fields.");
            }
            var name = ZipEncoding.GetString(directory, cursor + 46, nameLength);
            ValidateName(name);
            if (!names.Add(name)) throw new InvalidDataException("Duplicate or case-colliding ZIP entries are unsupported.");
            int unixType = (int)(attributes >> 16) & 0xf000;
            if ((flags & 0x41) != 0 || method is not (0 or 8) || disk != 0 ||
                unixType is not (0 or 0x8000 or 0x4000) || (attributes & 0x400) != 0 ||
                localOffset > centralOffset - 30 || compressed > MaximumTotalSize)
                throw new InvalidDataException("Encrypted, linked or unsupported ZIP entry.");
            total += expanded;
            if (expanded > MaximumImageSize || total > MaximumTotalSize || expanded > Math.Max(1, compressed) * 1000)
                throw new InvalidDataException("ZIP expansion limits exceeded.");
            var local = new byte[30];
            stream.Position = localOffset;
            stream.ReadExactly(local);
            if (U32(local, 0) != 0x04034b50 || (U16(local, 6) & 0x41) != 0 || U16(local, 8) != method ||
                localOffset + 30 + U16(local, 26) + U16(local, 28) + compressed > centralOffset)
                throw new InvalidDataException("Invalid ZIP local header or entry range.");
            cursor = next;
        }
        if (cursor != directory.Length) throw new InvalidDataException("Unexpected trailing central-directory records.");
    }

    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    private static ulong U64(byte[] bytes,int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset,8));
    private static long BoundedU64(byte[] bytes,int offset,long maximum)
    {
        var value = U64(bytes,offset);
        if (maximum < 0 || value > (ulong)maximum) throw new InvalidDataException("ZIP64 value exceeds supported bounds.");
        return (long)value;
    }
    private static long ReadZip64Extra(byte[] bytes,ref int position,int end)
    {
        if (position + 8 > end) throw new InvalidDataException("Truncated ZIP64 size field.");
        var value = BoundedU64(bytes,position,MaximumTotalSize); position += 8; return value;
    }
}
