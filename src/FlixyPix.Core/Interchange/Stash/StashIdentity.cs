using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

public enum StashIdentityKeyKind { Natural, MediaPath, ContentHash, ExportPath }
public sealed record StashIdentityKey(StashIdentityKeyKind Kind,string Value);
/// <summary>A persistent source identity, independent of mutable export filenames and of target IDs.</summary>
public sealed record StashIdentity
{
 public Guid Id {get;init;}=Guid.NewGuid();
 public Guid SourceId {get;init;}
 public StashEntityKind Kind {get;init;}
 public long Revision {get;init;}
 public List<StashIdentityKey> Keys {get;init;}=[];
}
public enum StashIdentityState { New, Matched, ConfirmationRequired, Ambiguous, Invalid }
public sealed record StashIdentityCandidate(Guid Id,long Revision,IReadOnlyList<StashIdentityKey> Evidence,bool EvidenceTruncated=false);
public sealed record StashIdentityProposal(long SourceOrdinal,long SourceRevision,StashIdentityState State,IReadOnlyList<StashIdentityCandidate> Candidates,bool Truncated=false);
internal sealed record StashIdentityChange(StashIdentity Identity,long? ExpectedRevision);

public static partial class StashExport
{
 public static IReadOnlyList<StashIdentityKey> IdentityKeys(StashSourceDocument document)
 {
  if(!document.Valid||document.Document is not {} root)return [];
  var keys=new List<StashIdentityKey>();
  if(document.Kind is StashEntityKind.Scene or StashEntityKind.Image)
  {
   if(root.TryGetProperty("files",out var files))foreach(var file in files.EnumerateArray())keys.Add(new(StashIdentityKeyKind.MediaPath,ReferencePath(file.GetString()!)));
  }
  else if(document.Kind is StashEntityKind.File or StashEntityKind.Folder)keys.Add(new(StashIdentityKeyKind.MediaPath,ReferencePath(root.GetProperty("path").GetString()!)));
  else if(document.Kind==StashEntityKind.Gallery)
  {if(GalleryKey(root)is {} gallery)keys.Add(new(StashIdentityKeyKind.Natural,gallery));}
  else if(document.NaturalKey is {} natural)keys.Add(new(StashIdentityKeyKind.Natural,natural));
  keys.Add(new(StashIdentityKeyKind.ContentHash,document.Sha256));keys.Add(new(StashIdentityKeyKind.ExportPath,document.RelativePath));
  return keys.Distinct().ToArray();
 }
}

