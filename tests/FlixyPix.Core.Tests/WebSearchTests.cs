using FlixyPix.Core;
using Xunit;

namespace FlixyPix.Core.Tests;

/// <summary>Search addresses for "Im Browser suchen": escaping, language and checks.</summary>
public sealed class WebSearchTests
{
    [Fact]
    public void TheNameIsEscapedAndTheLanguageFilledIn()
    {
        var wikipedia = WebSearch.Defaults.Single(p => p.Name == "Wikipedia");
        Assert.Equal("https://de.wikipedia.org/w/index.php?search=J%C3%BCrgen%20%26%20Ana%C3%AFs%3F", WebSearch.Url(wikipedia, "  Jürgen & Anaïs? ", "de"));
        Assert.Equal("https://www.google.com/search?q=Studio%201", WebSearch.Url(WebSearch.Defaults[0], "Studio 1"));
        Assert.All(WebSearch.Defaults, p => Assert.Null(WebSearch.Problem(p)));
    }

    [Fact]
    public void ProvidersNeedANameAQueryAndAWebAddress()
    {
        Assert.NotNull(WebSearch.Problem(new(" ", "https://example.org/?q={query}")));
        Assert.NotNull(WebSearch.Problem(new("Ohne", "https://example.org/")));
        Assert.NotNull(WebSearch.Problem(new("Datei", "file:///C:/{query}")));
        Assert.NotNull(WebSearch.Problem(new("Relativ", "suche?q={query}")));
        Assert.Null(WebSearch.Problem(new("Eigene", "https://example.org/find/{QUERY}")));
    }
}
