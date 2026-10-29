using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;
public sealed partial class CatalogRepository
{
 private static readonly string[] MigrationTables=["legacy_sources","legacy_imports","legacy_mappings","migration_rows","migration_conflicts"];
 private sealed record MigrationStateRecord(string Table,Dictionary<string,JsonElement> Values);
 private sealed record PortableBlobChunk(string Sha256,long Length,long Offset,byte[] Data);
 private void WriteMigrationPortable(Utf8JsonWriter writer,SqliteTransaction tx)
 {
  writer.WriteStartArray("migrationState");
  foreach(var table in MigrationTables)
  {
   using var command=Command($"SELECT * FROM {table} ORDER BY rowid",tx);using var reader=command.ExecuteReader();
   while(reader.Read())
   {
    writer.WriteStartObject();writer.WriteString("table",table);writer.WriteStartObject("values");
    for(var i=0;i<reader.FieldCount;i++){writer.WritePropertyName(reader.GetName(i));JsonSerializer.Serialize(writer,reader.IsDBNull(i)?null:reader.GetValue(i),Json);}
    writer.WriteEndObject();writer.WriteEndObject();
   }
  }
  writer.WriteEndArray();writer.WriteStartArray("legacyBlobs");
  using(var command=Command("SELECT sha256,length FROM legacy_blobs ORDER BY sha256",tx))using(var reader=command.ExecuteReader())
  while(reader.Read())
  {
   var hash=reader.GetString(0);var length=reader.GetInt64(1);
   using var chunks=new PortableChunkWriter(writer,hash,length);
   try{if(!assets.CopyTo(hash,length,chunks))throw new InvalidDataException("Preserved blob failed its checksum while exporting.");}
   catch(InvalidDataException){throw new InvalidDataException("Preserved blob failed its checksum while exporting.");}
   chunks.Finish();
  }
  writer.WriteEndArray();
  foreach(var (section,table) in
   new[]{("tagPresets","tag_presets"),("legacySearchDrafts","legacy_search_drafts"),("legacyJobDrafts","legacy_job_drafts"),("legacyParserDrafts","legacy_parser_drafts"),("tagAssets","tag_assets"),("historicalParserDefinitions","historical_parser_definitions"),("legacyCombineLists","legacy_combine_lists"),("historicalContentFolders","historical_content_folders")})
  {writer.WriteStartArray(section);using var command=Command($"SELECT document FROM {table} ORDER BY id",tx);using var reader=command.ExecuteReader();while(reader.Read()){if(table=="tag_assets"){WriteWithData(writer,reader.GetString(0));continue;}using var value=JsonDocument.Parse(reader.GetString(0));value.RootElement.WriteTo(writer);}writer.WriteEndArray();}
 }
 private void ImportMigrationPortable(SqliteTransaction tx,CancellationToken token)
 {
  using(var create=Command("CREATE TEMP TABLE portable_blob_chunks(sha256 TEXT NOT NULL,length INTEGER NOT NULL,offset INTEGER NOT NULL,data BLOB NOT NULL,PRIMARY KEY(sha256,offset));",tx))create.ExecuteNonQuery();
  foreach(var chunk in ReadStagedPortable<PortableBlobChunk>("legacyblobs",tx,token))
  {
   if(chunk==null||chunk.Sha256==null||chunk.Sha256.Length!=64||chunk.Sha256.Any(c=>!char.IsAsciiHexDigitLower(c))||chunk.Length<0||chunk.Length>int.MaxValue||chunk.Offset<0||chunk.Data==null||chunk.Data.Length>1024*1024||chunk.Offset+chunk.Data.Length>chunk.Length)throw new InvalidDataException("Invalid preserved blob chunk.");
   using var insert=Command("INSERT INTO temp.portable_blob_chunks VALUES($hash,$length,$offset,$data)",tx,("$hash",chunk.Sha256),("$length",chunk.Length),("$offset",chunk.Offset),("$data",chunk.Data));insert.ExecuteNonQuery();
  }
  using(var select=Command("SELECT sha256,min(length),max(length) FROM temp.portable_blob_chunks GROUP BY sha256",tx))using(var reader=select.ExecuteReader())
  while(reader.Read())
  {
   token.ThrowIfCancellationRequested();var hash=reader.GetString(0);var length=reader.GetInt64(1);if(length!=reader.GetInt64(2))throw new InvalidDataException("Conflicting preserved blob lengths.");
   long expected=0;var chunks=0;
   using(var sequence=Command("SELECT offset,length(data) FROM temp.portable_blob_chunks WHERE sha256=$hash ORDER BY offset",tx,("$hash",hash)))using(var parts=sequence.ExecuteReader())while(parts.Read())
   {var offset=parts.GetInt64(0);var count=parts.GetInt64(1);if(offset!=expected||count==0&&(length!=0||chunks!=0))throw new InvalidDataException("Missing, overlapping or empty preserved blob chunk.");expected+=count;chunks++;}
   if(expected!=length||length==0&&chunks!=1)throw new InvalidDataException("Incomplete preserved blob chunks.");
   using var input=new BlobChunkInput(this,tx,hash,length,token);StoreLegacyBlob(hash,length,input,tx,token);
  }
  foreach(var record in ReadStagedPortable<MigrationStateRecord>("migrationstate",tx,token))
  {
   if(record==null||!MigrationTables.Contains(record.Table,StringComparer.Ordinal)||record.Values==null)throw new InvalidDataException("Unknown migration state table.");
   var columns=new List<string>();using(var schema=Command($"PRAGMA table_info({record.Table})",tx))using(var reader=schema.ExecuteReader())while(reader.Read())columns.Add(reader.GetString(1));
   if(!columns.ToHashSet(StringComparer.Ordinal).SetEquals(record.Values.Keys))throw new InvalidDataException("Migration state columns differ from the documented schema.");
   var parameters=new List<(string,object)>();for(var i=0;i<columns.Count;i++)
   {
    var value=record.Values[columns[i]];object data=value.ValueKind switch{JsonValueKind.Null=>DBNull.Value,JsonValueKind.String=>value.GetString()!,JsonValueKind.Number when value.TryGetInt64(out var integer)=>integer,_=>throw new InvalidDataException("Invalid migration state scalar.")};
    if(columns[i]=="target_id"&&data is string id&&Guid.TryParse(id,out var guid))data=MapPortableId(guid,tx).ToString();
    parameters.Add(("$v"+i,data));
   }
   using var insert=Command($"INSERT OR IGNORE INTO {record.Table}({string.Join(',',columns)}) VALUES({string.Join(',',parameters.Select(p=>p.Item1))})",tx,parameters.ToArray());insert.ExecuteNonQuery();
  }
  foreach(var kind in new[]{"tagpresets","legacysearchdrafts","legacyjobdrafts"})foreach(var document in ReadStagedPortable<JsonElement>(kind,tx,token))SaveHistoricalPortableDocument(kind,document,tx);
  foreach(var document in ReadStagedPortable<JsonElement>("legacyparserdrafts",tx,token))SavePortableLegacyParserDraft(document,tx);
  foreach(var document in ReadStagedPortable<JsonElement>("tagassets",tx,token))SavePortableTagAsset(document,tx);
  foreach(var document in ReadStagedPortable<JsonElement>("historicalparserdefinitions",tx,token))SavePortableHistoricalParserDefinition(document,tx);
  foreach(var document in ReadStagedPortable<JsonElement>("legacycombinelists",tx,token))SavePortableLegacyCombineList(document,tx);
  foreach(var document in ReadStagedPortable<JsonElement>("historicalcontentfolders",tx,token))SavePortableHistoricalContentFolder(document,tx);
  using(var refs=Command("""
   SELECT DISTINCT json_extract(CASE WHEN value.type='object' THEN value.value ELSE '{}' END,'$."$binaryRef".sha256'),json_extract(CASE WHEN value.type='object' THEN value.value ELSE '{}' END,'$."$binaryRef".length')
   FROM temp.portable_legacy_rows r,json_each(json_extract(r.record,'$.values')) value
   WHERE json_type(CASE WHEN value.type='object' THEN value.value ELSE '{}' END,'$."$binaryRef"')='object'
   """,tx))using(var reader=refs.ExecuteReader())while(reader.Read())
  {
   if(reader.IsDBNull(0)||reader.IsDBNull(1))throw new InvalidDataException("Invalid binary reference in preserved source record.");
   using var check=Command("SELECT length FROM legacy_blobs WHERE sha256=$hash",tx,("$hash",reader.GetString(0)));if(check.ExecuteScalar() is not long length||length!=reader.GetInt64(1))throw new InvalidDataException("A preserved source record references a missing or mismatched binary blob.");
  }
  using(var drop=Command("DROP TABLE temp.portable_blob_chunks",tx))drop.ExecuteNonQuery();
 }
 /// <summary>Writes the bytes it receives as portable chunks of at most 1 MiB; an empty blob is one empty chunk.</summary>
 private sealed class PortableChunkWriter(Utf8JsonWriter writer,string hash,long length):Stream
 {
  private readonly byte[] buffer=new byte[1024*1024];private int filled;private long offset;private bool written;
  public override void Write(byte[] data,int start,int count)=>Write(data.AsSpan(start,count));
  public override void Write(ReadOnlySpan<byte> data)
  {
   while(data.Length>0){var take=Math.Min(buffer.Length-filled,data.Length);data[..take].CopyTo(buffer.AsSpan(filled));filled+=take;data=data[take..];if(filled==buffer.Length)Emit();}
  }
  private void Emit()
  {
   writer.WriteStartObject();writer.WriteString("sha256",hash);writer.WriteNumber("length",length);writer.WriteNumber("offset",offset);writer.WriteBase64String("data",buffer.AsSpan(0,filled));writer.WriteEndObject();
   offset+=filled;filled=0;written=true;
  }
  public void Finish(){if(filled>0||!written)Emit();}
  public override bool CanRead=>false;public override bool CanSeek=>false;public override bool CanWrite=>true;public override long Length=>offset+filled;public override long Position{get=>Length;set=>throw new NotSupportedException();}
  public override void Flush(){}public override int Read(byte[] data,int start,int count)=>throw new NotSupportedException();public override long Seek(long position,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long value)=>throw new NotSupportedException();
 }
 private sealed class BlobChunkInput(CatalogRepository repository,SqliteTransaction tx,string hash,long expected,CancellationToken token):Stream
 {
  private long position;private byte[] chunk=[];private int index;private bool fetchedEmpty;
  public override int Read(byte[] buffer,int offset,int count)
  {
   token.ThrowIfCancellationRequested();if(count==0)return 0;
   if(index==chunk.Length)
   {
    if(position==expected&&fetchedEmpty)return 0;
    using var command=repository.Command("SELECT data FROM temp.portable_blob_chunks WHERE sha256=$hash AND offset=$offset",tx,("$hash",hash),("$offset",position));
    if(command.ExecuteScalar() is not byte[] bytes){if(position==expected)return 0;throw new InvalidDataException("Missing preserved blob chunk.");}
    chunk=bytes;index=0;fetchedEmpty=true;if(bytes.Length==0){if(position!=expected)throw new InvalidDataException("Empty incomplete blob chunk.");return 0;}
   }
   var copied=Math.Min(count,chunk.Length-index);Buffer.BlockCopy(chunk,index,buffer,offset,copied);index+=copied;position+=copied;return copied;
  }
  public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;public override long Length=>expected;public override long Position{get=>position;set=>throw new NotSupportedException();}
  public override void Flush(){}public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
 }
}
