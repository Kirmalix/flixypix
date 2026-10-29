using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

public sealed partial class CatalogRepository
{
 public IReadOnlyList<Scene> GetOrderedScenes(Guid mediaId)
 {
  lock(gate) { ThrowIfDisposed(); return ReadMediaDocuments<Scene>("scenes",mediaId).OrderBy(s=>s.DisplayOrder).ThenBy(s=>s.StartSeconds).ThenBy(s=>s.Id).ToArray(); }
 }
 public IReadOnlyList<Bookmark> GetOrderedBookmarks(Guid mediaId)
 {
  lock(gate) { ThrowIfDisposed(); return ReadMediaDocuments<Bookmark>("bookmarks",mediaId).OrderBy(s=>s.DisplayOrder).ThenBy(s=>s.PositionSeconds).ThenBy(s=>s.Id).ToArray(); }
 }
 public void MoveScene(Guid id,int delta)
 {
  if(delta is not -1 and not 1)throw new ArgumentOutOfRangeException(nameof(delta));
  lock(gate)
  {
   ThrowIfDisposed(); using var tx=connection.BeginTransaction(); var scene=ReadEntity<Scene>("scenes",id,tx)??throw new ArgumentException("Unknown scene.");
   var ordered=ReadMediaDocuments<Scene>("scenes",scene.MediaId,tx).OrderBy(s=>s.DisplayOrder).ThenBy(s=>s.StartSeconds).ThenBy(s=>s.Id).ToList();
   var from=ordered.FindIndex(s=>s.Id==id); var to=Math.Clamp(from+delta,0,ordered.Count-1); (ordered[from],ordered[to])=(ordered[to],ordered[from]);
   for(var i=0;i<ordered.Count;i++)SaveScene(ordered[i] with { DisplayOrder=i },tx); tx.Commit();
  }
 }
 public void MoveBookmark(Guid id,int delta)
 {
  if(delta is not -1 and not 1)throw new ArgumentOutOfRangeException(nameof(delta));
  lock(gate)
  {
   ThrowIfDisposed(); using var tx=connection.BeginTransaction(); var bookmark=ReadEntity<Bookmark>("bookmarks",id,tx)??throw new ArgumentException("Unknown bookmark.");
   var ordered=ReadMediaDocuments<Bookmark>("bookmarks",bookmark.MediaId,tx).OrderBy(s=>s.DisplayOrder).ThenBy(s=>s.PositionSeconds).ThenBy(s=>s.Id).ToList();
   var from=ordered.FindIndex(s=>s.Id==id); var to=Math.Clamp(from+delta,0,ordered.Count-1); (ordered[from],ordered[to])=(ordered[to],ordered[from]);
   for(var i=0;i<ordered.Count;i++)SaveBookmark(ordered[i] with { DisplayOrder=i },tx); tx.Commit();
  }
 }
 public Scene SetScenePreview(Guid sceneId,Guid? assetId)
 {
  lock(gate)
  {
   ThrowIfDisposed(); using var tx=connection.BeginTransaction(); var scene=ReadEntity<Scene>("scenes",sceneId,tx)??throw new ArgumentException("Unknown scene.");
   EnsurePreviewOwnership(scene.MediaId,assetId,tx); var updated=scene with { PreviewAssetId=assetId }; SaveScene(updated,tx); tx.Commit(); return updated;
  }
 }
 public Bookmark SetBookmarkPreview(Guid bookmarkId,Guid? assetId)
 {
  lock(gate)
  {
   ThrowIfDisposed(); using var tx=connection.BeginTransaction(); var bookmark=ReadEntity<Bookmark>("bookmarks",bookmarkId,tx)??throw new ArgumentException("Unknown bookmark.");
   EnsurePreviewOwnership(bookmark.MediaId,assetId,tx); var updated=bookmark with { PreviewAssetId=assetId }; SaveBookmark(updated,tx); tx.Commit(); return updated;
  }
 }
 public Scene SetSceneTags(Guid sceneId,IEnumerable<string> tags)
 {
  var normalized=NormalizeSceneTags(tags);
  lock(gate)
  {
   ThrowIfDisposed(); using var tx=connection.BeginTransaction(); var scene=ReadEntity<Scene>("scenes",sceneId,tx)??throw new ArgumentException("Unknown scene.");
   var updated=scene with { Tags=normalized }; SaveScene(updated,tx); tx.Commit(); return updated;
  }
 }
 public static List<string> NormalizeSceneTags(IEnumerable<string> tags)
 {
  ArgumentNullException.ThrowIfNull(tags); var result=new List<string>(); var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  foreach(var tag in tags)
  { if(tag==null || tag.Length>256)throw new ArgumentException("Scene tags must be non-null strings through 256 characters."); var value=tag.Trim(); if(value.Length>0 && seen.Add(value))result.Add(value); if(result.Count>256)throw new ArgumentException("At most 256 scene tags are supported."); }
  return result;
 }
 private void EnsurePreviewOwnership(Guid mediaId,Guid? assetId,SqliteTransaction tx)
 {
  if(assetId==null)return;
  using var command=Command("SELECT media_id FROM media_assets WHERE id=$id",tx,("$id",assetId.Value.ToString()));
  if(!Equals(command.ExecuteScalar(),mediaId.ToString()))throw new ArgumentException("The preview asset is missing or belongs to another media item.");
 }
 private void DeleteMediaAssetCore(Guid id,SqliteTransaction tx)
 {
  using(var command=Command("SELECT document FROM media WHERE json_extract(document,'$.extra.\"thumbnail.assetId\"')=$id",tx,("$id",id.ToString())))
  {
   var documents=new List<string>(); using(var reader=command.ExecuteReader())while(reader.Read())documents.Add(reader.GetString(0));
   foreach(var document in documents)
   {
    var media=JsonSerializer.Deserialize<MediaItem>(document,Json)!;
    var extra=new Dictionary<string,string?>(media.Extra); extra.Remove("thumbnail.assetId");
    UpsertCore(media with { ThumbnailPath=null,Extra=extra },tx);
   }
  }
  foreach(var table in new[]{"scenes","bookmarks"})
  {
   using var command=Command($"SELECT document FROM {table} WHERE json_extract(document,'$.previewAssetId')=$id",tx,("$id",id.ToString()));
   var documents=new List<string>(); using(var reader=command.ExecuteReader())while(reader.Read())documents.Add(reader.GetString(0));
   foreach(var document in documents)
   {
    if(table=="scenes")SaveScene(JsonSerializer.Deserialize<Scene>(document,Json)! with { PreviewAssetId=null },tx);
    else SaveBookmark(JsonSerializer.Deserialize<Bookmark>(document,Json)! with { PreviewAssetId=null },tx);
   }
  }
  using var delete=Command("DELETE FROM media_assets WHERE id=$id",tx,("$id",id.ToString())); delete.ExecuteNonQuery();
 }
 private T? ReadEntity<T>(string table,Guid id,SqliteTransaction? tx=null)
 {
  if(table is not "scenes" and not "bookmarks" and not "playlists" and not "collection_sets")throw new ArgumentException("Unsupported document table.");
  using var command=Command($"SELECT document FROM {table} WHERE id=$id",tx,("$id",id.ToString()));
  return command.ExecuteScalar() is string document?JsonSerializer.Deserialize<T>(document,Json):default;
 }
 private List<T> ReadMediaDocuments<T>(string table,Guid mediaId,SqliteTransaction? tx=null)
 {
  if(table is not "scenes" and not "bookmarks")throw new ArgumentException("Unsupported document table.");
  using var command=Command($"SELECT document FROM {table} WHERE media_id=$media",tx,("$media",mediaId.ToString())); using var reader=command.ExecuteReader(); var result=new List<T>();
  while(reader.Read())result.Add(JsonSerializer.Deserialize<T>(reader.GetString(0),Json)??throw new InvalidDataException("Invalid media document.")); return result;
 }
}
