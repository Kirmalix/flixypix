using FlixyPix.Core;
using Xunit;

namespace FlixyPix.Core.Tests;

/// <summary>Short forms in the search box that become filters.</summary>
public sealed class SearchShorthandTests
{
    [Fact]
    public void ShortFormsBecomeFiltersAndTheRestStaysText()
    {
        var parsed = SearchShorthand.Parse("  sommer #Urlaub -#nacht ★4 typ:bilder am see #urlaub @Kuss @kuss ");
        Assert.True(parsed.Recognized);
        Assert.Equal("sommer am see", parsed.Text);
        Assert.Equal(["Urlaub"], parsed.Include);
        Assert.Equal(["nacht"], parsed.Exclude);
        Assert.Equal(4, parsed.MinimumStars);
        Assert.Equal(MediaKind.Image, parsed.Kind);
        Assert.Equal(["Kuss"], parsed.MarkTags);
    }

    [Theory]
    [InlineData("type:video", MediaKind.Video)]
    [InlineData("Kind:Audio", MediaKind.Audio)]
    [InlineData("art:foto", MediaKind.Image)]
    public void TheTypeTakesSeveralSpellings(string text, MediaKind kind) => Assert.Equal(kind, SearchShorthand.Parse(text).Kind);

    [Theory]
    [InlineData("")]
    [InlineData("#")]
    [InlineData("-#")]
    [InlineData("@ name@example.org")]
    [InlineData("★6 *0 typ:dokument C# zeit:12:30")]
    public void OtherWordsAreNoShortForms(string text)
    {
        var parsed = SearchShorthand.Parse(text);
        Assert.False(parsed.Recognized);
        Assert.Equal(string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries)), parsed.Text);
    }
}
