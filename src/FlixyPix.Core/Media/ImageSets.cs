using Microsoft.Data.Sqlite;
namespace FlixyPix.Core;
public sealed record ImageSet
{
 public Guid Id {get;init;}=Guid.NewGuid();
 public string Name {get;init;}="";
 public string? Description {get;init;}
 public List<Guid> MediaIds {get;init;}=[];
 public Guid? CoverId {get;init;}
}
public sealed partial class CatalogRepository
{
 private void InitializeImageSets(SqliteTransaction transaction)
 {
  using var command=Command("CREATE TABLE IF NOT EXISTS image_sets(id TEXT PRIMARY KEY,document TEXT NOT NULL); CREATE TABLE IF NOT EXISTS image_set_members(set_id TEXT NOT NULL REFERENCES image_sets(id) ON DELETE CASCADE,media_id TEXT NOT NULL REFERENCES media(id) ON DELETE CASCADE,position INTEGER NOT NULL,PRIMARY KEY(set_id,media_id),UNIQUE(set_id,position)); CREATE INDEX IF NOT EXISTS image_set_members_media ON image_set_members(media_id,set_id);",transaction);
  command.ExecuteNonQuery();
 }
 public IReadOnlyList<ImageSet> GetImageSets()
 {
  lock(gate){ThrowIfDisposed();return ReadDocuments<ImageSet>("image_sets").OrderBy(s=>s.Name,StringComparer.CurrentCultureIgnoreCase).ThenBy(s=>s.Id).ToArray();}
 }
 public ImageSet? GetImageSet(Guid id)
 {lock(gate){ThrowIfDisposed();return ReadEntity<ImageSet>("image_sets",id);}}
 public ImageSet AddImageSetMembers(Guid id,IEnumerable<Guid> mediaIds)
 {
  ArgumentNullException.ThrowIfNull(mediaIds);
  lock(gate)
  {
   ThrowIfDisposed();using var transaction=connection.BeginTransaction();
   var current=ReadEntity<ImageSet>("image_sets",id,transaction)??throw new InvalidOperationException("The image set no longer exists.");
   var saved=current with {MediaIds=current.MediaIds.Concat(mediaIds).Distinct().ToList()};
   ValidateImageSet(saved);ValidateImageSetMembers(saved,transaction);SaveImageSet(saved,transaction);transaction.Commit();return saved;
  }
 }
 public ImageSet UpsertImageSet(ImageSet set)
 {
  ArgumentNullException.ThrowIfNull(set);
  ValidateImageSet(set);
  lock(gate)
  {
   ThrowIfDisposed();using var transaction=connection.BeginTransaction();
   ValidateImageSetMembers(set,transaction);
   var saved=set with {Name=set.Name.Trim(),MediaIds=set.MediaIds.ToList()};SaveImageSet(saved,transaction);transaction.Commit();return saved;
  }
 }
 private static void ValidateImageSet(ImageSet set)
 {
  if(set.Id==Guid.Empty||string.IsNullOrWhiteSpace(set.Name)||set.Name.Length>256||set.MediaIds==null||set.MediaIds.Count>100000||set.MediaIds.Distinct().Count()!=set.MediaIds.Count||set.CoverId is {} cover&&!set.MediaIds.Contains(cover))throw new ArgumentException("Invalid image set.");
 }
 private void ValidateImageSetMembers(ImageSet set,SqliteTransaction transaction)
 {foreach(var id in set.MediaIds)if(ReadMediaDocument(id,transaction) is not {Kind:MediaKind.Image})throw new ArgumentException("Image sets require existing image media.");}
 private void SaveImageSet(ImageSet set,SqliteTransaction transaction)
 {
  SaveDocument("image_sets",set.Id,set,transaction);
  ReplaceImageSetMembers(set,transaction);
 }
 private void ReplaceImageSetMembers(ImageSet set,SqliteTransaction transaction)
 {
  using(var clear=Command("DELETE FROM image_set_members WHERE set_id=$id",transaction,("$id",set.Id.ToString())))clear.ExecuteNonQuery();
  for(var i=0;i<set.MediaIds.Count;i++)
  {using var member=Command("INSERT INTO image_set_members(set_id,media_id,position) VALUES($set,$media,$position)",transaction,("$set",set.Id.ToString()),("$media",set.MediaIds[i].ToString()),("$position",i));member.ExecuteNonQuery();}
 }
 public void DeleteImageSet(Guid id)
 {
  lock(gate)
  {
   ThrowIfDisposed();using var transaction=connection.BeginTransaction();
   RemovePublicationGroupMembers(PublicationGroupMemberKind.ImageSet,new HashSet<Guid>{id},transaction);
   using var command=Command("DELETE FROM image_sets WHERE id=$id",transaction,("$id",id.ToString()));command.ExecuteNonQuery();transaction.Commit();
  }
 }
 private void RemoveImageSetMedia(Guid id,SqliteTransaction transaction)
  =>RemoveImageSetMedia(new HashSet<Guid>{id},transaction);
 private void RemoveImageSetMedia(IReadOnlySet<Guid> gone,SqliteTransaction transaction)
 {
  foreach(var set in ReadDocuments<ImageSet>("image_sets",transaction))
   if(set.MediaIds.Any(gone.Contains))
    SaveImageSet(set with {MediaIds=set.MediaIds.Where(id=>!gone.Contains(id)).ToList(),CoverId=set.CoverId is {} cover&&gone.Contains(cover)?null:set.CoverId},transaction);
 }
 private void MoveImageSetMedia(IReadOnlySet<Guid> removed,Guid keep,SqliteTransaction transaction)
 {
  foreach(var set in ReadDocuments<ImageSet>("image_sets",transaction))
   if(set.MediaIds.Any(removed.Contains))
   {
    var moved=set with {MediaIds=set.MediaIds.Select(id=>removed.Contains(id)?keep:id).Distinct().ToList(),CoverId=set.CoverId is {} cover&&removed.Contains(cover)?keep:set.CoverId};
    ValidateImageSetMembers(moved,transaction);SaveImageSet(moved,transaction);
   }
 }
}
