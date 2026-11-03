using System.Text.Json;

namespace FlixyPix.Core;

public sealed record StashSourceReference(StashEntityKind Kind,string Key,string Relation,int Position,int? Order=null);

public static partial class StashExport
{
 internal static string ReferenceName(string name)=>"name:"+name;
 internal static string ReferencePath(string path)
 {var foreign=StashPaths.Foreign(path);return foreign.Windows?"win:"+foreign.Path.ToUpperInvariant():"unix:"+foreign.Path;}
 internal static string? GalleryKey(JsonElement value)
 {
  if(value.TryGetProperty("folder_path",out var folder)&&folder.ValueKind==JsonValueKind.String&&!string.IsNullOrWhiteSpace(folder.GetString()))return "folder:"+ReferencePath(folder.GetString()!);
  if(value.TryGetProperty("zip_files",out var zip)&&zip.ValueKind==JsonValueKind.Array&&zip.GetArrayLength()>0)return "zip:"+JsonSerializer.Serialize(zip.EnumerateArray().Select(item=>ReferencePath(item.GetString()!)).Order(StringComparer.Ordinal).ToArray());
  return value.TryGetProperty("title",out var title)&&title.ValueKind==JsonValueKind.String&&!string.IsNullOrWhiteSpace(title.GetString())?ReferenceName(title.GetString()!):null;
 }
 internal static IReadOnlyList<string> LookupKeys(StashEntityKind kind,JsonElement root)
 {
  if(kind is StashEntityKind.File or StashEntityKind.Folder)return [ReferencePath(root.GetProperty("path").GetString()!)];
  if(kind is StashEntityKind.Tag or StashEntityKind.Studio or StashEntityKind.Performer or StashEntityKind.Group)return [ReferenceName(root.GetProperty("name").GetString()!)];
  if(kind==StashEntityKind.Gallery)
  {
   var keys=new List<string>();if(GalleryKey(root) is {} preferred)keys.Add(preferred);
   if(root.TryGetProperty("title",out var title)&&title.ValueKind==JsonValueKind.String&&!string.IsNullOrWhiteSpace(title.GetString()))keys.Add(ReferenceName(title.GetString()!));return keys.Distinct(StringComparer.Ordinal).ToArray();
  }
  return [];
 }
 internal static IEnumerable<StashSourceReference> References(StashEntityKind kind,JsonElement root)
 {
  foreach(var(field,target,relation)in new[]{("tags",StashEntityKind.Tag,"tag"),("performers",StashEntityKind.Performer,"performer"),("parents",StashEntityKind.Tag,"parent")})
  {
   if(field=="parents"&&kind!=StashEntityKind.Tag)continue;
   if(root.TryGetProperty(field,out var values)&&values.ValueKind==JsonValueKind.Array)
   {var position=0;foreach(var value in values.EnumerateArray())yield return new(target,ReferenceName(value.GetString()!),relation,position++);}
  }
  if(root.TryGetProperty("studio",out var studio)&&studio.ValueKind==JsonValueKind.String&&!string.IsNullOrWhiteSpace(studio.GetString()))yield return new(StashEntityKind.Studio,ReferenceName(studio.GetString()!),"studio",0);
  if(kind==StashEntityKind.Studio&&root.TryGetProperty("parent_studio",out var network)&&network.ValueKind==JsonValueKind.String&&!string.IsNullOrWhiteSpace(network.GetString()))yield return new(StashEntityKind.Studio,ReferenceName(network.GetString()!),"parent-studio",0);
  if(kind is StashEntityKind.Scene or StashEntityKind.Image)
  {
   if(root.TryGetProperty("files",out var files)&&files.ValueKind==JsonValueKind.Array){var i=0;foreach(var file in files.EnumerateArray())yield return new(StashEntityKind.File,ReferencePath(file.GetString()!),"file",i++);}
   if(root.TryGetProperty("galleries",out var galleries)&&galleries.ValueKind==JsonValueKind.Array){var i=0;foreach(var gallery in galleries.EnumerateArray())if(GalleryKey(gallery)is {} key)yield return new(StashEntityKind.Gallery,key,"gallery",i++);}
  }
  if(kind==StashEntityKind.Scene&&root.TryGetProperty("movies",out var movies)&&movies.ValueKind==JsonValueKind.Array)
  {var i=0;foreach(var movie in movies.EnumerateArray())yield return new(StashEntityKind.Group,ReferenceName(movie.GetProperty("movieName").GetString()!),"group",i++,movie.TryGetProperty("scene_index",out var order)?order.GetInt32():null);}
  if(kind==StashEntityKind.Scene&&root.TryGetProperty("markers",out var markers)&&markers.ValueKind==JsonValueKind.Array)
  {
   var markerIndex=0;foreach(var marker in markers.EnumerateArray())
   {
    var tags=new List<string>();
    if(marker.TryGetProperty("tags",out var values)&&values.ValueKind==JsonValueKind.Array)tags.AddRange(values.EnumerateArray().Select(value=>value.GetString()!));
    if(marker.TryGetProperty("primary_tag",out var primary)&&primary.ValueKind==JsonValueKind.String&&!string.IsNullOrWhiteSpace(primary.GetString()))tags.Add(primary.GetString()!);
    var position=0;foreach(var tag in tags.Distinct(StringComparer.Ordinal))yield return new(StashEntityKind.Tag,ReferenceName(tag),"marker-tag:"+markerIndex,position++);
    markerIndex++;
   }
  }
 }
}
