using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

/// <summary>
/// Random 256-bit data key of one encrypted library. SQLite3 Multiple Ciphers encrypts the
/// catalog with it; the user's password and the recovery key only wrap this key.
/// </summary>
public sealed class CatalogKey : IDisposable
{
    private readonly byte[] material;
    internal CatalogKey(byte[] material)
    {
        if (material.Length != 32) throw new ArgumentException("Invalid catalog key length.", nameof(material));
        this.material = material;
    }
    internal ReadOnlySpan<byte> Material => material;
    /// <summary>Hex text handed to SQLite as key; it carries the full 256 bits of entropy.</summary>
    internal string SqliteKey => Convert.ToHexStringLower(material);
    internal CatalogKey Clone() => new((byte[])material.Clone());
    public void Dispose() => CryptographicOperations.ZeroMemory(material);
}

public sealed record CatalogKeySlot(string Kdf, int Iterations, string Salt, string Nonce, string Ciphertext, string Tag);

/// <summary>Wrapped copies of the data key. Safe to store next to the catalog or in a container header.</summary>
public sealed record CatalogKeySlots(string Format, int Version, CatalogKeySlot Password, CatalogKeySlot Recovery);

public sealed record CatalogEncryptionResult(string RecoveryKey);

public sealed class CatalogLockedException(string message) : InvalidOperationException(message);

public static class CatalogEncryption
{
    public const string KeyFileSuffix = ".keys.json";
    public const int MinimumPasswordLength = 8;
    private const string SlotFormat = "flixypix-catalog-keys";
    private const int PasswordIterations = 600_000;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static string KeyFilePath(string databasePath) => Path.GetFullPath(databasePath) + KeyFileSuffix;
    private static string PendingKeyFilePath(string databasePath) => KeyFilePath(databasePath) + ".pending";

    /// <summary>True when the library has a key file and therefore needs an unlock secret.</summary>
    public static bool IsEncrypted(string databasePath)
    {
        CompletePendingChange(databasePath);
        return File.Exists(KeyFilePath(databasePath));
    }

    public static CatalogKeySlots ReadSlots(string databasePath)
    {
        CompletePendingChange(databasePath);
        return ParseSlots(File.ReadAllText(KeyFilePath(databasePath), Encoding.UTF8));
    }

    internal static CatalogKeySlots ParseSlots(string text)
    {
        var slots = JsonSerializer.Deserialize<CatalogKeySlots>(text, Json) ?? throw new InvalidDataException(T("Invalid key file."));
        if (slots.Format != SlotFormat || slots.Version != 1 || slots.Password is null || slots.Recovery is null)
            throw new InvalidDataException(T("Unknown key format."));
        return slots;
    }
    internal static string SerializeSlots(CatalogKeySlots slots) => JsonSerializer.Serialize(slots, Json);

    /// <summary>Unlocks with the password or, failing that, with the recovery key.</summary>
    public static CatalogKey Unlock(CatalogKeySlots slots, string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (TryUnwrap(slots.Password, PasswordKek(secret, slots.Password), "password", out var key)) return key;
        if (TryParseRecoveryKey(secret, out var recovery))
        {
            try { if (TryUnwrap(slots.Recovery, RecoveryKek(recovery, slots.Recovery), "recovery", out key)) return key; }
            finally { CryptographicOperations.ZeroMemory(recovery); }
        }
        throw new CryptographicException(T("The password or recovery key is wrong."));
    }

    public static CatalogKey Unlock(string databasePath, string secret) => Unlock(ReadSlots(databasePath), secret);

    /// <summary>Encrypts an unencrypted library in place. No repository may have it open.</summary>
    public static CatalogEncryptionResult EnableEncryption(string databasePath, string password, IProgress<CatalogOpenProgress>? progress = null)
    {
        ValidatePassword(password);
        var database = Path.GetFullPath(databasePath);
        EnsureWritable(database);
        if (IsEncrypted(database)) throw new InvalidOperationException(T("The library is already encrypted."));
        using var key = new CatalogKey(RandomNumberGenerator.GetBytes(32));
        var recovery = RandomNumberGenerator.GetBytes(32);
        try
        {
            var slots = new CatalogKeySlots(SlotFormat, 1,WrapWithPassword(key, password), WrapWithRecovery(key, recovery));
            Rekey(database, null, key);
            // The pending key file is written before the encrypted catalog replaces the original,
            // so an interruption never leaves an encrypted catalog without its wrapped keys.
            ReplaceCatalog(database, slots);
            // The picture files follow: opening the store with the key encrypts them (unless the library chose otherwise before).
            new AssetStore(database, key, progress).Dispose();
            return new(FormatRecoveryKey(recovery));
        }
        finally { CryptographicOperations.ZeroMemory(recovery); }
    }

