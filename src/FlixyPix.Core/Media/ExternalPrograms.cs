using System.Globalization;
using System.Text.Json;

namespace FlixyPix.Core;

/// <summary>A program of this computer that opens media on request ("Öffnen mit"). <see cref="Arguments"/> is a
/// template; <see cref="ExternalPrograms.Placeholders"/> are replaced as plain text, so the template sets its own quotes.
/// No kinds means every kind.</summary>
public sealed record ExternalProgram
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public string Arguments { get; init; } = ExternalPrograms.DefaultArguments;
    public List<MediaKind> Kinds { get; init; } = [];
    /// <summary>Shortcut such as "Ctrl+Shift+V" in the main window; empty for none.</summary>
    public string Hotkey { get; init; } = "";
}

/// <summary>Opening media with other programs (original: custom media players of <c>customplayersettings</c>, menu
/// "Play video with"). FlixyPix only builds the command line; starting the process is up to the caller.</summary>
public static class ExternalPrograms
{
    public const string DefaultArguments = "\"{path}\"";
    public static IReadOnlyList<string> Placeholders { get; } = ["{path}", "{folder}", "{name}", "{title}", "{seconds}"];

    public static bool Applies(ExternalProgram program, MediaKind kind) => program.Kinds.Count == 0 || program.Kinds.Contains(kind);

    /// <summary>The arguments for one medium; <paramref name="seconds"/> is a playback position, otherwise 0.</summary>
    public static string BuildArguments(ExternalProgram program, MediaItem item, double? seconds = null)
    {
        var full = System.IO.Path.GetFullPath(item.Path);
        return program.Arguments
            .Replace("{path}", full, StringComparison.OrdinalIgnoreCase)
            .Replace("{folder}", System.IO.Path.GetDirectoryName(full) ?? full, StringComparison.OrdinalIgnoreCase)
            .Replace("{name}", System.IO.Path.GetFileName(full), StringComparison.OrdinalIgnoreCase)
            .Replace("{title}", item.Title.Replace("\"", "'", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase)
            .Replace("{seconds}", Math.Max(0, Math.Floor(seconds ?? 0)).ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Null when the program can be saved, otherwise the reason.</summary>
    public static string? Problem(ExternalProgram program)
    {
        if (string.IsNullOrWhiteSpace(program.Name)) return T("Please enter a name.");
        if (string.IsNullOrWhiteSpace(program.Path) || !System.IO.Path.IsPathFullyQualified(program.Path)) return T("Please give the program with its full path.");
        if (!program.Arguments.Contains("{path}", StringComparison.OrdinalIgnoreCase) && !program.Arguments.Contains("{folder}", StringComparison.OrdinalIgnoreCase))
            return T("The arguments need {0} or {1}; otherwise the program does not know what to open.", "{path}", "{folder}");
        return null;
    }

    /// <summary>The original's command line followed the quoted file name; an empty one means just the file.</summary>
    public static string ArgumentsFromOriginal(string? commandLine) =>
        string.IsNullOrWhiteSpace(commandLine) ? DefaultArguments : DefaultArguments + " " + commandLine.Trim();
}

public sealed partial class CatalogRepository
{
    /// <summary>The original's custom media players from the rows a migration kept verbatim, once per program file.
    /// Instance type and the clip command line (m3u/asx playlists) have no counterpart and are left out.</summary>
    public IReadOnlyList<ExternalProgram> GetOriginalExternalPrograms()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var command = Command("SELECT record FROM legacy_records WHERE json_extract(record,'$.type')='row' AND json_extract(record,'$.table')='customplayersettings' COLLATE NOCASE ORDER BY id", null);
            using var reader = command.ExecuteReader();
            var programs = new List<ExternalProgram>();
            while (reader.Read())
            {
                using var parsed = JsonDocument.Parse(reader.GetString(0));
                if (!parsed.RootElement.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Object) continue;
                string Text(string name) => values.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : "";
                var path = Text("exename");
                if (path.Length == 0 || programs.Any(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
                var name = Text("mediaplayername");
                programs.Add(new ExternalProgram
                {
                    Name = name.Length > 0 ? name : System.IO.Path.GetFileNameWithoutExtension(path),
                    Path = path, Arguments = ExternalPrograms.ArgumentsFromOriginal(Text("commandline")), Kinds = [MediaKind.Video], Hotkey = Text("hotkey"),
                });
            }
            return programs;
        }
    }
}
