using FlixyPix.Core;

namespace FlixyPix.Core.Tests;

/// <summary>Independent small-fixture oracle, intentionally outside the production query implementation.</summary>
internal static class WallReference
{
 internal static IReadOnlyList<WallMark> Read(CatalogRepository catalog,WallQuery query)
 {
  var media=catalog.Search().Where(item=>item.Kind==MediaKind.Video).ToDictionary(item=>item.Id);
  var marks=new List<WallMark>();
  if(query.Scenes)foreach(var scene in catalog.GetScenes())
   if(media.TryGetValue(scene.MediaId,out var item))marks.Add(new(WallMarkKind.Scene,scene.Id,item,scene.Title,scene.Description,scene.StartSeconds,scene.EndSeconds,scene.Tags,scene.PreviewAssetId));
  if(query.Bookmarks)foreach(var mark in catalog.GetBookmarks())
   if(media.TryGetValue(mark.MediaId,out var item))marks.Add(new(WallMarkKind.Bookmark,mark.Id,item,mark.Title,mark.Description,mark.PositionSeconds,null,mark.Tags,mark.PreviewAssetId));
  var text=string.IsNullOrWhiteSpace(query.Text)?null:query.Text.Trim();
  var tags=(query.Tags??[]).Where(t=>!string.IsNullOrWhiteSpace(t)).Select(t=>t.Trim()).ToArray();
  return marks.Where(mark=>(text is null || new[]{mark.Title,mark.Description,mark.Media.Title,mark.Media.Description}.Any(field=>field?.Contains(text,StringComparison.CurrentCultureIgnoreCase)==true))
   && tags.All(tag=>mark.AllTags.Contains(tag,StringComparer.CurrentCultureIgnoreCase)))
   .OrderByDescending(mark=>mark.Media.AddedAt).ThenBy(mark=>mark.Media.Id).ThenBy(mark=>mark.StartSeconds).ThenBy(mark=>mark.Id).ToArray();
 }
}
