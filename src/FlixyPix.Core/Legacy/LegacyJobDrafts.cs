using System.Globalization;
using System.Text.Json;

namespace FlixyPix.Core;

/// <summary>Archival inventory, deliberately unrelated to executable MediaJob. OriginalCommand is text only.</summary>
public sealed record LegacyJobDraft
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string SourceId { get; init; } = "";
    public string LegacyId { get; init; } = "";
    public long? OriginalOrder { get; init; }
    public string? InputPath { get; init; }
    public string? OutputPath { get; init; }
    public string? ClipDestinationPath { get; init; }
    public string? ClipName { get; init; }
    public double? DurationSeconds { get; init; }
    public string? OriginalCommand { get; init; }
    public string RawJson { get; init; } = "";
    public HistoricalProjectionStatus Status { get; init; } = HistoricalProjectionStatus.Partial;
    public List<string> Warnings { get; init; } = [];
    public bool IsExecutable => false;
}

public static partial class LegacyHistoricalReader
{
    public static LegacyJobDraft ReadJobDraft(JsonElement values, string sourceId)
    {
        if (values.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Historical queue row must be an object.");
        static JsonElement Find(JsonElement row, string name) => row.EnumerateObject().FirstOrDefault(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
        static string? Text(JsonElement row, string name)
        {
            var value = Find(row, name); return value.ValueKind switch { JsonValueKind.String => value.GetString(), JsonValueKind.Number => value.GetRawText(), _ => null };
        }
        var key = Text(values, "ID") ?? throw new InvalidDataException("Historical queue row has no ID."); RequireIdentity(sourceId, key);
        var durationText = Text(values, "totalseconds"); var duration = double.TryParse(durationText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed) && parsed >= 0 ? (double?)parsed : null;
        var warnings = new List<string> { "Inactive historical queue draft. The original command is archival text and is never executed or automatically converted into a new job." };
        if (durationText != null && duration == null) warnings.Add("Invalid historical duration retained in raw JSON.");
        var draft = new LegacyJobDraft
        {
            Id = HistoricalId(sourceId, "cliptovideo_queue", key), SourceId = sourceId, LegacyId = key,
            OriginalOrder = long.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var order) ? order : null,
            InputPath = Text(values, "sourcefile"), OutputPath = Text(values, "outputfile"), ClipDestinationPath = Text(values, "videotoclipdestinationfile"),
            ClipName = Text(values, "txtcliptovideoname"), DurationSeconds = duration, OriginalCommand = Text(values, "cmd"), RawJson = values.GetRawText(), Warnings = warnings
        };
        ValidateJobDraft(draft); return draft;
    }

    internal static void ValidateJobDraft(LegacyJobDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft); RequireIdentity(draft.SourceId, draft.LegacyId);
        if (draft.Id == Guid.Empty || !Enum.IsDefined(draft.Status) || draft.DurationSeconds is { } duration && (!double.IsFinite(duration) || duration < 0) || draft.Warnings is null ||
            new[] { draft.InputPath, draft.OutputPath, draft.ClipDestinationPath, draft.ClipName, draft.OriginalCommand }.Any(value => value?.Length > 64 * 1024) ||
            draft.RawJson is null || draft.RawJson.Length > 1024 * 1024)
            throw new ArgumentException("Invalid bounded historical queue draft.");
        using var parsed = JsonDocument.Parse(draft.RawJson);
        if (parsed.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("Historical queue draft requires its original object record.");
    }
}
