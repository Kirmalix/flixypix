using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FlixyPix.Core;

/// <summary>
/// Binary contents of one catalog as files beside it, in "&lt;catalog name&gt;-assets": scene images, artwork, tag images and
/// the binary values of the original. The catalog keeps only SHA-256 and length; a file never changes once written, and the
/// same bytes are stored once. Files lie in 256 subfolders named after the first two characters of their name.
/// <para>In an encrypted library the files are encrypted by default (<see cref="EncryptFiles"/>): AES-256-GCM in chunks of
/// <see cref="ChunkSize"/> with a key derived from the library's data key, each chunk bound to its index and to whether it is
/// the last one, so reordering and truncation are detected. Such a file is named by a keyed hash of its SHA-256 and carries
/// the SHA-256 inside, so neither name nor content tells anything and it can be decrypted without the catalog. The name
/// decides the form: a file at the plain name is plain, one at the keyed name encrypted. Switching rewrites file by file and
/// resumes after an interruption.</para>
/// </summary>
internal sealed class AssetStore : IDisposable
{
    internal const string StateFileName = "store.json";
    internal const int ChunkSize = 1024 * 1024;
    private const int TagSize = 16, PrefixSize = 7, HashSize = 32;
    private static readonly byte[] Magic = "FXPA01"u8.ToArray();
    private static readonly JsonSerializerOptions StateJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    /// <summary><paramref name="FilesEncrypted"/> is null while a switch is under way.</summary>
    private sealed record State(string Format, int Version, bool EncryptFiles, bool? FilesEncrypted);

    private readonly byte[]? contentKey, nameKey;
    private State state;

    public string Directory { get; }
    /// <summary>Whether the library may encrypt its files at all: only with the data key of an encrypted library.</summary>
    public bool HasKey => contentKey is not null;
    /// <summary>The choice of an encrypted library to encrypt its files; on by default, remembered while unencrypted.</summary>
    public bool EncryptFiles => state.EncryptFiles;
    /// <summary>Whether new files are written encrypted.</summary>
    public bool Encrypts => HasKey && state.EncryptFiles;

    public static string DirectoryFor(string databasePath)
    {
        var full = Path.GetFullPath(databasePath);
        return Path.Combine(Path.GetDirectoryName(full)!, Path.GetFileNameWithoutExtension(full) + "-assets");
    }

    /// <summary>Opens the store of a catalog. With a key, a switch that was interrupted, or the first encryption after the
    /// library was encrypted, is completed here.</summary>
    public AssetStore(string databasePath, CatalogKey? key, IProgress<CatalogOpenProgress>? progress = null, CancellationToken token = default)
    {
        Directory = DirectoryFor(databasePath);
        if (key is not null)
        {
            contentKey = new byte[32]; nameKey = new byte[32];
            HKDF.DeriveKey(HashAlgorithmName.SHA256, key.Material, contentKey, [], "flixypix-assets-content-v1"u8);
            HKDF.DeriveKey(HashAlgorithmName.SHA256, key.Material, nameKey, [], "flixypix-assets-names-v1"u8);
        }
        state = ReadState();
        if (HasKey && state.FilesEncrypted != state.EncryptFiles) Rewrite(state.EncryptFiles, progress, token);
    }

    /// <summary>How many files an opening with the key would rewrite to finish a switch (or the first encryption).</summary>
    public static int PendingRewrite(string databasePath)
    {
        using var store = new AssetStore(DirectoryFor(databasePath));
        return store.state.FilesEncrypted == store.state.EncryptFiles ? 0 : store.Files().Count();
    }

    /// <summary>Reads only the state; no key, no rewrite.</summary>
    private AssetStore(string directory)
    {
        Directory = directory; state = ReadState();
    }

    private string StatePath => Path.Combine(Directory, StateFileName);

