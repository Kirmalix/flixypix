using System.Globalization;
using System.Text;
using System.Text.Json;
using FlixyPix.Core;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class HistoricalParserExecutionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "flixypix-historical-parser-" + Guid.NewGuid().ToString("N"));
    private static readonly HistoricalTagFormatting Reviewed = new() { Confirmed = true, SourceDescription = "Explicit synthetic formatting context", CultureName = "en-US" };
    public HistoricalParserExecutionTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    [Fact]
    public void TranslationRequiresReviewedFormattingAndLeavesSourceDraftInactive()
    {
        var draft = Draft();
        Assert.False(HistoricalFilenameParser.Translate(draft, new()).CanActivate);
        var preview = HistoricalFilenameParser.Translate(draft, Reviewed);
        Assert.True(preview.CanActivate, string.Join("; ", preview.Blockers));
        Assert.False(draft.IsExecutable); Assert.Equal(HistoricalProjectionStatus.Partial, draft.Status);
        Assert.Equal(draft.PayloadSha256, preview.Definition!.SourcePayloadSha256);
        Assert.Contains(preview.Warnings, warning => warning.Contains("runtime parity"));
    }

    [Fact]
    public void StandardModeUsesNearestFoldersAndCaseSensitiveSubstitutionTokens()
    {
        var definition = Definition(Draft(flags: new() { ["_parsePath"] = true, ["_applyExToFilename"] = true, ["_applyExToPath"] = false },
            strings: new() { ["_removeCharacters"] = "[ ]", ["_replaceWithSpace"] = "_ -", ["_tagExclusions"] = "skip" }, numbers: new() { ["_folderLevel"] = 2 }));
        var result = HistoricalFilenameParser.Evaluate(definition, new MediaItem { Path = "U:\\Library\\Parent Folder\\Nearest\\[Alice]_skip-Bob.mp4" });
        Assert.Equal(new[] { "Nearest", "Parent Folder", "Alice", "Bob" }, result.Tags);
        Assert.Null(result.Title); Assert.Empty(result.Properties);
    }

    [Theory]
    [InlineData(0, "One Two", "One", "Two")]
    [InlineData(1, "One,Two", "One", "Two")]
    [InlineData(2, "One;Two", "One", "Two")]
    [InlineData(3, "One_Two", "One", "Two")]
    [InlineData(4, "One-Two", "One", "Two")]
    [InlineData(5, "One Two", "One Two", null)]
    public void SixObservedSeparatorCodesAreDistinct(int code, string filename, string first, string? second)
    {
        var result = HistoricalFilenameParser.Evaluate(Definition(Draft(numbers: new() { ["_tagSeparator"] = code })), new MediaItem { Path = "U:\\media\\" + filename + ".mp4" });
        Assert.Equal(second == null ? new[] { first } : new[] { first, second }, result.Tags);
    }

    [Fact]
    public void GlobalFormattingStripsMarkupAndAppliesBypassBeforeCaseExclusions()
    {
        var context = Reviewed with { ExcludedSubstrings = "ignore;remove", BypassPatterns = "%KEEP%", ExcludeCapitalStart = true, ExcludeAllCapitals = true, ReservedDelimiter = "reserved-marker" };
        Assert.Equal("lower", HistoricalFilenameParser.FormatTag(" <i>lower</i>/ignore% ", context));
        Assert.Equal("KEEPignore", HistoricalFilenameParser.FormatTag("KEEPignore", context));
        Assert.Equal("", HistoricalFilenameParser.FormatTag("Capital", context));
        Assert.Equal("", HistoricalFilenameParser.FormatTag("CAPS", context));
        Assert.Equal("Multi Word", HistoricalFilenameParser.FormatTag("Multi Word", context));
        Assert.Equal("", HistoricalFilenameParser.FormatTag("reserved-marker", context));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public void VocabularyMatchingUsesLongestPhraseAndOptionalUnknownWords(int option, bool keepUnknown)
    {
        var definition = Definition(Draft(numbers: new() { ["_standardMatchOption"] = option }));
        var result = HistoricalFilenameParser.Evaluate(definition, new MediaItem { Path = "U:\\media\\new york unknown.mp4" },
            [new("New York", "Places"), new("New", "Words")]);
        Assert.Equal(keepUnknown ? new[] { "New York", "unknown" } : new[] { "New York" }, result.Tags);
        Assert.Empty(result.TagCategories); // Category identity is not inferred from the matched spelling.
    }

    [Fact]
    public void RegexSplitTokenExclusionsCategoriesHardcodedTagsAndTitleUseOriginalTokenOrder()
    {
        var draft = Draft(mode: 1, strings: new() { ["_regexTemplate"] = "_", ["_regexExcludeByToken"] = "$2", ["_titleTemplate"] = "$2 / $1" },
            templates: [new(0, "People", "$1 \"Fixed\"", false, 0, false)]);
        var result = HistoricalFilenameParser.Evaluate(Definition(draft), new MediaItem { Path = "U:\\media\\Alice_Berlin.mp4" });
        Assert.Equal(new[] { "Alice", "Fixed" }, result.Tags);
        Assert.Equal("Berlin / Alice", result.Title);
        Assert.Equal(new[] { "Alice", "Fixed" }, result.TagCategories["People"]);
    }

    [Fact]
    public void RegexMatchUsesGroupOneAndOriginalExtensionGetterIsAlwaysFalse()
    {
        var draft = Draft(mode: 1, flags: new() { ["_regexIncludeExtension"] = true },
            strings: new() { ["_regexTemplate"] = "\\[(.+?)\\]", ["_regexTagExclusions"] = "Two" }, numbers: new() { ["_regexFormulaOption"] = 1 });
        var preview = HistoricalFilenameParser.Translate(draft, Reviewed);
        Assert.True(preview.CanActivate); Assert.Contains(preview.Warnings, text => text.Contains("getter"));
        var result = HistoricalFilenameParser.Evaluate(preview.Definition!, new MediaItem { Path = "U:\\media\\[One] [Two].mp4" });
        Assert.Equal(new[] { "One" }, result.Tags);
    }

    [Fact]
    public void RegexCaseVariantsKeepSeparateOrdinalsBeforeCatalogTagNormalization()
    {
        var definition = Definition(Draft(mode: 1, strings: new() { ["_regexTemplate"] = "_", ["_regexExcludeByToken"] = "$2", ["_titleTemplate"] = "$3" }));
        var result = HistoricalFilenameParser.Evaluate(definition, new MediaItem { Path = "U:\\media\\Alpha_alpha_Beta.mp4" });
        Assert.Equal(new[] { "Alpha", "Beta" }, result.Tags); Assert.Equal("Beta", result.Title);
    }

    [Fact]
    public void ExplicitRegexCultureIsRestoredAfterConstruction()
    {
        var before = CultureInfo.CurrentCulture;
        var definition = Definition(Draft(mode: 1, strings: new() { ["_regexTemplate"] = "(I)" }, numbers: new() { ["_regexFormulaOption"] = 1 }), Reviewed with { CultureName = "tr-TR" });
        var result = HistoricalFilenameParser.Evaluate(definition, new MediaItem { Path = "U:\\media\\ı.mp4" });
        Assert.Equal(new[] { "ı" }, result.Tags); Assert.Equal(before, CultureInfo.CurrentCulture);
    }

    [Fact]
    public void PropertyModeRequiresTypesAndProducesTypedTextNumberAndDateValues()
    {
        var draft = Draft(mode: 1, strings: new() { ["_regexTemplate"] = "_" }, numbers: new() { ["_mode"] = 1 },
            templates: [new(0, null, "$1", false, 10, true), new(1, null, "$2", false, 11, true), new(2, null, "$3", false, 12, true)]);
        Assert.False(HistoricalFilenameParser.Translate(draft, Reviewed).CanActivate);
        HistoricalParserPropertyBinding[] bindings = [new(10, "Label", HistoricalParserPropertyType.Text), new(11, "Score", HistoricalParserPropertyType.Number), new(12, "Date", HistoricalParserPropertyType.Date)];
        var preview = HistoricalFilenameParser.Translate(draft, Reviewed with { CultureName = "de-DE" }, propertyBindings: bindings);
        Assert.True(preview.CanActivate, string.Join("; ", preview.Blockers));
        var result = HistoricalFilenameParser.Evaluate(preview.Definition!, new MediaItem { Path = "U:\\media\\text_12,5_2024-10-24.mp4" });
        Assert.Empty(result.Tags); Assert.Equal(new[] { "text", "12.5", "2024-10-24T00:00:00.0000000" }, result.Properties.Select(p => p.Value));
        Assert.False(HistoricalFilenameParser.Translate(draft, Reviewed, propertyBindings: [bindings[0] with { HasDomainValues = true }, bindings[1], bindings[2]]).CanActivate);
    }

    [Fact]
    public void TagRuleModeUsesExplicitRuleSnapshotsAndGlobalFormatting()
    {
        var draft = Draft(mode: 2);
        Assert.False(HistoricalFilenameParser.Translate(draft, Reviewed).CanActivate);
        FilenameTagRule[] rules = [new() { Name = "Match", Groups = [new() { Match = TagRuleMatch.Any, Tags = ["tag-ignore"], Conditions = [new(TagRuleField.Filename, TagRuleComparison.Contains, "Alice"), new(TagRuleField.Title, TagRuleComparison.Contains, "other")] }] }];
        var preview = HistoricalFilenameParser.Translate(draft, Reviewed with { ExcludedSubstrings = "-ignore" }, rules);
        Assert.True(preview.CanActivate);
        Assert.Equal(new[] { "tag" }, HistoricalFilenameParser.Evaluate(preview.Definition!, new MediaItem { Path = "U:\\media\\Alice.mp4" }).Tags);
    }

    [Fact]
    public void UnknownCodesMembersMissingInventoryAndModifiedInventoryBlockActivation()
    {
        Assert.False(HistoricalFilenameParser.Translate(Draft(numbers: new() { ["_tagSeparator"] = 77 }), Reviewed).CanActivate);
        Assert.False(HistoricalFilenameParser.Translate(Draft(unknown: true), Reviewed).CanActivate);
        var draft = Draft();
        Assert.False(HistoricalFilenameParser.Translate(draft with { Flags = [] }, Reviewed).CanActivate);
        var flags = new Dictionary<string, bool>(draft.Flags) { ["_parseFilename"] = false };
        var changed = HistoricalFilenameParser.Translate(draft with { Flags = flags }, Reviewed);
        Assert.False(changed.CanActivate); Assert.Contains(changed.Blockers, text => text.Contains("original bytes"));
    }

    [Fact]
    public void ActivationAndApplicationAreExplicitAtomicAndKeepRawDraftRatingAndOtherMetadata()
    {
        using var catalog = new CatalogRepository(Path.Combine(root, "catalog.db"));
        var draft = Draft(); catalog.UpsertLegacyParserDraft(draft);
        var item = catalog.Upsert(new MediaItem { Path = "U:\\media\\Alice Bob.mp4", Title = "Kept", Rating = 9, Tags = ["manual"], Description = "Kept description", WatchLater = true });
        var translation = catalog.PreviewLegacyParserTranslation(draft.Id, Reviewed);
        Assert.Equal(new[] { "manual" }, catalog.Search().Single().Tags);
        var definition = catalog.ActivateLegacyParserTranslation(translation);
        Assert.Single(catalog.GetHistoricalParserDefinitions());
        var preview = catalog.PreviewHistoricalParser(definition.Id, [item.Id]);
        Assert.Equal(new[] { "manual" }, catalog.Search().Single().Tags);
        Assert.Equal(1, catalog.ApplyHistoricalParser(preview));
        var saved = catalog.Search().Single();
        Assert.Equal(new[] { "Alice", "Bob", "manual" }, saved.Tags.Order()); Assert.Equal(9, saved.Rating);
        Assert.True(saved.WatchLater); Assert.Equal(item.Description, saved.Description); Assert.Equal("Kept", saved.Title);
        Assert.Equal(draft.PayloadSha256, catalog.GetLegacyParserDrafts().Single().PayloadSha256); Assert.False(catalog.GetLegacyParserDrafts().Single().IsExecutable);
        Assert.Throws<InvalidOperationException>(() => catalog.ApplyHistoricalParser(preview));
        Assert.Equal(0, catalog.ApplyHistoricalParser(catalog.PreviewHistoricalParser(definition.Id, [item.Id])));
    }

    [Fact]
    public void StaleMediaOrVocabularyAbortsAllSelectedChanges()
    {
        using var catalog = new CatalogRepository(Path.Combine(root, "catalog.db"));
        var definition = Activate(catalog, Draft());
        var first = catalog.Upsert(new MediaItem { Path = "U:\\media\\First.mp4", Rating = 7 });
        var second = catalog.Upsert(new MediaItem { Path = "U:\\media\\Second.mp4", Rating = 8 });
        var preview = catalog.PreviewHistoricalParser(definition.Id, [first.Id, second.Id]);
        catalog.Upsert(second with { Rating = 9 });
        Assert.Throws<InvalidOperationException>(() => catalog.ApplyHistoricalParser(preview));
        Assert.All(catalog.Search(), item => Assert.Empty(item.Tags));
        preview = catalog.PreviewHistoricalParser(definition.Id, [first.Id, second.Id]);
        catalog.UpsertVocabularyTag(new("New", "People"));
        Assert.Throws<InvalidOperationException>(() => catalog.ApplyHistoricalParser(preview));
        Assert.All(catalog.Search(), item => Assert.Empty(item.Tags));
    }

    [Fact]
    public void ForgedPreviewAfterValuesCannotReplaceUnselectedMetadata()
    {
        using var catalog = new CatalogRepository(Path.Combine(root, "catalog.db"));
        var definition = Activate(catalog, Draft());
        var item = catalog.Upsert(new MediaItem { Path = "U:\\media\\Name.mp4", Rating = 9 });
        var preview = catalog.PreviewHistoricalParser(definition.Id, [item.Id]);
        var forged = preview with { Changes = [preview.Changes.Single() with { After = item with { Rating = 0, Title = "Forged" } }] };
        Assert.Equal(1, catalog.ApplyHistoricalParser(forged));
        var saved = catalog.Search().Single(); Assert.Equal(9, saved.Rating); Assert.NotEqual("Forged", saved.Title); Assert.Equal(new[] { "Name" }, saved.Tags);
    }

    [Fact]
    public void CancellationMissingSelectionAndRegexTimeoutNeverChangeCatalog()
    {
        using var catalog = new CatalogRepository(Path.Combine(root, "catalog.db"));
        var definition = Activate(catalog, Draft());
        var item = catalog.Upsert(new MediaItem { Path = "U:\\media\\Name.mp4", Rating = 9 });
        Assert.Throws<KeyNotFoundException>(() => catalog.PreviewHistoricalParser(definition.Id, [item.Id, Guid.NewGuid()]));
        var preview = catalog.PreviewHistoricalParser(definition.Id, [item.Id]);
        Assert.Throws<OperationCanceledException>(() => catalog.ApplyHistoricalParser(preview, new CancellationToken(true)));
        var timeoutDraft = Draft(mode: 1, strings: new() { ["_regexTemplate"] = "(a+)+$" }, numbers: new() { ["_regexFormulaOption"] = 1 }, sourceKey: "timeout");
        var timeoutDefinition = Activate(catalog, timeoutDraft);
        var longItem = catalog.Upsert(new MediaItem { Path = "U:\\media\\" + new string('a', 20000) + "!.mp4", Rating = 8 });
        Assert.Throws<InvalidOperationException>(() => catalog.PreviewHistoricalParser(timeoutDefinition.Id, [item.Id, longItem.Id]));
        Assert.All(catalog.Search(), value => Assert.Empty(value.Tags));
    }

    [Fact]
    public void ChangedDraftAndTamperedTranslationCannotBeActivated()
    {
        using var catalog = new CatalogRepository(Path.Combine(root, "catalog.db"));
        var draft = Draft(); catalog.UpsertLegacyParserDraft(draft);
        var preview = catalog.PreviewLegacyParserTranslation(draft.Id, Reviewed);
        var forged = preview with { Definition = preview.Definition! with { Options = preview.Definition.Options with { ParseFilename = false } } };
        Assert.Throws<InvalidOperationException>(() => catalog.ActivateLegacyParserTranslation(forged));
        catalog.UpsertLegacyParserDraft(Draft(numbers: new() { ["_tagSeparator"] = 3 }));
        Assert.Throws<InvalidOperationException>(() => catalog.ActivateLegacyParserTranslation(preview));
    }

    [Fact]
    public void PortableRoundtripRetainsActivatedDefinitionAndRawDraftWithoutRunningIt()
    {
        var path = Path.Combine(root, "portable.json"); Guid definitionId; Guid itemId; string payloadHash;
        using (var catalog = new CatalogRepository(Path.Combine(root, "catalog.db")))
        {
            var draft = Draft(); payloadHash = draft.PayloadSha256; var definition = Activate(catalog, draft); definitionId = definition.Id;
            itemId = catalog.Upsert(new MediaItem { Path = "U:\\media\\Alice Bob.mp4", Rating = 9 }).Id;
            catalog.Export(path);
        }
        using var restored = new CatalogRepository(Path.Combine(root, "restored.db"));
        Assert.Empty(restored.ImportJson(path).Errors);
        var restoredDefinition = Assert.Single(restored.GetHistoricalParserDefinitions()); Assert.Equal(definitionId, restoredDefinition.Id);
        Assert.Equal(payloadHash, Assert.Single(restored.GetLegacyParserDrafts()).PayloadSha256);
        Assert.Empty(restored.Search().Single().Tags);
        Assert.Equal(1, restored.ApplyHistoricalParser(restored.PreviewHistoricalParser(definitionId, [itemId])));
        Assert.Equal(new[] { "Alice", "Bob" }, restored.Search().Single().Tags);
    }

    [Fact]
    public void TypedPropertyValuesPersistWithSourceSpecificIdentityAndKeepOtherExtraValues()
    {
        using var catalog = new CatalogRepository(Path.Combine(root, "catalog.db"));
        var draft = Draft(mode: 1, strings: new() { ["_regexTemplate"] = "_" }, numbers: new() { ["_mode"] = 1 },
            templates: [new(0, null, "$1", false, 10, true)]);
        catalog.UpsertLegacyParserDraft(draft);
        var translation = catalog.PreviewLegacyParserTranslation(draft.Id, Reviewed, propertyBindings: [new(10, "Score", HistoricalParserPropertyType.Percentage)]);
        var definition = catalog.ActivateLegacyParserTranslation(translation);
        var foreignKey = HistoricalFilenameParser.PropertyMetadataKey("other-source", 10);
        var item = catalog.Upsert(new MediaItem { Path = "U:\\media\\12.5.mp4", Rating = 9, Extra = new() { ["manual"] = "kept", [foreignKey] = "foreign data" } });
        Assert.Equal(1, catalog.ApplyHistoricalParser(catalog.PreviewHistoricalParser(definition.Id, [item.Id])));
        var saved = catalog.Search().Single(); Assert.Empty(saved.Tags); Assert.Equal(9, saved.Rating); Assert.Equal("kept", saved.Extra["manual"]); Assert.Equal("foreign data", saved.Extra[foreignKey]);
        using var value = JsonDocument.Parse(saved.Extra[HistoricalFilenameParser.PropertyMetadataKey(definition.SourceId, 10)]!);
        Assert.Equal("12.5", value.RootElement[0].GetProperty("value").GetString());
        Assert.Equal("Percentage", value.RootElement[0].GetProperty("type").GetString());
    }

    [Fact]
    public void NonContiguousCategoryOrdinalsAndDomainBindingsAreNotGuessed()
    {
        var draft = Draft(mode: 1, strings: new() { ["_regexTemplate"] = "_" }, templates: [new(2, "People", "$1", false, 0, false)]);
        var translation = HistoricalFilenameParser.Translate(draft, Reviewed);
        Assert.False(translation.CanActivate); Assert.Contains(translation.Blockers, text => text.Contains("contiguous"));
    }

    private static HistoricalParserDefinition Activate(CatalogRepository catalog, LegacyParserDraft draft)
    {
        catalog.UpsertLegacyParserDraft(draft); return catalog.ActivateLegacyParserTranslation(catalog.PreviewLegacyParserTranslation(draft.Id, Reviewed));
    }
    private static HistoricalParserDefinition Definition(LegacyParserDraft draft, HistoricalTagFormatting? formatting = null)
    {
        var preview = HistoricalFilenameParser.Translate(draft, formatting ?? Reviewed);
        Assert.True(preview.CanActivate, string.Join("; ", preview.Blockers)); return preview.Definition!;
    }

    // Handwritten MS-NRBF fixture. The declared assembly is absent; no original type is instantiated.
    private static LegacyParserDraft Draft(int mode = 0, Dictionary<string, string>? strings = null, Dictionary<string, bool>? flags = null,
        Dictionary<string, int>? numbers = null, LegacyParserCategoryTemplate[]? templates = null, bool unknown = false, string sourceKey = "17")
    {
        const string parser = "MyMediaViewer.frmParseFilenameToTags+ParseFilenameSettings";
        string[] stringNames = ["_removeCharacters", "_replaceWithSpace", "_tagExclusions", "_regexTagExclusions", "_regexExcludeByToken", "_regexTemplate", "_titleTemplate", "_regex_removeCharacters", "_regex_replaceWithSpace", "_regex_dateformat"];
        string[] flagNames = ["_parseFilename", "_parsePath", "_applyExToFilename", "_applyExToPath", "_applyTagSepToFilename", "_applyTagSepToPath", "_matchExisting", "_useRegex", "_regexIncludePath", "_regexCaseSensitive", "_regexIncludeExtension", "_regexMatchExisting"];
        string[] numberNames = ["_tagSeparator", "_folderLevel", "_standardMatchOption", "_regexMatchOption", "_activeTab", "_mode", "_regexFormulaOption"];
        var members = stringNames.Concat(flagNames).Concat(numberNames).Append("_categoryTemplates").Concat(unknown ? ["_futureOption"] : Array.Empty<string>()).ToArray();
        var kinds = stringNames.Select(_ => (byte)1).Concat(flagNames.Select(_ => (byte)0)).Concat(numberNames.Select(name => name == "_activeTab" ? (byte)2 : (byte)0)).Append((byte)2).Concat(unknown ? [(byte)1] : Array.Empty<byte>()).ToArray();
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write((byte)0); writer.Write(1); writer.Write(-1); writer.Write(1); writer.Write(0);
        writer.Write((byte)12); writer.Write(99); writer.Write("Absent.Original.Assembly, Version=4.8.4.0, Culture=neutral, PublicKeyToken=null");
        Class(writer, 1, parser, members, kinds, [.. flagNames.Select(_ => (byte)1), .. numberNames.Where(name => name != "_activeTab").Select(_ => (byte)8)], true);
        var id = 10;
        foreach (var name in stringNames) String(writer, id++, strings?.GetValueOrDefault(name) ?? (name == "_regex_dateformat" ? "yyyy-MM-dd" : ""));
        foreach (var name in flagNames) writer.Write(flags?.GetValueOrDefault(name, name is "_parseFilename" or "_applyTagSepToFilename") ?? name is "_parseFilename" or "_applyTagSepToFilename");
        foreach (var name in numberNames)
        {
            if (name == "_activeTab") { Class(writer, id++, "MyMediaViewer.frmParseFilenameToTags+ParseFilenameTabs", ["value__"], [0], [8], true); writer.Write(mode); }
            else writer.Write(numbers?.GetValueOrDefault(name) ?? 0);
        }
        if (templates == null || templates.Length == 0) writer.Write((byte)10);
        else
        {
            var templateType = parser + "+regexCategoryTemplate"; var args = "[[System.Int32, mscorlib],[" + templateType + ", Absent.Original.Assembly]]";
            Class(writer, id++, "System.Collections.Generic.Dictionary`2" + args, ["HashSize", "KeyValuePairs"], [0, 2], [8]); writer.Write(templates.Length);
            writer.Write((byte)7); writer.Write(id++); writer.Write((byte)0); writer.Write(1); writer.Write(templates.Length); writer.Write((byte)3); writer.Write("System.Collections.Generic.KeyValuePair`2" + args);
            foreach (var template in templates)
            {
                Class(writer, id++, "System.Collections.Generic.KeyValuePair`2" + args, ["key", "value"], [0, 2], [8]); writer.Write(template.Ordinal);
                Class(writer, id++, templateType, ["category", "template", "isDefault", "property_id", "isproperty"], [1, 1, 0, 0, 0], [1, 8, 1], true);
                if (template.Category == null) writer.Write((byte)10); else String(writer, id++, template.Category);
                if (template.Template == null) writer.Write((byte)10); else String(writer, id++, template.Template);
                writer.Write(template.IsDefault); writer.Write(template.PropertyId); writer.Write(template.IsProperty);
            }
        }
        if (unknown) String(writer, id++, "future data"); writer.Write((byte)11); writer.Flush();
        return LegacyObjectReader.ReadParserSettings(stream.ToArray(), "synthetic-source", sourceKey);
    }
    private static void Class(BinaryWriter writer, int id, string type, string[] members, byte[] kinds, byte[] primitives, bool library = false)
    {
        writer.Write(library ? (byte)5 : (byte)4); writer.Write(id); writer.Write(type); writer.Write(members.Length); foreach (var member in members) writer.Write(member);
        writer.Write(kinds); writer.Write(primitives); if (library) writer.Write(99);
    }
    private static void String(BinaryWriter writer, int id, string value) { writer.Write((byte)6); writer.Write(id); writer.Write(value); }
}
