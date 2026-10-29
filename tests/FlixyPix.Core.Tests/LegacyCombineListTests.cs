using System.Security.Cryptography;
using System.Text;
using FlixyPix.Core;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class LegacyCombineListTests : IDisposable
{
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "flixypix-combine-reader-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void EncryptedDictionaryPreservesOrderRangesOfflinePathsAndEverySourceByte()
    {
        var bytes = Encrypt(Payload()); var draft = LegacyCombineListReader.Read(bytes, "fixture-source");
        Assert.Equal(HistoricalProjectionStatus.Partial, draft.Status); Assert.False(draft.IsExecutable);
        Assert.Equal(bytes, Convert.FromBase64String(draft.PayloadBase64));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), draft.PayloadSha256);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Payload())), draft.PlaintextSha256);
        Assert.Equal(new[] { 0, 1 }, draft.Entries.Select(entry => entry.Order));
        Assert.Equal(new[] { @"U:\Filme\München.mp4", @"U:\Filme\Zwei.mp4" }, draft.Entries.Select(entry => entry.InputPath));
        Assert.Equal(37, draft.Entries[0].StartSeconds); Assert.Equal(42, draft.Entries[0].EndSeconds);
        Assert.True(draft.Entries[0].IsClip); Assert.Empty(draft.Entries.SelectMany(entry => entry.BlockingIssues));
        Assert.Equal(draft.Id, LegacyCombineListReader.Read(bytes, "fixture-source").Id);
        Assert.NotEqual(draft.Id, LegacyCombineListReader.Read(bytes, "another-source").Id);
    }

    [Fact]
    public void ActiveUntranslatedAndUnknownOptionsRemainBlockingWhileBytesSurvive()
    {
        var bytes = Encrypt(Payload(unsupported: true)); var draft = LegacyCombineListReader.Read(bytes, "source");
        Assert.Equal(2, draft.Entries.Count);
        // Crossfade is translated since phase C; it is carried as an option instead of blocking the entry.
        Assert.True(draft.Entries[0].Crossfade); Assert.False(draft.Entries[1].Crossfade);
        Assert.DoesNotContain(draft.Entries[0].BlockingIssues, issue => issue.Contains("_crossfade"));
        Assert.Contains(draft.Entries[0].BlockingIssues, issue => issue.Contains("_futureOption"));
        Assert.Equal(bytes, Convert.FromBase64String(draft.PayloadBase64));
    }

    [Fact]
    public void WrongEncryptionForeignRootMalformedAndTrailingRecordsRemainOpaque()
    {
        foreach (var bytes in new[] { new byte[9], Encrypt([0, 1, 2]), Encrypt([.. Payload(), 0]), Encrypt(ForeignPayload()) })
        {
            var draft = LegacyCombineListReader.Read(bytes, "source");
            Assert.Equal(HistoricalProjectionStatus.Opaque, draft.Status); Assert.Empty(draft.Entries);
            Assert.Equal(bytes, Convert.FromBase64String(draft.PayloadBase64)); Assert.NotEmpty(draft.Warnings);
        }
        Assert.Throws<InvalidDataException>(() => LegacyCombineListReader.Read(new byte[7], "source"));
        Assert.Throws<InvalidDataException>(() => LegacyCombineListReader.Read(new byte[LegacyObjectReader.MaximumPayloadBytes + 1], "source"));
    }

    [Fact]
    public void DuplicateKeysAndUnboundedArrayAreNotMaterializedAsUsableLists()
    {
        Assert.Equal(HistoricalProjectionStatus.Opaque, LegacyCombineListReader.Read(Encrypt(Payload(duplicate: true)), "source").Status);
        Assert.Equal(HistoricalProjectionStatus.Opaque, LegacyCombineListReader.Read(Encrypt(Payload(arrayLength: 100_000_000)), "source").Status);
    }

    [Fact]
    public void RepositoryImportIsIdempotentCancellationSafeAndPortable()
    {
        Directory.CreateDirectory(scratch); var bytes = Encrypt(Payload()); var path = Path.Combine(scratch, "Liste.ezl"); File.WriteAllBytes(path, bytes);
        using var catalog = new CatalogRepository(Path.Combine(scratch, "source.db"));
        var first = catalog.ImportLegacyCombineList(path, "archive"); var repeat = catalog.ImportLegacyCombineList(path, "archive");
        Assert.Equal(first.Id, repeat.Id); Assert.Single(catalog.GetLegacyCombineLists()); Assert.Equal(bytes, File.ReadAllBytes(path));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => catalog.ImportLegacyCombineList(path, "cancelled-source", cancel.Token));
        Assert.Single(catalog.GetLegacyCombineLists());
        var portable = Path.Combine(scratch, "portable.json"); catalog.Export(portable);
        using var imported = new CatalogRepository(Path.Combine(scratch, "restored.db")); imported.ImportJson(portable);
        var restored = Assert.Single(imported.GetLegacyCombineLists()); Assert.Equal(first.Id, restored.Id);
        Assert.Equal(first.PayloadSha256, restored.PayloadSha256); Assert.Equal(bytes, Convert.FromBase64String(restored.PayloadBase64));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(first.Entries), System.Text.Json.JsonSerializer.Serialize(restored.Entries));
    }

    // Handwritten MS-NRBF records: no BinaryFormatter, original assembly or original object is used.
    private static byte[] Payload(bool unsupported = false, bool duplicate = false, int arrayLength = 2)
    {
        const string type = "MyMediaViewer.frmVideoTools+myVideos";
        const string args = "[[System.Guid, mscorlib],[MyMediaViewer.frmVideoTools+myVideos, Absent.Original.Assembly]]";
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        Header(writer); writer.Write((byte)12); writer.Write(99); writer.Write("Absent.Original.Assembly, Version=4.8.4.0, Culture=neutral, PublicKeyToken=null");
        Class(writer, 1, "System.Collections.Generic.Dictionary`2" + args, ["HashSize", "KeyValuePairs"], [0, 2], [8]); writer.Write(2);
        writer.Write((byte)7); writer.Write(2); writer.Write((byte)0); writer.Write(1); writer.Write(arrayLength);
        writer.Write((byte)3); writer.Write("System.Collections.Generic.KeyValuePair`2" + args);
        if (arrayLength > 2) { writer.Write((byte)14); writer.Write(arrayLength); writer.Write((byte)11); writer.Flush(); return stream.ToArray(); }
        var record = 10;
        for (var index = 0; index < 2; index++)
        {
            Class(writer, record++, "System.Collections.Generic.KeyValuePair`2" + args, ["key", "value"], [2, 2], []);
            GuidRecord(writer, record++, index == 0 || duplicate ? new("11111111-1111-1111-1111-111111111111") : new("22222222-2222-2222-2222-222222222222"));
            string[] members = ["_id", "_starttime", "_endtime", "_isClip", "_video", "_crossfade", "_guid"];
            byte[] kinds = [0, 0, 0, 0, 2, 0, 2]; byte[] primitives = [8, 8, 8, 1, 1];
            if (unsupported) { members = [.. members, "_futureOption"]; kinds = [.. kinds, 0]; primitives = [.. primitives, 1]; }
            Class(writer, record++, type, members, kinds, primitives, true);
            writer.Write(index + 1); writer.Write(index == 0 ? 37 : 0); writer.Write(index == 0 ? 42 : 15); writer.Write(index == 0);
            Class(writer, record++, "MyMediaViewer.movies", ["_fullpath", "_filename", "_width", "_height"], [1, 1, 0, 0], [8, 8], true);
            String(writer, record++, @"U:\Filme\"); String(writer, record++, index == 0 ? "München.mp4" : "Zwei.mp4"); writer.Write(160); writer.Write(90);
            writer.Write(unsupported && index == 0);
            GuidRecord(writer, record++, new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")); // Separate constructor-created GUID.
            if (unsupported) writer.Write(true);
        }
        writer.Write((byte)11); writer.Flush(); return stream.ToArray();
    }
    private static byte[] ForeignPayload()
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        Header(writer); String(writer, 1, "not a combine dictionary"); writer.Write((byte)11); writer.Flush(); return stream.ToArray();
    }
    private static byte[] Encrypt(byte[] value)
    {
        using var cipher = DES.Create(); cipher.Key = Convert.FromHexString("657A6670657A6670"); cipher.IV = cipher.Key;
        using var encrypt = cipher.CreateEncryptor(); return encrypt.TransformFinalBlock(value, 0, value.Length);
    }
    private static void Header(BinaryWriter writer) { writer.Write((byte)0); writer.Write(1); writer.Write(-1); writer.Write(1); writer.Write(0); }
    private static void String(BinaryWriter writer, int id, string value) { writer.Write((byte)6); writer.Write(id); writer.Write(value); }
    private static void Class(BinaryWriter writer, int id, string name, string[] members, byte[] kinds, byte[] primitiveTypes, bool library = false)
    {
        writer.Write(library ? (byte)5 : (byte)4); writer.Write(id); writer.Write(name); writer.Write(members.Length);
        foreach (var member in members) writer.Write(member);
        writer.Write(kinds); writer.Write(primitiveTypes); if (library) writer.Write(99);
    }
    private static void GuidRecord(BinaryWriter writer, int id, Guid value)
    {
        Class(writer, id, "System.Guid", ["_a", "_b", "_c", "_d", "_e", "_f", "_g", "_h", "_i", "_j", "_k"], new byte[11], [8, 7, 7, 2, 2, 2, 2, 2, 2, 2, 2]); writer.Write(value.ToByteArray());
    }
    public void Dispose() { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
}
