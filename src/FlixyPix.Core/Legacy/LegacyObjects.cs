using System.Formats.Nrbf;
using System.Runtime.Serialization;
using System.Security.Cryptography;

namespace FlixyPix.Core;

/// <summary>Opaque structural inventory. Payload bytes are retained, never instantiated as .NET types.</summary>
public sealed record LegacyObjectInspection(string Sha256, string RootType, int RecordCount,
    IReadOnlyList<string> Types, IReadOnlyList<string> Warnings, string PayloadBase64);

/// <summary>Bounded NRBF record reading. No BinaryFormatter, reflection type loading, constructors or callbacks.</summary>
public static partial class LegacyObjectReader
{
    public const int MaximumPayloadBytes = 32 * 1024 * 1024;
    private const int MaximumRecords = 200_000;
    private const int MaximumRuleItems = 10_000;
    private const string RuleType = "MyMediaViewer.TagRule";
    private const string ItemType = "MyMediaViewer.TagRuleItem";

    public static LegacyObjectInspection Inspect(string path)
    {
        var decoded = Read(path);
        var warnings = new List<string> { "Structural inspection only; no serialized type was loaded or executed. No queued operation is started." };
        if (decoded.Root.TypeName.FullName != RuleType) warnings.Add("This root type is preserved as opaque data; no automatic projection is defined.");
        foreach (var array in decoded.Records.Values.OfType<ArrayRecord>())
            if (array.Lengths.ToArray().Any(x => x > MaximumRuleItems)) warnings.Add("A large array is present; inspection does not materialize it.");
        return new(decoded.Sha256, decoded.Root.TypeName.FullName, decoded.Records.Count,
            decoded.Records.Values.Select(x => x.TypeName.FullName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            warnings.Distinct().ToArray(), Convert.ToBase64String(decoded.Bytes));
    }

    /// <summary>Projects only the statically observed MyMediaViewer.TagRule dictionary shape. The source payload survives in Extra.</summary>
    public static FilenameTagRule ReadTagRule(string path)
    {
        var decoded = Read(path);
        try
        {
            var root = RequireClass(decoded.Root, RuleType);
            var name = RequiredString(root, "_ruleName");
            var dictionary = RequireClass(root.GetClassRecord("_tagRuleItems"), "System.Collections.Generic.Dictionary`2", true);
            RequireGenericArguments(dictionary, "System.Int32", ItemType);
            var array = dictionary.GetSerializationRecord("KeyValuePairs") as SZArrayRecord<SerializationRecord> ?? throw Invalid("Rule dictionary has no KeyValuePairs array.");
            if (array.Rank != 1 || array.Lengths[0] > MaximumRuleItems) throw Invalid("Rule dictionary exceeds 10000 items or has an unsupported rank.");
            // Only serialization records are requested: no original KeyValuePair or TagRuleItem object is instantiated.
            var pairs = array.GetArray(allowNulls: false);
            var items = new SortedDictionary<int, ClassRecord>();
            foreach (var value in pairs)
            {
                var pair = RequireClass(value as SerializationRecord, "System.Collections.Generic.KeyValuePair`2", true);
                RequireGenericArguments(pair, "System.Int32", ItemType);
                var key = pair.GetInt32("key");
                if (key < 0 || !items.TryAdd(key, RequireClass(pair.GetClassRecord("value"), ItemType))) throw Invalid("Rule dictionary contains duplicate or negative ordinals.");
            }
            if (items.Count == 0 || !items.Keys.SequenceEqual(Enumerable.Range(0, items.Count))) throw Invalid("Rule dictionary ordinals must be contiguous from zero.");
            var groups = new List<TagRuleGroup>();
            var groupIds = new Dictionary<Guid, int>();
            foreach (var item in items.Values)
            {
                var fieldValue = EnumValue(item, "_ruleType", "MyMediaViewer.module2+TagRuleType");
                var field = fieldValue switch { 0 => TagRuleField.Filename, 1 => TagRuleField.Path, 2 => TagRuleField.Tag, 3 => TagRuleField.Title, _ => throw Invalid("Unknown rule field.") };
                var join = EnumValue(item, "_joinType", "MyMediaViewer.module2+TagRuleJoinType");
                if (join is not (0 or 10 or 20)) throw Invalid("Unknown rule join.");
                var comparisonValue = EnumValue(item, "_compareType", "MyMediaViewer.module2+TagRuleCompareType");
                if (comparisonValue is not (0 or 1 or 2)) throw Invalid("Unknown rule comparison.");
                var comparison = field == TagRuleField.Tag ? TagRuleComparison.HasTag : comparisonValue switch
                { 0 => TagRuleComparison.Matches, 1 => TagRuleComparison.Contains, _ => throw Invalid("HasTag comparison requires a tag field.") };
                var key = RequiredString(item, "_ruleKey");
                var id = ReadGuid(item.GetClassRecord("_ruleGUID"));
                if (!groupIds.TryGetValue(id, out var index))
                {
                    groupIds[id] = index = groups.Count;
                    groups.Add(new() { Tags = RequiredString(item, "_tag").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList() });
                }
                if (join == 20) groups[index] = groups[index] with { Match = TagRuleMatch.Any };
                groups[index].Conditions.Add(new(field, comparison, key));
            }
            var rule = new FilenameTagRule
            {
                Id = new Guid(Convert.FromHexString(decoded.Sha256)[..16]), Name = name, Groups = groups,
                Extra = new()
                {
                    ["legacy.nrbf.sha256"] = decoded.Sha256,
                    ["legacy.nrbf.rootType"] = RuleType,
                    ["legacy.nrbf.base64"] = Convert.ToBase64String(decoded.Bytes),
                    ["legacy.nrbf.isDemo"] = root.HasMember("_isdemo") ? root.GetBoolean("_isdemo").ToString() : null,
                    ["legacy.nrbf.projection"] = "Known tag rule fields only; original payload preserves all other records. Original runtime parity unverified."
                }
            };
            // Reuse the same limits and bounded regular-expression validation as manually authored rules.
            _ = CatalogRepository.EvaluateTagRule(new MediaItem { Path = Path.Combine(Path.GetTempPath(), "validation.jpg") }, rule with { Enabled = false });
            return rule;
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or ArgumentException or OverflowException)
        { throw Invalid("Unsupported or malformed original tag rule: " + error.Message, error); }
    }

    private static int EnumValue(ClassRecord owner, string member, string expectedType)
    {
        try { return owner.GetInt32(member); }
        catch (InvalidOperationException) { return RequireClass(owner.GetClassRecord(member), expectedType).GetInt32("value__"); }
    }

    private static Guid ReadGuid(ClassRecord? record)
    {
        var value = RequireClass(record, "System.Guid");
        return new(value.GetInt32("_a"), value.GetInt16("_b"), value.GetInt16("_c"),
            value.GetByte("_d"), value.GetByte("_e"), value.GetByte("_f"), value.GetByte("_g"),
            value.GetByte("_h"), value.GetByte("_i"), value.GetByte("_j"), value.GetByte("_k"));
    }

    private static ClassRecord RequireClass(SerializationRecord? value, string expected, bool generic = false)
    {
        if (value is not ClassRecord record || (generic ? !record.TypeName.IsConstructedGenericType || record.TypeName.GetGenericTypeDefinition().FullName != expected : record.TypeName.FullName != expected))
            throw Invalid("Unexpected serialized type; expected " + expected + ".");
        return record;
    }
    private static void RequireGenericArguments(ClassRecord record, string first, string second)
    {
        var arguments = record.TypeName.GetGenericArguments();
        if (arguments.Length != 2 || arguments[0].FullName != first || arguments[1].FullName != second) throw Invalid("Unexpected generic dictionary/entry types.");
    }
    private static string RequiredString(ClassRecord record, string field)
    {
        var text = record.GetString(field);
        if (text is null || text.Length > 4096) throw Invalid("Missing or oversized string field " + field + ".");
        return text;
    }

    private sealed record Decoded(byte[] Bytes, string Sha256, SerializationRecord Root,
        IReadOnlyDictionary<SerializationRecordId, SerializationRecord> Records);
    private static Decoded Read(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 17 or > MaximumPayloadBytes) throw Invalid("NRBF payload must be between 17 bytes and 32 MiB.");
        var bytes = new byte[(int)file.Length]; file.ReadExactly(bytes);
        return ReadBytes(bytes);
    }
    private static Decoded ReadBytes(byte[] bytes)
    {
        if(bytes.Length is <17 or >MaximumPayloadBytes)throw Invalid("NRBF payload must be between 17 bytes and 32 MiB.");
        using var stream = new MemoryStream(bytes, writable: false);
        try
        {
            if (!NrbfDecoder.StartsWithPayloadHeader(stream)) throw Invalid("Not an NRBF payload.");
            var root = NrbfDecoder.Decode(stream, out var records, leaveOpen: true);
            if (stream.Position != stream.Length) throw Invalid("Trailing data after the NRBF message is unsupported.");
            if (records.Count > MaximumRecords) throw Invalid("NRBF payload exceeds 200000 records.");
            return new(bytes, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), root, records);
        }
        catch (Exception error) when (error is SerializationException or NotSupportedException or EndOfStreamException or System.Text.DecoderFallbackException or ArgumentException)
        { throw Invalid("Invalid or unsupported NRBF records: " + error.Message, error); }
    }
    private static InvalidDataException Invalid(string message, Exception? inner = null) => new(message, inner);
}
