using FlixyPix.Core;
using Xunit;
using System.Text.Json;

namespace FlixyPix.Core.Tests;
public sealed class SceneOrganizationTests : IDisposable
{
 private readonly string root=Path.Combine(Path.GetTempPath(),"flixypix-scene-org-"+Guid.NewGuid());
 public SceneOrganizationTests()=>Directory.CreateDirectory(root);
 public void Dispose()=>Directory.Delete(root,true);
 [Fact]
 public void ReorderingKeepsStableIdsAndPlaylistReferences()
 {
  using var repository=new CatalogRepository(Path.Combine(root,"catalog.db"));
  var media=repository.Upsert(new() { Path=Path.Combine(root,"movie.mp4"),Kind=MediaKind.Video });
  var first=repository.UpsertScene(new() { MediaId=media.Id,Title="first",StartSeconds=1,EndSeconds=2 });
  var second=repository.UpsertScene(new() { MediaId=media.Id,Title="second",StartSeconds=5,EndSeconds=6 });
  var playlist=repository.UpsertPlaylist(new() { Name="Clips",MediaIds=[media.Id],Entries=[new(media.Id,second.Id)] });
  repository.MoveScene(second.Id,-1); Assert.Equal(new[]{second.Id,first.Id},repository.GetOrderedScenes(media.Id).Select(s=>s.Id));
  Assert.Equal(second.Id,Assert.Single(repository.GetPlaylists()).Entries[0].SceneId);
  var b1=repository.UpsertBookmark(new() { MediaId=media.Id,PositionSeconds=1 }); var b2=repository.UpsertBookmark(new() { MediaId=media.Id,PositionSeconds=2 });
  repository.MoveBookmark(b1.Id,1); Assert.Equal(new[]{b2.Id,b1.Id},repository.GetOrderedBookmarks(media.Id).Select(b=>b.Id));
  repository.MoveBookmark(b1.Id,1); Assert.Equal(new[]{b2.Id,b1.Id},repository.GetOrderedBookmarks(media.Id).Select(b=>b.Id));
 }
 [Fact]
 public void PreviewBindingsRejectCrossMediaAndRoundtripWithTagsAndDescriptions()
 {
  using var repository=new CatalogRepository(Path.Combine(root,"catalog.db"));
  var media=repository.Upsert(new() { Path=Path.Combine(root,"movie.mp4"),Kind=MediaKind.Video });
  var other=repository.Upsert(new() { Path=Path.Combine(root,"other.mp4"),Kind=MediaKind.Video });
  var scene=repository.UpsertScene(new() { MediaId=media.Id,Title="Clip",StartSeconds=1,EndSeconds=2 });
  var bookmark=repository.UpsertBookmark(new() { MediaId=media.Id,Title="Point",PositionSeconds=1,Description="Original note" });
  var asset=repository.UpsertMediaAsset(new() { MediaId=media.Id,Data=[1,2,3],Label="Raw preview" });
  var wrong=repository.UpsertMediaAsset(new() { MediaId=other.Id,Data=[4,5,6] });
  Assert.Throws<ArgumentException>(()=>repository.SetScenePreview(scene.Id,wrong.Id)); Assert.Throws<ArgumentException>(()=>repository.SetBookmarkPreview(bookmark.Id,wrong.Id));
  repository.SetScenePreview(scene.Id,asset.Id); repository.SetBookmarkPreview(bookmark.Id,asset.Id); repository.SetSceneTags(scene.Id,[" People ","people","City"]);
  repository.Upsert(media with { ThumbnailPath=Path.Combine(root,"preview.png"),Extra=new() { ["thumbnail.assetId"]=asset.Id.ToString(),["retained"]="value" } });
  var backup=Path.Combine(root,"backup.json"); repository.Export(backup);
  using var restored=new CatalogRepository(Path.Combine(root,"restored.db")); Assert.Empty(restored.ImportJson(backup).Errors);
  var restoredScene=Assert.Single(restored.GetOrderedScenes(media.Id)); Assert.Equal(new[]{"People","City"},restoredScene.Tags); Assert.Equal(asset.Id,restoredScene.PreviewAssetId);
  var restoredBookmark=Assert.Single(restored.GetOrderedBookmarks(media.Id)); Assert.Equal("Original note",restoredBookmark.Description); Assert.Equal(asset.Id,restoredBookmark.PreviewAssetId);
  Assert.Null(restored.SetScenePreview(scene.Id,null).PreviewAssetId);
  restored.DeleteMediaAsset(asset.Id); Assert.Null(Assert.Single(restored.GetOrderedBookmarks(media.Id)).PreviewAssetId);
  var afterDeletion=restored.GetMedia(media.Id)!; Assert.Null(afterDeletion.ThumbnailPath); Assert.False(afterDeletion.Extra.ContainsKey("thumbnail.assetId")); Assert.Equal("value",afterDeletion.Extra["retained"]);
  var deletedBackup=Path.Combine(root,"deleted-preview.json"); restored.Export(deletedBackup);
  using var afterDelete=new CatalogRepository(Path.Combine(root,"after-delete.db")); Assert.Empty(afterDelete.ImportJson(deletedBackup).Errors);
 }
 [Fact]
 public void LegacyClipTagsUseType15RemoteClipIdWithoutLeakingOntoMedia()
 {
  object Schema(string table,params string[] columns)=>new { type="schema",table,columns=columns.Select(name=>new { name }) };
  object Row(string table,object values)=>new { type="row",table,values };
  object[] records=[
   new { type="header",format="flixypix-legacy",version=1,sourceSha256=new string('c',64) },
   Schema("movies","ID","fullpath","filename"),Row("movies",new { ID=1,fullpath=root,filename="video.mp4" }),
   Schema("videoclips","ID","videoid","starttime","endtime","description"),Row("videoclips",new { ID=5,videoid=1,starttime=3,endtime=7,description="Clip" }),
   Schema("keyword","ID","type","remoteID","keytags"),Row("keyword",new { ID=10,type=15,remoteID=5,keytags="\"Person\" \"Place\"" }),
   new { type="footer",tables=3,rows=3,tableRows=new Dictionary<string,int> { ["movies"]=1,["videoclips"]=1,["keyword"]=1 } }
  ];
  var path=Path.Combine(root,"legacy.jsonl"); File.WriteAllLines(path,records.Select(record=>JsonSerializer.Serialize(record)));
  using var repository=new CatalogRepository(Path.Combine(root,"catalog.db")); var result=repository.ImportLegacyJsonl(path); Assert.Empty(result.Errors);
  var media=Assert.Single(repository.Search()); Assert.Empty(media.Tags); Assert.Equal(new[]{"Person","Place"},Assert.Single(repository.GetOrderedScenes(media.Id)).Tags);
 }
}
