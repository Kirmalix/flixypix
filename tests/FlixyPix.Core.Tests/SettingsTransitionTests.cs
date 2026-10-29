using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;
public sealed class SettingsTransitionTests:IDisposable
{
 private readonly string root=Path.Combine(Path.GetTempPath(),"flixypix-transition-"+Guid.NewGuid());
 private string Previous=>Path.Combine(root,"previous");private string Current=>Path.Combine(root,"FlixyPix");
 private string OldLibrary=>Path.Combine(Previous,"Library");private string NewLibrary=>Path.Combine(Current,"Library");
 public SettingsTransitionTests()=>Directory.CreateDirectory(root);
 public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
 private void Settings(string? library=null)
 {Directory.CreateDirectory(Previous);File.WriteAllText(Path.Combine(Previous,"appsettings.json"),JsonSerializer.Serialize(new{DataDirectory=library??OldLibrary,Mpv=@"C:\tools\mpv.exe",ViewMode=1,ProtectedSourcePaths=new[]{@"U:\"},FutureSetting="preserved"}));}
 [Fact]public void FreshProfileNeedsNoMigrationAndCreatesNoDirectory()
 {var result=SettingsTransition.EnsureMigrated(Previous,Current);Assert.False(result.Migrated);Assert.False(Directory.Exists(Current));}
 [Fact]public void ExistingNewSettingsWinWithoutReadingBrokenPreviousSettings()
 {Settings();File.WriteAllText(Path.Combine(Previous,"appsettings.json"),"broken");Directory.CreateDirectory(Current);var
  path=Path.Combine(Current,"appsettings.json");File.WriteAllText(path,"new settings");Assert.False(SettingsTransition.EnsureMigrated(Previous,Current).Migrated);Assert.Equal("new settings",File.ReadAllText(path));}
 [Fact]public void CustomOfflineLibraryIsRetainedWithoutDriveAccess()
 {Settings(@"U:\offline-library");var
  result=SettingsTransition.EnsureMigrated(Previous,Current);Assert.True(result.Migrated);Assert.False(result.CopiedLibrary);var
  json=JsonNode.Parse(File.ReadAllText(result.SettingsPath))!;Assert.Equal(@"U:\offline-library",json["DataDirectory"]!.GetValue<string>());Assert.Equal("preserved",json["FutureSetting"]!.GetValue<string>());Assert.False(Directory.Exists(NewLibrary));Assert.True(File.Exists(Path.Combine(Previous,"appsettings.json")));}
 [Fact]public void ManagedLibraryCopiesBytesAndRemapsOnlyOwnedLiveReferences()
 {
  Settings();Directory.CreateDirectory(Path.Combine(OldLibrary,"archive-cache"));Directory.CreateDirectory(Path.Combine(OldLibrary,"thumbnails"));
  var image=Path.Combine(OldLibrary,"archive-cache","cached.png");var thumb=Path.Combine(OldLibrary,"thumbnails","thumb.png");File.WriteAllBytes(image,[1,2,3]);File.WriteAllBytes(thumb,[4,5,6]);
  Guid id;using(var db=new CatalogRepository(Path.Combine(OldLibrary,"catalog.db")))
  {id=db.Upsert(new(){Path=image,ThumbnailPath=thumb,Kind=MediaKind.Image,Rating=9,Tags=["Keep"],Extra=new(){["archivePath"]=Path.Combine(OldLibrary,"original.zip"),["legacy.values"]="historical:"+image}}).Id;db.Upsert(new(){Path=@"U:\offline\source.mp4",Kind=MediaKind.Video});db.UpsertBookmark(new(){MediaId=id,PositionSeconds=0});}
  var queue=Path.Combine(OldLibrary,"media-jobs.json");File.WriteAllText(queue,JsonSerializer.Serialize(new{Format=ProductIdentity.PreviousFormatPrefix+"-media-queue",Version=1,Jobs=new[]{new{Input=image,Output=Path.Combine(OldLibrary,"new.mp4"),State="Running"}}}));
  var oldSettings=File.ReadAllBytes(Path.Combine(Previous,"appsettings.json"));var dbHash=SHA256.HashData(File.ReadAllBytes(Path.Combine(OldLibrary,"catalog.db")));var queueBytes=File.ReadAllBytes(queue);
  var result=SettingsTransition.EnsureMigrated(Previous,Current);Assert.True(result.Migrated);Assert.True(result.CopiedLibrary);Assert.Equal(oldSettings,File.ReadAllBytes(Path.Combine(Previous,"appsettings.json")));Assert.Equal(dbHash,SHA256.HashData(File.ReadAllBytes(Path.Combine(OldLibrary,"catalog.db"))))
   ;Assert.Equal(queueBytes,File.ReadAllBytes(queue));
  Assert.Equal(new byte[]{1,2,3},File.ReadAllBytes(Path.Combine(NewLibrary,"archive-cache","cached.png")));
  using var target=new CatalogRepository(Path.Combine(NewLibrary,"catalog.db"));var
   item=target.GetMedia(id)!;Assert.Equal(Path.Combine(NewLibrary,"archive-cache","cached.png"),item.Path);Assert.Equal(Path.Combine(NewLibrary,"thumbnails","thumb.png"),item.ThumbnailPath);Assert.Equal("historical:"+image,item.Extra["legacy.values"]);Assert.Equal(Path.Combine(NewLibrary,"original.zip"),item.Extra["archivePath"]);Assert.Equal(9,item.Rating);Assert.Contains("Keep",item.Tags);Assert.Single(target.GetBookmarks());Assert.True(target.CheckIntegrity().IsValid);Assert.Contains(target.QueryPage(new()).Items,m=>m.Path==@"U:\offline\source.mp4");
  var copiedQueue=JsonNode.Parse(File.ReadAllText(Path.Combine(NewLibrary,"media-jobs.json")))!;Assert.Equal("flixypix-media-queue",copiedQueue["Format"]!.GetValue<string>());Assert.Equal(item.Path,copiedQueue["Jobs"]![0]!["Input"]!.GetValue<string>());Assert.Equal("Running",copiedQueue["Jobs"]![0]!["State"]!.GetValue<string>());
  Assert.False(SettingsTransition.EnsureMigrated(Previous,Current).Migrated);
 }
 [Fact]public void CommittedWalContentsAreIncludedWithoutOpeningTheOriginalProvider()
 {
  Settings();Directory.CreateDirectory(OldLibrary);var path=Path.Combine(OldLibrary,"catalog.db");using(var init=new CatalogRepository(path)){}
  using var connection=new SqliteConnection($"Data Source={path};Pooling=False");connection.Open();using(var
   setup=connection.CreateCommand()){setup.CommandText="PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE transition_probe(value TEXT); INSERT INTO transition_probe VALUES('committed in WAL');";setup.ExecuteNonQuery();}
  Assert.True(File.Exists(path+"-wal"));SettingsTransition.EnsureMigrated(Previous,Current);
  using var copied=new SqliteConnection($"Data Source={Path.Combine(NewLibrary,"catalog.db")};Pooling=False");copied.Open();using var verify=copied.CreateCommand();verify.CommandText="SELECT value FROM transition_probe";Assert.Equal("committed in WAL",verify.ExecuteScalar());
 }
 [Fact]public void DefaultLibraryWithoutSettingsIsDiscovered()
 {Directory.CreateDirectory(OldLibrary);using(var db=new
  CatalogRepository(Path.Combine(OldLibrary,"catalog.db")))db.Upsert(new(){Path=@"U:\offline\kept.jpg"});var
  result=SettingsTransition.EnsureMigrated(Previous,Current);Assert.True(result.CopiedLibrary);using var copied=new
  CatalogRepository(Path.Combine(NewLibrary,"catalog.db"));Assert.Single(copied.QueryPage(new()).Items);}
 [Fact]public void ExistingDestinationIsNeverMergedOrOverwritten()
 {Settings();Directory.CreateDirectory(Current);var
  sentinel=Path.Combine(Current,"sentinel.txt");File.WriteAllText(sentinel,"keep");Assert.Throws<IOException>(()=>SettingsTransition.EnsureMigrated(Previous,Current));Assert.Equal("keep",File.ReadAllText(sentinel));Assert.False(File.Exists(Path.Combine(Current,"appsettings.json")));}
 [Fact]public void MalformedSettingsAndCancellationLeavePreviousDataIntact()
 {Settings();var settings=Path.Combine(Previous,"appsettings.json");File.WriteAllText(settings,"{broken");Assert.ThrowsAny<JsonException>(()=>SettingsTransition.EnsureMigrated(Previous,Current));Assert.False(Directory.Exists(Current));Settings();using
  var cancel=new CancellationTokenSource();cancel.Cancel();Assert.Throws<OperationCanceledException>(()=>SettingsTransition.EnsureMigrated(Previous,Current,cancel.Token));Assert.False(Directory.Exists(Current));}
 [Fact]public void CopyFailureLeavesNoPublishedSettingsOrPartialLibrary()
 {Settings();Directory.CreateDirectory(OldLibrary);File.WriteAllText(Path.Combine(OldLibrary,"catalog.db"),"not a SQLite database");var
  settings=File.ReadAllBytes(Path.Combine(Previous,"appsettings.json"));Assert.ThrowsAny<Exception>(()=>SettingsTransition.EnsureMigrated(Previous,Current));Assert.False(Directory.Exists(Current));Assert.Empty(Directory.GetDirectories(root,".flixypix-settings-*"));Assert.Equal(settings,File.ReadAllBytes(Path.Combine(Previous,"appsettings.json")));}
 [Fact]public void LibraryJunctionIsRejectedWithoutReadingOrChangingItsTarget()
 {
  Settings();Directory.CreateDirectory(OldLibrary);var outside=Path.Combine(root,"separate-source");Directory.CreateDirectory(outside);var sentinel=Path.Combine(outside,"keep.txt");File.WriteAllText(sentinel,"unchanged");var alias=Path.Combine(OldLibrary,"linked-cache");
  var start=new System.Diagnostics.ProcessStartInfo("cmd.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};foreach(var
   value in new[]{"/d","/c","mklink","/J",alias,outside})start.ArgumentList.Add(value);using(var process=System.Diagnostics.Process.Start(start)!){var
   output=process.StandardOutput.ReadToEnd();var
   error=process.StandardError.ReadToEnd();process.WaitForExit();Assert.True(process.ExitCode==0,output+error);}
  try{Assert.Throws<InvalidDataException>(()=>SettingsTransition.EnsureMigrated(Previous,Current));Assert.False(Directory.Exists(Current));Assert.Equal("unchanged",File.ReadAllText(sentinel));Assert.Empty(Directory.GetDirectories(root,".flixypix-settings-*"));}
  finally{Directory.Delete(alias,false);}
 }
}
