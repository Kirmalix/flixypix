namespace FlixyPix.Core;

/// <summary>Short forms in the search box that become filters (UX plan U4.2): <c>#tag</c> must be there, <c>-#tag</c> must
/// not, <c>★4</c> or <c>*4</c> asks for at least four stars, <c>type:video</c> (also <c>typ:</c>, and image, bild, audio …)
/// chooses the media type, <c>@tag</c> asks for a bookmark or scene with that tag (U6). Everything else stays search text.</summary>
public sealed record SearchShorthand(string Text, IReadOnlyList<string> Include, IReadOnlyList<string> Exclude, int? MinimumStars, MediaKind? Kind,
    IReadOnlyList<string> MarkTags)
{
    /// <summary>Whether at least one short form was found.</summary>
    public bool Recognized => Include.Count + Exclude.Count + MarkTags.Count > 0 || MinimumStars != null || Kind != null;

    private static readonly Dictionary<string, MediaKind> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["video"] = MediaKind.Video, ["videos"] = MediaKind.Video, ["film"] = MediaKind.Video,
        ["image"] = MediaKind.Image, ["images"] = MediaKind.Image, ["bild"] = MediaKind.Image, ["bilder"] = MediaKind.Image, ["photo"] = MediaKind.Image, ["foto"] = MediaKind.Image,
        ["audio"] = MediaKind.Audio, ["music"] = MediaKind.Audio, ["musik"] = MediaKind.Audio,
    };

    public static SearchShorthand Parse(string? text)
    {
        var rest = new List<string>(); var include = new List<string>(); var exclude = new List<string>(); var marks = new List<string>();
        int? stars = null; MediaKind? kind = null;
        foreach (var word in (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.StartsWith("-#", StringComparison.Ordinal) && word.Length > 2) Add(exclude, word[2..]);
            else if (word.StartsWith('#') && word.Length > 1) Add(include, word[1..]);
            else if (word.StartsWith('@') && word.Length > 1) Add(marks, word[1..]);
            else if (word.Length == 2 && word[0] is '★' or '*' && word[1] is >= '1' and <= '5') stars = word[1] - '0';
            else if (word.IndexOf(':') is var colon and > 0 && word[..colon].ToLowerInvariant() is "type" or "typ" or "kind" or "art"
                && Kinds.TryGetValue(word[(colon + 1)..], out var found)) kind = found;
            else rest.Add(word);
        }
        return new(string.Join(' ', rest), include, exclude, stars, kind, marks);
    }

    private static void Add(List<string> tags, string tag)
    {
        if (!tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) tags.Add(tag);
    }
}
