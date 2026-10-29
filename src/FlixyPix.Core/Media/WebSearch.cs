namespace FlixyPix.Core;

/// <summary>A web page that searches for a name: <see cref="Url"/> contains <c>{query}</c> and may contain
/// <c>{lang}</c> (language of the interface).</summary>
public sealed record WebSearchProvider(string Name, string Url);

/// <summary>"Im Browser suchen" for performers, studios and tags (original: Google, IMDb, TMDb and IAFD in the
/// cast and media windows). FlixyPix only builds the address; the browser opens it, nothing is downloaded.</summary>
public static class WebSearch
{
    public static IReadOnlyList<WebSearchProvider> Defaults { get; } =
    [
        new("Google", "https://www.google.com/search?q={query}"),
        new("Wikipedia", "https://{lang}.wikipedia.org/w/index.php?search={query}"),
        new("IMDb", "https://www.imdb.com/find/?q={query}"),
        new("TMDb", "https://www.themoviedb.org/search?query={query}"),
        new("IAFD", "https://www.iafd.com/results.asp?searchtype=comprehensive&searchstring={query}"),
    ];

    public static string Url(WebSearchProvider provider, string query, string language = "en") =>
        provider.Url.Replace("{query}", Uri.EscapeDataString(query.Trim()), StringComparison.OrdinalIgnoreCase)
            .Replace("{lang}", Uri.EscapeDataString(language), StringComparison.OrdinalIgnoreCase);

    /// <summary>Null when the provider can be saved, otherwise the reason.</summary>
    public static string? Problem(WebSearchProvider provider)
    {
        if (string.IsNullOrWhiteSpace(provider.Name)) return T("Please enter a name.");
        if (!provider.Url.Contains("{query}", StringComparison.OrdinalIgnoreCase)) return T("The address needs {0} for the name searched for.", "{query}");
        return Uri.TryCreate(Url(provider, "x"), UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"
            ? null : T("Please give a web address with https://.");
    }
}
