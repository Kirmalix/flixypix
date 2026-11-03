using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

public sealed partial class CatalogRepository
{
 // Runs within the existing portable import transaction after media, assets and directory preferences.
 private void ImportPortableOrganization(SqliteTransaction transaction,CancellationToken token)
 {
  var identities=new HashSet<Guid>();
  foreach(var source in ReadStagedPortable<StashSourceProfile>("stashsources",transaction,token))
  {
   token.ThrowIfCancellationRequested();ValidateStashSource(source);
   if(!identities.Add(source.Id))throw new InvalidDataException("Duplicate portable Stash source identity.");
   var current=ReadEntity<StashSourceProfile>("stash_sources",source.Id,transaction);
   if(current!=null)
   {
    if(current.Name!=source.Name||current.SourceDirectory!=source.SourceDirectory||current.ExportVersion!=source.ExportVersion||!current.PathMappings.SequenceEqual(source.PathMappings))throw new InvalidDataException("The imported Stash source conflicts with the local source profile.");
    continue;
   }
   SaveDocument("stash_sources",source.Id,source with {Revision=1},transaction);
  }
  ImportPortableStashIdentities(transaction,token);
  ImportPortableStashProvenance(transaction,token);
  identities.Clear();
  foreach(var set in ReadStagedPortable<ImageSet>("imagesets",transaction,token))
  {
   token.ThrowIfCancellationRequested();
   if(set==null||!identities.Add(set.Id))throw new InvalidDataException("Null or duplicate portable image-set identity.");
   ValidateImageSet(set);
   var mapped=set with {Name=set.Name.Trim(),MediaIds=set.MediaIds.Select(id=>MapPortableId(id,transaction)).ToList(),CoverId=set.CoverId is {} cover?MapPortableId(cover,transaction):null};
   ValidateImageSet(mapped);ValidateImageSetMembers(mapped,transaction);SaveImageSet(mapped,transaction);
  }
  identities.Clear();
  foreach(var group in ReadStagedPortable<PublicationGroup>("publicationgroups",transaction,token))
  {
   token.ThrowIfCancellationRequested();
   if(group==null||!identities.Add(group.Id))throw new InvalidDataException("Null or duplicate portable publication-group identity.");
   ValidatePublicationGroup(group);
   // Set IDs occupy their own namespace, even when a set and a video have the same GUID.
   var mapped=group with {Name=group.Name.Trim(),Members=group.Members.Select(member=>member.Kind==PublicationGroupMemberKind.Video?member with {TargetId=MapPortableId(member.TargetId,transaction)}:member).ToList()};
   ValidatePublicationGroup(mapped);ValidatePublicationGroupTargets(mapped,transaction);SavePublicationGroup(mapped,transaction);
  }
  var count=0;
  foreach(var graph in ReadStagedPortable<TagHierarchy>("taghierarchies",transaction,token))
  {
   token.ThrowIfCancellationRequested();
   if(graph==null||++count>1)throw new InvalidDataException("Portable tag hierarchy must contain one graph.");
   ValidateHierarchy(graph,token);
   if(graph.Edges.Distinct(TagParentEdgeComparer.Instance).Count()!=graph.Edges.Count)throw new InvalidDataException("Duplicate portable tag-parent edge.");
   var before=ReadTagHierarchy(transaction);
   // Import adds relationships; absent source edges never delete unrelated destination relationships.
   var after=before with {Edges=NormalizeHierarchyEdges(before.Edges.Concat(graph.Edges))};
   ValidateHierarchy(after,token);SaveTagHierarchyIfChanged(before,after,transaction);
  }
  ImportPortableStashBindings(transaction,token);
 }
}
