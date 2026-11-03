using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

/// <summary>Encrypted, disposable disk staging; queries materialize only one bounded page. No target catalog is changed.</summary>
public sealed partial class StashExportSnapshot:IDisposable
{
 private readonly SqliteConnection connection;
 private readonly string work;
 private readonly object gate=new();private bool disposed;
 public string SourceDirectory {get;}
 public int Count {get;}
 public long TotalBytes {get;}
 public int ErrorDocuments {get;}
 internal string StagingDatabase=>Path.Combine(work,"source.db");
 internal StashExportSnapshot(SqliteConnection connection,string work,string source,int count,long bytes,int errors)
 {this.connection=connection;this.work=work;SourceDirectory=source;Count=count;TotalBytes=bytes;ErrorDocuments=errors;}
 /// <summary>At most256 documents and64MiB of original bytes. Advance offset by the returned Count.</summary>
 public IReadOnlyList<StashSourceDocument> ReadPage(int offset,int limit=120)
 {
  if(offset<0||limit is <1 or >256)throw new ArgumentOutOfRangeException(nameof(limit));
  lock(gate)
  {
   ObjectDisposedException.ThrowIf(disposed,this);var result=new List<StashSourceDocument>();
   using var command=connection.CreateCommand();command.CommandText="SELECT ordinal,kind,path,sha256,natural_key,original,issues,json_valid,length(original) FROM documents ORDER BY kind,path LIMIT $limit OFFSET $offset";command.Parameters.AddWithValue("$limit",limit);command.Parameters.AddWithValue("$offset",offset);
   using var reader=command.ExecuteReader();
   long pageBytes=0;
   while(reader.Read())
   {
    var length=reader.GetInt64(8);if(result.Count>0&&length>64L*1024*1024-pageBytes)break;pageBytes+=length;
    result.Add(ReadRecord(reader));
   }
   return result;
  }
 }
 /// <summary>One staged record by its ordinal, for the selected apply; null when the ordinal is not part of this snapshot.</summary>
 internal StashSourceDocument? ReadDocument(long ordinal)
 {
  lock(gate)
  {
   ObjectDisposedException.ThrowIf(disposed,this);
   using var command=connection.CreateCommand();command.CommandText="SELECT ordinal,kind,path,sha256,natural_key,original,issues,json_valid FROM documents WHERE ordinal=$ordinal";command.Parameters.AddWithValue("$ordinal",ordinal);
   using var reader=command.ExecuteReader();return reader.Read()?ReadRecord(reader):null;
  }
 }
 private static StashSourceDocument ReadRecord(SqliteDataReader reader)
 {
  var bytes=(byte[])reader.GetValue(5);JsonElement? json=null;
  if(reader.GetBoolean(7)){using var document=JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=64});json=document.RootElement.Clone();}
  return new(reader.GetInt64(0),(StashEntityKind)reader.GetInt32(1),reader.GetString(2),reader.GetString(3),reader.IsDBNull(4)?null:reader.GetString(4),bytes,json,
   JsonSerializer.Deserialize<StashReadIssue[]>(reader.GetString(6))!);
 }
 /// <summary>Size and modification time of each file when its content last matched, and when that hash began.</summary>
 private readonly Dictionary<string,(long Length,long Written,long Hashed)> verified=new(StringComparer.Ordinal);
 /// <summary>A file written this close to its hash could still change within the same time stamp (FAT keeps 2 seconds), so it is hashed again.</summary>
 private static readonly long RacyTicks=TimeSpan.FromSeconds(3).Ticks;
 /// <summary>Rechecks the selected JSON inventory and hashes before a later preview can be applied. A file whose size and time stamp are
 /// unchanged since an earlier hash, and that was not written shortly before that hash, is not read again; any other file is hashed.</summary>
 public Task VerifySourceAsync(CancellationToken token=default)=>Task.Run(()=>
 {
  lock(gate)
  {
   ObjectDisposedException.ThrowIf(disposed,this);StashExport.PlainSourcePath(SourceDirectory);var seen=0;
   foreach(var(file,_,relative)in StashExport.SourceFiles(SourceDirectory,token))
   {
    token.ThrowIfCancellationRequested();if(++seen>Count)throw new IOException("Stash export files were added after the preview.");
    using var query=connection.CreateCommand();query.CommandText="SELECT sha256,length(original) FROM documents WHERE path=$path";query.Parameters.AddWithValue("$path",relative);
    using var reader=query.ExecuteReader();if(!reader.Read())throw new IOException("Stash export file names changed after the preview.");var hash=reader.GetString(0);var length=reader.GetInt64(1);
    if(file.Length!=length)throw new IOException("Stash export contents changed after the preview.");
    var written=file.LastWriteTimeUtc.Ticks;
    if(verified.TryGetValue(relative,out var stamp)&&stamp.Length==length&&stamp.Written==written&&stamp.Hashed-written>RacyTicks)continue;
    var hashed=DateTime.UtcNow.Ticks;
    StashExport.PlainSourcePath(file.FullName);using var input=new FileStream(file.FullName,FileMode.Open,FileAccess.Read,FileShare.Read);
    if(input.Length!=length)throw new IOException("Stash export contents changed after the preview.");
    using var digest=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var buffer=new byte[81920];long bytes=0;int count;
    while((count=input.Read(buffer))!=0){token.ThrowIfCancellationRequested();bytes+=count;if(bytes>length)throw new IOException("Stash export grew while being rechecked.");digest.AppendData(buffer.AsSpan(0,count));}
    if(bytes!=length||!Convert.ToHexStringLower(digest.GetHashAndReset()).Equals(hash,StringComparison.Ordinal))throw new IOException("Stash export contents changed after the preview.");
    // The time stamp is remembered only when it did not change while the file was read.
    file.Refresh();if(file.Exists&&file.Length==length&&file.LastWriteTimeUtc.Ticks==written)verified[relative]=(length,written,hashed);else verified.Remove(relative);
   }
   if(seen!=Count)throw new IOException("Stash export files were removed after the preview.");
  }
 },token);
 public void Dispose()
 {
  lock(gate){if(disposed)return;disposed=true;connection.Dispose();StashExport.CleanupStage(work);}
 }
}

