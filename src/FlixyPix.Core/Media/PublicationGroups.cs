using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

public enum PublicationGroupMemberKind { Video, ImageSet }
public sealed record PublicationGroupMember(PublicationGroupMemberKind Kind,Guid TargetId);
public sealed record PublicationGroup
{
 public Guid Id {get;init;}=Guid.NewGuid();
 public string Name {get;init;}="";
 public string? Description {get;init;}
 public List<PublicationGroupMember> Members {get;init;}=[];
}

public sealed partial class CatalogRepository
{
 private void InitializePublicationGroups(SqliteTransaction transaction)
 {
  using var command=Command("""
   CREATE TABLE IF NOT EXISTS publication_groups(id TEXT PRIMARY KEY,document TEXT NOT NULL);
   CREATE TABLE IF NOT EXISTS publication_group_members(
    group_id TEXT NOT NULL REFERENCES publication_groups(id) ON DELETE CASCADE,
    position INTEGER NOT NULL,
    video_id TEXT REFERENCES media(id) ON DELETE CASCADE,
    image_set_id TEXT REFERENCES image_sets(id) ON DELETE CASCADE,
    PRIMARY KEY(group_id,position),UNIQUE(group_id,video_id),UNIQUE(group_id,image_set_id),
    CHECK((video_id IS NULL) != (image_set_id IS NULL)));
   CREATE INDEX IF NOT EXISTS publication_group_video ON publication_group_members(video_id,group_id);
   CREATE INDEX IF NOT EXISTS publication_group_image_set ON publication_group_members(image_set_id,group_id);
   """,transaction);command.ExecuteNonQuery();
 }
 public IReadOnlyList<PublicationGroup> GetPublicationGroups()
 {lock(gate){ThrowIfDisposed();return ReadDocuments<PublicationGroup>("publication_groups").OrderBy(g=>g.Name,StringComparer.CurrentCultureIgnoreCase).ThenBy(g=>g.Id).ToArray();}}
 public PublicationGroup? GetPublicationGroup(Guid id)
 {lock(gate){ThrowIfDisposed();return ReadEntity<PublicationGroup>("publication_groups",id);}}
 public PublicationGroup UpsertPublicationGroup(PublicationGroup group)
 {
  ArgumentNullException.ThrowIfNull(group);ValidatePublicationGroup(group);
  lock(gate)
  {
   ThrowIfDisposed();using var transaction=connection.BeginTransaction();ValidatePublicationGroupTargets(group,transaction);
   var saved=group with {Name=group.Name.Trim(),Members=group.Members.ToList()};SavePublicationGroup(saved,transaction);transaction.Commit();return saved;
  }
 }
 public PublicationGroup AddPublicationGroupMembers(Guid id,IEnumerable<PublicationGroupMember> members)
 {
  ArgumentNullException.ThrowIfNull(members);
  lock(gate)
  {
   ThrowIfDisposed();using var transaction=connection.BeginTransaction();
   var current=ReadEntity<PublicationGroup>("publication_groups",id,transaction)??throw new InvalidOperationException("The publication group no longer exists.");
   var saved=current with {Members=current.Members.Concat(members).Distinct().ToList()};
   ValidatePublicationGroup(saved);ValidatePublicationGroupTargets(saved,transaction);SavePublicationGroup(saved,transaction);transaction.Commit();return saved;
  }
 }
 public void DeletePublicationGroup(Guid id)
 {lock(gate){ThrowIfDisposed();using var command=Command("DELETE FROM publication_groups WHERE id=$id",null,("$id",id.ToString()));command.ExecuteNonQuery();}}
 private static void ValidatePublicationGroup(PublicationGroup group)
 {
  if(group.Id==Guid.Empty||string.IsNullOrWhiteSpace(group.Name)||group.Name.Length>256||group.Members==null||group.Members.Count>100000||
     group.Members.Any(m=>m==null||m.TargetId==Guid.Empty||!Enum.IsDefined(m.Kind))||group.Members.Distinct().Count()!=group.Members.Count)
   throw new ArgumentException("Invalid publication group.");
 }
 private void ValidatePublicationGroupTargets(PublicationGroup group,SqliteTransaction transaction)
 {
  foreach(var member in group.Members)
  {
   if(member.Kind==PublicationGroupMemberKind.Video)
   {if(ReadMediaDocument(member.TargetId,transaction) is not {Kind:MediaKind.Video})throw new ArgumentException("Publication groups require existing videos or image sets.");}
   else if(ReadEntity<ImageSet>("image_sets",member.TargetId,transaction)==null)throw new ArgumentException("The referenced image set no longer exists.");
  }
 }
 private void SavePublicationGroup(PublicationGroup group,SqliteTransaction transaction)
 {SaveDocument("publication_groups",group.Id,group,transaction);ReplacePublicationGroupMembers(group,transaction);}
 private void ReplacePublicationGroupMembers(PublicationGroup group,SqliteTransaction transaction)
 {
  using(var clear=Command("DELETE FROM publication_group_members WHERE group_id=$id",transaction,("$id",group.Id.ToString())))clear.ExecuteNonQuery();
  for(var i=0;i<group.Members.Count;i++)
  {
   var member=group.Members[i];
   using var add=Command("INSERT INTO publication_group_members(group_id,position,video_id,image_set_id) VALUES($group,$position,$video,$set)",transaction,
    ("$group",group.Id.ToString()),("$position",i),("$video",member.Kind==PublicationGroupMemberKind.Video?(object)member.TargetId.ToString():DBNull.Value),
    ("$set",member.Kind==PublicationGroupMemberKind.ImageSet?(object)member.TargetId.ToString():DBNull.Value));add.ExecuteNonQuery();
  }
 }
 private void RemovePublicationGroupMembers(PublicationGroupMemberKind kind,IReadOnlySet<Guid> removed,SqliteTransaction transaction)
 {
  foreach(var group in ReadDocuments<PublicationGroup>("publication_groups",transaction))
   if(group.Members.Any(m=>m.Kind==kind&&removed.Contains(m.TargetId)))
    SavePublicationGroup(group with {Members=group.Members.Where(m=>m.Kind!=kind||!removed.Contains(m.TargetId)).ToList()},transaction);
 }
 private void RedirectPublicationGroupVideos(IReadOnlySet<Guid> removed,Guid keep,SqliteTransaction transaction)
 {
  foreach(var group in ReadDocuments<PublicationGroup>("publication_groups",transaction))
   if(group.Members.Any(m=>m.Kind==PublicationGroupMemberKind.Video&&removed.Contains(m.TargetId)))
   {
    var updated=group with {Members=group.Members.Select(m=>m.Kind==PublicationGroupMemberKind.Video&&removed.Contains(m.TargetId)?m with {TargetId=keep}:m).Distinct().ToList()};
    ValidatePublicationGroupTargets(updated,transaction);SavePublicationGroup(updated,transaction);
   }
 }
}
