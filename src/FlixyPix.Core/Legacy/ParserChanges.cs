namespace FlixyPix.Core;
public enum ParserChangeKind { Title, Tag, CategoryTag, Property }
public sealed record ParserChangeKey(Guid MediaId, ParserChangeKind Kind, string Category = "", string Value = "");
public sealed record ParserChangeRow(ParserChangeKey Key, string Before, string After);
public sealed partial class CatalogRepository
{
 public static IReadOnlyList<ParserChangeRow> ParserChanges(HistoricalParserPreview preview)
 {
  var rows = new List<ParserChangeRow>();
  foreach (var change in preview.Changes)
  {
   var before=change.Before; var after=change.After; var id=change.MediaId;
   if(before.Title!=after.Title) rows.Add(new(new(id,ParserChangeKind.Title),before.Title,after.Title));
   foreach(var tag in after.Tags.Except(before.Tags,StringComparer.OrdinalIgnoreCase)) rows.Add(new(new(id,ParserChangeKind.Tag,Value:tag),"",tag));
   foreach(var (category,tags) in after.TagCategories)
    foreach(var tag in tags.Except(before.TagCategories.GetValueOrDefault(category,[]),StringComparer.OrdinalIgnoreCase)) rows.Add(new(new(id,ParserChangeKind.CategoryTag,category,tag),"",tag));
   foreach(var (key,value) in after.Extra)
    if(before.Extra.GetValueOrDefault(key)!=value) rows.Add(new(new(id,ParserChangeKind.Property,Value:key),before.Extra.GetValueOrDefault(key)??"",value??""));
  }
  return rows;
 }
}
