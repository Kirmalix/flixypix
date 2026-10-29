using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

public sealed partial class CatalogRepository
{
 private void ImportLegacyOrganization(Func<string,IEnumerable<JsonElement>> rows,SqliteTransaction transaction,List<string> warnings)
 {
  var preferences=ReadPreferences(transaction);
  var favoredIds=rows("favoriteperformers").Select(row=>Text(row,"ID")).Where(id=>id!=null).ToHashSet(StringComparer.Ordinal);
  var vocabulary=preferences.TagVocabulary.ToList();
  foreach(var row in rows("searchcriteria"))
  {
   if(Text(row,"movie")!="SEARCHTAGINFO"||Text(row,"value1") is not {} tag||Text(row,"category") is not {} category||string.IsNullOrWhiteSpace(tag)||string.IsNullOrWhiteSpace(category)||category is "Recent" or "Uncategorized")continue;
   var value=new TagVocabularyEntry(tag,category,favoredIds.Contains(Text(row,"ID")),Text(row,"ID"));
   AuditLegacy("searchcriteria",Text(row,"ID"),"partially-projected","tag-vocabulary",transaction);
   var index=vocabulary.FindIndex(t=>Same(t.Tag,tag)&&Same(t.Category,category));
   if(index<0)vocabulary.Add(value);else vocabulary[index]=value with {IsFavorite=value.IsFavorite||vocabulary[index].IsFavorite};
  }
  var folders=preferences.FolderFavorites.ToList();
  foreach(var row in rows("favoritefolders"))
  {
   var path=Text(row,"fullpath");var kind=Number(row,"mtype") switch {0=>MediaKind.Video,1=>MediaKind.Image,_=>(MediaKind?)null};
   if(string.IsNullOrWhiteSpace(path)||kind==null){warnings.Add("favoritefolders: unresolved path/type retained raw.");continue;}
   try
   {
    var value=new FolderFavorite(System.IO.Path.GetFullPath(path),kind,Text(row,"category"));
    AuditLegacy("favoritefolders",Text(row,"ID"),"partially-projected","folder-favorite",transaction);
    if(!folders.Any(f=>PathKey(f.Path)==PathKey(value.Path)&&f.Kind==value.Kind&&Same(f.Category,value.Category)))folders.Add(value);
   }
   catch(ArgumentException){warnings.Add("favoritefolders: invalid path retained raw.");}
  }
  SaveDocument("preferences",Guid.Empty,preferences with {FolderFavorites=folders,TagVocabulary=vocabulary},transaction);
 }
}
