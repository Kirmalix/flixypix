using System.Collections;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

public enum LegacyConflictPolicy { PreserveExisting, ReplaceFromSource }
public sealed record LegacyImportOptions
{
 public string? SourceId { get; init; }
 public LegacyConflictPolicy ConflictPolicy { get; init; } = LegacyConflictPolicy.PreserveExisting;
 public int MaximumRecordCharacters { get; init; } = 64 * 1024 * 1024;
 /// <summary>False leaves the original's program settings raw, so the takeover assistant can offer them by group.</summary>
 public bool AdoptAppSettings { get; init; } = true;
 /// <summary>What the original writes on its first start; raw rows equal to it are reported as defaults.</summary>
 public OriginalDefaults OriginalDefaults { get; init; } = OriginalDefaults.Builtin;
}
/// <summary>Rows of one original table; <paramref name="OriginalDefaults"/> counts raw rows that are first-run defaults of the original.</summary>
public sealed record MigrationTableSummary(string Table,long Rows,long PartiallyProjected,long RawRetained,long Invalid,long OriginalDefaults=0);
public sealed record MigrationReport(string Batch,string SourceId,string SnapshotSha256,string ExportSha256,long Rows,long Blobs,long BlobBytes,
 long Conflicts,IReadOnlyList<MigrationTableSummary> Tables,bool AlreadyImported=false,bool IntegrityVerified=false);
public sealed record MigrationRow(long Ordinal,string Table,string? Key,string Status,string Code,IReadOnlyList<string> TargetIds);
public sealed record MigrationConflict(string Kind,string TargetId,IReadOnlyList<string> ChangedFields,string Resolution,
 IReadOnlyDictionary<string,JsonElement>? ExistingValues=null,IReadOnlyDictionary<string,JsonElement>? IncomingValues=null);
public sealed record MigrationConflictPreview(MigrationConflict Conflict,string CurrentSha256,IReadOnlyDictionary<string,JsonElement> CurrentValues);
public sealed record LegacySource(string SourceId,string SnapshotSha256);

