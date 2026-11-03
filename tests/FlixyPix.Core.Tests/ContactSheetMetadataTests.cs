using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;

public sealed class ContactSheetMetadataTests:IDisposable
{
 private readonly string root=Path.Combine(Path.GetTempPath(),"flixypix-contact-metadata-"+Guid.NewGuid().ToString("N"));
 private string Database=>Path.Combine(root,"catalog.db");
 private const string Secret="synthetic-contact-sheet-password";
 public ContactSheetMetadataTests()=>Directory.CreateDirectory(root);
 public void Dispose(){SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
 private CatalogKey? Prepare(bool encrypted)
 {using(var empty=new CatalogRepository(Database)){}if(!encrypted)return null;CatalogEncryption.EnableEncryption(Database,Secret);return CatalogEncryption.Unlock(Database,Secret);}
 private string Fixture(string name,byte[] bytes)
 {var path=Path.Combine(root,name);File.WriteAllBytes(path,bytes);File.SetLastWriteTimeUtc(path,new DateTime(2001,2,3,4,5,6,DateTimeKind.Utc));return path;}
 private static ContactSheetMetadata Metadata(string source,string image)=>new(200,100,Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(image))),source,new FileInfo(source).Length,File.GetLastWriteTimeUtc(source),
  [new(0,new(10,20,80,70),1.25),new(1,new(110,20,80,70),7.5)]);
 private (MediaItem Item,ContactSheetMetadata Sheet) Add(CatalogRepository db)
 {
  var source=Fixture("video.mp4",[1,2,3]);var image=Fixture("sheet.jpg",[7,8,9]);var sheet=Metadata(source,image);
  return(db.Upsert(new(){Path=source,Kind=MediaKind.Video,ThumbnailPath=image,ContactSheet=sheet,DurationSeconds=10}),sheet);
 }
 [Theory][InlineData(false)][InlineData(true)]
 public async Task PersistencePortableAndActualMediaRestorePreserveImageAndSourceBinding(bool encrypted)
 {
  using var key=Prepare(encrypted);Guid id;ContactSheetMetadata sheet;string image;
  using(var db=new CatalogRepository(Database,key))
  {
   var fixture=Add(db);id=fixture.Item.Id;sheet=fixture.Sheet;image=fixture.Item.ThumbnailPath!;
   var export=Path.Combine(root,"portable.json");db.Export(export);
   using(var imported=new CatalogRepository(Path.Combine(root,"imported.db")))
   {
    Assert.Empty(imported.ImportJson(export,CatalogRepository.MaximumPortableEntityBytes,secret:encrypted?Secret:null).Errors);
    var copy=imported.GetMedia(id)!;Assert.Equal(sheet.Cells,copy.ContactSheet!.Cells);Assert.True(copy.ContactSheet.MatchesImage(copy.ThumbnailPath!));Assert.True(copy.ContactSheet.MatchesSource(copy));
   }
   var package=Path.Combine(root,"backup.zip");await db.CreateBackupAsync(package,true);
   Assert.Equal(1,(await CatalogRepository.VerifyBackupAsync(package,secret:encrypted?Secret:null)).MediaFiles);
   var restored=await CatalogRepository.RestoreBackupAsync(package,Path.Combine(root,"restored"),secret:encrypted?Secret:null);
   using var restoredKey=encrypted?CatalogEncryption.Unlock(restored.DatabasePath,Secret):null;
   using var destination=new CatalogRepository(restored.DatabasePath,restoredKey);var after=destination.GetMedia(id)!;
   Assert.NotEqual(sheet.SourcePath,after.Path);Assert.Equal(after.Path,after.ContactSheet!.SourcePath);Assert.Equal(sheet.SourceModifiedAtUtc,new DateTimeOffset(File.GetLastWriteTimeUtc(after.Path)));
   Assert.True(after.ContactSheet.MatchesSource(after));Assert.True(after.ContactSheet.MatchesImage(after.ThumbnailPath!));Assert.Equal(File.ReadAllBytes(image),File.ReadAllBytes(after.ThumbnailPath!));Assert.Equal(sheet.Cells,after.ContactSheet.Cells);
  }
  using var reopened=new CatalogRepository(Database,key);var saved=reopened.GetMedia(id)!;Assert.Equal(sheet.ImageSha256,saved.ContactSheet!.ImageSha256);Assert.Equal(sheet.Cells,saved.ContactSheet.Cells);Assert.True(saved.ContactSheet.MatchesSource(saved));
 }
 [Theory][InlineData(false)]
 public void FreshThumbnailCommitKeepsConcurrentEditsAndRejectsStaleRendering(bool encrypted)
 {
  using var key=Prepare(encrypted);using var db=new CatalogRepository(Database,key);var(original,_)=Add(db);var source=PlaybackSource.Resolve(original,original.Path);
  using var second=new CatalogRepository(Database,key);second.Upsert(second.GetMedia(original.Id)! with {Description="Concurrent edit",Tags=["Retained"],Extra=new(){["independent"]="Retained"}});
  var nextImage=Fixture("next.jpg",[9,8,7]);var metadata=Metadata(original.Path,nextImage);
  var saved=db.SetGeneratedThumbnail(original,source,nextImage,metadata,"style-next");Assert.Equal("Concurrent edit",saved.Description);Assert.Equal(["Retained"],saved.Tags);Assert.Equal("Retained",saved.Extra["independent"]);
  Assert.Equal("style-next",saved.Extra[CatalogRepository.GeneratedThumbnailStyleKey]);Assert.Equal(nextImage,saved.ThumbnailPath);
  Assert.Throws<InvalidOperationException>(()=>db.SetGeneratedThumbnail(original,source,original.ThumbnailPath!,original.ContactSheet,"stale"));
  File.SetLastWriteTimeUtc(original.Path,new DateTime(2002,2,3,4,5,6,DateTimeKind.Utc));
  Assert.Throws<InvalidOperationException>(()=>db.SetGeneratedThumbnail(saved,source,nextImage,metadata,"changed-source"));Assert.False(metadata.MatchesSource(db.GetMedia(original.Id)!));
  Assert.Equal("style-next",db.GetMedia(original.Id)!.Extra[CatalogRepository.GeneratedThumbnailStyleKey]);
 }
 [Theory][InlineData(false)]
 public void PromotedMainKeepsSheetVersionUntilThatVersionIsDetached(bool encrypted)
 {
  using var key=Prepare(encrypted);using var db=new CatalogRepository(Database,key);var(item,sheet)=Add(db);var source=PlaybackSource.Resolve(item,item.Path);
  var alternative=db.Upsert(new(){Path=Fixture("alternative.mp4",[4,5,6]),Kind=MediaKind.Video});db.MergeVersions(item.Id,[alternative.Id]);
  var promoted=db.SetMainVersion(item.Id,alternative.Path);Assert.True(promoted.ContactSheet!.MatchesSource(promoted));Assert.Equal(item.Path,promoted.ContactSheet.SourcePath);
  Assert.Throws<InvalidOperationException>(()=>db.SetGeneratedThumbnail(item,source,item.ThumbnailPath!,sheet,"stale-main"));
  db.SplitVersion(item.Id,item.Path);Assert.Null(db.GetMedia(item.Id)!.ContactSheet);Assert.False(sheet.MatchesSource(db.GetMedia(item.Id)!));
 }
 [Theory][InlineData(false)]
 public void InvalidLayoutOrHashCannotBePublishedOrPartiallyImported(bool encrypted)
 {
  using var key=Prepare(encrypted);using var db=new CatalogRepository(Database,key);var(item,sheet)=Add(db);
  var invalid=new[]{sheet with {ImageSha256="bad"},sheet with {Width=0},sheet with {Cells=[new(0,new(10,20,200,70),1)]},sheet with {Cells=[new(0,new(10,20,80,70),1),new(1,new(20,20,80,70),2)]},sheet with {Cells=[new(1,new(10,20,80,70),1)]}};
  foreach(var value in invalid)
  {
   Assert.Throws<ArgumentException>(()=>db.Upsert(item with {ContactSheet=value}));
   var export=Path.Combine(root,"invalid.json");File.WriteAllText(export,JsonSerializer.Serialize(new {format="flixypix-catalog",version=4,items=new[]{item with {Description="Must roll back"},item with {Id=Guid.NewGuid(),Path=Path.Combine(root,"invalid.mp4"),ContactSheet=value}}},CatalogRepository.Json));
   Assert.NotEmpty(db.ImportJson(export).Errors);Assert.Null(db.GetMedia(item.Id)!.Description);
  }
  Assert.Throws<ArgumentException>(()=>(sheet with {Cells=[new(0,new(10,20,80,70),double.NaN)]}).Validate());
  File.WriteAllBytes(item.ThumbnailPath!,[1,1,1]);Assert.False(sheet.MatchesImage(item.ThumbnailPath!));
  Assert.Throws<ArgumentException>(()=>db.SetGeneratedThumbnail(item,PlaybackSource.Resolve(item,item.Path),item.ThumbnailPath!,sheet,"bad-image"));
 }
 [Fact]
 public void UniformHitMappingExcludesLetterboxHeaderGapsAndTrailingEdges()
 {
  var sheet=Metadata(Fixture("video.mp4",[1]),Fixture("sheet.jpg",[2]));
  Assert.Equal(0,sheet.HitTestView(20,140,400,400)!.Index);Assert.Equal(1,sheet.HitTestView(379,279,400,400)!.Index);
  foreach(var(x,y)in new[]{(10d,180d),(200d,180d),(100d,120d),(100d,300d),(380d,180d),(100d,50d),(double.NaN,180d)})Assert.Null(sheet.HitTestView(x,y,400,400));
  Assert.Equal(1,sheet.HitTestView(57.5,30,100,50)!.Index);
  Assert.Null(sheet.HitTestView(10,10,0,400));Assert.Null(sheet.HitTestView(10,10,double.PositiveInfinity,400));
 }
 [Theory][InlineData(false)]
 public void BackgroundPreparationPublishesMetadataAndRetainsConcurrentThumbnailAndTags(bool encrypted)
 {
  using var key=Prepare(encrypted);using var db=new CatalogRepository(Database,key);var(item,_)=Add(db);var source=PlaybackSource.Resolve(item,item.Path);
  var next=Fixture("prepared.jpg",[9,7,8]);var sheet=Metadata(item.Path,next);var prepared=item with {ThumbnailPath=next,ContactSheet=sheet,Width=320,Height=180,Extra=new(){[CatalogRepository.GeneratedThumbnailStyleKey]="prepared"}};
  using var second=new CatalogRepository(Database,key);second.Upsert(second.GetMedia(item.Id)! with {Description="Fresh",Tags=["Fresh"]});
  var saved=db.SetPreparedMedia(item,prepared,source.Size,source.ModifiedAtUtc)!;Assert.Equal("Fresh",saved.Description);Assert.Equal(["Fresh"],saved.Tags);Assert.Equal(next,saved.ThumbnailPath);Assert.Equal(sheet,saved.ContactSheet);
  var custom=Fixture("custom.jpg",[5,5,5]);second.Upsert(second.GetMedia(item.Id)! with {ThumbnailPath=custom,ContactSheet=null,Extra=new(){["thumbnail.assetId"]="chosen"}});
  var after=db.SetPreparedMedia(item,prepared,source.Size,source.ModifiedAtUtc)!;Assert.Equal(custom,after.ThumbnailPath);Assert.Null(after.ContactSheet);Assert.Equal("chosen",after.Extra["thumbnail.assetId"]);
  File.SetLastWriteTimeUtc(item.Path,new DateTime(2002,1,1,0,0,0,DateTimeKind.Utc));Assert.Null(db.SetPreparedMedia(item,prepared,source.Size,source.ModifiedAtUtc));
 }
 [Theory][InlineData(false)]
 public void RelocationUpdatesCurrentSourceButRetainsHistoricalProvenance(bool encrypted)
 {
  using var key=Prepare(encrypted);using var db=new CatalogRepository(Database,key);var(item,_)=Add(db);db.Upsert(item with {Extra=new(){["origin.path"]=item.Path}});
  var target=Path.Combine(root,"moved");db.RelocateStoragePaths([new(root,target)]);var after=db.GetMedia(item.Id)!;
  Assert.Equal(Path.Combine(target,"video.mp4"),after.ContactSheet!.SourcePath);Assert.Equal(item.Path,after.Extra["origin.path"]);Assert.Equal(Path.Combine(target,"sheet.jpg"),after.ThumbnailPath);
 }
 [Fact]
 public async Task OldBackupWithoutMtimeStaysReadableButDoesNotTrustRestoredSheetSource()
 {
  using var db=new CatalogRepository(Database);var(item,_)=Add(db);var package=Path.Combine(root,"old.zip");await db.CreateBackupAsync(package,true);
  using(var zip=ZipFile.Open(package,ZipArchiveMode.Update))
  {
   var entry=zip.GetEntry("manifest.ndjson")!;string text;using(var reader=new StreamReader(entry.Open()))text=reader.ReadToEnd();entry.Delete();
   using var writer=new StreamWriter(zip.CreateEntry("manifest.ndjson").Open());
   foreach(var line in text.Split('\n',StringSplitOptions.RemoveEmptyEntries)){var record=JsonNode.Parse(line)!.AsObject();record.Remove("modifiedAtUtc");writer.WriteLine(record.ToJsonString());}
  }
  var restored=await CatalogRepository.RestoreBackupAsync(package,Path.Combine(root,"old-restored"));using var destination=new CatalogRepository(restored.DatabasePath);var after=destination.GetMedia(item.Id)!;
  Assert.True(after.ContactSheet!.MatchesImage(after.ThumbnailPath!));Assert.False(after.ContactSheet.MatchesSource(after));
 }
 [Theory][InlineData(false)]
 public void FailedThumbnailWriteRollsBackImageMetadataAndStyle(bool encrypted)
 {
  using var key=Prepare(encrypted);using var db=new CatalogRepository(Database,key);var(item,_)=Add(db);var next=Fixture("next.jpg",[3,8,1]);var sheet=Metadata(item.Path,next);
  using var raw=new SqliteConnection(CatalogEncryption.ConnectionString(Database,key).ToString());raw.Open();CatalogRepository.AddOrdinalCollation(raw);
  using var trigger=raw.CreateCommand();trigger.CommandText="CREATE TRIGGER reject_sheet AFTER UPDATE ON media BEGIN SELECT RAISE(ABORT,'synthetic thumbnail failure'); END;";trigger.ExecuteNonQuery();
  Assert.Throws<SqliteException>(()=>db.SetGeneratedThumbnail(item,PlaybackSource.Resolve(item,item.Path),next,sheet,"not-published"));
  var after=db.GetMedia(item.Id)!;Assert.Equal(item.ThumbnailPath,after.ThumbnailPath);Assert.Equal(item.ContactSheet!.ImageSha256,after.ContactSheet!.ImageSha256);Assert.False(after.Extra.ContainsKey(CatalogRepository.GeneratedThumbnailStyleKey));
 }
 [Fact]
 public async Task ImpossibleManifestTimestampRejectsBeforePublishingRestore()
 {
  using var db=new CatalogRepository(Database);Add(db);var package=Path.Combine(root,"bad-time.zip");await db.CreateBackupAsync(package,true);
  using(var zip=ZipFile.Open(package,ZipArchiveMode.Update))
  {
   var entry=zip.GetEntry("manifest.ndjson")!;string text;using(var reader=new StreamReader(entry.Open()))text=reader.ReadToEnd();entry.Delete();
   using var writer=new StreamWriter(zip.CreateEntry("manifest.ndjson").Open());
   foreach(var line in text.Split('\n',StringSplitOptions.RemoveEmptyEntries)){var record=JsonNode.Parse(line)!.AsObject();if((string?)record["type"]=="file")record["modifiedAtUtc"]="0001-01-01T00:00:00+00:00";writer.WriteLine(record.ToJsonString());}
  }
  var target=Path.Combine(root,"rejected-time");await Assert.ThrowsAsync<InvalidDataException>(()=>CatalogRepository.RestoreBackupAsync(package,target));Assert.False(Directory.Exists(target));Assert.Empty(Directory.GetDirectories(root,".flixypix-restore-*"));
 }
}
