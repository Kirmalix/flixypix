using FlixyPix.Core;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class OrganizationTests:IDisposable
{
 private readonly string root=Path.Combine(Path.GetTempPath(),"flixypix-organization-"+Guid.NewGuid().ToString("N"));
 [Fact]
 public void RenameMergesAllCategoriesVocabularyAndTypedRuleAndSearchReferences()
 {
  using var catalog=new CatalogRepository(Path.Combine(root,"catalog.db"));
  var media=catalog.Upsert(new(){Path=Path.Combine(root,"photo.jpg"),Tags=["Anna","Anne"],TagCategories=new(){["Personen"]=["Anna"],["Freunde"]=["Anna"]}});
  catalog.UpsertVocabularyTag(new("Anna","Personen",true));catalog.UpsertVocabularyTag(new("Anne","Personen"));
  catalog.UpsertFilenameTagRule(new(){Name="Rule",Groups=[new(){Conditions=[new(TagRuleField.Tag,TagRuleComparison.HasTag,"Anna; Tom")],Tags=["Anna"]}]});
  catalog.UpsertSavedSearch(new(){Name="Anna",Tag="Anna",Expression=SearchExpression.Where(new(){Field=SearchField.Tag,Comparison=SearchComparison.Equals,Text="Anna"})});
  var result=catalog.ChangeTag("Anna","Anne");Assert.Equal(new TagChangeResult(1,1,1),result);
  var item=catalog.GetMedia(media.Id)!;Assert.Equal(new[]{"Anne"},item.Tags);Assert.All(item.TagCategories,p=>Assert.Equal(new[]{"Anne"},p.Value));
  Assert.Single(catalog.GetPreferences().TagVocabulary);Assert.True(catalog.GetPreferences().TagVocabulary[0].IsFavorite);
  Assert.Equal("Anne",catalog.GetSavedSearches().Single().Tag);Assert.Equal("Anne;Tom",catalog.GetFilenameTagRules().Single().Groups[0].Conditions[0].Value);
  Assert.Equal(1,catalog.MoveTagCategory("Anne","Personen","Mitwirkende"));Assert.Contains("Mitwirkende",catalog.GetMedia(media.Id)!.TagCategories.Keys);
 }
 [Fact]
 public void DeleteTagDoesNotBroadenSavedSearchAndDisablesReferencingRule()
 {
  using var catalog=new CatalogRepository(Path.Combine(root,"catalog.db"));catalog.Upsert(new(){Path=Path.Combine(root,"a.jpg"),Tags=["delete"]});
  var search=catalog.UpsertSavedSearch(new(){Name="Narrow",Tag="delete"});catalog.UpsertFilenameTagRule(new(){Name="Rule",FilenameContains="a",Tags=["delete"]});
  catalog.ChangeTag("delete",null);Assert.Empty(catalog.SearchSaved(search.Id));Assert.False(catalog.GetFilenameTagRules().Single().Enabled);Assert.Empty(catalog.GetTags());
 }
 [Fact]
 public void FolderFavoritesAndParserPresetsSurviveBackupAndParserNeverChangesFiles()
 {
  using var catalog=new CatalogRepository(Path.Combine(root,"catalog.db"));var path=Path.Combine(root,"Familie","Urlaub-2024_test.jpg");
  catalog.ToggleFolderFavorite(new(root,MediaKind.Image));catalog.UpsertVocabularyTag(new("Leer","Personen",true));
  var parser=catalog.UpsertFilenameParser(new(){Name="Parser",ParentFolderDepth=1,Separators="_-",Replacements=[new("2024","Sommer")],Exclusions=["test"],Category="Automatisch"});
  var item=catalog.Upsert(new(){Path=path});Assert.Equal(1,catalog.ApplyFilenameParser(parser.Id));Assert.Equal(0,catalog.ApplyFilenameParser(parser.Id));
  Assert.Equal(new[]{"Urlaub","Sommer","Familie"},catalog.GetMedia(item.Id)!.Tags);Assert.False(File.Exists(path));
  var backup=Path.Combine(root,"backup.json");catalog.Export(backup);using var copy=new CatalogRepository(Path.Combine(root,"copy.db"));Assert.Empty(copy.ImportJson(backup).Errors);
  Assert.Single(copy.GetPreferences().FolderFavorites);Assert.Single(copy.GetPreferences().TagVocabulary);Assert.Single(copy.GetPreferences().FilenameParsers);
  copy.ToggleFolderFavorite(new(root,MediaKind.Image));Assert.Empty(copy.GetPreferences().FolderFavorites);
 }
 [Fact]
 public void LegacyVocabularyFavoritesAndFolderReferencesArePreservedWithoutTaggingMedia()
 {
  Directory.CreateDirectory(root);
  object Schema(string table,params string[] columns)=>new{type="schema",table,columns=columns.Select(name=>new{name})};
  object Row(string table,object values)=>new{type="row",table,values};
  object[] records=[new{type="header",format="flixypix-legacy",version=1,sourceSha256=new string('f',64)},
   Schema("searchcriteria","ID","movie","value1","category"),Row("searchcriteria",new{ID=5,movie="SEARCHTAGINFO",value1="Anna",category="Personen"}),
   Schema("favoriteperformers","ID"),Row("favoriteperformers",new{ID=5}),Schema("favoritefolders","ID","fullpath","category","mtype"),Row("favoritefolders",new{ID=1,fullpath=root,category="Archiv",mtype=1}),
   new{type="footer",tables=3,rows=3,tableRows=new Dictionary<string,int>{["searchcriteria"]=1,["favoriteperformers"]=1,["favoritefolders"]=1}}];
  var path=Path.Combine(root,"legacy.jsonl");File.WriteAllLines(path,records.Select(record=>System.Text.Json.JsonSerializer.Serialize(record)));
  using var catalog=new CatalogRepository(Path.Combine(root,"catalog.db"));Assert.Empty(catalog.ImportLegacyJsonl(path).Errors);Assert.Empty(catalog.Search());
  var preferences=catalog.GetPreferences();Assert.True(Assert.Single(preferences.TagVocabulary).IsFavorite);Assert.Equal("Anna",preferences.TagVocabulary[0].Tag);Assert.Equal(MediaKind.Image,Assert.Single(preferences.FolderFavorites).Kind);
  Assert.Empty(catalog.ImportLegacyJsonl(path).Errors);Assert.Single(catalog.GetPreferences().TagVocabulary);Assert.Single(catalog.GetPreferences().FolderFavorites);
 }
 [Fact]
 public void CancelledGlobalChangeRollsBackMetadata()
 {
  using var catalog=new CatalogRepository(Path.Combine(root,"catalog.db"));catalog.Upsert(new(){Path=Path.Combine(root,"a.jpg"),Tags=["Original"]});
  using var cancellation=new CancellationTokenSource();cancellation.Cancel();Assert.Throws<OperationCanceledException>(()=>catalog.ChangeTag("Original","Changed",cancellation.Token));Assert.Equal(new[]{"Original"},catalog.GetTags());
 }
 public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}