public static partial class StashExport
{
 /// <summary>Reads known Stash export subdirectories from a selected local folder, with no network or media-file access.</summary>
 public static Task<StashExportSnapshot> ReadDirectoryAsync(string sourceDirectory,string stagingParent,StashReadLimits? limits=null,
  CancellationToken token=default,IProgress<StashReadProgress>? progress=null)
 =>Task.Run(()=>ReadDirectory(sourceDirectory,stagingParent,limits??new(),token,progress),token);

 private static StashExportSnapshot ReadDirectory(string sourceDirectory,string stagingParent,StashReadLimits limits,CancellationToken token,IProgress<StashReadProgress>? progress)
 {
  limits.Validate();token.ThrowIfCancellationRequested();var source=Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory));PlainSourcePath(source);
  var parent=Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingParent));
  var sourcePrefix=Path.EndsInDirectorySeparator(source)?source:source+Path.DirectorySeparatorChar;
  if(parent.Equals(source,StringComparison.OrdinalIgnoreCase)||parent.StartsWith(sourcePrefix,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Stash staging must be outside the source export.");
  FileAccessPolicy.Current.EnsureWritable(parent);PlainSourcePath(parent,allowMissing:true);Directory.CreateDirectory(parent);
  var work=Path.Combine(parent,".flixypix-stash-"+Guid.NewGuid().ToString("N"));FileAccessPolicy.Current.EnsureWritable(work);Directory.CreateDirectory(work);
  SqliteConnection? connection=null;
  try
  {
   // Fresh ephemeral encryption key, no key file. Even plain-target previews never write source JSON to a plain staging database.
   PlainSourcePath(work);using var key=new CatalogKey(RandomNumberGenerator.GetBytes(32));
   connection=new(CatalogEncryption.ConnectionString(Path.Combine(work,"source.db"),key).ToString());connection.Open();CatalogRepository.AddOrdinalCollation(connection);
   using(var setup=connection.CreateCommand()){setup.CommandText="PRAGMA journal_mode=DELETE; PRAGMA temp_store=MEMORY; CREATE TABLE documents(ordinal INTEGER PRIMARY KEY,kind INTEGER NOT NULL,path TEXT NOT NULL COLLATE EFP_ORDINAL UNIQUE,sha256 TEXT NOT NULL,natural_key TEXT,original BLOB NOT NULL,issues TEXT NOT NULL,json_valid INTEGER NOT NULL); CREATE INDEX document_pages ON documents(kind,path); CREATE INDEX document_natural_keys ON documents(kind,natural_key); CREATE TABLE reference_keys(kind INTEGER NOT NULL,key TEXT NOT NULL,ordinal INTEGER NOT NULL,PRIMARY KEY(kind,key,ordinal)); CREATE TABLE source_links(owner INTEGER NOT NULL,kind INTEGER NOT NULL,key TEXT NOT NULL,relation TEXT NOT NULL,position INTEGER NOT NULL,source_order INTEGER); CREATE INDEX source_links_target ON source_links(kind,key,owner); CREATE INDEX source_links_owner ON source_links(owner,position);";setup.ExecuteNonQuery();}
   int count=0,errors=0,links=0;long total=0;
   using(var transaction=connection.BeginTransaction())
   {
    foreach(var(input,kind,relative)in SourceFiles(source,token))
     {
      if(++count>limits.MaximumFiles)throw new InvalidDataException("Stash export exceeds the file limit.");
      var stamp=input.LastWriteTimeUtc.Ticks;byte[] bytes;
      PlainSourcePath(input.FullName);
      using(var stream=new FileStream(input.FullName,FileMode.Open,FileAccess.Read,FileShare.Read))
      {
       if(stream.Length>limits.MaximumEntityBytes||stream.Length>limits.MaximumTotalBytes-total)throw new InvalidDataException("Stash export exceeds the entity or total byte limit.");
       bytes=new byte[(int)stream.Length];stream.ReadExactly(bytes);
       if(stream.ReadByte()!=-1)throw new IOException("Stash source changed while reading.");
      }
      input.Refresh();if(!input.Exists||input.Length!=bytes.Length||input.LastWriteTimeUtc.Ticks!=stamp)throw new IOException("Stash source changed while reading.");
      total+=bytes.Length;var recordKind=kind;string? naturalKey=null;List<StashReadIssue> issues;var parsed=false;IReadOnlyList<string> lookupKeys=[];
      try
      {
       using var document=JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=64});parsed=true;
       (recordKind,naturalKey,issues)=ValidateDocument(kind,document.RootElement);
       if(!issues.Any(issue=>issue.Severity==StashIssueSeverity.Error))
       {
        try{lookupKeys=LookupKeys(recordKind,document.RootElement);foreach(var reference in References(recordKind,document.RootElement)){token.ThrowIfCancellationRequested();_=reference.Key;}}
        catch(ArgumentException error){if(issues.Count==256)issues.RemoveAt(255);issues.Add(new("reference-path",StashIssueSeverity.Error,error.Message));lookupKeys=[];}
       }
      }
      catch(JsonException){issues=[new("json",StashIssueSeverity.Error,"Invalid UTF-8 JSON or exceeded nesting limit.")];}
      if(issues.Any(issue=>issue.Severity==StashIssueSeverity.Error))errors++;
      using var insert=connection.CreateCommand();insert.Transaction=transaction;insert.CommandText="INSERT INTO documents VALUES($ordinal,$kind,$path,$sha,$key,$original,$issues,$valid)";
      insert.Parameters.AddWithValue("$ordinal",count);insert.Parameters.AddWithValue("$kind",(int)recordKind);insert.Parameters.AddWithValue("$path",relative);
      insert.Parameters.AddWithValue("$sha",Convert.ToHexStringLower(SHA256.HashData(bytes)));insert.Parameters.AddWithValue("$key",(object?)naturalKey??DBNull.Value);
      insert.Parameters.AddWithValue("$original",bytes);insert.Parameters.AddWithValue("$issues",JsonSerializer.Serialize(issues));insert.Parameters.AddWithValue("$valid",parsed);insert.ExecuteNonQuery();
      if(!issues.Any(issue=>issue.Severity==StashIssueSeverity.Error))
      {
       foreach(var lookup in lookupKeys)
       {
        using var index=connection.CreateCommand();index.Transaction=transaction;index.CommandText="INSERT INTO reference_keys VALUES($kind,$key,$ordinal)";index.Parameters.AddWithValue("$kind",(int)recordKind);index.Parameters.AddWithValue("$key",lookup);index.Parameters.AddWithValue("$ordinal",count);index.ExecuteNonQuery();
       }
       using var document=JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=64});
       foreach(var reference in References(recordKind,document.RootElement))
       {
        token.ThrowIfCancellationRequested();if(++links>10_000_000)throw new InvalidDataException("Stash export exceeds the reference limit.");
        using var link=connection.CreateCommand();link.Transaction=transaction;link.CommandText="INSERT INTO source_links VALUES($owner,$kind,$key,$relation,$position,$order)";
        link.Parameters.AddWithValue("$owner",count);link.Parameters.AddWithValue("$kind",(int)reference.Kind);link.Parameters.AddWithValue("$key",reference.Key);link.Parameters.AddWithValue("$relation",reference.Relation);link.Parameters.AddWithValue("$position",reference.Position);link.Parameters.AddWithValue("$order",(object?)reference.Order??DBNull.Value);link.ExecuteNonQuery();
       }
      }
      progress?.Report(new(relative,count,total));
     }
    if(count==0)throw new InvalidDataException("No supported Stash export JSON documents were found.");
    token.ThrowIfCancellationRequested();transaction.Commit();
   }
   return new(connection,work,source,count,total,errors);
  }
  catch{connection?.Dispose();CleanupStage(work);throw;}
 }
 internal static IEnumerable<(FileInfo File,StashEntityKind Kind,string RelativePath)> SourceFiles(string source,CancellationToken token)
 {
  var rootNames=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  foreach(var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
  {
   token.ThrowIfCancellationRequested();RejectLink(entry);
   if(!rootNames.Add(entry.Name))throw new InvalidDataException("Ambiguous Stash export directory name.");
   if(!Sections.TryGetValue(entry.Name,out var kind))continue;
   if(entry is not DirectoryInfo directory)throw new InvalidDataException("Stash export sections must be directories.");
   foreach(var child in directory.EnumerateFileSystemInfos())
   {
    token.ThrowIfCancellationRequested();RejectLink(child);
    if(child is DirectoryInfo)throw new InvalidDataException("Stash export section documents must be direct children, not nested directories.");
    if(child is FileInfo file&&file.Extension.Equals(".json",StringComparison.OrdinalIgnoreCase))yield return(file,kind,entry.Name+"/"+file.Name);
   }
  }
 }
 private static void RejectLink(FileSystemInfo entry)
 {if((entry.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Stash source or staging links are not followed.");}
 internal static void PlainSourcePath(string path,bool allowMissing=false)
 {
  for(string? current=Path.GetFullPath(path);current!=null;current=Path.GetDirectoryName(current))
  {
   FileAttributes attributes;
   try{attributes=File.GetAttributes(current);}catch(Exception error)when(allowMissing&&(error is FileNotFoundException or DirectoryNotFoundException)){continue;}
   if((attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Stash source or staging links are not followed.");
  }
 }
 internal static void CleanupStage(string work)
 {
  var full=Path.GetFullPath(work);if(!Path.GetFileName(full).StartsWith(".flixypix-stash-",StringComparison.Ordinal))throw new IOException("Invalid owned Stash staging directory.");
  if(!Directory.Exists(full))return;PlainSourcePath(full);FileAccessPolicy.Current.EnsureWritable(full);
  foreach(var entry in new DirectoryInfo(full).EnumerateFileSystemInfos())
  {RejectLink(entry);if(entry is DirectoryInfo)throw new IOException("Unexpected directory in owned Stash staging.");}
  Directory.Delete(full,recursive:true);
 }
}
