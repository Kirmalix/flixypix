using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace FlixyPix.Core;

public sealed record SettingsTransitionResult(string SettingsPath,bool Migrated,bool CopiedLibrary);

/// <summary>One-time, copy-only transition of this reconstruction's settings and managed library.
/// It never opens SDF files or accesses external media paths.</summary>
public static class SettingsTransition
{
 public static SettingsTransitionResult EnsureMigrated(string previousRoot,string newRoot,CancellationToken token=default)
 {
  var source=Path.GetFullPath(previousRoot);var target=Path.GetFullPath(newRoot);
  var settings=Path.Combine(target,"appsettings.json");
  if(File.Exists(settings))return new(settings,false,false);
  if(Within(target,source)||Within(source,target))throw new ArgumentException("The previous and new settings directories must be separate.");
  token.ThrowIfCancellationRequested();RejectLinks(source);
  var oldSettings=Path.Combine(source,"appsettings.json");var oldLibrary=Path.Combine(source,"Library");
  JsonObject document;
  if(File.Exists(oldSettings))
  {
   RejectLinks(oldSettings);using var input=new FileStream(oldSettings,FileMode.Open,FileAccess.Read,FileShare.Read);
   if(input.Length>1024*1024)throw new InvalidDataException(T("The previous settings are unexpectedly large."));
   using var reader=new StreamReader(input);document=JsonNode.Parse(reader.ReadToEnd()) as JsonObject??throw new InvalidDataException(T("The previous settings are invalid."));
  }
  else if(File.Exists(Path.Combine(oldLibrary,"catalog.db")))document=new JsonObject();
  else return new(settings,false,false);
  var key=document.Select(p=>p.Key).FirstOrDefault(k=>k.Equals("DataDirectory",StringComparison.OrdinalIgnoreCase))??"DataDirectory";
  var directory=document[key]==null?oldLibrary:document[key]!.GetValue<string>();
  if(string.IsNullOrWhiteSpace(directory)||!Path.IsPathFullyQualified(directory))throw new InvalidDataException(T("The previous library folder is invalid."));
  var copyLibrary=Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)).Equals(Path.TrimEndingDirectorySeparator(oldLibrary),StringComparison.OrdinalIgnoreCase);
  FileAccessPolicy.Current.EnsureWritable(target);
  if(Directory.Exists(target)||File.Exists(target))throw new IOException(T("The new FlixyPix settings folder is already in use. No previous data was changed."));
  var parent=Path.GetDirectoryName(target)??throw new ArgumentException("The new settings directory needs a parent.");
  FileAccessPolicy.Current.EnsureWritable(parent);Directory.CreateDirectory(parent);
  var work=Path.Combine(parent,".flixypix-settings-"+Guid.NewGuid().ToString("N"));
  FileAccessPolicy.Current.EnsureWritable(work);Directory.CreateDirectory(work);var published=false;
  try
  {
   if(copyLibrary)
   {
    var stageLibrary=Path.Combine(work,"Library");FileAccessPolicy.Current.EnsureWritable(stageLibrary);Directory.CreateDirectory(stageLibrary);
    if(Directory.Exists(oldLibrary))CopyLibrary(oldLibrary,stageLibrary,token);
    var dbPath=Path.Combine(stageLibrary,"catalog.db");
    if(File.Exists(dbPath))
    {
     using var catalog=new CatalogRepository(dbPath);
     catalog.RemapManagedLibraryPaths(oldLibrary,Path.Combine(target,"Library"),token);
     var integrity=catalog.CheckIntegrity(token);if(!integrity.IsValid)throw new InvalidDataException(T("The copied library failed the integrity check."));
    }
    RewriteManagedManifests(stageLibrary,oldLibrary,Path.Combine(target,"Library"),token);
    document[key]=Path.Combine(target,"Library");
   }
   // User-selected library paths are preserved verbatim, even if the drive is offline.
   FileAccessPolicy.Current.EnsureWritable(Path.Combine(work,"appsettings.json"));
   File.WriteAllText(Path.Combine(work,"appsettings.json"),document.ToJsonString(new(){WriteIndented=true}));
   token.ThrowIfCancellationRequested();FileAccessPolicy.Current.EnsureWritable(target);FileAccessPolicy.Current.EnsureWritable(work);
   if(Directory.Exists(target)||File.Exists(target))throw new IOException(T("The new settings folder was created in the meantime."));
   Directory.Move(work,target);published=true;return new(settings,true,copyLibrary);
  }
  finally{if(!published)DeleteOwnedWork(work,parent);}
 }
 private static void CopyLibrary(string source,string target,CancellationToken token)
 {
  RejectLinks(source);
  var database=Path.Combine(source,"catalog.db");
  if(File.Exists(database))
  {
   RejectLinks(database);var output=Path.Combine(target,"catalog.db");FileAccessPolicy.Current.EnsureWritable(output);
   using var input=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=database,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());
   using var snapshot=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=output,Pooling=false}.ToString());
   input.Open();snapshot.Open();token.ThrowIfCancellationRequested();input.BackupDatabase(snapshot);
  }
  CopyDirectory(source,target,true);
  void CopyDirectory(string from,string to,bool libraryRoot)
  {
   RejectLinks(from);
   foreach(var entry in new DirectoryInfo(from).EnumerateFileSystemInfos())
   {
    token.ThrowIfCancellationRequested();if((entry.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException(T("Library links are not followed when the name changes."));
    var destination=Path.Combine(to,entry.Name);FileAccessPolicy.Current.EnsureWritable(destination);
    if(entry is DirectoryInfo){Directory.CreateDirectory(destination);CopyDirectory(entry.FullName,destination,false);continue;}
    if(libraryRoot&&entry.Name is "catalog.db" or "catalog.db-wal" or "catalog.db-shm")continue;
    using var input=new FileStream(entry.FullName,FileMode.Open,FileAccess.Read,FileShare.Read);
    var length=input.Length;var before=SHA256.HashData(input);input.Position=0;
    using(var output=new FileStream(destination,FileMode.CreateNew,FileAccess.Write,FileShare.None))
    {var buffer=new byte[1024*1024];int count;while((count=input.Read(buffer))>0){token.ThrowIfCancellationRequested();output.Write(buffer,0,count);}if(output.Length!=length)throw new IOException(T("A library file was changed during the import."));}
    input.Position=0;if(!CryptographicOperations.FixedTimeEquals(before,SHA256.HashData(input)))throw new IOException(T("A library file was changed during the import."));
   }
  }
 }
 private static void RewriteManagedManifests(string directory,string oldLibrary,string newLibrary,CancellationToken token)
 {
  foreach(var path in Directory.EnumerateFiles(directory,"*.json",SearchOption.AllDirectories))
  {
   token.ThrowIfCancellationRequested();if(new FileInfo(path).Length>16*1024*1024)continue;
   JsonNode? node;try{node=JsonNode.Parse(File.ReadAllText(path));}catch(JsonException){continue;}
   if(node is not JsonObject document)continue;
   var formatKey=document.Select(p=>p.Key).FirstOrDefault(k=>k.Equals("format",StringComparison.OrdinalIgnoreCase));
   if(formatKey==null||document[formatKey] is not JsonValue marker||!marker.TryGetValue<string>(out var format))continue;
   var current=new[]{"flixypix-media-queue","flixypix-scene-previews"}.FirstOrDefault(c=>ProductIdentity.IsFormat(format!,c));if(current==null)continue;
   document[formatKey]=current;Rewrite(document);
   FileAccessPolicy.Current.EnsureWritable(path);File.WriteAllText(path,document.ToJsonString(new(){WriteIndented=true}));
  }
  void Rewrite(JsonNode value)
  {
   if(value is JsonObject obj)foreach(var key in obj.Select(p=>p.Key).ToArray()){if(obj[key] is JsonValue scalar&&scalar.TryGetValue<string>(out var text))obj[key]=Remap(text,oldLibrary,newLibrary);else if(obj[key] is {} child)Rewrite(child);}
   else if(value is JsonArray array)for(var i=0;i<array.Count;i++){if(array[i] is JsonValue scalar&&scalar.TryGetValue<string>(out var text))array[i]=Remap(text,oldLibrary,newLibrary);else if(array[i] is {} child)Rewrite(child);}
  }
 }
 internal static string Remap(string path,string previous,string current)=>Within(path,previous)?current+path[Path.TrimEndingDirectorySeparator(previous).Length..]:path;
 private static bool Within(string path,string root)
 {root=Path.TrimEndingDirectorySeparator(root);return path.Equals(root,StringComparison.OrdinalIgnoreCase)||path.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||path.StartsWith(root+Path.AltDirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);}
 private static void RejectLinks(string path)
 {
  for(string? current=Path.GetFullPath(path);current!=null;current=Path.GetDirectoryName(current))
  {
   try{if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException(T("Links in the previous library path are not followed."));}
   catch(Exception error)when(error is FileNotFoundException or DirectoryNotFoundException)
   {if(new FileInfo(current).LinkTarget!=null||new DirectoryInfo(current).LinkTarget!=null)throw new InvalidDataException(T("A previous library path contains an unreachable link."));}
  }
 }
 private static void DeleteOwnedWork(string work,string parent)
 {
  var full=Path.GetFullPath(work);var owner=Path.GetFullPath(parent);
  if(!Within(full,owner)||!Path.GetFileName(full).StartsWith(".flixypix-settings-",StringComparison.Ordinal))throw new IOException("Unexpected transition work directory.");
  if(!Directory.Exists(full))return;Remove(full);
  static void Remove(string path)
  {FileAccessPolicy.Current.EnsureWritable(path);if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new
   IOException("Unexpected link in transition work.");foreach(var entry in new
   DirectoryInfo(path).EnumerateFileSystemInfos()){FileAccessPolicy.Current.EnsureWritable(entry.FullName);if(entry is
   DirectoryInfo)Remove(entry.FullName);else{if((entry.Attributes&FileAttributes.ReparsePoint)!=0)throw new
   IOException("Unexpected file link.");File.Delete(entry.FullName);}}Directory.Delete(path,false);}
 }
}

