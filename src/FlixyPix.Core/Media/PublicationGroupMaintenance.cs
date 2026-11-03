using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

public sealed partial class CatalogRepository
{
 private sealed record PublicationGroupAudit(IReadOnlyList<string> InvalidDocuments,IReadOnlyList<PublicationGroup> Rebuild,IReadOnlyList<string> OrphanGroups)
 {internal int MembershipProblems=>Rebuild.Count+OrphanGroups.Count;}
 private PublicationGroupAudit AuditPublicationGroups(SqliteTransaction transaction,CancellationToken token)
 {
  var documents=new List<(string Id,string Json)>();
  using(var command=Command("SELECT id,document FROM publication_groups ORDER BY id",transaction))
  using(var cancel=token.Register(command.Cancel))
  using(var reader=command.ExecuteReader())while(reader.Read())documents.Add((reader.GetString(0),reader.GetString(1)));
  var valid=new List<PublicationGroup>();var invalid=new List<string>();
  foreach(var row in documents)
  {
   token.ThrowIfCancellationRequested();
   try
   {
    var group=JsonSerializer.Deserialize<PublicationGroup>(row.Json,Json)??throw new InvalidDataException("Null publication group.");
    ValidatePublicationGroup(group);if(group.Id.ToString()!=row.Id)throw new InvalidDataException("Publication-group identity mismatch.");
    ValidatePublicationGroupTargets(group,transaction);valid.Add(group);
   }
   catch(Exception error)when(error is JsonException or ArgumentException or InvalidDataException or FormatException){invalid.Add(row.Id);}
  }
  var rows=new Dictionary<string,List<(long Position,string? Video,string? Set)>>(StringComparer.Ordinal);
  using(var command=Command("SELECT group_id,CASE WHEN typeof(position)='integer' THEN position ELSE -1 END,video_id,image_set_id FROM publication_group_members ORDER BY group_id,position",transaction))
  using(var cancel=token.Register(command.Cancel))
  using(var reader=command.ExecuteReader())while(reader.Read())
  {
   var id=reader.GetString(0);if(!rows.TryGetValue(id,out var members))rows[id]=members=[];
   members.Add((reader.GetInt64(1),reader.IsDBNull(2)?null:reader.GetString(2),reader.IsDBNull(3)?null:reader.GetString(3)));
  }
  var rebuild=new List<PublicationGroup>();
  foreach(var group in valid)
  {
   token.ThrowIfCancellationRequested();rows.TryGetValue(group.Id.ToString(),out var members);members??=[];
   if(members.Count!=group.Members.Count||members.Where((row,index)=>
     row.Position!=index||(group.Members[index].Kind==PublicationGroupMemberKind.Video?
      row.Video!=group.Members[index].TargetId.ToString()||row.Set!=null:
      row.Set!=group.Members[index].TargetId.ToString()||row.Video!=null)).Any())rebuild.Add(group);
  }
  var ids=documents.Select(d=>d.Id).ToHashSet(StringComparer.Ordinal);
  return new(invalid,rebuild,rows.Keys.Where(id=>!ids.Contains(id)).ToArray());
 }
 private int RepairPublicationGroupMembers(SqliteTransaction transaction,CancellationToken token)
 {
  var audit=AuditPublicationGroups(transaction,token);
  foreach(var id in audit.OrphanGroups)
  {token.ThrowIfCancellationRequested();using var remove=Command("DELETE FROM publication_group_members WHERE group_id=$id",transaction,("$id",id));remove.ExecuteNonQuery();}
  foreach(var group in audit.Rebuild){token.ThrowIfCancellationRequested();ReplacePublicationGroupMembers(group,transaction);}
  return audit.MembershipProblems;
 }
}
