using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

public sealed partial class CatalogRepository
{
 private sealed record ImageSetAudit(IReadOnlyList<string> InvalidDocuments,IReadOnlyList<ImageSet> Rebuild,IReadOnlyList<string> OrphanSets)
 {
  internal int MembershipProblems=>Rebuild.Count+OrphanSets.Count;
 }
 private ImageSetAudit AuditImageSets(SqliteTransaction transaction,CancellationToken token)
 {
  var documents=new List<(string Id,string Json)>();
  using(var command=Command("SELECT id,document FROM image_sets ORDER BY id",transaction))
  using(var cancel=token.Register(command.Cancel))
  using(var reader=command.ExecuteReader())while(reader.Read())documents.Add((reader.GetString(0),reader.GetString(1)));
  var valid=new List<ImageSet>();var invalid=new List<string>();
  foreach(var row in documents)
  {
   token.ThrowIfCancellationRequested();
   try
   {
    var set=JsonSerializer.Deserialize<ImageSet>(row.Json,Json)??throw new InvalidDataException("Null image set.");
    ValidateImageSet(set);
    if(set.Id.ToString()!=row.Id)throw new InvalidDataException("Image-set identity mismatch.");
    ValidateImageSetMembers(set,transaction);valid.Add(set);
   }
   catch(Exception error)when(error is JsonException or ArgumentException or InvalidDataException or FormatException)
   {invalid.Add(row.Id);}
  }
  var rows=new Dictionary<string,List<(string Media,long Position)>>(StringComparer.Ordinal);
  using(var command=Command("SELECT set_id,media_id,CASE WHEN typeof(position)='integer' THEN position ELSE -1 END FROM image_set_members ORDER BY set_id,position",transaction))
  using(var cancel=token.Register(command.Cancel))
  using(var reader=command.ExecuteReader())while(reader.Read())
  {var id=reader.GetString(0);if(!rows.TryGetValue(id,out var members))rows[id]=members=[];members.Add((reader.GetString(1),reader.GetInt64(2)));}
  var rebuild=new List<ImageSet>();
  foreach(var set in valid)
  {
   token.ThrowIfCancellationRequested();rows.TryGetValue(set.Id.ToString(),out var members);members??=[];
   if(members.Count!=set.MediaIds.Count||members.Where((row,index)=>row.Position!=index||row.Media!=set.MediaIds[index].ToString()).Any())rebuild.Add(set);
  }
  var ids=documents.Select(d=>d.Id).ToHashSet(StringComparer.Ordinal);
  return new(invalid,rebuild,rows.Keys.Where(id=>!ids.Contains(id)).ToArray());
 }
 private int RepairImageSetMembers(SqliteTransaction transaction,CancellationToken token)
 {
  // Re-read documents inside the write transaction: a report may predate newer edits.
  var audit=AuditImageSets(transaction,token);
  foreach(var id in audit.OrphanSets)
  {token.ThrowIfCancellationRequested();using var remove=Command("DELETE FROM image_set_members WHERE set_id=$id",transaction,("$id",id));remove.ExecuteNonQuery();}
  foreach(var set in audit.Rebuild){token.ThrowIfCancellationRequested();ReplaceImageSetMembers(set,transaction);}
  return audit.MembershipProblems;
 }
}
