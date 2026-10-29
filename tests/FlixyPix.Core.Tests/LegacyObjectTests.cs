using System.Security.Cryptography;
using System.Text;
using FlixyPix.Core;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class LegacyObjectTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "flixypix-nrbf-" + Guid.NewGuid().ToString("N"));
    private string FileFor(byte[] bytes)
    {
        Directory.CreateDirectory(directory); var path = Path.Combine(directory, Guid.NewGuid() + ".bin"); File.WriteAllBytes(path, bytes); return path;
    }

    [Fact]
    public void KnownRuleDictionaryMapsGuidGroupsOrSemanticsAndRetainsEveryByte()
    {
        var bytes = RulePayload(); var file = FileFor(bytes);
        var hash = SHA256.HashData(bytes);
        var rule = LegacyObjectReader.ReadTagRule(file);
        Assert.Equal("Synthetisch München", rule.Name);
        Assert.Equal(2, rule.Groups.Count);
        Assert.Equal(TagRuleMatch.Any, rule.Groups[0].Match);
        Assert.Equal(2, rule.Groups[0].Conditions.Count);
        Assert.Equal("FilenameTag", Assert.Single(rule.Groups[0].Tags));
        Assert.Equal("Portrait", Assert.Single(rule.Groups[1].Tags));
        Assert.Equal(new Guid(hash[..16]), rule.Id);
        Assert.Equal(bytes, Convert.FromBase64String(rule.Extra["legacy.nrbf.base64"]!));
        Assert.Equal(bytes, File.ReadAllBytes(file));
        var media = new MediaItem { Path = Path.Combine(directory, "nomatch.jpg"), Title = "München", Tags = ["Person"] };
        Assert.Equal(new[] { "FilenameTag", "Portrait" }, CatalogRepository.EvaluateTagRule(media, rule));
        var inspection = LegacyObjectReader.Inspect(file);
        Assert.Equal("MyMediaViewer.TagRule", inspection.RootType);
        Assert.Contains("MyMediaViewer.TagRuleItem", inspection.Types);
        Assert.Equal(Convert.ToHexString(hash).ToLowerInvariant(), inspection.Sha256);
        Assert.True(inspection.RecordCount > 5);
    }

    [Fact]
    public void UnknownRootIsInspectableAsOpaqueDataAndNeverProjected()
    {
        var bytes = StringPayload("unknown user value");
        var file = FileFor(bytes);
        var inspection = LegacyObjectReader.Inspect(file);
        Assert.Equal("System.String", inspection.RootType);
        Assert.Equal(bytes, Convert.FromBase64String(inspection.PayloadBase64));
        Assert.Contains(inspection.Warnings, x => x.Contains("opaque"));
        Assert.Throws<InvalidDataException>(() => LegacyObjectReader.ReadTagRule(file));
    }

    [Fact]
    public void MalformedTruncatedOversizedTrailingAndUnknownRuleShapesAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => LegacyObjectReader.ReadTagRule(FileFor(RulePayload(unknownField: true))));
        Assert.Throws<InvalidDataException>(() => LegacyObjectReader.ReadTagRule(FileFor(RulePayload(duplicateOrdinal: true))));
        Assert.Throws<InvalidDataException>(() => LegacyObjectReader.Inspect(FileFor(RulePayload()[..^3])));
        Assert.Throws<InvalidDataException>(() => LegacyObjectReader.Inspect(FileFor([..StringPayload("trailing"), 0])));
        Assert.Throws<InvalidDataException>(() => LegacyObjectReader.Inspect(FileFor(Encoding.UTF8.GetBytes("not an NRBF payload"))));
        var large = FileFor([]); using (var stream = File.OpenWrite(large)) stream.SetLength(LegacyObjectReader.MaximumPayloadBytes + 1L);
        Assert.Throws<InvalidDataException>(() => LegacyObjectReader.Inspect(large));
    }

    [Fact]
    public void LargeNullArrayInspectionNeverAllocatesItsClaimedElements()
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        Header(writer); writer.Write((byte)16); writer.Write(1); writer.Write(100_000_000);
        writer.Write((byte)14); writer.Write(100_000_000); writer.Write((byte)11); writer.Flush();
        var file = FileFor(stream.ToArray());
        var inspection = LegacyObjectReader.Inspect(file);
        Assert.Contains(inspection.Warnings, x => x.Contains("large array"));
        Assert.Throws<InvalidDataException>(() => LegacyObjectReader.ReadTagRule(file));
    }

    // Byte-level MS-NRBF fixtures. This writer creates only declared test records;
    // it never invokes BinaryFormatter and does not load any serialized type.
    private static byte[] RulePayload(bool unknownField = false, bool duplicateOrdinal = false)
    {
        const string itemName = "MyMediaViewer.TagRuleItem";
        const string arguments = "[[System.Int32, mscorlib],[MyMediaViewer.TagRuleItem, Absent.Original.Assembly]]";
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        Header(writer); writer.Write((byte)12); writer.Write(99); writer.Write("Absent.Original.Assembly, Version=4.8.4.0, Culture=neutral, PublicKeyToken=null");
        Class(writer, 1, "MyMediaViewer.TagRule", ["_ruleName", "_isdemo", "_tagRuleItems"], [1, 0, 2], [1], library: true);
        String(writer, 2, "Synthetisch München"); writer.Write(false);
        Class(writer, 3, "System.Collections.Generic.Dictionary`2" + arguments, ["HashSize", "KeyValuePairs"], [0, 2], [8]);
        writer.Write(3);
        // Typed zero-based BinaryArray of KeyValuePair<int,TagRuleItem>.
        writer.Write((byte)7); writer.Write(4); writer.Write((byte)0); writer.Write(1); writer.Write(3);
        writer.Write((byte)3); writer.Write("System.Collections.Generic.KeyValuePair`2" + arguments);
        var objectId = 10;
        var group1 = new Guid("11111111-1111-1111-1111-111111111111");
        var group2 = new Guid("22222222-2222-2222-2222-222222222222");
        for (var index = 0; index < 3; index++)
        {
            Class(writer, objectId++, "System.Collections.Generic.KeyValuePair`2" + arguments, ["key", "value"], [0, 2], [8]);
            writer.Write(duplicateOrdinal && index == 1 ? 0 : index);
            Class(writer, objectId++, itemName, ["_ruleType", "_joinType", "_ruleKey", "_compareType", "_tag", "_ruleGUID"], [2, 0, 1, 0, 1, 2], [8, 8], library: true);
            // Test the original boxed enum's value__ representation as well as raw primitive enums.
            Class(writer, objectId++, "MyMediaViewer.module2+TagRuleType", ["value__"], [0], [8], library: true);
            writer.Write(unknownField && index == 0 ? 99 : index == 0 ? 0 : index == 1 ? 3 : 2);
            writer.Write(index == 1 ? 20 : 0);
            String(writer, objectId++, index == 0 ? "^never$" : index == 1 ? "München" : "Person");
            writer.Write(index == 0 ? 0 : index == 1 ? 1 : 2);
            String(writer, objectId++, index == 0 ? "FilenameTag" : index == 1 ? "IgnoredSecondOutput" : "Portrait");
            GuidRecord(writer, objectId++, index < 2 ? group1 : group2);
        }
        writer.Write((byte)11); writer.Flush(); return stream.ToArray();
    }
    private static void GuidRecord(BinaryWriter writer, int id, Guid value)
    {
        Class(writer, id, "System.Guid", ["_a", "_b", "_c", "_d", "_e", "_f", "_g", "_h", "_i", "_j", "_k"], new byte[11], [8, 7, 7, 2, 2, 2, 2, 2, 2, 2, 2]);
        writer.Write(value.ToByteArray());
    }
    private static void Class(BinaryWriter writer, int id, string name, string[] members, byte[] kinds, byte[] primitiveTypes, bool library = false)
    {
        writer.Write(library ? (byte)5 : (byte)4); writer.Write(id); writer.Write(name); writer.Write(members.Length);
        foreach (var member in members) writer.Write(member);
        writer.Write(kinds); writer.Write(primitiveTypes); if (library) writer.Write(99);
    }
    private static byte[] StringPayload(string value)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        Header(writer); String(writer, 1, value); writer.Write((byte)11); writer.Flush(); return stream.ToArray();
    }
    private static void Header(BinaryWriter writer) { writer.Write((byte)0); writer.Write(1); writer.Write(-1); writer.Write(1); writer.Write(0); }
    private static void String(BinaryWriter writer, int id, string value) { writer.Write((byte)6); writer.Write(id); writer.Write(value); }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
}
