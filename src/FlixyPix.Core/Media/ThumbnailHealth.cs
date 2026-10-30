namespace FlixyPix.Core;

public sealed partial class CatalogRepository
{
 public (string Signature, bool Available)? GetThumbnailHealth(Guid id)
 {
  lock(gate) { using var command=Command("SELECT signature,available FROM media_thumbnail_state WHERE media_id=$id",null,("$id",id.ToString())); using var reader=command.ExecuteReader(); return reader.Read() ? (reader.GetString(0),reader.GetInt32(1)!=0) : null; }
 }
 public void SaveThumbnailHealth(Guid id,string signature,bool available)
 {
  lock(gate) { using var command=Command("INSERT INTO media_thumbnail_state(media_id,signature,available) SELECT $id,$stamp,$value WHERE EXISTS(SELECT 1 FROM media WHERE id=$id) ON CONFLICT(media_id) DO UPDATE SET signature=$stamp,available=$value",null,("$id",id.ToString()),("$stamp",signature),("$value",available?1:0));command.ExecuteNonQuery(); }
 }
}
