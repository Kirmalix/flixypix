using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FlixyPix.Core;

/// <summary>
/// Encrypted wrapper for backups and portable exports of encrypted libraries.
/// Layout: magic, header length, UTF-8 JSON header (wrapped library keys, content type, salt),
/// then AES-256-GCM chunks of <see cref="ChunkSize"/> plaintext bytes plus tag. Each chunk is
/// authenticated together with the header hash, its index and a final flag, so reordering,
/// truncation and header changes are detected. Fixed chunks allow random-access reads, which
/// lets ZIP restore work without writing a plaintext copy.
/// </summary>
public static class EncryptedContainer
{
    public const string BackupContent = "backup";
    public const string PortableContent = "catalog";
    public const int ChunkSize = 1024 * 1024;
    private const int TagSize = 16;
    private const int MaximumHeaderBytes = 64 * 1024;
    private static readonly byte[] Magic = "FXPENC01"u8.ToArray();
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private sealed record Header(string Format, int Version, string Content, int ChunkSize, string Salt, CatalogKeySlots Keys);

    public static bool IsContainer(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> prefix = stackalloc byte[8];
        return stream.ReadAtLeast(prefix, 8, false) == 8 && prefix.SequenceEqual(Magic);
    }

    /// <summary>Write-only stream; disposing it writes the authenticated final chunk and leaves <paramref name="output"/> open.</summary>
    public static Stream Create(Stream output, string content, CatalogKeySlots keys, CatalogKey key)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var header = JsonSerializer.SerializeToUtf8Bytes(new Header("flixypix-encrypted", 1, content, ChunkSize, Convert.ToBase64String(salt), keys), Json);
        Span<byte> length = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(length, header.Length);
        output.Write(Magic); output.Write(length); output.Write(header);
        return new EncryptingStream(output, ContentKey(key, salt, content), SHA256.HashData(header));
    }

    /// <summary>Opens a container with the library password or recovery key; the result is seekable.</summary>
    public static Stream OpenRead(string path, string expectedContent, string secret)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.RandomAccess);
        try
        {
            Span<byte> prefix = stackalloc byte[12];
            if (file.ReadAtLeast(prefix, 12, false) != 12 || !prefix[..8].SequenceEqual(Magic)) throw new InvalidDataException(T("Not an encrypted FlixyPix file."));
            var headerLength = BinaryPrimitives.ReadInt32LittleEndian(prefix[8..]);
            if (headerLength is <= 0 or > MaximumHeaderBytes) throw new InvalidDataException(T("Invalid header of the encrypted file."));
            var headerBytes = new byte[headerLength];
            file.ReadExactly(headerBytes);
            var header = JsonSerializer.Deserialize<Header>(headerBytes, Json) ?? throw new InvalidDataException(T("Invalid header of the encrypted file."));
            if (header.Format != "flixypix-encrypted" || header.Version != 1 || header.ChunkSize != ChunkSize || header.Content != expectedContent || header.Keys is null)
                throw new InvalidDataException(T("Unknown or unsuitable encrypted file."));
            using var key = CatalogEncryption.Unlock(header.Keys, secret);
            return new DecryptingStream(file, 12 + headerLength, ContentKey(key, Convert.FromBase64String(header.Salt), header.Content), SHA256.HashData(headerBytes));
        }
        catch { file.Dispose(); throw; }
    }

    /// <summary>Wrapped keys stored in a container, used to restore an encrypted library's key file.</summary>
    public static CatalogKeySlots ReadKeys(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> prefix = stackalloc byte[12];
        if (file.ReadAtLeast(prefix, 12, false) != 12 || !prefix[..8].SequenceEqual(Magic)) throw new InvalidDataException(T("Not an encrypted FlixyPix file."));
        var headerLength = BinaryPrimitives.ReadInt32LittleEndian(prefix[8..]);
        if (headerLength is <= 0 or > MaximumHeaderBytes) throw new InvalidDataException(T("Invalid header of the encrypted file."));
        var headerBytes = new byte[headerLength]; file.ReadExactly(headerBytes);
        return (JsonSerializer.Deserialize<Header>(headerBytes, Json) ?? throw new InvalidDataException(T("Invalid header."))).Keys;
    }

    private static byte[] ContentKey(CatalogKey key, byte[] salt, string content) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, key.Material.ToArray(), 32, salt, Encoding.ASCII.GetBytes("flixypix-container-v1|" + content));

    private static void ChunkContext(Span<byte> nonce, Span<byte> associated, ReadOnlySpan<byte> headerHash, long index, bool final)
    {
        nonce.Clear(); BinaryPrimitives.WriteInt64BigEndian(nonce[4..], index);
        headerHash.CopyTo(associated); BinaryPrimitives.WriteInt64BigEndian(associated[32..], index); associated[40] = final ? (byte)1 : (byte)0;
    }

    private sealed class EncryptingStream(Stream output, byte[] contentKey, byte[] headerHash) : Stream
    {
        private readonly AesGcm aes = new(contentKey, TagSize);
        private readonly byte[] plain = new byte[ChunkSize];
        private readonly byte[] cipher = new byte[ChunkSize];
        private int filled;
        private long index;
        private bool completed;

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(completed, this);
            while (!buffer.IsEmpty)
            {
                // A full buffer is only sealed once more data arrives, so the last chunk is always marked final.
                if (filled == ChunkSize) Seal(false);
                var take = Math.Min(buffer.Length, ChunkSize - filled);
                buffer[..take].CopyTo(plain.AsSpan(filled)); filled += take; buffer = buffer[take..];
            }
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) { Write(buffer, offset, count); return Task.CompletedTask; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) { Write(buffer.Span); return ValueTask.CompletedTask; }
        private void Seal(bool final)
        {
            Span<byte> nonce = stackalloc byte[12]; Span<byte> associated = stackalloc byte[41]; Span<byte> tag = stackalloc byte[TagSize];
            ChunkContext(nonce, associated, headerHash, index++, final);
            aes.Encrypt(nonce, plain.AsSpan(0, filled), cipher.AsSpan(0, filled), tag, associated);
            output.Write(cipher, 0, filled); output.Write(tag); filled = 0;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !completed)
            {
                completed = true;
                try { Seal(true); output.Flush(); }
                finally { aes.Dispose(); CryptographicOperations.ZeroMemory(contentKey); CryptographicOperations.ZeroMemory(plain); }
            }
            base.Dispose(disposing);
        }
        public override void Flush() => output.Flush();
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => !completed;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class DecryptingStream : Stream
    {
        private readonly FileStream file;
        private readonly long dataStart;
        private readonly AesGcm aes;
        private readonly byte[] contentKey, headerHash;
        private readonly long chunkCount, length;
        private readonly byte[] plain = new byte[ChunkSize], cipher = new byte[ChunkSize];
        private long cachedChunk = -1, position;
        private int cachedLength;

        public DecryptingStream(FileStream file, long dataStart, byte[] contentKey, byte[] headerHash)
        {
            this.file = file; this.dataStart = dataStart; this.contentKey = contentKey; this.headerHash = headerHash;
            aes = new AesGcm(contentKey, TagSize);
            var payload = file.Length - dataStart;
            const long stride = ChunkSize + TagSize;
            chunkCount = payload <= 0 ? 0 : (payload + stride - 1) / stride;
            var last = payload - (chunkCount - 1) * stride - TagSize;
            if (chunkCount == 0 || last < 0) throw new InvalidDataException(T("The encrypted file is incomplete."));
            length = (chunkCount - 1) * ChunkSize + last;
            // Authenticating the final chunk up front rejects truncated files before any content is used.
            Load(chunkCount - 1);
        }

        private void Load(long chunk)
        {
            if (chunk == cachedChunk) return;
            var final = chunk == chunkCount - 1;
            var size = final ? (int)(length - chunk * ChunkSize) : ChunkSize;
            Span<byte> nonce = stackalloc byte[12]; Span<byte> associated = stackalloc byte[41]; Span<byte> tag = stackalloc byte[TagSize];
            file.Position = dataStart + chunk * (ChunkSize + TagSize);
            file.ReadExactly(cipher, 0, size); file.ReadExactly(tag);
            ChunkContext(nonce, associated, headerHash, chunk, final);
            try { aes.Decrypt(nonce, cipher.AsSpan(0, size), tag, plain.AsSpan(0, size), associated); }
            catch (AuthenticationTagMismatchException) { cachedChunk = -1; throw new InvalidDataException(T("The encrypted file is damaged or was changed.")); }
            cachedChunk = chunk; cachedLength = size;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (position >= length || buffer.IsEmpty) return 0;
            var chunk = position / ChunkSize; Load(chunk);
            var within = (int)(position - chunk * ChunkSize);
            var take = Math.Min(buffer.Length, cachedLength - within);
            plain.AsSpan(within, take).CopyTo(buffer); position += take; return take;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromResult(Read(buffer, offset, count));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromResult(Read(buffer.Span));
        public override long Seek(long offset, SeekOrigin origin)
        {
            var target = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => position + offset, _ => length + offset };
            if (target < 0) throw new IOException(T("Position before the start of the file."));
            return position = target;
        }
        public override bool CanRead => true; public override bool CanSeek => true; public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => position; set => Seek(value, SeekOrigin.Begin); }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { aes.Dispose(); CryptographicOperations.ZeroMemory(contentKey); CryptographicOperations.ZeroMemory(plain); file.Dispose(); }
            base.Dispose(disposing);
        }
    }
}

/// <summary>Pass-through that leaves the wrapped stream open, mirroring the encrypted writer's ownership.</summary>
internal sealed class NonClosingStream(Stream inner) : Stream
{
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.WriteAsync(buffer, offset, count, cancellationToken);
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
