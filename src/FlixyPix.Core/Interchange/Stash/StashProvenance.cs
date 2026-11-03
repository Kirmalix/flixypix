using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

public sealed record StashProvenance
{
 public Guid Id {get;init;}
 public Guid IdentityId {get;init;}
 public string RelativePath {get;init;}="";
 public string Sha256 {get;init;}="";
 public int OriginalLength {get;init;}
 public DateTimeOffset RecordedAt {get;init;}
 public const int ChunkBytes=1024*1024,MaximumBytes=64*1024*1024;
}
public sealed record StashProvenanceChunk(Guid ProvenanceId,int Index,byte[] Data);
internal sealed record StashProvenanceCapture(Guid IdentityId,long ExpectedIdentityRevision,StashSourceDocument Document);

public sealed partial class CatalogRepository
{
 private void InitializeStashProvenance(SqliteTransaction transaction)
 {
  using var command=Command("""
   CREATE TABLE IF NOT EXISTS stash_provenance(id TEXT PRIMARY KEY,identity_id TEXT NOT NULL REFERENCES stash_identities(id),document TEXT NOT NULL);
   CREATE INDEX IF NOT EXISTS stash_provenance_owner ON stash_provenance(identity_id,id);
   CREATE TABLE IF NOT EXISTS stash_provenance_chunks(provenance_id TEXT NOT NULL REFERENCES stash_provenance(id) ON DELETE CASCADE,part INTEGER NOT NULL,data BLOB NOT NULL,PRIMARY KEY(provenance_id,part));
   """,transaction);command.ExecuteNonQuery();
 }
 private static Guid StashProvenanceId(Guid identity,string path,string sha)
 =>new(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString("N")+"\0"+path+"\0"+sha)).AsSpan(0,16));
 private static void ValidateStashProvenance(StashProvenance header)
 {
  if(header==null||header.Id==Guid.Empty||header.IdentityId==Guid.Empty||header.OriginalLength is <0 or >StashProvenance.MaximumBytes||header.RecordedAt==default||
   string.IsNullOrWhiteSpace(header.RelativePath)||header.RelativePath.Length>32768||header.RelativePath.Any(char.IsControl)||header.RelativePath.Contains('\\')||
   header.RelativePath.Split('/') is not [var section,var name]||!StashExport.Sections.ContainsKey(section)||name is "" or "." or ".."||!name.EndsWith(".json",StringComparison.OrdinalIgnoreCase)||
   header.Sha256==null||header.Sha256.Length!=64||header.Sha256.Any(character=>character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))||header.Id!=StashProvenanceId(header.IdentityId,header.RelativePath,header.Sha256))throw new ArgumentException("Invalid Stash raw provenance header.");
 }
 public StashProvenance? GetStashProvenance(Guid id)
 {lock(gate){ThrowIfDisposed();return ReadEntity<StashProvenance>("stash_provenance",id);}}
 public IReadOnlyList<StashProvenance> GetStashProvenanceHistory(Guid identityId,int offset=0,int limit=100)
 {
  if(offset<0||limit is <1 or >256)throw new ArgumentOutOfRangeException(nameof(limit));
  lock(gate)
  {
   ThrowIfDisposed();using var command=Command("SELECT document FROM stash_provenance WHERE identity_id=$identity ORDER BY json_extract(document,'$.recordedAt'),id LIMIT $limit OFFSET $offset",null,("$identity",identityId.ToString()),("$limit",limit),("$offset",offset));using var reader=command.ExecuteReader();var result=new List<StashProvenance>();while(reader.Read())result.Add(JsonSerializer.Deserialize<StashProvenance>(reader.GetString(0),Json)!);return result;
  }
 }
 public byte[] ReadStashProvenanceBytes(Guid id,CancellationToken token=default)
 {
  token.ThrowIfCancellationRequested();lock(gate)
  {
   ThrowIfDisposed();using var transaction=connection.BeginTransaction();var header=ReadEntity<StashProvenance>("stash_provenance",id,transaction)??throw new ArgumentException("Unknown Stash provenance.");
   ValidateStashProvenance(header);var bytes=new byte[header.OriginalLength];ProcessStashProvenance(header,transaction,(index,data)=>data.CopyTo(bytes,index*StashProvenance.ChunkBytes),token);return bytes;
  }
 }
 // Internal until the selection/apply transaction owns target fields, identity and provenance together.
 internal IReadOnlyList<StashProvenance> CaptureStashProvenance(Guid sourceId,long expectedSourceRevision,IReadOnlyList<StashProvenanceCapture> captures,CancellationToken token=default)
 {
  token.ThrowIfCancellationRequested();lock(gate)
  {
   ThrowIfDisposed();using var transaction=connection.BeginTransaction();var source=ReadEntity<StashSourceProfile>("stash_sources",sourceId,transaction)??throw new ArgumentException("Unknown Stash source.");if(source.Revision!=expectedSourceRevision)throw new InvalidOperationException("The Stash source changed after preview.");
   var result=new List<StashProvenance>();foreach(var capture in captures)
   {
    token.ThrowIfCancellationRequested();var identity=ReadEntity<StashIdentity>("stash_identities",capture.IdentityId,transaction)??throw new ArgumentException("Unknown Stash identity.");
    if(identity.SourceId!=sourceId||identity.Kind!=capture.Document.Kind)throw new ArgumentException("The raw record does not belong to this Stash source identity.");if(identity.Revision!=capture.ExpectedIdentityRevision)throw new InvalidOperationException("The Stash identity changed after preview.");
    result.Add(SaveStashProvenance(capture.IdentityId,capture.Document,transaction,token));
   }
   token.ThrowIfCancellationRequested();transaction.Commit();return result;
  }
 }
 private StashProvenance SaveStashProvenance(Guid identityId,StashSourceDocument document,SqliteTransaction transaction,CancellationToken token)
 {
  if(document.OriginalBytes==null||document.OriginalBytes.Length>StashProvenance.MaximumBytes)throw new ArgumentException("Stash raw record exceeds the provenance limit.");
  var bytes=document.OriginalBytes.ToArray();token.ThrowIfCancellationRequested();if(Convert.ToHexStringLower(SHA256.HashData(bytes))!=document.Sha256)throw new InvalidDataException("The Stash raw record no longer matches its source hash.");
  var header=new StashProvenance{Id=StashProvenanceId(identityId,document.RelativePath,document.Sha256),IdentityId=identityId,RelativePath=document.RelativePath,Sha256=document.Sha256,OriginalLength=bytes.Length,RecordedAt=DateTimeOffset.UtcNow};ValidateStashProvenance(header);
  var current=ReadEntity<StashProvenance>("stash_provenance",header.Id,transaction);
  if(current!=null){RequireSameStashProvenance(current,header);ProcessStashProvenance(current,transaction,null,token);return current;}
  SaveStashProvenanceHeader(header,transaction);
  for(var offset=0;offset<bytes.Length;offset+=StashProvenance.ChunkBytes)
  {token.ThrowIfCancellationRequested();var data=bytes.AsSpan(offset,Math.Min(StashProvenance.ChunkBytes,bytes.Length-offset)).ToArray();InsertStashProvenanceChunk(header.Id,offset/StashProvenance.ChunkBytes,data,transaction);}
  return header;
 }
 private void SaveStashProvenanceHeader(StashProvenance header,SqliteTransaction transaction)
 {
  ValidateStashProvenance(header);if(ReadEntity<StashIdentity>("stash_identities",header.IdentityId,transaction)==null)throw new ArgumentException("Unknown Stash provenance identity.");
  using var command=Command("INSERT INTO stash_provenance VALUES($id,$identity,$json)",transaction,("$id",header.Id.ToString()),("$identity",header.IdentityId.ToString()),("$json",JsonSerializer.Serialize(header,Json)));command.ExecuteNonQuery();
 }
 private void InsertStashProvenanceChunk(Guid id,int index,byte[] data,SqliteTransaction transaction)
 {using var command=Command("INSERT INTO stash_provenance_chunks VALUES($id,$index,$data)",transaction,("$id",id.ToString()),("$index",index),("$data",data));command.ExecuteNonQuery();}
 private static void RequireSameStashProvenance(StashProvenance current,StashProvenance incoming)
 {
  if(current.IdentityId!=incoming.IdentityId||current.RelativePath!=incoming.RelativePath||current.Sha256!=incoming.Sha256||current.OriginalLength!=incoming.OriginalLength)throw new InvalidDataException("Imported Stash raw provenance conflicts with the local record.");
 }
 private void ProcessStashProvenance(StashProvenance header,SqliteTransaction transaction,Action<int,byte[]>? consume,CancellationToken token)
 {
  ValidateStashProvenance(header);using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var index=0;var total=0;
  using var command=Command("SELECT part,data FROM stash_provenance_chunks WHERE provenance_id=$id ORDER BY part",transaction,("$id",header.Id.ToString()));using var reader=command.ExecuteReader();while(reader.Read())
  {
   token.ThrowIfCancellationRequested();if(reader.GetBytes(1,0,null,0,0) is <1 or >StashProvenance.ChunkBytes)throw new InvalidDataException("Invalid Stash raw provenance chunk size.");var data=(byte[])reader.GetValue(1);if(reader.GetInt32(0)!=index||data.Length!=Math.Min(StashProvenance.ChunkBytes,header.OriginalLength-total))throw new InvalidDataException("Incomplete or invalid Stash raw provenance chunks.");
   hash.AppendData(data);consume?.Invoke(index,data);total+=data.Length;index++;
  }
  if(total!=header.OriginalLength||Convert.ToHexStringLower(hash.GetHashAndReset())!=header.Sha256)throw new InvalidDataException("Stash raw provenance length or hash verification failed.");
 }
 private void WriteStashProvenanceChunks(Utf8JsonWriter writer,SqliteTransaction transaction)
 {
  writer.WriteStartArray("stashProvenanceChunks");using var command=Command("SELECT document FROM stash_provenance ORDER BY id",transaction);using var reader=command.ExecuteReader();while(reader.Read())
  {
   var header=JsonSerializer.Deserialize<StashProvenance>(reader.GetString(0),Json)!;
   ProcessStashProvenance(header,transaction,(index,data)=>JsonSerializer.Serialize(writer,new StashProvenanceChunk(header.Id,index,data),Json),CancellationToken.None);
  }
  writer.WriteEndArray();
 }
 private void ImportPortableStashProvenance(SqliteTransaction transaction,CancellationToken token)
 {
  using(var create=Command("CREATE TEMP TABLE stash_provenance_import(id TEXT NOT NULL,part INTEGER NOT NULL,data BLOB NOT NULL,PRIMARY KEY(id,part))",transaction))create.ExecuteNonQuery();
  var ids=new HashSet<Guid>();var added=new HashSet<Guid>();
  foreach(var header in ReadStagedPortable<StashProvenance>("stashprovenance",transaction,token))
  {
   token.ThrowIfCancellationRequested();ValidateStashProvenance(header);if(!ids.Add(header.Id))throw new InvalidDataException("Duplicate portable Stash raw provenance header.");
   var current=ReadEntity<StashProvenance>("stash_provenance",header.Id,transaction);if(current!=null)RequireSameStashProvenance(current,header);else{SaveStashProvenanceHeader(header,transaction);added.Add(header.Id);}
  }
  foreach(var chunk in ReadStagedPortable<StashProvenanceChunk>("stashprovenancechunks",transaction,token))
  {
   token.ThrowIfCancellationRequested();if(chunk==null||!ids.Contains(chunk.ProvenanceId)||chunk.Index is <0 or >=64||chunk.Data==null||chunk.Data.Length is <1 or >StashProvenance.ChunkBytes)throw new InvalidDataException("Invalid or orphan portable Stash provenance chunk.");
   using var insert=Command("INSERT INTO temp.stash_provenance_import VALUES($id,$index,$data)",transaction,("$id",chunk.ProvenanceId.ToString()),("$index",chunk.Index),("$data",chunk.Data));insert.ExecuteNonQuery();
  }
  foreach(var id in ids)
  {
   token.ThrowIfCancellationRequested();var header=ReadEntity<StashProvenance>("stash_provenance",id,transaction)!;using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var index=0;var total=0;
   using var command=Command("SELECT part,data FROM temp.stash_provenance_import WHERE id=$id ORDER BY part",transaction,("$id",id.ToString()));using var reader=command.ExecuteReader();while(reader.Read())
   {
    token.ThrowIfCancellationRequested();var data=(byte[])reader.GetValue(1);if(reader.GetInt32(0)!=index||data.Length!=Math.Min(StashProvenance.ChunkBytes,header.OriginalLength-total)||data.Length==0)throw new InvalidDataException("Incomplete portable Stash provenance chunks.");hash.AppendData(data);
    if(added.Contains(id))InsertStashProvenanceChunk(id,index,data,transaction);
    else{using var existing=Command("SELECT data FROM stash_provenance_chunks WHERE provenance_id=$id AND part=$index",transaction,("$id",id.ToString()),("$index",index));if(existing.ExecuteScalar()is not byte[] original||!original.AsSpan().SequenceEqual(data))throw new InvalidDataException("Imported Stash raw bytes conflict with local provenance.");}
    total+=data.Length;index++;
   }
   if(total!=header.OriginalLength||Convert.ToHexStringLower(hash.GetHashAndReset())!=header.Sha256)throw new InvalidDataException("Portable Stash raw provenance length or hash verification failed.");
   ProcessStashProvenance(header,transaction,null,token);
  }
  using var drop=Command("DROP TABLE temp.stash_provenance_import",transaction);drop.ExecuteNonQuery();
 }
}
