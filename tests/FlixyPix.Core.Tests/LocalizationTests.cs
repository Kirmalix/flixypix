using System.Globalization;
using System.Text.Json;
using System.Text;
using FlixyPix.Core;
using Xunit;

namespace FlixyPix.Core.Tests;

/// <summary>Language choice, fallback, plurals and the shipped translation files. The tests use their own
/// <see cref="Translator"/> instances and never change <see cref="Texts.Current"/>, which other tests share.</summary>
public sealed class LocalizationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "flixypix-languages-" + Guid.NewGuid().ToString("N"));
    public LocalizationTests() => Directory.CreateDirectory(directory);
    public void Dispose() { try { Directory.Delete(directory, true); } catch (IOException) { } }

    [Theory]
    [InlineData(null, "de-DE", "de")]
    [InlineData("system", "de-AT", "de")]
    [InlineData("system", "de-CH", "de")]
    [InlineData("system", "pt-PT", "pt")]
    [InlineData("system", "pt-BR", "pt")]
    [InlineData("system", "ja-JP", "ja")]
    [InlineData("system", "sv-SE", "en")]
    [InlineData("system", "zh-CN", "en")]
    [InlineData("", "fr-CA", "fr")]
    [InlineData("pl", "de-DE", "pl")]
    [InlineData("RU", "de-DE", "ru")]
    [InlineData("klingon", "nl-BE", "nl")]
    [InlineData("qps", "de-DE", "qps")]
    public void TheChoiceOrTheDisplayLanguageOfWindowsDecides(string? choice, string display, string expected) =>
        Assert.Equal(expected, Texts.Resolve(choice, CultureInfo.GetCultureInfo(display)));

    [Fact]
    public void InvariantDisplayCultureFallsBackToEnglish() => Assert.Equal("en", Texts.Resolve("system", CultureInfo.InvariantCulture));

    [Theory]
    [InlineData("German", "de")]
    [InlineData("English", "en")]
    [InlineData(" Spanish ", "es")]
    [InlineData("Japanese", "ja")]
    [InlineData("Dutch", "nl")]
    [InlineData("Chinese", null)]
    [InlineData("Danish", null)]
    [InlineData("Korean", null)]
    [InlineData(null, null)]
    public void TheOriginalsLanguageIsTakenOverWhenFlixyPixSpeaksIt(string? original, string? expected) => Assert.Equal(expected, Texts.FromOriginal(original));

    [Theory]
    [InlineData("de", 0, "other")] [InlineData("de", 1, "one")] [InlineData("de", 2, "other")]
    [InlineData("en", 1, "one")] [InlineData("en", 11, "other")]
    [InlineData("fr", 0, "one")] [InlineData("fr", 1, "one")] [InlineData("fr", 2, "other")]
    [InlineData("pt", 0, "one")] [InlineData("pt", 1, "one")] [InlineData("pt", 2, "other")]
    [InlineData("es", 0, "other")] [InlineData("it", 1, "one")] [InlineData("nl", 3, "other")]
    [InlineData("pl", 1, "one")] [InlineData("pl", 2, "few")] [InlineData("pl", 4, "few")] [InlineData("pl", 5, "many")] [InlineData("pl", 12, "many")]
    [InlineData("pl", 21, "many")] [InlineData("pl", 22, "few")] [InlineData("pl", 112, "many")] [InlineData("pl", 0, "many")]
    [InlineData("ru", 1, "one")] [InlineData("ru", 21, "one")] [InlineData("ru", 11, "many")] [InlineData("ru", 2, "few")] [InlineData("ru", 14, "many")]
    [InlineData("ru", 24, "few")] [InlineData("ru", 0, "many")] [InlineData("ru", 101, "one")] [InlineData("ru", 111, "many")]
    [InlineData("ja", 1, "other")] [InlineData("ja", 5, "other")]
    [InlineData("fr", 1_000_000, "many")] [InlineData("pt", 2_000_000, "many")]
    [InlineData("es", 1_000_000, "many")] [InlineData("it", 1_000_000, "many")]
    [InlineData("fr", 999_999, "other")] [InlineData("pt", 1_000_001, "other")]
    [InlineData("it", 0, "other")] [InlineData("es", -1_000_000, "many")]
    [InlineData("en", long.MinValue, "other")]
    public void PluralCategoriesFollowTheCldr(string language, long count, string expected) => Assert.Equal(expected, Plurals.Category(language, count));

    [Theory]
    [InlineData("fr", 0, "0 média")] [InlineData("fr", 1_000_000, "1.000.000 de médias")]
    [InlineData("es", 1, "1 elemento")] [InlineData("es", 2, "2 elementos")]
    [InlineData("it", 1_000_000, "1.000.000 di elementi")]
    [InlineData("pt", 0, "0 item")] [InlineData("pt", 2, "2 itens")]
    [InlineData("nl", 1, "1 item")] [InlineData("nl", 2, "2 items")]
    [InlineData("pl", 1, "1 element")] [InlineData("pl", 2, "2 elementy")]
    [InlineData("pl", 5, "5 elementów")] [InlineData("pl", 21, "21 elementów")]
    [InlineData("ru", 21, "21 элемент")] [InlineData("ru", 22, "22 элемента")]
    [InlineData("ru", 11, "11 элементов")] [InlineData("ja", 2, "2 件のメディア")]
    public void ShippedCountersUseTheRightFormAndKeepTheActualNumber(string language, long count, string expected)
    {
        var translator = Translator.Load(language, formatting: CultureInfo.GetCultureInfo("de-DE"));
        Assert.Equal(expected, translator.Plural(count, "1 item", "{0:N0} items", []));
    }

    [Fact]
    public void TranslatedDefaultFileAndFolderNamesAreValidOnWindows()
    {
        foreach (var language in Texts.Languages)
        {
            var translator = Translator.Load(language.Code, formatting: CultureInfo.InvariantCulture);
            foreach (var name in new[] { translator.Format("Import-{0:yyyyMMdd-HHmm}", [new DateTime(2026, 10, 26, 12, 0, 0)]), translator.Text("Media artwork") })
            {
                Assert.False(string.IsNullOrWhiteSpace(name), language.Code);
                Assert.DoesNotContain(name, c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c));
                Assert.False(name.EndsWith('.') || name.EndsWith(' '), language.Code + ": " + name);
            }
        }
    }

    [Fact]
    public void EnglishShowsTheKeyFormatsWithTheRegionalFormatAndHidesTheContext()
    {
        var english = Translator.Load("en", formatting: CultureInfo.GetCultureInfo("de-DE"));
        Assert.Equal("Settings", english.Text("Settings"));
        Assert.Equal("1.234 items", english.Format("{0:N0} items", [1234]));
        Assert.Equal("View", english.Text("Menu" + Translator.ContextSeparator + "View"));
        Assert.Equal("1 item", english.Plural(1, "1 item", "{0:N0} items", []));
        Assert.Equal("2 items in “Holiday”", english.Plural(2, "1 item in “{1}”", "{0:N0} items in “{1}”", ["Holiday"]));
    }

    [Fact]
    public void GermanIsATranslationOfTheEnglishKeys()
    {
        var german = Translator.Load("de", formatting: CultureInfo.GetCultureInfo("de-DE"));
        Assert.Equal("Einstellungen", german.Text("Settings"));
        Assert.Equal("1 Medium", german.Plural(1, "1 item", "{0:N0} items", []));
        Assert.Equal("1.234 Medien", german.Plural(1234, "1 item", "{0:N0} items", []));
    }

    [Fact]
    public void TheUsersFileReplacesSingleTextsAndMissingOnesFallBackToTheEnglishKey()
    {
        File.WriteAllText(Path.Combine(directory, "en.json"), """{ "Test text A": "Better text A", "{0:N0} test things": { "one": "1 test item", "other": "{0:N0} test items" } }""");
        File.WriteAllText(Path.Combine(directory, "pl.json"), """{ "Test text B": "Tekst B", "{0:N0} test things": { "one": "1 rzecz", "few": "{0} rzeczy", "many": "{0} rzeczy (dużo)" } }""");
        var polish = Translator.Load("pl", directory, CultureInfo.GetCultureInfo("de-DE"));
        Assert.Empty(polish.Problems);
        Assert.Equal("Tekst B", polish.Text("Test text B"));
        Assert.Equal("Test text A", polish.Text("Test text A"));
        Assert.Equal("1 rzecz", polish.Plural(1, "1 test thing", "{0:N0} test things", []));
        Assert.Equal("3 rzeczy", polish.Plural(3, "1 test thing", "{0:N0} test things", []));
        Assert.Equal("5 rzeczy (dużo)", polish.Plural(5, "1 test thing", "{0:N0} test things", []));
        var english = Translator.Load("en", directory, CultureInfo.GetCultureInfo("en-US"));
        Assert.Equal("Better text A", english.Text("Test text A"));
        Assert.Equal("1,500 test items", english.Plural(1500, "1 test thing", "{0:N0} test things", []));
        Assert.Equal("2 others", english.Plural(2, "1 other", "{0} others", []));
    }

    [Fact]
    public void AnUnreadableFileOfTheUserIsReportedAndIgnored()
    {
        File.WriteAllText(Path.Combine(directory, "fr.json"), "{ kaputt");
        var french = Translator.Load("fr", directory);
        Assert.Single(french.Problems);
        Assert.Equal("Testtext", french.Text("Testtext"));
    }

    [Fact]
    public void PseudoTextIsLongerAndMarkedButKeepsPlaceholders()
    {
        var pseudo = Translator.Load(Translator.PseudoCode, formatting: CultureInfo.InvariantCulture);
        var text = pseudo.Format("{0:N0} items found", [12]);
        Assert.StartsWith("[12 îtémš", text);
        Assert.EndsWith("]", text);
        Assert.Contains("~~~", text);
        Assert.True(text.Length > "12 items found".Length + 4);
    }

    [Fact]
    public void TranslatedInputInstructionsUseTheWordsTheDialogsAccept()
    {
        // These dialogs compare user input to translated tokens. A synonymous
        // word in the prompt can make the documented action impossible.
        var contracts = new (string Prompt, string[] Tokens)[]
        {
            ("Mode: Count or Interval", ["Count", "Interval"]),
            ("Enter Count or Interval as the mode and a matching value.", ["Count", "Interval"]),
            ("Replace existing generated images: yes/no", ["yes", "no"]),
            ("Replace: enter yes or no.", ["yes", "no"]),
            ("Active · yes or no", ["yes", "no"]),
            ("If the target is taken: Cancel, Skip or Unique", ["Cancel", "Skip", "Unique"]),
            ("Please enter Cancel, Skip or Unique.", ["Cancel", "Skip", "Unique"]),
        };
        foreach (var language in Texts.Languages)
        {
            var translator = Translator.Load(language.Code);
            foreach (var (prompt, tokens) in contracts)
                foreach (var token in tokens)
                    Assert.True(translator.Text(prompt).Contains(translator.Text(token), StringComparison.OrdinalIgnoreCase),
                        $"{language.Code}: '{prompt}' does not name the accepted input '{translator.Text(token)}'.");
        }
    }

    [Fact]
    public void EveryLanguageHasAValidFileWithTheFormsItsPluralsNeed()
    {
        foreach (var language in Texts.Languages.Where(l => l.Code != "en"))
        {
            using var stream = typeof(Translator).Assembly.GetManifestResourceStream($"FlixyPix.Core.Languages.{language.Code}.json");
            Assert.NotNull(stream);
            using var document = JsonDocument.Parse(stream);
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                var values = entry.Value.ValueKind == JsonValueKind.Object
                    ? entry.Value.EnumerateObject().Select(f => f.Value.GetString()) : [entry.Value.GetString()];
                foreach (var value in values)
                {
                    Assert.False(string.IsNullOrWhiteSpace(value), $"{language.Code}: '{entry.Name}' is empty.");
                    if (System.Text.RegularExpressions.Regex.IsMatch(value!, @"\{\d")) CompositeFormat.Parse(value!);
                }
            }
            foreach (var entry in document.RootElement.EnumerateObject().Where(e => e.Value.ValueKind == JsonValueKind.Object))
                foreach (var form in Plurals.Needed(language.Code))
                    Assert.True(entry.Value.TryGetProperty(form, out _), $"{language.Code}: '{entry.Name}' lacks the plural form '{form}'.");
            Assert.Equal(language.Code, Translator.Load(language.Code).Language);
            Assert.Equal(language.CultureName, CultureInfo.GetCultureInfo(language.CultureName).Name);
        }
    }
}
