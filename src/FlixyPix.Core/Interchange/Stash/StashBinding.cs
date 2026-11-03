using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

public enum StashTargetKind { Video,Image,DirectoryEntry,ImageSet,PublicationGroup,ProvenanceOnly }
public sealed record StashTargetReference(StashTargetKind Kind,Guid? Id=null,TagKey? Tag=null);
public sealed record StashBinding
{
 /// <summary>The persistent source identity, not the target identity.</summary>
 public Guid Id {get;init;}
 public Guid ProvenanceId {get;init;}
 public long Revision {get;init;}
 public required StashTargetReference Target {get;init;}
 public bool Detached {get;init;}
 public List<StashFieldBaseline> Fields {get;init;}=[];
}
internal sealed record StashBindingChange(StashBinding Binding,long? ExpectedRevision,bool RetargetConfirmed=false);

public sealed partial class CatalogRepository
{
 private void InitializeStashBindings(SqliteTransaction transaction)
 {
  using(var command=Command("CREATE TABLE IF NOT EXISTS stash_bindings(id TEXT PRIMARY KEY REFERENCES stash_identities(id),document TEXT NOT NULL); CREATE INDEX IF NOT EXISTS stash_binding_target ON stash_bindings(json_extract(document,'$.target.kind'),json_extract(document,'$.target.id'))",transaction))command.ExecuteNonQuery();
  foreach(var(table,kinds)in new[]{("media","'Video','Image'"),("image_sets","'ImageSet'"),("publication_groups","'PublicationGroup'")})
  {
   using var command=Command($"CREATE TRIGGER IF NOT EXISTS stash_detach_{table} AFTER DELETE ON {table} BEGIN UPDATE stash_bindings SET document=json_set(document,'$.detached',json('true'),'$.revision',coalesce(json_extract(document,'$.revision'),0)+1) WHERE json_extract(document,'$.target.id')=OLD.id AND json_extract(document,'$.target.kind') IN ({kinds}) AND coalesce(json_extract(document,'$.detached'),0)=0; END",transaction);command.ExecuteNonQuery();
  }
 }
 private sealed class StashBindingSizeStream:Stream
 {
  private long length;
  public override bool CanRead=>false;public override bool CanSeek=>false;public override bool CanWrite=>true;public override long Length=>length;public override long Position{get=>length;set=>throw new NotSupportedException();}
  public override void Flush(){}
  public override void Write(byte[] buffer,int offset,int count)=>Write(buffer.AsSpan(offset,count));
  public override void Write(ReadOnlySpan<byte> buffer){if(buffer.Length>MaximumPortableEntityBytes-length)throw new ArgumentException("Stash field baselines exceed the portable entity limit.");length+=buffer.Length;}
  public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long value)=>throw new NotSupportedException();
 }
 private sealed class StashTargetLookup(CatalogRepository repository,SqliteTransaction transaction)
 {
  private CatalogPreferences? preferences;private HashSet<TagKey>? directory;
  public bool Exists(StashTargetReference target,StashEntityKind sourceKind)
  {
   if(target.Kind==StashTargetKind.ProvenanceOnly)return true;
   if(target.Kind is StashTargetKind.Video or StashTargetKind.Image)
   {using var command=repository.Command("SELECT json_extract(document,'$.kind') FROM media WHERE id=$id",transaction,("$id",target.Id!.Value.ToString()));var value=command.ExecuteScalar();var expected=target.Kind==StashTargetKind.Video?MediaKind.Video:MediaKind.Image;return value is string kind?kind==expected.ToString():value is long number&&number==(long)expected;}
   if(target.Kind is StashTargetKind.ImageSet or StashTargetKind.PublicationGroup)
   {using var command=repository.Command(target.Kind==StashTargetKind.ImageSet?"SELECT 1 FROM image_sets WHERE id=$id":"SELECT 1 FROM publication_groups WHERE id=$id",transaction,("$id",target.Id!.Value.ToString()));return command.ExecuteScalar()!=null;}
   preferences??=repository.ReadPreferences(transaction);
   if(directory==null)
   {
    directory=new(TagAssetTagComparer.Instance);foreach(var entry in preferences.TagVocabulary)directory.Add(new(entry.Category,entry.Tag));
    using var command=repository.Command("SELECT DISTINCT category,tag FROM media_tags",transaction);using var reader=command.ExecuteReader();while(reader.Read())directory.Add(new(reader.GetString(0),reader.GetString(1)));
    foreach(var edge in repository.ReadTagHierarchy(transaction).Edges){directory.Add(edge.Parent);directory.Add(edge.Child);}
   }
   var role=sourceKind==StashEntityKind.Performer?TagRole.Performer:sourceKind==StashEntityKind.Studio?TagRole.Studio:TagRole.Tag;
   return directory.Contains(target.Tag!)&&TagRoles.Of(preferences,target.Tag!.Category)==role;
  }
 }
 public StashBinding? GetStashBinding(Guid identityId)
 {
  lock(gate)
  {
   ThrowIfDisposed();using var transaction=connection.BeginTransaction();var binding=ReadEntity<StashBinding>("stash_bindings",identityId,transaction);if(binding==null)return null;
   var identity=ReadEntity<StashIdentity>("stash_identities",identityId,transaction)!;return ObserveStashBinding(binding,identity.Kind,new(this,transaction));
  }
 }
 public IReadOnlyList<StashBinding> GetStashBindings(Guid sourceId,int offset=0,int limit=100)
 {
  if(offset<0||limit is <1 or >256)throw new ArgumentOutOfRangeException(nameof(limit));lock(gate)
  {
   ThrowIfDisposed();using var transaction=connection.BeginTransaction();var lookup=new StashTargetLookup(this,transaction);using var command=Command("SELECT b.document,i.kind FROM stash_bindings b JOIN stash_identities i ON i.id=b.id WHERE i.source_id=$source ORDER BY b.id LIMIT $limit OFFSET $offset",transaction,("$source",sourceId.ToString()),("$limit",limit),("$offset",offset));using var reader=command.ExecuteReader();var result=new List<StashBinding>();while(reader.Read())result.Add(ObserveStashBinding(JsonSerializer.Deserialize<StashBinding>(reader.GetString(0),Json)!,(StashEntityKind)reader.GetInt32(1),lookup));return result;
  }
 }
 private static StashBinding ObserveStashBinding(StashBinding binding,StashEntityKind kind,StashTargetLookup lookup)
 =>binding.Detached||lookup.Exists(binding.Target,kind)?binding:binding with{Detached=true};
 // Target writes and snapshot checks are added by the selected apply transaction; preview cannot persist decisions.
 internal IReadOnlyList<StashBinding> StoreStashBindings(Guid sourceId,long expectedSourceRevision,IReadOnlyList<StashBindingChange> changes,CancellationToken token=default)
 {
  token.ThrowIfCancellationRequested();lock(gate)
  {
   ThrowIfDisposed();using var transaction=connection.BeginTransaction();var source=ReadEntity<StashSourceProfile>("stash_sources",sourceId,transaction)??throw new ArgumentException("Unknown Stash source.");if(source.Revision!=expectedSourceRevision)throw new InvalidOperationException("The Stash source changed after preview.");
   var result=new List<StashBinding>();var ids=new HashSet<Guid>();var lookup=new StashTargetLookup(this,transaction);
   foreach(var change in changes)
   {
    token.ThrowIfCancellationRequested();if(!ids.Add(change.Binding.Id))throw new ArgumentException("Duplicate Stash binding.");var identity=ReadEntity<StashIdentity>("stash_identities",change.Binding.Id,transaction)??throw new ArgumentException("Unknown Stash source identity.");if(identity.SourceId!=sourceId)throw new ArgumentException("The Stash binding belongs to a different source.");
    result.Add(SaveStashBinding(change.Binding,change.ExpectedRevision,change.RetargetConfirmed,transaction,lookup));
   }
   token.ThrowIfCancellationRequested();transaction.Commit();return result;
  }
 }
 private static void ValidateStashBinding(StashBinding binding)
 {
  if(binding==null||binding.Id==Guid.Empty||binding.ProvenanceId==Guid.Empty||binding.Revision<0||binding.Target==null||!Enum.IsDefined(binding.Target.Kind))throw new ArgumentException("Invalid Stash binding.");
  var target=binding.Target;
  if(target.Kind==StashTargetKind.ProvenanceOnly?target.Id!=null||target.Tag!=null:target.Kind==StashTargetKind.DirectoryEntry?target.Id!=null||target.Tag==null:target.Id==null||target.Id==Guid.Empty||target.Tag!=null)throw new ArgumentException("Invalid typed Stash target.");
  if(target.Tag is {} tag)NormalizeTagKey(tag);
  StashFieldConflicts.Validate(binding.Fields);
  // The same bound applies to the streaming portable reader; fail before saving an unexchangeable decision record.
  using var size=new StashBindingSizeStream();using var writer=new Utf8JsonWriter(size);JsonSerializer.Serialize(writer,binding,Json);writer.Flush();
 }
 private static bool SameStashBinding(StashBinding left,StashBinding right)
 =>left.ProvenanceId==right.ProvenanceId&&left.Target==right.Target&&left.Detached==right.Detached&&left.Fields.Count==right.Fields.Count&&left.Fields.All(field=>right.Fields.FirstOrDefault(other=>other.Field==field.Field)is {} other&&field.Decision==other.Decision&&StashFieldConflicts.Same(field.Source,other.Source)&&StashFieldConflicts.Same(field.Local,other.Local));
 private StashBinding SaveStashBinding(StashBinding binding,long? expectedRevision,bool retargetConfirmed,SqliteTransaction transaction,StashTargetLookup lookup)
 {
  ValidateStashBinding(binding);var identity=ReadEntity<StashIdentity>("stash_identities",binding.Id,transaction)??throw new ArgumentException("Unknown Stash source identity.");var provenance=ReadEntity<StashProvenance>("stash_provenance",binding.ProvenanceId,transaction);if(provenance?.IdentityId!=binding.Id)throw new ArgumentException("Stash provenance belongs to a different source identity.");
  if(binding.Target.Tag is {} tag)binding=binding with{Target=binding.Target with{Tag=NormalizeTagKey(tag)}};
  var kind=binding.Target.Kind;var permitted=kind==StashTargetKind.ProvenanceOnly||identity.Kind switch{StashEntityKind.Scene=>kind==StashTargetKind.Video,StashEntityKind.Image=>kind==StashTargetKind.Image,StashEntityKind.Gallery=>kind==StashTargetKind.ImageSet,StashEntityKind.Group=>kind==StashTargetKind.PublicationGroup,StashEntityKind.Tag or StashEntityKind.Performer or StashEntityKind.Studio=>kind==StashTargetKind.DirectoryEntry,_=>false};if(!permitted)throw new ArgumentException("The Stash source and target types do not match.");
  if(!binding.Detached&&!lookup.Exists(binding.Target,identity.Kind))throw new ArgumentException("The Stash target is missing or has a different role.");
  var current=ReadEntity<StashBinding>("stash_bindings",binding.Id,transaction);
  if(current==null?expectedRevision!=null:current.Revision!=expectedRevision)throw new InvalidOperationException("The Stash binding changed after preview.");
  if(current!=null&&(current.Target!=binding.Target||ObserveStashBinding(current,identity.Kind,lookup).Detached&&!binding.Detached)&&!retargetConfirmed)throw new InvalidOperationException("Confirm the changed or detached Stash target before rebinding.");
  if(current!=null&&SameStashBinding(current,binding))return current;
  var saved=binding with{Revision=checked((current?.Revision??0)+1),Fields=binding.Fields.Select(field=>field with{Source=field.Source.Clone(),Local=field.Local.Clone()}).OrderBy(field=>field.Field,StringComparer.Ordinal).ToList()};ValidateStashBinding(saved);SaveDocument("stash_bindings",saved.Id,saved,transaction);return saved;
 }
 private StashBinding MapPortableStashBinding(StashBinding binding,SqliteTransaction transaction)
 {
  var target=binding.Target.Tag is {} tag?binding.Target with{Tag=NormalizeTagKey(tag)}:binding.Target;var fields=binding.Fields;
  JsonElement MapValue(string field,JsonElement value)
  {
   if(target.Kind==StashTargetKind.ImageSet&&field=="mediaIds")return JsonSerializer.SerializeToElement((value.Deserialize<List<Guid>>(Json)??throw new InvalidDataException("Invalid Stash image membership baseline.")).Select(id=>MapPortableId(id,transaction)),Json);
   if(target.Kind==StashTargetKind.ImageSet&&field=="coverId")
   {if(value.ValueKind==JsonValueKind.Null)return JsonSerializer.SerializeToElement<Guid?>(null,Json);if(value.ValueKind!=JsonValueKind.String||!value.TryGetGuid(out var cover)||cover==Guid.Empty)throw new InvalidDataException("Invalid Stash cover baseline.");return JsonSerializer.SerializeToElement(MapPortableId(cover,transaction),Json);}
   if(target.Kind==StashTargetKind.PublicationGroup&&field=="members")return JsonSerializer.SerializeToElement((value.Deserialize<List<PublicationGroupMember>>(Json)??throw new InvalidDataException("Invalid Stash group membership baseline.")).Select(member=>member==null||!Enum.IsDefined(member.Kind)||member.TargetId==Guid.Empty?throw new InvalidDataException("Invalid Stash group member baseline."):member.Kind==PublicationGroupMemberKind.Video?member with{TargetId=MapPortableId(member.TargetId,transaction)}:member),Json);
   return value.Clone();
  }
  return binding with{Target=target.Kind is StashTargetKind.Video or StashTargetKind.Image?target with{Id=MapPortableId(target.Id!.Value,transaction)}:target,Fields=fields.Select(field=>field with{Source=MapValue(field.Field,field.Source),Local=MapValue(field.Field,field.Local)}).ToList()};
 }
 private void ImportPortableStashBindings(SqliteTransaction transaction,CancellationToken token)
 {
  var ids=new HashSet<Guid>();var lookup=new StashTargetLookup(this,transaction);foreach(var binding in ReadStagedPortable<StashBinding>("stashbindings",transaction,token))
  {
   token.ThrowIfCancellationRequested();ValidateStashBinding(binding);if(!ids.Add(binding.Id))throw new InvalidDataException("Duplicate portable Stash binding.");var mapped=MapPortableStashBinding(binding,transaction);var current=ReadEntity<StashBinding>("stash_bindings",binding.Id,transaction);
   if(current!=null){var identity=ReadEntity<StashIdentity>("stash_identities",binding.Id,transaction)!;if(!SameStashBinding(ObserveStashBinding(current,identity.Kind,lookup),mapped))throw new InvalidDataException("Imported Stash binding conflicts with local field decisions.");continue;}
   SaveStashBinding(mapped,null,false,transaction,lookup);
  }
 }
 private void WriteStashBindings(Utf8JsonWriter writer,SqliteTransaction transaction)
 {
  var lookup=new StashTargetLookup(this,transaction);writer.WriteStartArray("stashBindings");using var command=Command("SELECT b.document,i.kind FROM stash_bindings b JOIN stash_identities i ON i.id=b.id ORDER BY b.id",transaction);using var reader=command.ExecuteReader();while(reader.Read())JsonSerializer.Serialize(writer,ObserveStashBinding(JsonSerializer.Deserialize<StashBinding>(reader.GetString(0),Json)!,(StashEntityKind)reader.GetInt32(1),lookup),Json);writer.WriteEndArray();
 }
}