    /// <summary>Removes encryption after unlocking. No repository may have the library open.</summary>
    public static void DisableEncryption(string databasePath, string secret, IProgress<CatalogOpenProgress>? progress = null)
    {
        var database = Path.GetFullPath(databasePath);
        EnsureWritable(database);
        using var key = Unlock(database, secret);
        // The picture files become plain first, while the key still exists; an interruption leaves an encrypted library.
        using (var store = new AssetStore(database, key, progress)) store.Rewrite(false, progress);
        Rekey(database, key, null);
        ReplaceCatalog(database, null);
    }

    /// <summary>Re-wraps the data key; the catalog itself and the recovery key stay unchanged.</summary>
    public static void ChangePassword(string databasePath, string currentSecret, string newPassword)
    {
        ValidatePassword(newPassword);
        EnsureWritable(Path.GetFullPath(databasePath));
        var slots = ReadSlots(databasePath);
        using var key = Unlock(slots, currentSecret);
        WriteKeyFileAtomically(KeyFilePath(databasePath), slots with { Password = WrapWithPassword(key, newPassword) });
    }

    private static void EnsureWritable(string database)
    {
        foreach (var path in new[] { database, database + ".rekey", KeyFilePath(database), PendingKeyFilePath(database), PendingKeyFilePath(database) + ".remove" })
            FileAccessPolicy.Current.EnsureWritable(path);
    }