public sealed partial class CatalogRepository
{
 private LegacySession? legacySession;
 private sealed record LegacySession(string SourceId,string Snapshot,string Batch,LegacyConflictPolicy Policy,CancellationToken Token,bool AdoptAppSettings=true);
 private void InitializeMigrationSchema(SqliteTransaction tx)
 {
  // AuditLegacy matches table_name COLLATE NOCASE once per row. The former BINARY index could only use
  // its batch prefix, so every audit scanned the whole batch; existing catalogs get the replacement here.
  using var command=Command("""
   CREATE TABLE IF NOT EXISTS legacy_sources(source_id TEXT PRIMARY KEY,snapshot_sha256 TEXT NOT NULL);
   CREATE TABLE IF NOT EXISTS legacy_imports(batch TEXT PRIMARY KEY,source_id TEXT NOT NULL,snapshot_sha256 TEXT NOT NULL,report TEXT NOT NULL);
   CREATE TABLE IF NOT EXISTS legacy_mappings(source_id TEXT NOT NULL,legacy_key TEXT NOT NULL,target_id TEXT NOT NULL,PRIMARY KEY(source_id,legacy_key));
   CREATE TABLE IF NOT EXISTS migration_rows(batch TEXT NOT NULL,ordinal INTEGER NOT NULL,table_name TEXT NOT NULL,legacy_key TEXT,status TEXT NOT NULL,code TEXT NOT NULL,target_ids TEXT NOT NULL,PRIMARY KEY(batch,ordinal));
   CREATE INDEX IF NOT EXISTS migration_rows_lookup ON migration_rows(batch,table_name COLLATE NOCASE,legacy_key);
   DROP INDEX IF EXISTS migration_rows_key;
   CREATE TABLE IF NOT EXISTS migration_conflicts(batch TEXT NOT NULL,kind TEXT NOT NULL,target_id TEXT NOT NULL,document TEXT NOT NULL,PRIMARY KEY(batch,kind,target_id));
   CREATE TABLE IF NOT EXISTS legacy_blobs(sha256 TEXT NOT NULL UNIQUE,length INTEGER NOT NULL);
   """,tx);command.ExecuteNonQuery();
 }
 private Guid StableLegacyId(string snapshot,string suffix)=>StableId((legacySession?.SourceId??snapshot)+suffix);
 private void BeginLegacyProjection(string source,string batch,LegacyImportOptions options,SqliteTransaction tx,CancellationToken token)
 {
  var identity=options.SourceId??source;
  if(string.IsNullOrWhiteSpace(identity)||identity.Length>256)throw new ArgumentException("A legacy source identity must contain 1 through 256 characters.");
  legacySession=new(identity,source,batch,options.ConflictPolicy,token,options.AdoptAppSettings);
  using(var create=Command("CREATE TEMP TABLE legacy_protected_entities(kind TEXT NOT NULL,id TEXT NOT NULL,PRIMARY KEY(kind,id)); CREATE TEMP TABLE legacy_media_map(legacy_key TEXT PRIMARY KEY,target_id TEXT NOT NULL);",tx))create.ExecuteNonQuery();
  foreach(var table in
   new[]{"media","scenes","bookmarks","playlists","preferences","media_assets","saved_searches","filename_rules","tag_presets","legacy_search_drafts","legacy_job_drafts","legacy_parser_drafts","tag_assets","historical_content_folders","historical_parser_definitions","legacy_combine_lists"})
  {using var protect=Command($"INSERT INTO temp.legacy_protected_entities SELECT $kind,id FROM {table}",tx,("$kind",table));protect.ExecuteNonQuery();}
  using(var save=Command("INSERT INTO legacy_sources VALUES($id,$snapshot) ON CONFLICT(source_id) DO UPDATE SET snapshot_sha256=excluded.snapshot_sha256",tx,("$id",identity),("$snapshot",source)))save.ExecuteNonQuery();
 }
 private bool KeepLegacyExisting(string kind,string id,string proposed,SqliteTransaction tx)
 {
  var session=legacySession;if(session==null)return false;session.Token.ThrowIfCancellationRequested();
  using(var check=Command("SELECT 1 FROM temp.legacy_protected_entities WHERE kind=$kind AND id=$id",tx,("$kind",kind),("$id",id)))if(check.ExecuteScalar()==null)return false;
  using var read=Command($"SELECT document FROM {kind} WHERE id=$id",tx,("$id",id));
  if(read.ExecuteScalar() is not string existing)return false;
  using var before=JsonDocument.Parse(existing);using var after=JsonDocument.Parse(proposed);
  // Media documents leave out null properties: a property only the stored document has, with a value, changed to null.
  var changed=after.RootElement.EnumerateObject().Where(p=>!(kind=="media"&&p.Name=="extra")&&(!before.RootElement.TryGetProperty(p.Name,out var old)||!JsonElement.DeepEquals(old,p.Value))).Select(p=>p.Name)
   .Concat(before.RootElement.EnumerateObject().Where(p=>!(kind=="media"&&p.Name=="extra")&&p.Value.ValueKind!=JsonValueKind.Null&&!after.RootElement.TryGetProperty(p.Name,out _)).Select(p=>p.Name)).ToArray();
  if(changed.Length>0)
  {
   MigrationConflict? prior=null;using(var
    previous=Command("SELECT document FROM migration_conflicts WHERE batch=$batch AND kind=$kind AND target_id=$id",tx,("$batch",session.Batch),("$kind",kind),("$id",id)))if(previous.ExecuteScalar()
    is string text)prior=JsonSerializer.Deserialize<MigrationConflict>(text,Json);
   var oldValues=prior?.ExistingValues?.ToDictionary(p=>p.Key,p=>p.Value.Clone())??new Dictionary<string,JsonElement>();
   var incoming=prior?.IncomingValues?.ToDictionary(p=>p.Key,p=>p.Value.Clone())??new Dictionary<string,JsonElement>();
   foreach(var field in changed){if(before.RootElement.TryGetProperty(field,out var value))oldValues[field]=value.Clone();incoming[field]=after.RootElement.TryGetProperty(field,out var next)?next.Clone():JsonSerializer.SerializeToElement<object?>(null);}
   var conflict=new MigrationConflict(kind,id,(prior?.ChangedFields??[]).Concat(changed).Distinct().ToArray(),session.Policy==LegacyConflictPolicy.PreserveExisting?"preserved-existing":"replaced-explicitly",oldValues,incoming);
   using var write=Command("INSERT INTO migration_conflicts VALUES($batch,$kind,$id,$document) ON CONFLICT(batch,kind,target_id) DO UPDATE SET document=excluded.document",tx,
    ("$batch",session.Batch),("$kind",kind),("$id",id),("$document",JsonSerializer.Serialize(conflict,Json)));write.ExecuteNonQuery();
  }
  return session.Policy==LegacyConflictPolicy.PreserveExisting;
 }
 /// <summary>The key a row has in the migration report: its ID, or for program settings (which have none) the parameter name;
 /// must match the staging insert in ImportLegacyJsonl.</summary>
 private static string? LegacyRowKey(string table,JsonElement row)=>Text(row,"ID")??Text(row,"id")??(table.Equals("configsettings",StringComparison.OrdinalIgnoreCase)?Text(row,"parameter"):null);
 private void AuditLegacy(string table,string? key,string status,string code,SqliteTransaction tx,params Guid[] targets)
 {
  if(legacySession==null)return;
  if(status=="raw-only")status="raw-retained";
  if(status is not ("raw-retained" or "partially-projected" or "invalid"))throw new ArgumentException("Unknown migration audit status.");
  using var command=Command("UPDATE migration_rows SET status=$status,code=$code,target_ids=$targets WHERE batch=$batch AND table_name=$table COLLATE NOCASE AND legacy_key=$key",tx,
   ("$status",status),("$code",code),("$targets",JsonSerializer.Serialize(targets.Select(t=>t.ToString()),Json)),("$batch",legacySession.Batch),("$table",table),("$key",(object?)key??DBNull.Value));command.ExecuteNonQuery();
 }
 private MigrationReport FinishLegacyReport(string source,string batch,string exportHash,SqliteTransaction tx)
 {
  var integrity=CheckIntegrityCore(tx,legacySession!.Token);
  if(!integrity.IsValid)throw new InvalidDataException("The migrated catalog failed integrity checks: "+string.Join("; ",integrity.Issues.Take(10)));
  var tables=new List<MigrationTableSummary>();
  using(var command=Command("SELECT table_name,count(*),sum(status='partially-projected'),sum(status='raw-retained'),sum(status='invalid'),sum(code='original-default') FROM migration_rows WHERE batch=$batch GROUP BY table_name ORDER BY table_name",tx,("$batch",batch)))
  using(var reader=command.ExecuteReader())while(reader.Read())tables.Add(new(reader.GetString(0),reader.GetInt64(1),reader.GetInt64(2),reader.GetInt64(3),reader.GetInt64(4),reader.GetInt64(5)));
  long blobs,bytes,conflicts;
  using(var count=Command("SELECT count(*),coalesce(sum(length),0) FROM temp.legacy_blob_refs",tx))using(var reader=count.ExecuteReader()){reader.Read();blobs=reader.GetInt64(0);bytes=reader.GetInt64(1);}
  using(var count=Command("SELECT count(*) FROM migration_conflicts WHERE batch=$batch",tx,("$batch",batch)))conflicts=Convert.ToInt64(count.ExecuteScalar());
  var report=new MigrationReport(batch,legacySession!.SourceId,source,exportHash,tables.Sum(t=>t.Rows),blobs,bytes,conflicts,tables,IntegrityVerified:true);
  using(var save=Command("INSERT INTO legacy_imports VALUES($batch,$source,$snapshot,$report)",tx,("$batch",batch),("$source",report.SourceId),("$snapshot",source),("$report",JsonSerializer.Serialize(report,Json))))save.ExecuteNonQuery();
  return report;
 }
 public IReadOnlyList<LegacySource> GetLegacySources()
 {
  lock(gate){ThrowIfDisposed();using var command=Command("SELECT source_id,snapshot_sha256 FROM legacy_sources ORDER BY source_id",null);using var
   reader=command.ExecuteReader();var result=new
   List<LegacySource>();while(reader.Read())result.Add(new(reader.GetString(0),reader.GetString(1)));return result;}
 }
 public IReadOnlyList<MigrationReport> GetMigrationReports()
 {
  lock(gate){ThrowIfDisposed();using var command=Command("SELECT report FROM legacy_imports ORDER BY rowid",null);using var
   reader=command.ExecuteReader();var result=new
   List<MigrationReport>();while(reader.Read())result.Add(JsonSerializer.Deserialize<MigrationReport>(reader.GetString(0),Json)!);return result;}
 }
 public IReadOnlyList<MigrationRow> GetMigrationRows(string batch,int offset=0,int limit=120)
 {
  if(offset<0||limit is <1 or >1000)throw new ArgumentOutOfRangeException(nameof(limit));
  lock(gate){ThrowIfDisposed();using var
   command=Command("SELECT ordinal,table_name,legacy_key,status,code,target_ids FROM migration_rows WHERE batch=$batch ORDER BY ordinal LIMIT $limit OFFSET $offset",null,("$batch",batch),("$limit",limit),("$offset",offset));using
   var reader=command.ExecuteReader();var rows=new
   List<MigrationRow>();while(reader.Read())rows.Add(new(reader.GetInt64(0),reader.GetString(1),reader.IsDBNull(2)?null:reader.GetString(2),reader.GetString(3),reader.GetString(4),JsonSerializer.Deserialize<string[]>(reader.GetString(5),Json)!));return
   rows;}
 }
 public IReadOnlyList<MigrationConflict> GetMigrationConflicts(string batch,int offset=0,int limit=120)
 {
  if(offset<0||limit is <1 or >1000)throw new ArgumentOutOfRangeException(nameof(limit));
  lock(gate){ThrowIfDisposed();using var
   command=Command("SELECT document FROM migration_conflicts WHERE batch=$batch ORDER BY kind,target_id LIMIT $limit OFFSET $offset",null,("$batch",batch),("$limit",limit),("$offset",offset));using
   var reader=command.ExecuteReader();var result=new
   List<MigrationConflict>();while(reader.Read())result.Add(JsonSerializer.Deserialize<MigrationConflict>(reader.GetString(0),Json)!);return result;}
 }
 public MigrationConflictPreview PreviewMigrationMediaConflict(string batch,Guid mediaId)
 {lock(gate){ThrowIfDisposed();return PreviewMigrationMediaConflictCore(batch,mediaId,null);}}
 private MigrationConflictPreview PreviewMigrationMediaConflictCore(string batch,Guid mediaId,SqliteTransaction? tx)
 {
  using var read=Command("SELECT document FROM migration_conflicts WHERE batch=$batch AND kind='media' AND target_id=$id",tx,("$batch",batch),("$id",mediaId.ToString()));var
   conflict=JsonSerializer.Deserialize<MigrationConflict>(read.ExecuteScalar() as string??throw new
   ArgumentException("Unknown media conflict."),Json)!;
   using var current=Command("SELECT document FROM media WHERE id=$id",tx,("$id",mediaId.ToString()));var text=current.ExecuteScalar() as string??throw new InvalidOperationException("The target media item was removed.");using var parsed=JsonDocument.Parse(text);
   return new(conflict,Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))),parsed.RootElement.EnumerateObject().ToDictionary(p=>p.Name,p=>p.Value.Clone()));
 }
 public MediaItem ResolveMigrationMediaConflict(string batch,Guid mediaId,IReadOnlyList<string> takeIncomingFields,string expectedCurrentSha256)
 {
  ArgumentNullException.ThrowIfNull(takeIncomingFields);
  lock(gate)
  {
   ThrowIfDisposed();using var tx=connection.BeginTransaction();var preview=PreviewMigrationMediaConflictCore(batch,mediaId,tx);
   if(preview.CurrentSha256!=expectedCurrentSha256)throw new InvalidOperationException("The media item changed after conflict preview; reload the preview.");
   var incoming=preview.Conflict.IncomingValues??throw new InvalidOperationException("This older conflict has no field values; preserve it and compare a new source snapshot.");
   var values=preview.CurrentValues.ToDictionary(p=>p.Key,p=>p.Value.Clone());
   foreach(var field in takeIncomingFields.Distinct(StringComparer.Ordinal))
   {if(field is "id" or "legacyId" or "extra"||!incoming.TryGetValue(field,out var value))throw new ArgumentException("Select a reported data field; identities and provenance cannot be replaced.");values[field]=value.Clone();}
   var media=JsonSerializer.Deserialize<MediaItem>(JsonSerializer.Serialize(values,Json),Json)!;var saved=UpsertCore(media,tx);
   var resolution=preview.Conflict with {Resolution="selected-fields:"+string.Join(',',takeIncomingFields)};using(var
    write=Command("UPDATE migration_conflicts SET document=$document WHERE batch=$batch AND kind='media' AND target_id=$id",tx,("$document",JsonSerializer.Serialize(resolution,Json)),("$batch",batch),("$id",mediaId.ToString())))write.ExecuteNonQuery();tx.Commit();return
    saved;
  }
 }
 public void ExportMigrationReport(string batch,string path)
 {
  FileAccessPolicy.Current.EnsureWritable(path);var temporary=Path.GetFullPath(path)+"."+Guid.NewGuid().ToString("N")+".tmp";
  try{lock(gate){ThrowIfDisposed();using var command=Command("SELECT report FROM legacy_imports WHERE batch=$batch",null,("$batch",batch));var summary=command.ExecuteScalar() as string??throw new ArgumentException("Unknown migration batch.");
   using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))using(var writer=new Utf8JsonWriter(stream,new(){Indented=true}))
   {writer.WriteStartObject();writer.WriteString("format","flixypix-migration-report");writer.WriteNumber("version",1);writer.WritePropertyName("summary");using(var json=JsonDocument.Parse(summary))json.RootElement.WriteTo(writer);writer.WriteStartArray("rows");
    for(var offset=0;;offset+=1000){var rows=GetMigrationRows(batch,offset,1000);foreach(var row in rows)JsonSerializer.Serialize(writer,row,Json);if(rows.Count<1000)break;}
    writer.WriteEndArray();writer.WriteStartArray("conflicts");for(var offset=0;;offset+=1000){var
     conflicts=GetMigrationConflicts(batch,offset,1000);foreach(var conflict in
     conflicts)JsonSerializer.Serialize(writer,conflict,Json);if(conflicts.Count<1000)break;}writer.WriteEndArray();writer.WriteEndObject();}
   FileAccessPolicy.Current.EnsureWritable(path);File.Move(temporary,path,false);}}
  finally{if(File.Exists(temporary))File.Delete(temporary);}
 }
 private sealed class LegacyMediaMap(CatalogRepository repository,SqliteTransaction tx) : IEnumerable<KeyValuePair<string,MediaItem>>
 {
  public MediaItem this[string key]
  {
   get=>TryGetValue(key,out var value)?value:throw new KeyNotFoundException(key);
   set
   {
    using(var insert=repository.Command("INSERT INTO temp.legacy_media_map VALUES($key,$id) ON CONFLICT(legacy_key) DO UPDATE SET target_id=excluded.target_id",tx,("$key",key),("$id",value.Id.ToString())))insert.ExecuteNonQuery();
    using var save=repository.Command("INSERT INTO legacy_mappings VALUES($source,$key,$id) ON CONFLICT(source_id,legacy_key) DO UPDATE SET target_id=excluded.target_id",tx,("$source",repository.legacySession!.SourceId),("$key",key),("$id",value.Id.ToString()));save.ExecuteNonQuery();
   }
  }
  public bool TryGetValue(string key,out MediaItem value)
  {
   using var command=repository.Command("SELECT m.document FROM temp.legacy_media_map l JOIN media m ON m.id=l.target_id WHERE legacy_key=$key",tx,("$key",key));
   if(command.ExecuteScalar() is string document){value=JsonSerializer.Deserialize<MediaItem>(document,Json)!;return true;}value=null!;return false;
  }
  public IEnumerable<MediaItem> Values=>this.Select(p=>p.Value);
  public IEnumerator<KeyValuePair<string,MediaItem>> GetEnumerator()
  {
   string after="";
   while(true)
   {
    var keys=new List<string>();using(var command=repository.Command("SELECT legacy_key FROM temp.legacy_media_map WHERE legacy_key>$after ORDER BY legacy_key LIMIT 256",tx,("$after",after)))using(var reader=command.ExecuteReader())while(reader.Read())keys.Add(reader.GetString(0));
    if(keys.Count==0)yield break;
    foreach(var key in keys){repository.legacySession!.Token.ThrowIfCancellationRequested();yield return new(key,this[key]);}after=keys[^1];
   }
  }
  IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
 }
}
