using System.Text.Json;

namespace FlixyPix.Core;

/// <summary>What a tag change touched; merges and deletions of the directory also carry their <see cref="TagChangeUndo"/>.</summary>
public sealed record TagChangeResult(int MediaChanged,int SearchesChanged,int RulesChanged,TagChangeUndo? Undo=null);

public sealed partial class CatalogRepository
{
 /// <summary>Renames/merges globally, or deletes when replacement is null. Raw legacy provenance stays unchanged.</summary>
 public TagChangeResult ChangeTag(string tag,string? replacement,CancellationToken cancellationToken=default)
 {
  ArgumentException.ThrowIfNullOrWhiteSpace(tag);
  if(replacement!=null)ArgumentException.ThrowIfNullOrWhiteSpace(replacement);
  lock(gate)
  {
   ThrowIfDisposed();using var transaction=connection.BeginTransaction();
   var result=ChangeTagCore(tag,replacement,transaction,cancellationToken);
   cancellationToken.ThrowIfCancellationRequested();transaction.Commit();return result;
  }
 }

 private TagChangeResult ChangeTagCore(string tag,string? replacement,Microsoft.Data.Sqlite.SqliteTransaction transaction,CancellationToken cancellationToken)
 {
  tag=tag.Trim();replacement=replacement?.Trim();
  {
   var mediaChanged=0;var searchesChanged=0;var rulesChanged=0;
   using var select=Command("SELECT DISTINCT media_id FROM media_tags WHERE tag=$tag COLLATE EFP_ORDINAL",transaction,("$tag",tag));
   var ids=new List<string>();using(var reader=select.ExecuteReader())while(reader.Read())ids.Add(reader.GetString(0));
   foreach(var id in ids)
   {
    cancellationToken.ThrowIfCancellationRequested();using var command=Command("SELECT document FROM media WHERE id=$id",transaction,("$id",id));
    var item=JsonSerializer.Deserialize<MediaItem>((string)command.ExecuteScalar()!,Json)!;
    UpsertCore(item with {Tags=Replace(item.Tags),TagCategories=item.TagCategories.ToDictionary(p=>p.Key,p=>Replace(p.Value),StringComparer.OrdinalIgnoreCase)},transaction);mediaChanged++;
   }
   foreach(var search in ReadDocuments<SavedSearch>("saved_searches",transaction))
   {
    cancellationToken.ThrowIfCancellationRequested();
    var changed=search with {Tag=Same(search.Tag,tag)?replacement:search.Tag,Expression=Rewrite(search.Expression)};
    // A deleted direct constraint must not silently broaden a saved search.
    if(replacement==null&&Same(search.Tag,tag))
    {
     var missing=SearchExpression.Where(new(){Field=SearchField.Tag,Comparison=SearchComparison.Equals,Text=tag});
     changed=changed with {Expression=changed.Expression==null?missing:SearchExpression.All(changed.Expression,missing)};
    }
    if(search.State is {} state)changed=changed with {State=RewriteState(state)};
    if(JsonSerializer.Serialize(search,Json)==JsonSerializer.Serialize(changed,Json))continue;
    SaveDocument("saved_searches",changed.Id,changed,transaction);searchesChanged++;
   }
   foreach(var scene in ReadDocuments<Scene>("scenes",transaction))
   {
    cancellationToken.ThrowIfCancellationRequested();
    if(scene.Tags.Any(t=>Same(t,tag)))SaveScene(scene with {Tags=Replace(scene.Tags)},transaction);
   }
   foreach(var rule in ReadDocuments<FilenameTagRule>("filename_rules",transaction))
   {
    var affected=rule.Tags.Any(t=>Same(t,tag))||rule.Groups.Any(g=>g.Tags.Any(t=>Same(t,tag))||g.Conditions.Any(c=>c.Field==TagRuleField.Tag&&c.Value.Split(';').Any(t=>Same(t.Trim(),tag))));
    if(!affected)continue;
    var changed=rule with {Tags=Replace(rule.Tags),Groups=rule.Groups.Select(g=>g with {Tags=Replace(g.Tags),Conditions=g.Conditions.Select(c=>c.Field==TagRuleField.Tag?c with {Value=string.Join(';',Replace(c.Value.Split(';').Select(t=>t.Trim())))}:c).ToList()}).ToList()};
    if(replacement==null)
    {
     // Keep historical rule text for review; prevent a deletion from enabling an unintended rule.
     changed=rule with {Enabled=false,Extra=new(rule.Extra){["disabled.deletedTag"]=tag}};
    }
    SaveDocument("filename_rules",changed.Id,changed,transaction);rulesChanged++;
   }
   var preferences=ReadPreferences(transaction);
   // A merged tag keeps the other's aliases, favourite mark and description; the old name finds it as an alias.
   // Studios that belonged to the tag follow its new name, or belong to nothing once it is deleted.
   var vocabulary=preferences.TagVocabulary.Where(t=>!Same(t.Tag,tag)||replacement!=null)
    .Select(t=>Same(t.Tag,tag)?t with {Tag=replacement!,Aliases=[..(t.Aliases??[]),t.Tag]}:t)
    .Select(t=>t.Profile?.ParentStudio is {} parent&&Same(parent,tag)?t with {Profile=(t.Profile with {ParentStudio=replacement}).Normalized()}:t)
    .Select(t=>t.Profile?.ParentStudio is {} parent&&Same(parent,t.Tag)?t with {Profile=(t.Profile with {ParentStudio=null}).Normalized()}:t)
    .GroupBy(t=>t.Category+"\0"+t.Tag,StringComparer.OrdinalIgnoreCase).Select(MergeEntries).ToList();
   SaveDocument("preferences",Guid.Empty,preferences with {TagVocabulary=vocabulary, QuickTags=RewriteQuickTags(preferences.QuickTags)},transaction);
   List<string?>? RewriteQuickTags(List<string?>? slots)
   {
    if(slots==null)return null;var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    return slots.Select(t=>t!=null&&Same(t,tag)?replacement:t).Select(t=>t!=null&&seen.Add(t)?t:null).ToList();
   }
   RewriteHistoricalTagPresets(tag,replacement,transaction,cancellationToken);
   RewriteTagAssets(tag,replacement,transaction,cancellationToken);
   return new(mediaChanged,searchesChanged,rulesChanged);
  }
  List<string> Replace(IEnumerable<string> values)=>values.Where(t=>!Same(t,tag)||replacement!=null).Select(t=>Same(t,tag)?replacement!:t).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
  // The saved view: tag filters and the filter bar follow a rename; a deleted tag that was required keeps the search empty.
  ViewState RewriteState(ViewState state)
  {
   List<TagFacet> Facets(List<TagFacet> facets)=>facets.Where(f=>!Same(f.Tag,tag)||replacement!=null).Select(f=>Same(f.Tag,tag)?f with {Tag=replacement!}:f).Distinct().ToList();
   var required=state.Tag is {} single&&Same(single,tag)||state.RequiredTags.Any(t=>Same(t,tag))||state.Filter.Include.Any(f=>Same(f.Tag,tag));
   var result=state with {Tag=Same(state.Tag,tag)?replacement:state.Tag,RequiredTags=Replace(state.RequiredTags),ExcludedTags=Replace(state.ExcludedTags),Expression=Rewrite(state.Expression),
    Filter=state.Filter with {Include=Facets(state.Filter.Include),Exclude=Facets(state.Filter.Exclude),Profiles=state.Filter.Profiles.Select(RewriteStudio).ToList()}};
   if(replacement==null&&required)
   {
    var missing=SearchExpression.Where(new(){Field=SearchField.Tag,Comparison=SearchComparison.Equals,Text=tag});
    result=result with {Expression=result.Expression==null?missing:SearchExpression.All(result.Expression,missing)};
   }
   return result;
  }
  // A studio condition follows a renamed studio; after a deletion it keeps the name and finds nothing more.
  SearchCondition RewriteStudio(SearchCondition condition)=>
   condition is {Field:SearchField.Profile,Profile:ProfileProperty.Studio,Text:{} name}&&Same(name,tag)&&replacement!=null?condition with {Text=replacement}:condition;
  SearchExpression? Rewrite(SearchExpression? expression)
  {
   if(expression==null)return null;
   if(expression.Condition is {Field:SearchField.Tag or SearchField.CategorizedTag,Comparison:SearchComparison.Equals or SearchComparison.NotEquals,Text:{} value} condition&&Same(value,tag))
    return replacement==null?expression:expression with {Condition=condition with {Text=replacement}};
   if(expression.Condition is {} studio&&RewriteStudio(studio) is var renamed&&!ReferenceEquals(renamed,studio))return expression with {Condition=renamed};
   return expression with {Children=expression.Children.Select(e=>Rewrite(e)!).ToList()};
  }
 }
 internal static TagVocabularyEntry MergeEntries(IEnumerable<TagVocabularyEntry> group)
 {
  var entries=group.ToList();var first=entries[0];
  var aliases=entries.SelectMany(e=>e.Aliases??[]).Where(a=>!Same(a,first.Tag)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
  // Profiles are combined field by field; the first entry wins where both have a value.
  var profile=entries.Select(e=>e.Profile).Aggregate((TagProfile?)null,(sum,next)=>sum is null?next:sum.FillFrom(next));
  return first with {IsFavorite=entries.Any(e=>e.IsFavorite),Aliases=aliases.Count==0?null:aliases,Description=entries.Select(e=>e.Description).FirstOrDefault(d=>!string.IsNullOrWhiteSpace(d)),
   Profile=profile?.Normalized()};
 }
 public int MoveTagCategory(string tag,string fromCategory,string toCategory,CancellationToken cancellationToken=default)
 {
  ArgumentException.ThrowIfNullOrWhiteSpace(tag);ArgumentException.ThrowIfNullOrWhiteSpace(fromCategory);ArgumentException.ThrowIfNullOrWhiteSpace(toCategory);
  lock(gate)
  {
   ThrowIfDisposed();using var transaction=connection.BeginTransaction();
   var changed=MoveTagCategoryCore(tag,fromCategory,toCategory,transaction,cancellationToken);
   cancellationToken.ThrowIfCancellationRequested();transaction.Commit();return changed;
  }
 }

 private int MoveTagCategoryCore(string tag,string fromCategory,string toCategory,Microsoft.Data.Sqlite.SqliteTransaction transaction,CancellationToken cancellationToken)
 {
  {
   var changed=0;
   using var select=Command("SELECT DISTINCT media_id FROM media_tags WHERE tag=$tag COLLATE EFP_ORDINAL AND category=$category COLLATE EFP_ORDINAL",transaction,("$tag",tag),("$category",fromCategory));
   var ids=new List<string>();using(var reader=select.ExecuteReader())while(reader.Read())ids.Add(reader.GetString(0));
   foreach(var id in ids)
   {
    cancellationToken.ThrowIfCancellationRequested();using var command=Command("SELECT document FROM media WHERE id=$id",transaction,("$id",id));var item=JsonSerializer.Deserialize<MediaItem>((string)command.ExecuteScalar()!,Json)!;
    var categories=item.TagCategories.ToDictionary(p=>p.Key,p=>p.Value.ToList(),StringComparer.OrdinalIgnoreCase);
    categories[fromCategory]=categories[fromCategory].Where(t=>!Same(t,tag)).ToList();if(categories[fromCategory].Count==0)categories.Remove(fromCategory);
    categories[toCategory]=categories.GetValueOrDefault(toCategory,[]).Append(tag).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    UpsertCore(item with {TagCategories=categories},transaction);changed++;
   }
   var preferences=ReadPreferences(transaction);
   var tags=preferences.TagVocabulary.Select(t=>Same(t.Tag,tag)&&Same(t.Category,fromCategory)?t with {Category=toCategory}:t).GroupBy(t=>t.Category+"\0"+t.Tag,StringComparer.OrdinalIgnoreCase).Select(MergeEntries).ToList();
   SaveDocument("preferences",Guid.Empty,preferences with {TagVocabulary=tags},transaction);
   MoveHistoricalTagPresetCategory(tag,fromCategory,toCategory,transaction,cancellationToken);
   MoveTagAssetCategory(tag,fromCategory,toCategory,transaction,cancellationToken);
   MoveCategorizedTagSearchReferences(new(fromCategory,tag),new(toCategory,tag),transaction,cancellationToken);
   return changed;
  }
 }
}
