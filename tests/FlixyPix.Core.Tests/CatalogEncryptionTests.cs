using System.Security.Cryptography;
using System.Text;
using FlixyPix.Core;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class CatalogEncryptionTests : IDisposable
{
    private const string Password = "korrekt-pferd-batterie";
    private readonly string root = Path.Combine(Path.GetTempPath(), "flixypix-encryption-test-" + Guid.NewGuid().ToString("N"));
    public CatalogEncryptionTests() => Directory.CreateDirectory(root);

    private string Library(string title = "Geheimer Titel")
    {
        var database = Path.Combine(root, Guid.NewGuid().ToString("N"), "catalog.db");
        using var catalog = new CatalogRepository(database);
        catalog.Upsert(new() { Path = Path.Combine(root, "offline", "clip.mp4"), Title = title, Rating = 8 });
        return database;
    }

    private static bool ContainsAscii(string path, string text) =>
        File.ReadAllBytes(path).AsSpan().IndexOf(Encoding.UTF8.GetBytes(text)) >= 0;

    [Fact] public void EnableEncryptsFileAndRequiresPasswordOrRecoveryKey()
    {
        var database = Library();
        Assert.True(ContainsAscii(database, "Geheimer Titel"));
        var result = CatalogEncryption.EnableEncryption(database, Password);
        Assert.True(CatalogEncryption.IsEncrypted(database));
        Assert.False(ContainsAscii(database, "Geheimer Titel"));
        Assert.Equal(64, result.RecoveryKey.Count(char.IsLetterOrDigit) + 12);
        Assert.Throws<CatalogLockedException>(() => new CatalogRepository(database));
        Assert.Throws<CryptographicException>(() => CatalogEncryption.Unlock(database, "falsches-passwort"));

        using (var key = CatalogEncryption.Unlock(database, Password))
        using (var catalog = new CatalogRepository(database, key))
            Assert.Equal("Geheimer Titel", Assert.Single(catalog.Search()).Title);
        using (var key = CatalogEncryption.Unlock(database, result.RecoveryKey.ToLowerInvariant().Replace("-", " ")))
        using (var catalog = new CatalogRepository(database, key))
            Assert.Equal(8, Assert.Single(catalog.Search()).Rating);
    }

    [Fact] public void WritesAfterReopenStayEncryptedAndWalIsCheckpointedEncrypted()
    {
        var database = Library();
        CatalogEncryption.EnableEncryption(database, Password);
        using (var key = CatalogEncryption.Unlock(database, Password))
        using (var catalog = new CatalogRepository(database, key))
            catalog.Upsert(new() { Path = Path.Combine(root, "offline", "zweiter.mp4"), Title = "Nachtrag im Klartext?" });
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(database)!))
            Assert.False(ContainsAscii(file, "Nachtrag im Klartext?"), file);
        using var reopened = CatalogEncryption.Unlock(database, Password);
        using var check = new CatalogRepository(database, reopened);
        Assert.Equal(2, check.Search().Count);
    }

    [Fact] public void ChangePasswordKeepsRecoveryKeyAndDisableRestoresPlainCatalog()
    {
        var database = Library();
        var recovery = CatalogEncryption.EnableEncryption(database, Password).RecoveryKey;
        Assert.Throws<ArgumentException>(() => CatalogEncryption.ChangePassword(database, Password, "kurz"));
        CatalogEncryption.ChangePassword(database, recovery, "ein-neues-passwort");
        Assert.Throws<CryptographicException>(() => CatalogEncryption.Unlock(database, Password));
        CatalogEncryption.Unlock(database, "ein-neues-passwort").Dispose();
        CatalogEncryption.Unlock(database, recovery).Dispose();

        CatalogEncryption.DisableEncryption(database, "ein-neues-passwort");
        Assert.False(CatalogEncryption.IsEncrypted(database));
        Assert.False(File.Exists(CatalogEncryption.KeyFilePath(database)));
        using var catalog = new CatalogRepository(database);
        Assert.Equal("Geheimer Titel", Assert.Single(catalog.Search()).Title);
    }

    [Fact] public void InterruptedEnableWithoutKeyDecisionKeepsOriginalCatalog()
    {
        var database = Library();
        File.Copy(database, database + ".rekey");
        Assert.False(CatalogEncryption.IsEncrypted(database));
        Assert.False(File.Exists(database + ".rekey"));
        using var catalog = new CatalogRepository(database);
        Assert.Single(catalog.Search());
    }

    [Fact] public void EncryptedExportImportsWithSecretOnly()
    {
        var database = Library();
        var recovery = CatalogEncryption.EnableEncryption(database, Password).RecoveryKey;
        var export = Path.Combine(root, "export.json");
        using (var key = CatalogEncryption.Unlock(database, Password))
        using (var catalog = new CatalogRepository(database, key)) catalog.Export(export);
        Assert.True(EncryptedContainer.IsContainer(export));
        Assert.False(ContainsAscii(export, "Geheimer Titel"));

        using var target = new CatalogRepository(Path.Combine(root, "target", "catalog.db"));
        Assert.Throws<CatalogLockedException>(() => target.ImportJson(export));
        Assert.Throws<CryptographicException>(() => target.ImportJson(export, CatalogRepository.MaximumPortableEntityBytes, default, "falsch-falsch"));
        var result = target.ImportJson(export, CatalogRepository.MaximumPortableEntityBytes, default, recovery);
        Assert.Empty(result.Errors);
        Assert.Equal("Geheimer Titel", Assert.Single(target.Search()).Title);
    }

    [Fact] public async Task EncryptedBackupRestoresAsEncryptedLibrary()
    {
        var database = Library();
        CatalogEncryption.EnableEncryption(database, Password);
        var package = Path.Combine(root, "backup" + ProductIdentity.BackupExtension);
        using (var key = CatalogEncryption.Unlock(database, Password))
        using (var catalog = new CatalogRepository(database, key)) await catalog.CreateBackupAsync(package, false);
        Assert.True(EncryptedContainer.IsContainer(package));
        Assert.False(ContainsAscii(package, "manifest.ndjson"));

        await Assert.ThrowsAsync<CatalogLockedException>(() => CatalogRepository.RestoreBackupAsync(package, Path.Combine(root, "locked")));
        Assert.False(Directory.Exists(Path.Combine(root, "locked")));
        var restored = await CatalogRepository.RestoreBackupAsync(package, Path.Combine(root, "restored"), secret: Password);
        Assert.True(CatalogEncryption.IsEncrypted(restored.DatabasePath));
        Assert.False(ContainsAscii(restored.DatabasePath, "Geheimer Titel"));
        using var restoredKey = CatalogEncryption.Unlock(restored.DatabasePath, Password);
        using var check = new CatalogRepository(restored.DatabasePath, restoredKey);
        Assert.Equal("Geheimer Titel", Assert.Single(check.Search()).Title);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    [InlineData(EncryptedContainer.ChunkSize)]
    [InlineData(EncryptedContainer.ChunkSize * 2 + 5)]
    public void ContainerRoundTripsSeeksAndRejectsTamperingAndTruncation(int size)
    {
        var database = Library();
        var recovery = CatalogEncryption.EnableEncryption(database, Password).RecoveryKey;
        var slots = CatalogEncryption.ReadSlots(database);
        var payload = RandomNumberGenerator.GetBytes(size);
        var path = Path.Combine(root, $"container-{size}.bin");
        using (var key = CatalogEncryption.Unlock(slots, Password))
        using (var file = File.Create(path))
        using (var writer = EncryptedContainer.Create(file, EncryptedContainer.BackupContent, slots, key))
            for (var offset = 0; offset < size; offset += 70_001) writer.Write(payload, offset, Math.Min(70_001, size - offset));

        using (var reader = EncryptedContainer.OpenRead(path, EncryptedContainer.BackupContent, recovery))
        {
            Assert.Equal(size, reader.Length);
            using var copy = new MemoryStream(); reader.CopyTo(copy); Assert.Equal(payload, copy.ToArray());
            if (size > 10) { reader.Position = size - 3; var tail = new byte[8]; Assert.Equal(3, reader.Read(tail)); Assert.Equal(payload[^3..], tail[..3]); }
        }
        Assert.Throws<InvalidDataException>(() => EncryptedContainer.OpenRead(path, EncryptedContainer.PortableContent, Password).Dispose());

        var bytes = File.ReadAllBytes(path);
        var tampered = (byte[])bytes.Clone(); tampered[^1] ^= 1;
        File.WriteAllBytes(path, tampered);
        Assert.Throws<InvalidDataException>(() => EncryptedContainer.OpenRead(path, EncryptedContainer.BackupContent, Password).Dispose());
        if (size >= EncryptedContainer.ChunkSize)
        {
            // Dropping the final chunk leaves a complete but non-final chunk, which must be rejected.
            var finalPlain = size % EncryptedContainer.ChunkSize == 0 ? EncryptedContainer.ChunkSize : size % EncryptedContainer.ChunkSize;
            File.WriteAllBytes(path, bytes[..^(finalPlain + 16)]);
            Assert.Throws<InvalidDataException>(() => EncryptedContainer.OpenRead(path, EncryptedContainer.BackupContent, Password).Dispose());
        }
    }

    [Fact] public void RecoveryKeyFormatRoundTrips()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var text = CatalogEncryption.FormatRecoveryKey(bytes);
        Assert.Equal(13, text.Split('-').Length);
        Assert.True(CatalogEncryption.TryParseRecoveryKey(text, out var parsed)); Assert.Equal(bytes, parsed);
        Assert.False(CatalogEncryption.TryParseRecoveryKey(text + "A", out _));
        Assert.False(CatalogEncryption.TryParseRecoveryKey(text.Replace(text[0], '0'), out _));
    }

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }
}
