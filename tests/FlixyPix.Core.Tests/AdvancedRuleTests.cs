using System.Text.Json;
using FlixyPix.Core;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class AdvancedRuleTests : IDisposable
{
 private readonly string root = Path.Combine(Path.GetTempPath(),"flixypix-rule-tests-" + Guid.NewGuid().ToString("N"));
 public AdvancedRuleTests() => Directory.CreateDirectory(root);
 [Fact]
 public void GroupsSupportAllAnyRegexPathTitleAndExistingTags()
 {
  var item = new MediaItem { Path = Path.Combine(root,"Urlaub","Sommer-2024.mp4"), Title = "Erinnerung", Tags = ["Familie"] };
  var rule = new FilenameTagRule { Name = "Gruppen", Groups = [
   new() { Conditions = [new(TagRuleField.Filename,TagRuleComparison.Matches,@"Sommer-\d{4}$"),new(TagRuleField.Path,TagRuleComparison.Contains,"Urlaub")], Tags = ["Reise"] },
   new() { Match = TagRuleMatch.Any, Conditions = [new(TagRuleField.Title,TagRuleComparison.Contains,"nichts"),new(TagRuleField.Tag,TagRuleComparison.HasTag,"Freunde; Familie")], Tags = ["Menschen"] },
   new() { Conditions = [new(TagRuleField.Filename,TagRuleComparison.Contains,".mp4")], Tags = ["Falscher Treffer"] }
  ] };
  Assert.Equal(new[] { "Reise","Menschen" },CatalogRepository.EvaluateTagRule(item,rule));
  using var catalog = new CatalogRepository(Path.Combine(root,"catalog.db")); catalog.Upsert(item); catalog.UpsertFilenameTagRule(rule);
  Assert.Equal(1,catalog.ApplyFilenameTagRules()); Assert.Equal(0,catalog.ApplyFilenameTagRules());
  Assert.Equal(new[] { "Familie","Reise","Menschen" },catalog.Search().Single().Tags);
  catalog.Export(Path.Combine(root,"backup.json"));
  using var copy = new CatalogRepository(Path.Combine(root,"copy.db")); Assert.Empty(copy.ImportJson(Path.Combine(root,"backup.json")).Errors);
  Assert.Equal(3,copy.GetFilenameTagRules().Single().Groups.Count);
 }
 [Fact]
 public void OriginalJsonImportAndExportPreserveObservedGroupingSemantics()
 {
  var json = """{"tagrules":[{"rulename":"Original","ruleitems":[{"type":"filename","join":"none","comparison":"contains","key":"Sommer","tags":"Reise; Sonne"},{"type":"title","join":"or","comparison":"matches","key":"Erinnerung","tags":""},{"type":"tag","join":"none","comparison":"hastag","key":"Familie","tags":"Menschen"}]}]}""";
  var input = Path.Combine(root,"rules.json"); File.WriteAllText(input,json);
  using var catalog = new CatalogRepository(Path.Combine(root,"catalog.db"));
  Assert.Equal(1,catalog.ImportOriginalTagRules(input).Imported);
  var rule = catalog.GetFilenameTagRules().Single(); Assert.Equal(2,rule.Groups.Count); Assert.Equal(TagRuleMatch.Any,rule.Groups[0].Match);
  var item = new MediaItem { Path = Path.Combine(root,"anders.mp4"), Title = "Erinnerung", Tags = ["Familie"] };
  Assert.Equal(new[] { "Reise","Sonne","Menschen" },CatalogRepository.EvaluateTagRule(item,rule));
  var output = Path.Combine(root,"export.json"); catalog.ExportOriginalTagRules(output);
  using var copy = new CatalogRepository(Path.Combine(root,"copy.db")); Assert.Empty(copy.ImportOriginalTagRules(output).Errors);
  Assert.Equal(CatalogRepository.EvaluateTagRule(item,rule),CatalogRepository.EvaluateTagRule(item,copy.GetFilenameTagRules().Single()));
  Assert.Equal(json,File.ReadAllText(input)); Assert.Throws<IOException>(() => catalog.ExportOriginalTagRules(input));
 }
 [Fact]
 public void InvalidOriginalFileRollsBackAllRulesAndBadRegexIsRejected()
 {
  var input = Path.Combine(root,"invalid.json"); File.WriteAllText(input,"""{"tagrules":[{"rulename":"First","ruleitems":[{"type":"filename","key":"x","tags":"ok"}]},{"rulename":"Invalid","ruleitems":[{"type":"other","key":"x","tags":"bad"}]}]}""");
  using var catalog = new CatalogRepository(Path.Combine(root,"catalog.db"));
  Assert.NotEmpty(catalog.ImportOriginalTagRules(input).Errors); Assert.Empty(catalog.GetFilenameTagRules());
  Assert.ThrowsAny<ArgumentException>(() => catalog.UpsertFilenameTagRule(new() { Name = "bad regex", Groups = [new() { Conditions = [new(TagRuleField.Title,TagRuleComparison.Matches,"[")], Tags = ["x"] }] }));
 }
 [Fact]
 public void OriginalExchangeRejectsDisabledRulesWithoutPublishingAnOutput()
 {
  using var catalog = new CatalogRepository(Path.Combine(root,"catalog.db"));
  catalog.UpsertFilenameTagRule(new() { Name = "Disabled", Enabled = false, Groups = [new() { Conditions = [new(TagRuleField.Title,TagRuleComparison.Contains,"x")], Tags = ["y"] }] });
  var output = Path.Combine(root,"rules.json");
  Assert.Throws<InvalidOperationException>(() => catalog.ExportOriginalTagRules(output));
  Assert.False(File.Exists(output));
 }
 [Fact]
 public void RegexTimeoutRollsBackEarlierUpdatesInsteadOfPartiallyTagging()
 {
  using var catalog = new CatalogRepository(Path.Combine(root,"catalog.db"));
  catalog.Upsert(new() { Path = Path.Combine(root,"first.mp4"), Title = "a" });
  catalog.Upsert(new() { Path = Path.Combine(root,"second.mp4"), Title = new string('a',5000) + "!" });
  catalog.UpsertFilenameTagRule(new() { Name = "bounded", Groups = [new() { Conditions = [new(TagRuleField.Title,TagRuleComparison.Matches,"^(a+)+$")], Tags = ["x"] }] });
  Assert.Throws<InvalidOperationException>(() => catalog.ApplyFilenameTagRules()); Assert.All(catalog.Search(),i => Assert.Empty(i.Tags));
 }
 public void Dispose() => Directory.Delete(root,true);
}