    private State ReadState()
    {
        if (!File.Exists(StatePath)) return new("flixypix-assets", 1, true, false);
        var read = JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath, Encoding.UTF8), StateJson);
        if (read is null || read.Format != "flixypix-assets" || read.Version != 1) throw new InvalidDataException(T("Unknown format of the picture folder."));
        return read;
    }

    private void SaveState(State next)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var temporary = StatePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(next, StateJson), new UTF8Encoding(false));
        File.Move(temporary, StatePath, true);
        state = next;
    }

    private string PlainPath(string sha256) => Path.Combine(Directory, sha256[..2], sha256);

    private string? SealedPath(string sha256)
    {
        if (nameKey is null) return null;
        var name = Convert.ToHexStringLower(HMACSHA256.HashData(nameKey, Encoding.ASCII.GetBytes(sha256)));
        return Path.Combine(Directory, name[..2], name);
    }

    /// <summary>The file holding these bytes, in either form, or null.</summary>
    public string? Locate(string sha256)
    {
        ValidateHash(sha256);
        var plain = PlainPath(sha256); var sealedPath = SealedPath(sha256);
        foreach (var path in Encrypts ? [sealedPath, plain] : new[] { plain, sealedPath })
            if (path is not null && File.Exists(path)) return path;
        return null;
    }

    public bool Contains(string sha256) => Locate(sha256) is not null;

    /// <summary>Stores bytes and returns their SHA-256.</summary>
    public string Write(byte[] data)
    {
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(data));
        using var input = new MemoryStream(data, false);
        Write(input, sha256, data.LongLength, CancellationToken.None);
        return sha256;
    }

    /// <summary>Stores <paramref name="length"/> bytes from <paramref name="input"/> that must hash to <paramref name="sha256"/>.
    /// Existing bytes are only verified against the input, never written again.</summary>
    public void Write(Stream input, string sha256, long length, CancellationToken token)
    {
        ValidateHash(sha256);
        if (Contains(sha256)) { VerifyInput(input, sha256, length, token); return; }
        WriteForm(input, sha256, length, Encrypts, token);
    }

    private static void VerifyInput(Stream input, string sha256, long length, CancellationToken token)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[81920]; long total = 0; int read;
        while ((read = input.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); total += read; if (total > length) break; digest.AppendData(buffer, 0, read); }
        if (total != length || Convert.ToHexStringLower(digest.GetHashAndReset()) != sha256) throw new InvalidDataException(T("The picture bytes do not match their checksum."));
    }

    private void WriteForm(Stream input, string sha256, long length, bool encrypted, CancellationToken token)
    {
        var target = encrypted ? SealedPath(sha256)! : PlainPath(sha256);
        if (!File.Exists(StatePath)) SaveState(state with { FilesEncrypted = encrypted });
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920))
            using (var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                long total = 0;
                // Counts and hashes everything that passes, whichever form is written.
                int Read(Span<byte> buffer)
                {
                    token.ThrowIfCancellationRequested(); var read = input.Read(buffer);
                    total += read; if (total > length) throw new InvalidDataException(T("The picture bytes do not match their checksum."));
                    digest.AppendData(buffer[..read]); return read;
                }
                if (encrypted) Seal(output, sha256, Read);
                else { var buffer = new byte[81920]; int read; while ((read = Read(buffer)) > 0) output.Write(buffer, 0, read); }
                if (total != length || Convert.ToHexStringLower(digest.GetHashAndReset()) != sha256) throw new InvalidDataException(T("The picture bytes do not match their checksum."));
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private delegate int Reader(Span<byte> buffer);

    /// <summary>Header (magic, nonce prefix), then chunks of the plaintext "SHA-256 bytes + content", each followed by its tag.</summary>
    private void Seal(Stream output, string sha256, Reader read)
    {
        var prefix = RandomNumberGenerator.GetBytes(PrefixSize);
        var header = Magic.Concat(prefix).ToArray(); output.Write(header);
        using var aes = new AesGcm(contentKey!, TagSize);
        var current = new byte[ChunkSize]; var next = new byte[ChunkSize]; var cipher = new byte[ChunkSize]; var tag = new byte[TagSize];
        Convert.FromHexString(sha256).CopyTo(current, 0);
        var count = HashSize + Fill(current.AsSpan(HashSize));
        for (var index = 0; ; index++)
        {
            var nextCount = Fill(next); var final = nextCount == 0;
            aes.Encrypt(Nonce(prefix, index, final), current.AsSpan(0, count), cipher.AsSpan(0, count), tag, header);
            output.Write(cipher, 0, count); output.Write(tag);
            if (final) break;
            (current, next) = (next, current); count = nextCount;
        }
        int Fill(Span<byte> buffer) { var filled = 0; int got; while (filled < buffer.Length && (got = read(buffer[filled..])) > 0) filled += got; return filled; }
    }

    private static byte[] Nonce(byte[] prefix, int index, bool final)
    {
        var nonce = new byte[12]; prefix.CopyTo(nonce, 0);
        BinaryPrimitives.WriteInt32BigEndian(nonce.AsSpan(PrefixSize), index); nonce[11] = final ? (byte)1 : (byte)0;
        return nonce;
    }

    /// <summary>Copies the bytes to <paramref name="output"/> and verifies length and checksum. False when no file holds them;
    /// a damaged or foreign file throws <see cref="InvalidDataException"/>, possibly after part of it was written.</summary>
    public bool CopyTo(string sha256, long length, Stream output, CancellationToken token = default)
    {
        var path = Locate(sha256); if (path is null) return false;
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); long total = 0;
        void Emit(ReadOnlySpan<byte> data)
        {
            token.ThrowIfCancellationRequested(); total += data.Length;
            if (total > length) throw new InvalidDataException(T("A picture file is damaged."));
            digest.AppendData(data); output.Write(data);
        }
        if (path == PlainPath(sha256)) { var buffer = new byte[81920]; int read; while ((read = input.Read(buffer)) > 0) Emit(buffer.AsSpan(0, read)); }
        else if (Open(input, Emit) != sha256) throw new InvalidDataException(T("A picture file is damaged."));
        if (total != length || Convert.ToHexStringLower(digest.GetHashAndReset()) != sha256) throw new InvalidDataException(T("A picture file is damaged."));
        return true;
    }

    /// <summary>The bytes, or null when no file holds them. Damaged files throw <see cref="InvalidDataException"/>.</summary>
    public byte[]? Read(string sha256, long length)
    {
        if (length is < 0 or > int.MaxValue) throw new InvalidDataException(T("A picture file is damaged."));
        using var output = new MemoryStream((int)length);
        return CopyTo(sha256, length, output) ? output.ToArray() : null;
    }

    /// <summary>Decrypts one encrypted file chunk by chunk and returns the SHA-256 it carries.</summary>
    private string Open(FileStream input, Action<ReadOnlySpan<byte>> emit)
    {
        if (contentKey is null) throw new InvalidDataException(T("An encrypted picture file needs the unlocked library."));
        var header = new byte[Magic.Length + PrefixSize];
        if (input.ReadAtLeast(header, header.Length, false) != header.Length || !header.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException(T("A picture file is damaged."));
        var prefix = header[Magic.Length..];
        using var aes = new AesGcm(contentKey, TagSize);
        var sealedChunk = new byte[ChunkSize + TagSize]; var plain = new byte[ChunkSize]; string? sha256 = null;
        for (var index = 0; ; index++)
        {
            var remaining = input.Length - input.Position;
            var size = (int)Math.Min(ChunkSize + TagSize, remaining);
            if (size < TagSize || index == 0 && size < TagSize + HashSize) throw new InvalidDataException(T("A picture file is damaged."));
            input.ReadExactly(sealedChunk, 0, size);
            var final = size == remaining; var count = size - TagSize;
            try { aes.Decrypt(Nonce(prefix, index, final), sealedChunk.AsSpan(0, count), sealedChunk.AsSpan(count, TagSize), plain.AsSpan(0, count), header); }
            catch (AuthenticationTagMismatchException) { throw new InvalidDataException(T("A picture file is damaged or belongs to another library.")); }
            var content = plain.AsSpan(0, count);
            if (index == 0) { sha256 = Convert.ToHexStringLower(content[..HashSize]); content = content[HashSize..]; }
            emit(content);
            if (final) return sha256!;
        }
    }

    /// <summary>Turns encryption of the files on or off for an encrypted library and rewrites them.</summary>
    public void SetEncryptFiles(bool encrypt, IProgress<CatalogOpenProgress>? progress = null, CancellationToken token = default)
    {
        if (!HasKey) throw new InvalidOperationException(T("Only the pictures of an encrypted library can be encrypted."));
        SaveState(state with { EncryptFiles = encrypt });
        Rewrite(encrypt, progress, token);
    }

    /// <summary>Rewrites every file into the given form. Interrupted, the files stay readable and the next call continues.</summary>
    public void Rewrite(bool encrypt, IProgress<CatalogOpenProgress>? progress = null, CancellationToken token = default)
    {
        if (!HasKey) throw new InvalidOperationException(T("Only the pictures of an encrypted library can be encrypted."));
        if (!System.IO.Directory.Exists(Directory)) { state = state with { FilesEncrypted = encrypt }; return; }
        SaveState(state with { FilesEncrypted = null });
        var done = 0; var files = Files().ToList();
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            if (!IsSealed(file))
            {
                if (encrypt) { using (var input = file.OpenRead()) WriteForm(input, file.Name, file.Length, true, token); file.Delete(); }
            }
            else if (!encrypt)
            {
                using var plain = new MemoryStream(); string sha256;
                using (var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read)) sha256 = Open(input, data => plain.Write(data));
                if (!string.Equals(SealedPath(sha256), file.FullName, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(T("A picture file is damaged."));
                plain.Position = 0; WriteForm(plain, sha256, plain.Length, false, token); file.Delete();
            }
            progress?.Report(new(CatalogOpenPhase.PictureEncryption, ++done, files.Count));
        }
        SaveState(state with { FilesEncrypted = encrypt });
    }

    /// <summary>Whether a file is encrypted: it starts with the marker and is not plain bytes that happen to start alike
    /// (plain files hash to their name).</summary>
    private static bool IsSealed(FileInfo file)
    {
        using var input = file.OpenRead(); var start = new byte[Magic.Length];
        if (input.ReadAtLeast(start, start.Length, false) != start.Length || !start.AsSpan().SequenceEqual(Magic)) return false;
        input.Position = 0; return Convert.ToHexStringLower(SHA256.HashData(input)) != file.Name;
    }

    /// <summary>All stored files, without the state file and without leftovers of interrupted writes.</summary>
    public IEnumerable<FileInfo> Files()
    {
        if (!System.IO.Directory.Exists(Directory)) yield break;
        foreach (var folder in new DirectoryInfo(Directory).EnumerateDirectories())
            foreach (var file in folder.EnumerateFiles())
                if (IsHash(file.Name)) yield return file;
    }

    /// <summary>Deletes every file whose bytes are not among <paramref name="referenced"/>, and leftovers of interrupted writes.
    /// Returns the number and size of the deleted files.</summary>
    public (int Files, long Bytes) Collect(IEnumerable<string> referenced, CancellationToken token = default)
    {
        if (!System.IO.Directory.Exists(Directory)) return (0, 0);
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sha256 in referenced) { keep.Add(PlainPath(sha256)); if (SealedPath(sha256) is { } sealedPath) keep.Add(sealedPath); }
        int count = 0; long bytes = 0;
        foreach (var file in Unused(keep, token)) { var size = file.Length; file.Delete(); count++; bytes += size; }
        return (count, bytes);
    }

    /// <summary>Files that hold none of the bytes in <paramref name="keep"/> (full paths), including leftovers of interrupted writes.</summary>
    internal IEnumerable<FileInfo> Unused(IReadOnlySet<string> keep, CancellationToken token)
    {
        if (!System.IO.Directory.Exists(Directory)) yield break;
        foreach (var folder in new DirectoryInfo(Directory).EnumerateDirectories())
            foreach (var file in folder.EnumerateFiles())
            {
                token.ThrowIfCancellationRequested();
                if (!keep.Contains(file.FullName)) yield return file;
            }
    }

    /// <summary>Full paths of the files that may hold <paramref name="sha256"/>.</summary>
    internal IEnumerable<string> Candidates(string sha256)
    {
        yield return PlainPath(sha256);
        if (SealedPath(sha256) is { } sealedPath) yield return sealedPath;
    }

    /// <summary>Removes the file of bytes the catalog no longer uses.</summary>
    public void Delete(string sha256)
    {
        foreach (var path in Candidates(sha256)) if (File.Exists(path)) File.Delete(path);
    }

    private static bool IsHash(string name) => name.Length == 64 && name.All(char.IsAsciiHexDigitLower);

    internal static void ValidateHash(string sha256)
    {
        if (sha256 is null || !IsHash(sha256)) throw new InvalidDataException("Invalid picture checksum.");
    }

    public void Dispose()
    {
        if (contentKey is not null) CryptographicOperations.ZeroMemory(contentKey);
        if (nameKey is not null) CryptographicOperations.ZeroMemory(nameKey);
    }
}
