using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

/// <summary>Rows the original writes into an empty catalog on its first start: settings, property lists, display and player
/// presets. A migrated raw row that equals one of them is a default, not a decision of the user; the report says so instead of
/// leaving it unexplained. Only fingerprints are kept (scripts/original/first-run-defaults.py makes them from a synthetic
/// reference run), so no content of the original ships with FlixyPix.</summary>
public sealed class OriginalDefaults
{
    private readonly Dictionary<string, HashSet<string>> tables;
    private readonly HashSet<string> settings;

    public OriginalDefaults(IReadOnlyDictionary<string, IEnumerable<string>> fingerprints, IEnumerable<string> settingNames)
    {
        tables = fingerprints.ToDictionary(pair => pair.Key, pair => pair.Value.ToHashSet(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        settings = settingNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The defaults of EazyFlixPix 4.8.4, embedded with the reference run they come from.</summary>
    public static OriginalDefaults Builtin { get; } = Load();

    public string Reference { get; private init; } = "";
    public IReadOnlyCollection<string> Tables => tables.Keys;

    private static OriginalDefaults Load()
    {
        using var stream = typeof(OriginalDefaults).Assembly.GetManifestResourceStream("FlixyPix.Core.Legacy.original-first-run-defaults.json")
            ?? throw new InvalidOperationException("The original's first-run defaults are missing from the build.");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var fingerprints = root.GetProperty("tables").EnumerateObject()
            .ToDictionary(table => table.Name, table => table.Value.EnumerateArray().Select(value => value.GetString()!).ToArray().AsEnumerable());
        return new(fingerprints, root.GetProperty("settings").EnumerateArray().Select(value => value.GetString()!)) { Reference = root.GetProperty("reference").GetString() ?? "" };
    }

    /// <summary>"original-default" for a row equal to a default; "changed-original-setting" for a known program setting with
    /// another value; null otherwise.</summary>
    public string? Classify(string table, JsonElement values)
    {
        if (!tables.TryGetValue(table, out var known)) return null;
        if (known.Contains(Fingerprint(values))) return "original-default";
        return table.Equals("configsettings", StringComparison.OrdinalIgnoreCase) && Parameter(values) is { } name && settings.Contains(name) ? "changed-original-setting" : null;
    }

    private static string? Parameter(JsonElement values) =>
        values.EnumerateObject().FirstOrDefault(p => p.Name.Equals("parameter", StringComparison.OrdinalIgnoreCase)).Value is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    /// <summary>SHA-256 of a row without "ID" and without empty (null) columns, keys compared without case.</summary>
    public static string Fingerprint(JsonElement values)
    {
        var row = new StringBuilder("o{");
        var first = true;
        foreach (var (name, value) in values.EnumerateObject().Where(p => !p.Name.Equals("id", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind != JsonValueKind.Null)
            .Select(p => (p.Name.ToLowerInvariant(), p.Value)).OrderBy(p => p.Item1, StringComparer.Ordinal))
        { if (!first) row.Append('\u0002'); first = false; row.Append(name).Append('\u0001').Append(Canonical(value)); }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(row.Append('}').ToString())));
    }

    private static string Canonical(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => "z",
        JsonValueKind.True => "b1",
        JsonValueKind.False => "b0",
        JsonValueKind.String => "s" + value.GetString(),
        JsonValueKind.Number => "n" + Number(value),
        JsonValueKind.Array => "a[" + string.Join('\u0002', value.EnumerateArray().Select(Canonical)) + "]",
        _ => "o{" + string.Join('\u0002', value.EnumerateObject().Select(p => (Name: p.Name.ToLowerInvariant(), p.Value)).OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => p.Name + '\u0001' + Canonical(p.Value))) + "}",
    };

    private static string Number(JsonElement value)
    {
        if (value.TryGetInt64(out var whole)) return whole.ToString(CultureInfo.InvariantCulture);
        var number = value.GetDouble();
        return number == Math.Floor(number) && Math.Abs(number) < 9007199254740992d ? ((long)number).ToString(CultureInfo.InvariantCulture) : number.ToString("R", CultureInfo.InvariantCulture);
    }
}

public sealed partial class CatalogRepository
{
    /// <summary>Gives raw rows that match the original's first-run defaults their own code (status stays raw-retained: the
    /// rows are kept verbatim either way).</summary>
    private void MarkOriginalDefaults(OriginalDefaults defaults, string batch, SqliteTransaction tx)
    {
        var codes = new List<(long Ordinal, string Code)>();
        using (var read = Command($"""
            SELECT m.ordinal,s.table_name,s.record FROM migration_rows m JOIN temp.legacy_stage s ON s.ordinal=m.ordinal
            WHERE m.batch=$batch AND m.code='preserved-unverified-projection' AND lower(s.table_name) IN ({string.Join(',', defaults.Tables.Select((_, i) => "$t" + i))})
            """, tx, [("$batch", batch), .. defaults.Tables.Select((table, i) => ("$t" + i, (object)table.ToLowerInvariant()))]))
        using (var reader = read.ExecuteReader())
            while (reader.Read())
            {
                legacySession?.Token.ThrowIfCancellationRequested();
                using var record = JsonDocument.Parse(reader.GetString(2));
                if (defaults.Classify(reader.GetString(1), record.RootElement.GetProperty("values")) is { } code) codes.Add((reader.GetInt64(0), code));
            }
        foreach (var (ordinal, code) in codes)
        {
            using var update = Command("UPDATE migration_rows SET code=$code WHERE batch=$batch AND ordinal=$ordinal", tx, ("$code", code), ("$batch", batch), ("$ordinal", ordinal));
            update.ExecuteNonQuery();
        }
    }
}