public sealed partial class CatalogRepository
{
 private void InitializeStashIdentities(SqliteTransaction transaction)
 {
  using var command=Command("""
   CREATE TABLE IF NOT EXISTS stash_identities(id TEXT PRIMARY KEY,source_id TEXT NOT NULL REFERENCES stash_sources(id),kind INTEGER NOT NULL,document TEXT NOT NULL);
   CREATE INDEX IF NOT EXISTS stash_identity_source ON stash_identities(source_id,kind,id);
   CREATE TABLE IF NOT EXISTS stash_identity_keys(source_id TEXT NOT NULL,kind INTEGER NOT NULL,key_kind INTEGER NOT NULL,key TEXT NOT NULL,identity_id TEXT NOT NULL REFERENCES stash_identities(id) ON DELETE CASCADE,PRIMARY KEY(source_id,kind,key_kind,key,identity_id));
   CREATE INDEX IF NOT EXISTS stash_identity_key_owner ON stash_identity_keys(identity_id);
   """,transaction);command.ExecuteNonQuery();
 }
 public StashIdentity? GetStashIdentity(Guid id)
 {lock(gate){ThrowIfDisposed();return ReadEntity<StashIdentity>("stash_identities",id);}}
 public IReadOnlyList<StashIdentity> GetStashIdentities(Guid sourceId,int offset=0,int limit=100)
 {
  if(offset<0||limit is <1 or >256)throw new ArgumentOutOfRangeException(nameof(limit));
  lock(gate)
  {
   ThrowIfDisposed();using var command=Command("SELECT document FROM stash_identities WHERE source_id=$source ORDER BY kind,id LIMIT $limit OFFSET $offset",null,("$source",sourceId.ToString()),("$limit",limit),("$offset",offset));using var reader=command.ExecuteReader();var result=new List<StashIdentity>();while(reader.Read())result.Add(JsonSerializer.Deserialize<StashIdentity>(reader.GetString(0),Json)!);return result;
  }
 }
 public StashIdentityProposal ResolveStashIdentity(Guid sourceId,StashSourceDocument document,CancellationToken token=default)
 {
  token.ThrowIfCancellationRequested();
  lock(gate)
  {
   ThrowIfDisposed();using var transaction=connection.BeginTransaction();
   var source=ReadEntity<StashSourceProfile>("stash_sources",sourceId,transaction)??throw new ArgumentException("Unknown Stash source.");
   return ResolveStashIdentity(source,document,transaction,token);
  }
 }
 /// <summary>Resolution inside a caller's transaction, so the apply step sees the candidate set it writes against.</summary>
 private StashIdentityProposal ResolveStashIdentity(StashSourceProfile source,StashSourceDocument document,SqliteTransaction transaction,CancellationToken token)
 {
  var sourceId=source.Id;var keys=StashExport.IdentityKeys(document);
  {
   if(keys.Count==0)return new(document.Ordinal,source.Revision,StashIdentityState.Invalid,[]);
   var candidates=new Dictionary<Guid,(long Revision,List<StashIdentityKey> Evidence,bool Truncated)>();var truncated=false;
   foreach(var key in keys)
   {
    token.ThrowIfCancellationRequested();
    using var command=Command("SELECT i.id,json_extract(i.document,'$.revision') FROM stash_identity_keys k JOIN stash_identities i ON i.id=k.identity_id WHERE k.source_id=$source AND k.kind=$kind AND k.key_kind=$keyKind AND k.key=$key ORDER BY i.id LIMIT 257",transaction,("$source",sourceId.ToString()),("$kind",(int)document.Kind),("$keyKind",(int)key.Kind),("$key",key.Value));
    using var reader=command.ExecuteReader();while(reader.Read())
    {
     token.ThrowIfCancellationRequested();var id=Guid.Parse(reader.GetString(0));
     if(!candidates.TryGetValue(id,out var candidate))
     {if(candidates.Count==256){truncated=true;continue;}candidate=(reader.GetInt64(1),[],false);}
     // Keys arrive with natural/media evidence first, then the two weak fallback keys.
     // Preserve those fallback keys even for sources with thousands of file paths.
     if(candidate.Evidence.Count<64||key.Kind is StashIdentityKeyKind.ContentHash or StashIdentityKeyKind.ExportPath)candidate.Evidence.Add(key);else candidate.Truncated=true;
     candidates[id]=candidate;
    }
   }
   var result=candidates.OrderBy(pair=>pair.Key).Select(pair=>new StashIdentityCandidate(pair.Key,pair.Value.Revision,pair.Value.Evidence,pair.Value.Truncated)).ToArray();
   var state=result.Length==0?StashIdentityState.New:result.Length>1||truncated?StashIdentityState.Ambiguous:
    result[0].Evidence.Any(key=>key.Kind is StashIdentityKeyKind.Natural or StashIdentityKeyKind.MediaPath)||
    result[0].Evidence.Any(key=>key.Kind==StashIdentityKeyKind.ContentHash)&&result[0].Evidence.Any(key=>key.Kind==StashIdentityKeyKind.ExportPath)?StashIdentityState.Matched:StashIdentityState.ConfirmationRequired;
   return new(document.Ordinal,source.Revision,state,result,truncated);
  }
 }
 // Shared by the eventual target apply transaction. This entry point is internal so a preview cannot register itself as an import.
 internal IReadOnlyList<StashIdentity> StoreStashIdentities(Guid sourceId,long expectedSourceRevision,IReadOnlyList<StashIdentityChange> changes,CancellationToken token=default)
 {
  lock(gate)
  {
   ThrowIfDisposed();using var transaction=connection.BeginTransaction();var source=ReadEntity<StashSourceProfile>("stash_sources",sourceId,transaction)??throw new ArgumentException("Unknown Stash source.");
   if(source.Revision!=expectedSourceRevision)throw new InvalidOperationException("The Stash source changed after preview.");
   var result=new List<StashIdentity>();var ids=new HashSet<Guid>();
   foreach(var change in changes)
   {
    token.ThrowIfCancellationRequested();if(change.Identity.SourceId!=sourceId||!ids.Add(change.Identity.Id))throw new ArgumentException("Mixed source or duplicate Stash identity.");
    result.Add(SaveStashIdentity(change.Identity,change.ExpectedRevision,transaction));
   }
   token.ThrowIfCancellationRequested();transaction.Commit();return result;
  }
 }
 private StashIdentity SaveStashIdentity(StashIdentity identity,long? expectedRevision,SqliteTransaction transaction)
 {
  ValidateStashIdentity(identity);
  var current=ReadEntity<StashIdentity>("stash_identities",identity.Id,transaction);
  if(current==null?expectedRevision!=null:expectedRevision!=current.Revision)throw new InvalidOperationException("The Stash identity changed after preview.");
  if(current!=null&&(current.SourceId!=identity.SourceId||current.Kind!=identity.Kind))throw new ArgumentException("An existing Stash identity cannot change source or entity type.");
  if(ReadEntity<StashSourceProfile>("stash_sources",identity.SourceId,transaction)==null)throw new ArgumentException("Unknown Stash identity source.");
  var keys=(current?.Keys??[]).Concat(identity.Keys).Distinct().OrderBy(key=>key.Kind).ThenBy(key=>key.Value,StringComparer.Ordinal).ToList();
  if(current!=null&&current.Keys.ToHashSet().SetEquals(keys))return current;
  var saved=identity with{Keys=keys,Revision=checked((current?.Revision??0)+1)};ValidateStashIdentity(saved);
  using(var command=Command("INSERT INTO stash_identities(id,source_id,kind,document) VALUES($id,$source,$kind,$json) ON CONFLICT(id) DO UPDATE SET document=excluded.document",transaction,("$id",saved.Id.ToString()),("$source",saved.SourceId.ToString()),("$kind",(int)saved.Kind),("$json",JsonSerializer.Serialize(saved,Json))))command.ExecuteNonQuery();
  foreach(var key in saved.Keys)
  {using var command=Command("INSERT OR IGNORE INTO stash_identity_keys VALUES($source,$kind,$keyKind,$key,$id)",transaction,("$source",saved.SourceId.ToString()),("$kind",(int)saved.Kind),("$keyKind",(int)key.Kind),("$key",key.Value),("$id",saved.Id.ToString()));command.ExecuteNonQuery();}
  return saved;
 }
 private static void ValidateStashIdentity(StashIdentity identity)
 {
  if(identity==null||identity.Id==Guid.Empty||identity.SourceId==Guid.Empty||!Enum.IsDefined(identity.Kind)||identity.Revision<0||identity.Keys==null||identity.Keys.Count is <1 or >100000||identity.Keys.Any(key=>key==null||!Enum.IsDefined(key.Kind)||string.IsNullOrWhiteSpace(key.Value)||key.Value.Length>65536||key.Value.Any(char.IsControl))||identity.Keys.Distinct().Count()!=identity.Keys.Count)throw new ArgumentException("Invalid Stash source identity.");
  foreach(var key in identity.Keys)
   if(key.Kind==StashIdentityKeyKind.ContentHash&&(key.Value.Length!=64||key.Value.Any(character=>character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))))throw new ArgumentException("Invalid Stash source hash evidence.");
 }
 private void ImportPortableStashIdentities(SqliteTransaction transaction,CancellationToken token)
 {
  var ids=new HashSet<Guid>();foreach(var identity in ReadStagedPortable<StashIdentity>("stashidentities",transaction,token))
  {
   token.ThrowIfCancellationRequested();ValidateStashIdentity(identity);if(!ids.Add(identity.Id))throw new InvalidDataException("Duplicate portable Stash identity.");
   var current=ReadEntity<StashIdentity>("stash_identities",identity.Id,transaction);
   if(current!=null)
   {if(current.SourceId!=identity.SourceId||current.Kind!=identity.Kind||!current.Keys.ToHashSet().SetEquals(identity.Keys))throw new InvalidDataException("Imported Stash identity conflicts with local evidence.");continue;}
   SaveStashIdentity(identity,null,transaction);
  }
 }
}
