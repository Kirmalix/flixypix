using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class VersionBackupTests:IDisposable
{
 private readonly string root=Path.Combine(Path.GetTempPath(),"flixypix-version-backup-"+Guid.NewGuid().ToString("N"));
 private const string Secret="synthetic-version-backup-password";
 public VersionBackupTests()=>Directory.CreateDirectory(root);
 public void Dispose(){SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
 private string PathOf(string relative)=>Path.Combine(root,relative);
 private string FileAt(string relative,byte[] bytes)
 {var path=PathOf(relative);Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllBytes(path,bytes);File.SetLastWriteTimeUtc(path,new DateTime(2003,4,5,6,7,8,DateTimeKind.Utc));return path;}
 private CatalogKey? Prepare(bool encrypted)
 {var path=PathOf("catalog.db");using(var db=new CatalogRepository(path)){}if(!encrypted)return null;CatalogEncryption.EnableEncryption(path,Secret);return CatalogEncryption.Unlock(path,Secret);}
 private MediaItem Add(CatalogRepository db)
 {
  var first=FileAt("first/same.mp4",[1,2,3]);var second=FileAt("second/same.mp4",[4,5,6,7]);var third=FileAt("third/same.mp4",[8,9]);var image=FileAt("sheet.jpg",[7,7,7]);
  var stamp=new DateTimeOffset(File.GetLastWriteTimeUtc(second));
  return db.Upsert(new(){Path=first,Kind=MediaKind.Video,DurationSeconds=30,ThumbnailPath=image,Playback=new(){Speed=1.5,AudioDelaySeconds=.4},
   Files=[new(){Path=second,DurationSeconds=40,AudioTrack=new(){Mode=AudioTrackMode.Track,TrackId=2,SourceSize=4,SourceModifiedAtUtc=stamp}},new(){Path=third,DurationSeconds=50}],
   ContactSheet=new(100,100,Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(image))),second,4,stamp,[new(0,new(0,0,100,100),10)])});
 }
 [Theory][InlineData(false)][InlineData(true)]
 public async Task AllVersionsRestoreIntoNewPathsWithHashTimestampTrackAndSheetBinding(bool encrypted)
 {
  using var key=Prepare(encrypted);using var db=new CatalogRepository(PathOf("catalog.db"),key);var item=Add(db);var originals=MediaVersions.All(item).Select(file=>(file.Path,Bytes:File.ReadAllBytes(file.Path),Stamp:File.GetLastWriteTimeUtc(file.Path))).ToArray();
  var package=PathOf("complete.backup");var backup=await db.CreateBackupAsync(package,true);Assert.Empty(backup.Warnings);
  var verified=await CatalogRepository.VerifyBackupAsync(package,secret:encrypted?Secret:null);Assert.Equal(3,verified.MediaFiles);Assert.Equal(1,verified.Media);
  var result=await CatalogRepository.RestoreBackupAsync(package,PathOf("restored"),secret:encrypted?Secret:null);Assert.Equal(3,result.RestoredMediaFiles);
  using var restoredKey=encrypted?CatalogEncryption.Unlock(result.DatabasePath,Secret):null;using var destination=new CatalogRepository(result.DatabasePath,restoredKey);var copy=destination.GetMedia(item.Id)!;var files=MediaVersions.All(copy);
  Assert.Equal(3,files.Count);Assert.Equal(3,files.Select(file=>file.Path).Distinct().Count());
  for(var i=0;i<files.Count;i++)
  {
   Assert.StartsWith(result.DirectoryPath,files[i].Path);Assert.NotEqual(originals[i].Path,files[i].Path);Assert.Equal(originals[i].Bytes,File.ReadAllBytes(files[i].Path));Assert.Equal(originals[i].Stamp,File.GetLastWriteTimeUtc(files[i].Path));Assert.Equal(originals[i].Bytes,File.ReadAllBytes(originals[i].Path));
  }
  Assert.Equal(item.Playback,copy.Playback);Assert.Equal(item.Files[0].AudioTrack,copy.Files[0].AudioTrack);Assert.Equal(copy.Files[0].Path,copy.ContactSheet!.SourcePath);Assert.True(copy.ContactSheet.MatchesSource(copy));Assert.True(copy.ContactSheet.MatchesImage(copy.ThumbnailPath!));
  Assert.Equal(item.Files.Select(file=>file.Path),db.GetMedia(item.Id)!.Files.Select(file=>file.Path));await Assert.ThrowsAsync<IOException>(()=>CatalogRepository.RestoreBackupAsync(package,result.DirectoryPath,secret:encrypted?Secret:null));
 }
 [Theory][InlineData(false)][InlineData(true)]
 public async Task CatalogOnlyAndMissingVersionsKeepTheirOriginalPathsAndReportPrecisely(bool encrypted)
 {
  using var key=Prepare(encrypted);using var db=new CatalogRepository(PathOf("catalog.db"),key);var item=Add(db);var package=PathOf("catalog-only.backup");await db.CreateBackupAsync(package,false);
  var restored=await CatalogRepository.RestoreBackupAsync(package,PathOf("catalog-only"),secret:encrypted?Secret:null);
  using(var restoredKey=encrypted?CatalogEncryption.Unlock(restored.DatabasePath,Secret):null)
  using(var destination=new CatalogRepository(restored.DatabasePath,restoredKey))
  {Assert.Equal(0,restored.RestoredMediaFiles);Assert.Equal(MediaVersions.All(item).Select(file=>file.Path),MediaVersions.All(destination.GetMedia(item.Id)!).Select(file=>file.Path));}
  File.Delete(item.Files[0].Path);package=PathOf("missing.backup");var backup=await db.CreateBackupAsync(package,true);Assert.Single(backup.Warnings);Assert.Contains(item.Files[0].Path,backup.Warnings[0]);
  Assert.Equal(2,(await CatalogRepository.VerifyBackupAsync(package,secret:encrypted?Secret:null)).MediaFiles);
  var partial=await CatalogRepository.RestoreBackupAsync(package,PathOf("missing-restored"),secret:encrypted?Secret:null);Assert.Single(partial.Warnings);Assert.Equal(2,partial.RestoredMediaFiles);
  using var partialKey=encrypted?CatalogEncryption.Unlock(partial.DatabasePath,Secret):null;using var partialDb=new CatalogRepository(partial.DatabasePath,partialKey);var after=partialDb.GetMedia(item.Id)!;
  Assert.Equal(item.Files[0].Path,after.Files[0].Path);Assert.False(after.ContactSheet!.MatchesSource(after));Assert.True(File.Exists(after.Files[1].Path));
 }
 private static void RewriteManifest(string package,Action<List<JsonObject>> change)
 {
  using var zip=ZipFile.Open(package,ZipArchiveMode.Update);var entry=zip.GetEntry("manifest.ndjson")!;string text;using(var reader=new StreamReader(entry.Open()))text=reader.ReadToEnd();entry.Delete();
  var records=text.Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(line=>JsonNode.Parse(line)!.AsObject()).ToList();change(records);
  using var writer=new StreamWriter(zip.CreateEntry("manifest.ndjson").Open());foreach(var record in records)writer.WriteLine(record.ToJsonString());
 }
 [Theory][InlineData("index")][InlineData("duplicate")][InlineData("path")][InlineData("invalid-path")][InlineData("missing-index")][InlineData("index-on-thumbnail")][InlineData("unknown-missing")]
 public async Task InvalidVersionAssignmentsRejectVerifyAndRestoreWithoutPublication(string kind)
 {
  using var db=new CatalogRepository(PathOf("catalog.db"));Add(db);var package=PathOf("bad.backup");await db.CreateBackupAsync(package,true);
  RewriteManifest(package,records=>
  {
   var media=records.Where(record=>(string?)record["kind"]=="media").ToArray();
   switch(kind)
   {
    case "index":media[1]["versionIndex"]=99;media[1]["name"]="media/invalid/version-99/content.mp4";break;
    case "duplicate":media[1]["versionIndex"]=0;break;
    case "path":media[1]["sourcePath"]=PathOf("unrelated.mp4");break;
    case "invalid-path":media[1]["sourcePath"]="bad\0path";break;
    case "missing-index":media[1].Remove("versionIndex");break;
    case "index-on-thumbnail":records.Single(record=>(string?)record["kind"]=="thumbnail")["versionIndex"]=0;break;
    case "unknown-missing":records.Insert(1,new JsonObject{["type"]="missing",["kind"]="media",["mediaId"]=Guid.NewGuid().ToString(),["versionIndex"]=0,["sourcePath"]=PathOf("missing.mp4")});break;
   }
  });
  await Assert.ThrowsAsync<InvalidDataException>(()=>CatalogRepository.VerifyBackupAsync(package));var target=PathOf("not-published");await Assert.ThrowsAsync<InvalidDataException>(()=>CatalogRepository.RestoreBackupAsync(package,target));Assert.False(Directory.Exists(target));Assert.Empty(Directory.GetDirectories(root,".flixypix-restore-*"));
 }
 [Fact]
 public async Task Version1MainOnlyPackagesRemainReadable()
 {
  using var db=new CatalogRepository(PathOf("catalog.db"));var path=FileAt("old.mp4",[3,2,1]);var item=db.Upsert(new(){Path=path,Kind=MediaKind.Video});var package=PathOf("old.backup");await db.CreateBackupAsync(package,true);
  RewriteManifest(package,records=>{records[0]["version"]=1;foreach(var record in records)record.Remove("versionIndex");});
  Assert.Equal(1,(await CatalogRepository.VerifyBackupAsync(package)).MediaFiles);var result=await CatalogRepository.RestoreBackupAsync(package,PathOf("old-restored"));using var destination=new CatalogRepository(result.DatabasePath);Assert.Equal(File.ReadAllBytes(path),File.ReadAllBytes(destination.GetMedia(item.Id)!.Path));
 }
 [Fact]
 public async Task OldMainOnlySnapshotRetainsUncontainedVersionPaths()
 {
  using var db=new CatalogRepository(PathOf("catalog.db"));var item=Add(db);var package=PathOf("legacy-multiple.backup");await db.CreateBackupAsync(package,true);
  using(var zip=ZipFile.Open(package,ZipArchiveMode.Update))foreach(var entry in zip.Entries.Where(entry=>entry.FullName.Contains("/version-",StringComparison.Ordinal)).ToArray())entry.Delete();
  RewriteManifest(package,records=>
  {
   records.RemoveAll(record=>(int?)record["versionIndex"] is >0);records[0]["version"]=1;foreach(var record in records)record.Remove("versionIndex");
   var files=records.Where(record=>(string?)record["type"]=="file").ToArray();records[^1]["fileCount"]=files.Length;records[^1]["size"]=files.Sum(record=>(long)record["size"]!);
  });
  var result=await CatalogRepository.RestoreBackupAsync(package,PathOf("legacy-multiple-restored"));Assert.Equal(1,result.RestoredMediaFiles);using var destination=new CatalogRepository(result.DatabasePath);var after=destination.GetMedia(item.Id)!;
  Assert.NotEqual(item.Path,after.Path);Assert.Equal(item.Files.Select(file=>file.Path),after.Files.Select(file=>file.Path));Assert.Equal(item.ContactSheet!.SourcePath,after.ContactSheet!.SourcePath);
 }
 [Theory][InlineData(false)][InlineData(true)]
 public async Task CancellationWhileRestoringAdditionalVersionLeavesNoPublishedLibrary(bool encrypted)
 {
  using var key=Prepare(encrypted);using var db=new CatalogRepository(PathOf("catalog.db"),key);Add(db);var package=PathOf("complete.backup");await db.CreateBackupAsync(package,true);using var stop=new CancellationTokenSource();var target=PathOf("cancelled-restore");
  await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>CatalogRepository.RestoreBackupAsync(package,target,stop.Token,new Immediate<CatalogBackupProgress>(value=>{if(value.Name.Contains("/version-1/",StringComparison.Ordinal))stop.Cancel();}),secret:encrypted?Secret:null));
  Assert.False(Directory.Exists(target));Assert.Empty(Directory.GetDirectories(root,".flixypix-restore-*"));
 }
 [Fact]
 public async Task CorruptAdditionalFileFailsBeforeOpeningOrPublishingCatalog()
 {
  using var db=new CatalogRepository(PathOf("catalog.db"));Add(db);var package=PathOf("corrupt-version.backup");await db.CreateBackupAsync(package,true);
  using(var zip=ZipFile.Open(package,ZipArchiveMode.Update))
  {
   var entry=zip.Entries.Single(entry=>entry.FullName.Contains("/version-1/",StringComparison.Ordinal));var name=entry.FullName;entry.Delete();using var output=zip.CreateEntry(name).Open();output.Write(new byte[]{9,9,9,9});
  }
  await Assert.ThrowsAsync<InvalidDataException>(()=>CatalogRepository.VerifyBackupAsync(package));var target=PathOf("corrupt-restore");await Assert.ThrowsAsync<InvalidDataException>(()=>CatalogRepository.RestoreBackupAsync(package,target));Assert.False(Directory.Exists(target));
 }
 [Theory][InlineData(false)][InlineData(true)]
 public async Task CancellationDuringAdditionalFileDoesNotPublishAndLeavesSourceUntouched(bool encrypted)
 {
  using var key=Prepare(encrypted);using var db=new CatalogRepository(PathOf("catalog.db"),key);var item=Add(db);using var stop=new CancellationTokenSource();var package=PathOf("cancelled.backup");
  await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>db.CreateBackupAsync(package,true,stop.Token,new Immediate<CatalogBackupProgress>(value=>{if(value.Name==item.Files[0].Path)stop.Cancel();})));
  Assert.False(File.Exists(package));Assert.Empty(Directory.GetDirectories(root,".flixypix-backup-*"));Assert.Equal(new byte[]{4,5,6,7},File.ReadAllBytes(item.Files[0].Path));
 }
 private sealed class Immediate<T>(Action<T> action):IProgress<T>{public void Report(T value)=>action(value);}
}
