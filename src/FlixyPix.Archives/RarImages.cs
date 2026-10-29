using System.Security.Cryptography;
using SharpCompress.Archives.Rar;
using SharpCompress.Common;
using SharpCompress.Common.Rar;
using SharpCompress.Readers;
using SharpCompress.Readers.Rar;

namespace FlixyPix.Archives;

public sealed partial class ImageArchiveService
{
    private static bool IsRar(FileStream source)
    {
        Span<byte> magic = stackalloc byte[8]; source.Position = 0; var read = source.Read(magic); source.Position = 0;
        return read == 8 && magic[..6].SequenceEqual(new byte[] {82,97,114,33,26,7}) && magic[6] is 0 or 1;
    }

    private static IReadOnlyList<ArchiveImageEntry> InspectRar(FileStream source, string path)
    {
        ValidateRarHeaders(source); source.Position = 0;
        var hash = Convert.ToHexStringLower(SHA256.HashData(source)); source.Position = 0;
        using var rar = RarArchive.Open(source,new ReaderOptions { LeaveStreamOpen = true, LookForHeader = false });
        var images = new List<ArchiveImageEntry>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0; var count = 0;
        foreach (var member in rar.Entries)
        {
            if (++count > MaximumEntryCount) throw new InvalidDataException("RAR entry count exceeded.");
            ValidateRarMember(member,names); total = checked(total + member.Size);
            if (total > MaximumTotalSize || !member.IsComplete) throw new InvalidDataException("Incomplete or oversized RAR archive.");
            if (!member.IsDirectory && Extensions.Contains(Path.GetExtension(member.Key!)))
                images.Add(new(Path.GetFullPath(path),member.Key!,member.Size) { ArchiveSha256 = hash });
        }
        if (rar.Volumes.Any(volume => volume.IsMultiVolume)) throw new InvalidDataException("Multi-volume RAR is unsupported.");
        return images;
    }

    private async Task<string> ExtractRarAsync(FileStream source, ArchiveImageEntry entry, CancellationToken token)
    {
        var members = InspectRar(source,entry.ArchivePath);
        var inspected = members.SingleOrDefault(member => member.EntryName == entry.EntryName)
            ?? throw new InvalidDataException("The image entry no longer exists.");
        if (entry.ArchiveSha256.Length > 0 && entry.ArchiveSha256 != inspected.ArchiveSha256 || entry.UncompressedSize != inspected.UncompressedSize)
            throw new InvalidDataException("The RAR changed after inspection.");
        token.ThrowIfCancellationRequested(); source.Position = 0;
        // Always stream in archive order: solid RAR decoding depends on the preceding members.
        using var reader = RarReader.Open(source,new ReaderOptions { LeaveStreamOpen = true, LookForHeader = false });
        while (reader.MoveToNextEntry())
        {
            token.ThrowIfCancellationRequested();
            if (reader.Entry.IsDirectory) continue;
            await using var input = reader.OpenEntryStream();
            if (reader.Entry.Key == entry.EntryName)
                return await ExtractMemberAsync(source,input,entry,inspected.ArchiveSha256,token);
            // Explicitly drain preceding data with cancellation; do not let MoveToNextEntry hide unbounded work.
            await DrainRarEntryAsync(input,reader.Entry.Size,token);
        }
        throw new InvalidDataException("The image entry no longer exists.");
    }

    private static async Task DrainRarEntryAsync(Stream input,long size,CancellationToken token)
    {
        var buffer = new byte[81920]; long readTotal = 0; int read;
        while ((read = await input.ReadAsync(buffer,token)) != 0)
        {
            readTotal += read;
            if (readTotal > size || readTotal > MaximumImageSize) throw new InvalidDataException("RAR expansion exceeded declared length.");
        }
        if (readTotal != size) throw new InvalidDataException("Truncated RAR entry.");
    }