public sealed partial class CatalogRepository
{
 internal void RemapManagedLibraryPaths(string previous,string current,CancellationToken token)
 {
  lock(gate)
  {
   ThrowIfDisposed();using var tx=connection.BeginTransaction();
   using(var select=Command("SELECT document FROM media ORDER BY id",tx))using(var reader=select.ExecuteReader())while(reader.Read())
   {
    token.ThrowIfCancellationRequested();var item=JsonSerializer.Deserialize<MediaItem>(reader.GetString(0),Json)!;
    var extra=new Dictionary<string,string?>(item.Extra);if(extra.TryGetValue("archivePath",out var archive)&&archive!=null)extra["archivePath"]=SettingsTransition.Remap(archive,previous,current);
    UpsertCore(item with{Path=SettingsTransition.Remap(item.Path,previous,current),ThumbnailPath=item.ThumbnailPath==null?null:SettingsTransition.Remap(item.ThumbnailPath,previous,current),Extra=extra},tx);
   }
   var preferences=ReadPreferences(tx);SaveDocument("preferences",Guid.Empty,preferences with{FolderFavorites=preferences.FolderFavorites.Select(f=>f with{Path=SettingsTransition.Remap(f.Path,previous,current)}).ToList()},tx);
   token.ThrowIfCancellationRequested();tx.Commit();
  }
 }
}