    public static void ValidatePassword(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinimumPasswordLength)
            throw new ArgumentException(Tn(MinimumPasswordLength, "The password must be at least 1 character long.", "The password must be at least {0} characters long."), nameof(password));
    }

    internal static SqliteConnectionStringBuilder ConnectionString(string databasePath, CatalogKey? key, SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = mode, Pooling = false, DefaultTimeout = 10 };
        if (key is not null) builder.Password = key.SqliteKey;
        return builder;
    }

    /// <summary>Finishes an interrupted enable/disable so that catalog and key file always agree.</summary>
    internal static void CompletePendingChange(string databasePath)
    {
        var database = Path.GetFullPath(databasePath);
        var staged = database + ".rekey";
        var pending = PendingKeyFilePath(database);
        var removal = pending + ".remove";
        if (File.Exists(staged))
        {
            // The staged catalog is complete once a key decision exists; otherwise discard it.
            if (File.Exists(pending)) { File.Move(staged, database, true); File.Move(pending, KeyFilePath(database), true); }
            else if (File.Exists(removal)) { File.Move(staged, database, true); File.Delete(KeyFilePath(database)); File.Delete(removal); }
            else File.Delete(staged);
        }
        else
        {
            if (File.Exists(pending)) File.Move(pending, KeyFilePath(database), true);
            if (File.Exists(removal)) { File.Delete(KeyFilePath(database)); File.Delete(removal); }
        }
    }

    private static void Rekey(string database, CatalogKey? current, CatalogKey? next)
    {
        var staged = database + ".rekey";
        if (File.Exists(staged)) File.Delete(staged);
        using (var source = new SqliteConnection(ConnectionString(database, current, SqliteOpenMode.ReadWrite).ToString()))
        {
            source.Open();
            using var checkpoint = source.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            checkpoint.ExecuteNonQuery();
        }
        File.Copy(database, staged);
        try
        {
            using (var connection = new SqliteConnection(ConnectionString(staged, current, SqliteOpenMode.ReadWrite).ToString()))
            {
                connection.Open();
                // Changing the reserved page bytes makes rekey rebuild the file like VACUUM, including indexes.
                CatalogRepository.AddOrdinalCollation(connection);
                using var command = connection.CreateCommand();
                // SQLite3 Multiple Ciphers cannot rekey in WAL mode; the repository restores WAL on open.
                command.CommandText = "PRAGMA journal_mode=DELETE;";
                command.ExecuteNonQuery();
                command.CommandText = "SELECT quote($key);";
                command.Parameters.AddWithValue("$key", next?.SqliteKey ?? "");
                var quoted = (string)command.ExecuteScalar()!;
                command.Parameters.Clear();
                command.CommandText = $"PRAGMA rekey={quoted};";
                command.ExecuteNonQuery();
            }
            using (var verify = new SqliteConnection(ConnectionString(staged, next, SqliteOpenMode.ReadOnly).ToString()))
            {
                verify.Open();
                CatalogRepository.AddOrdinalCollation(verify);
                using var check = verify.CreateCommand();
                check.CommandText = "PRAGMA integrity_check;";
                if (!string.Equals(check.ExecuteScalar() as string, "ok", StringComparison.Ordinal))
                    throw new InvalidDataException(T("The re-keyed library fails the integrity check."));
            }
        }
        catch { File.Delete(staged); throw; }
    }

    private static void ReplaceCatalog(string database, CatalogKeySlots? slots)
    {
        var pending = PendingKeyFilePath(database);
        if (slots is not null) WriteKeyFileAtomically(pending, slots);
        else File.WriteAllText(pending + ".remove", "");
        foreach (var sidecar in new[] { database + "-wal", database + "-shm" }) if (File.Exists(sidecar)) File.Delete(sidecar);
        CompletePendingChange(database);
    }

    private static void WriteKeyFileAtomically(string path, CatalogKeySlots slots)
    {
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(Encoding.UTF8.GetBytes(SerializeSlots(slots)));
            stream.Flush(true);
        }
        File.Move(temporary, path, true);
    }

    internal static CatalogKeySlot WrapWithPassword(CatalogKey key, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var kek = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, PasswordIterations, HashAlgorithmName.SHA256, 32);
        try { return Wrap(key, kek, "pbkdf2-sha256", PasswordIterations, salt, "password"); }
        finally { CryptographicOperations.ZeroMemory(kek); }
    }

    private static CatalogKeySlot WrapWithRecovery(CatalogKey key, byte[] recovery)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var kek = HKDF.DeriveKey(HashAlgorithmName.SHA256, recovery, 32, salt, Encoding.ASCII.GetBytes("flixypix-recovery-v1"));
        try { return Wrap(key, kek, "hkdf-sha256", 0, salt, "recovery"); }
        finally { CryptographicOperations.ZeroMemory(kek); }
    }

    private static byte[] PasswordKek(string password, CatalogKeySlot slot)
    {
        if (slot.Kdf != "pbkdf2-sha256" || slot.Iterations is < 100_000 or > 10_000_000) throw new InvalidDataException(T("Unknown key derivation."));
        return Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), Convert.FromBase64String(slot.Salt), slot.Iterations, HashAlgorithmName.SHA256, 32);
    }

    private static byte[] RecoveryKek(byte[] recovery, CatalogKeySlot slot)
    {
        if (slot.Kdf != "hkdf-sha256") throw new InvalidDataException(T("Unknown key derivation."));
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, recovery, 32, Convert.FromBase64String(slot.Salt), Encoding.ASCII.GetBytes("flixypix-recovery-v1"));
    }

    private static CatalogKeySlot Wrap(CatalogKey key, byte[] kek, string kdf, int iterations, byte[] salt, string purpose)
    {
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var ciphertext = new byte[32]; var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        using var aes = new AesGcm(kek, tag.Length);
        aes.Encrypt(nonce, key.Material, ciphertext, tag, Encoding.ASCII.GetBytes("flixypix-catalog-key-v1|" + purpose));
        return new(kdf, iterations, Convert.ToBase64String(salt), Convert.ToBase64String(nonce), Convert.ToBase64String(ciphertext), Convert.ToBase64String(tag));
    }

    private static bool TryUnwrap(CatalogKeySlot slot, byte[] kek, string purpose, out CatalogKey key)
    {
        var material = new byte[32];
        try
        {
            var tag = Convert.FromBase64String(slot.Tag);
            using var aes = new AesGcm(kek, tag.Length);
            aes.Decrypt(Convert.FromBase64String(slot.Nonce), Convert.FromBase64String(slot.Ciphertext), tag, material, Encoding.ASCII.GetBytes("flixypix-catalog-key-v1|" + purpose));
            key = new CatalogKey(material); return true;
        }
        catch (AuthenticationTagMismatchException) { CryptographicOperations.ZeroMemory(material); key = null!; return false; }
        finally { CryptographicOperations.ZeroMemory(kek); }
    }

    private const string Base32 = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>Recovery keys use an unambiguous 32-letter alphabet in groups of four.</summary>
    public static string FormatRecoveryKey(byte[] bytes)
    {
        var text = new StringBuilder(); int buffer = 0, bits = 0;
        foreach (var value in bytes)
        {
            buffer = (buffer << 8) | value; bits += 8;
            while (bits >= 5) { text.Append(Base32[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) text.Append(Base32[(buffer << (5 - bits)) & 31]);
        return string.Join('-', text.ToString().Chunk(4).Select(chunk => new string(chunk)));
    }

    public static bool TryParseRecoveryKey(string text, out byte[] bytes)
    {
        var symbols = text.Where(c => c is not ('-' or ' ')).Select(char.ToUpperInvariant).ToArray();
        bytes = [];
        if (symbols.Length != 52) return false;
        var output = new byte[32]; int buffer = 0, bits = 0, index = 0;
        foreach (var symbol in symbols)
        {
            var value = Base32.IndexOf(symbol);
            if (value < 0) { CryptographicOperations.ZeroMemory(output); return false; }
            buffer = (buffer << 5) | value; bits += 5;
            if (bits >= 8 && index < 32) { output[index++] = (byte)(buffer >> (bits - 8)); bits -= 8; }
        }
        bytes = output; return index == 32;
    }
}