    private static void ValidateRarMember(RarEntry member,HashSet<string> names)
    {
        var name = member.Key ?? throw new InvalidDataException("RAR entry name is missing.");
        // RAR uses both slash conventions. Validate normalized components, retain the exact archive key.
        var normalized = name.Replace('\\','/'); ValidateName(normalized);
        if (!names.Add(normalized)) throw new InvalidDataException("Duplicate or case-colliding RAR entries are unsupported.");
        var attributes = unchecked((uint)(member.Attrib ?? 0)); var unixType = (attributes >> 16) & 0xf000;
        // Some RAR producers store Unix mode bits directly instead of in the high word.
        var lowUnixType = attributes & 0xf000;
        if (member.IsEncrypted || member.IsSplitAfter || member.IsRedir || member.LinkTarget is not null ||
            unixType is not (0 or 0x8000 or 0x4000) || lowUnixType is 0xa000 or 0x6000 or 0x2000 or 0xc000 || (attributes & 0x400) != 0)
            throw new InvalidDataException("Encrypted, linked or multi-volume RAR entry is unsupported.");
        if (member.Size < 0 || member.CompressedSize < 0 || member.Size > MaximumImageSize ||
            member.Size > Math.Max(1,member.CompressedSize) * 1000)
            throw new InvalidDataException("RAR expansion limits exceeded.");
    }

    private static void ValidateRarHeaders(FileStream source)
    {
        // Bound header metadata before passing it to the decoder. No file-path overload is used,
        // so the decoder cannot discover companion volumes or follow archive redirects.
        source.Position = 0; var magic = new byte[8]; source.ReadExactly(magic);
        var version5 = magic[6] == 1 && magic[7] == 0;
        if (magic[6] != 0 && !version5) throw new InvalidDataException("Unsupported RAR signature.");
        source.Position = version5 ? 8 : 7;
        long metadata = 0; var headers = 0;
        while (source.Position < source.Length)
        {
            if (++headers > MaximumEntryCount + 32) throw new InvalidDataException("Too many RAR headers.");
            var start = source.Position; long headerLength,dataLength;
            if (version5)
            {
                source.Position += 4;
                var length = ReadRarVarInt(source); var prefix = source.Position - start;
                if (length > 16 * 1024 * 1024 || length < 2) throw new InvalidDataException("RAR header size is invalid.");
                headerLength = checked(prefix + (long)length);
                var type = ReadRarVarInt(source); var flags = ReadRarVarInt(source);
                if ((flags & 1) != 0) _ = ReadRarVarInt(source);
                var data = (flags & 2) != 0 ? ReadRarVarInt(source) : 0;
                if (data > MaximumTotalSize) throw new InvalidDataException("RAR data size exceeds supported bounds.");
                dataLength = (long)data;
                if (type == 4) throw new InvalidDataException("Encrypted RAR headers are unsupported.");
                if (type == 1 && (ReadRarVarInt(source) & 1) != 0) throw new InvalidDataException("Multi-volume RAR is unsupported.");
            }
            else
            {
                var header = new byte[7]; source.ReadExactly(header);
                var type = header[2]; var flags = U16(header,3); headerLength = U16(header,5); dataLength = 0;
                if (headerLength < 7) throw new InvalidDataException("Invalid RAR4 header size.");
                if (type == 0x73 && (flags & 0x81) != 0) throw new InvalidDataException("Multi-volume or encrypted RAR is unsupported.");
                if ((flags & 0x8000) != 0)
                {
                    var packed = new byte[4]; source.ReadExactly(packed); dataLength = U32(packed,0);
                    if (type == 0x74 && (flags & 0x100) != 0)
                    {
                        if (headerLength < 40) throw new InvalidDataException("Invalid RAR4 large-file header.");
                        source.Position = start + 32; source.ReadExactly(packed);
                        dataLength = checked(dataLength + ((long)U32(packed,0) << 32));
                    }
                }
            }
            metadata = checked(metadata + headerLength);
            if (metadata > 16 * 1024 * 1024 || source.Position > start + headerLength ||
                headerLength > source.Length - start || dataLength > source.Length - start - headerLength)
                throw new InvalidDataException("RAR header/data range is invalid.");
            source.Position = start + headerLength + dataLength;
        }
        source.Position = 0;
    }

    private static ulong ReadRarVarInt(Stream stream)
    {
        ulong value = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            var next = stream.ReadByte();
            if (next < 0 || shift == 63 && next > 1) throw new InvalidDataException("Invalid RAR variable integer.");
            value |= (ulong)(next & 127) << shift;
            if ((next & 128) == 0) return value;
        }
        throw new InvalidDataException("Invalid RAR variable integer.");
    }
}
