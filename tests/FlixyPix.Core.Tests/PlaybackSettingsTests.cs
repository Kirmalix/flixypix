using System.Text.Json;
using FlixyPix.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FlixyPix.Core.Tests;
public sealed class PlaybackSettingsTests:IDisposable
{
 private readonly string root=Path.Combine(Path.GetTempPath(),"flixypix-playback-"+Guid.NewGuid().ToString("N"));
 private string Database=>Path.Combine(root,"catalog.db");
 public PlaybackSettingsTests()=>Directory.CreateDirectory(root);
 public void Dispose(){SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
 private CatalogKey? Prepare(bool encrypted)
 {using(var empty=new CatalogRepository(Database)){}if(!encrypted)return null;CatalogEncryption.EnableEncryption(Database,"synthetic-playback-password");return CatalogEncryption.Unlock(Database,"synthetic-playback-password");}
 private MediaItem Add(CatalogRepository db,string name,double duration)
 {var file=Path.Combine(root,name);File.WriteAllBytes(file,[1,2,3]);return db.Upsert(new(){Path=file,Kind=MediaKind.Video,DurationSeconds=duration});}
 private static AudioTrackPreference Track(PlaybackSource source,int id)=>new(){Mode=AudioTrackMode.Track,TrackId=id,SourceSize=source.Size,SourceModifiedAtUtc=source.ModifiedAtUtc};
 [Theory][InlineData(false)][InlineData(true)]
 public void FreshFieldUpdatesRetainConcurrentMetadataAndPersist(bool encrypted)
 {
  using var key=Prepare(encrypted);Guid id;
  using(var db=new CatalogRepository(Database,key))
  {
   var item=Add(db,"one.mp4",20);id=item.Id;var source=PlaybackSource.Resolve(item,item.Path);
   using var second=new CatalogRepository(Database,key);second.Upsert(second.GetMedia(id)! with {Description="Concurrent description",Rating=9});
   db.SetMediaPlaybackSettings(id,new(){Speed=1.5,AudioDelaySeconds=-.2});db.SetVersionAudioTrack(source,Track(source,2));
   second.ChangeMediaPlaybackSettings(id,audioDelaySeconds:.4);db.ChangeMediaPlaybackSettings(id,speed:2);Assert.Equal(.4,db.GetMedia(id)!.Playback.AudioDelaySeconds);
   db.ChangeMediaPlaybackSettings(id,speed:1.5,audioDelaySeconds:-.2);
   Assert.Equal("Concurrent description",db.GetMedia(id)!.Description);Assert.Equal(9,db.GetMedia(id)!.Rating);
  }
  using var reopened=new CatalogRepository(Database,key);var saved=reopened.GetMedia(id)!;Assert.Equal(1.5,saved.Playback.Speed);Assert.Equal(-.2,saved.Playback.AudioDelaySeconds);Assert.Equal(2,saved.AudioTrack.TrackId);
 }
 [Theory][InlineData(false)][InlineData(true)]
 public void MergePromotionAndSplitKeepAudioAttachedToEachFile(bool encrypted)
 {
  using var key=Prepare(encrypted);using var db=new CatalogRepository(Database,key);var a=Add(db,"a.mp4",20);var b=Add(db,"b.mp4",35);
  db.SetMediaPlaybackSettings(a.Id,new(){Speed=2});var sa=PlaybackSource.Resolve(a,a.Path);var sb=PlaybackSource.Resolve(b,b.Path);db.SetVersionAudioTrack(sa,Track(sa,1));db.SetVersionAudioTrack(sb,Track(sb,3));
  var merged=db.MergeVersions(a.Id,[b.Id]);Assert.Equal(1,merged.AudioTrack.TrackId);Assert.Equal(3,merged.Files.Single().AudioTrack.TrackId);
  Assert.Equal(35,PlaybackSource.Resolve(merged,b.Path).DurationSeconds);
  var promoted=db.SetMainVersion(a.Id,b.Path);Assert.Equal(3,promoted.AudioTrack.TrackId);Assert.Equal(1,promoted.Files.Single().AudioTrack.TrackId);Assert.Equal(2,promoted.Playback.Speed);
  var split=db.SplitVersion(a.Id,a.Path);Assert.Equal(1,split.AudioTrack.TrackId);Assert.Equal(2,split.Playback.Speed);Assert.Equal(3,db.GetMedia(a.Id)!.AudioTrack.TrackId);Assert.Equal(new byte[]{1,2,3},File.ReadAllBytes(a.Path));
 }
 [Theory][InlineData(false)][InlineData(true)]
 public void ChangedOrRemovedSourceCannotSaveStaleTrack(bool encrypted)
 {
  using var key=Prepare(encrypted);using var db=new CatalogRepository(Database,key);var a=Add(db,"a.mp4",20);var b=Add(db,"b.mp4",35);var source=PlaybackSource.Resolve(b,b.Path);db.MergeVersions(a.Id,[b.Id]);source=source with {MediaId=a.Id};
  File.WriteAllBytes(b.Path,[4,5,6,7]);Assert.Throws<InvalidOperationException>(()=>db.SetVersionAudioTrack(source,Track(source,2)));Assert.False(Track(source,2).Matches(4,source.ModifiedAtUtc));
  var fresh=PlaybackSource.Resolve(db.GetMedia(a.Id)!,b.Path);db.SplitVersion(a.Id,b.Path);Assert.Throws<ArgumentException>(()=>db.SetVersionAudioTrack(fresh,Track(fresh,2)));Assert.Equal(AudioTrackMode.Automatic,db.GetMedia(a.Id)!.AudioTrack.Mode);
 }
 [Theory][InlineData(false)][InlineData(true)]
 public async Task PortableAndBackupRetainPerVersionAudioAndPlayerSettings(bool encrypted)
 {
  using var key=Prepare(encrypted);using var db=new CatalogRepository(Database,key);var a=Add(db,"one.mp4",20);var b=Add(db,"two.mp4",30);var merged=db.MergeVersions(a.Id,[b.Id]);
  db.SetMediaPlaybackSettings(a.Id,new(){Speed=1.25,AudioDelaySeconds=.7});var source=PlaybackSource.Resolve(merged,b.Path);db.SetVersionAudioTrack(source,Track(source,2));
  var export=Path.Combine(root,"portable.json");db.Export(export);
  using(var imported=new CatalogRepository(Path.Combine(root,"imported","catalog.db")))
  {
   var result=imported.ImportJson(export,CatalogRepository.MaximumPortableEntityBytes,secret:encrypted?"synthetic-playback-password":null);Assert.Empty(result.Errors);
   var copy=imported.GetMedia(a.Id)!;Assert.Equal(1.25,copy.Playback.Speed);Assert.Equal(.7,copy.Playback.AudioDelaySeconds);Assert.Equal(2,copy.Files.Single().AudioTrack.TrackId);
  }
  var package=Path.Combine(root,"backup.zip");await db.CreateBackupAsync(package,false);
  var restored=await CatalogRepository.RestoreBackupAsync(package,Path.Combine(root,"restored"),secret:encrypted?"synthetic-playback-password":null);
  using var restoredKey=encrypted?CatalogEncryption.Unlock(restored.DatabasePath,"synthetic-playback-password"):null;using var destination=new CatalogRepository(restored.DatabasePath,restoredKey);
  Assert.Equal(db.GetMedia(a.Id)!.Playback,destination.GetMedia(a.Id)!.Playback);Assert.Equal(db.GetMedia(a.Id)!.Files.Single().AudioTrack,destination.GetMedia(a.Id)!.Files.Single().AudioTrack);
 }
 [Theory][InlineData(false)][InlineData(true)]
 public void DefaultsAndValidationApplyToDirectAndPortableWrites(bool encrypted)
 {
  using var key=Prepare(encrypted);using var db=new CatalogRepository(Database,key);var a=Add(db,"a.mp4",20);
  Assert.Equal(1,a.Playback.Speed);Assert.Equal(0,a.Playback.AudioDelaySeconds);Assert.Equal(AudioTrackMode.Automatic,a.AudioTrack.Mode);
  foreach(var speed in new[]{double.NaN,double.PositiveInfinity,.1,5})Assert.Throws<ArgumentException>(()=>db.SetMediaPlaybackSettings(a.Id,new(){Speed=speed}));
  Assert.Throws<ArgumentException>(()=>db.Upsert(a with {Playback=new(){AudioDelaySeconds=601}}));
  Assert.Throws<ArgumentException>(()=>db.Upsert(a with {AudioTrack=new(){Mode=AudioTrackMode.Track,TrackId=0}}));
  var json=JsonSerializer.Serialize(new{format="flixypix-catalog",version=4,items=new[]{a with {Playback=new(){Speed=99}}}},CatalogRepository.Json);var export=Path.Combine(root,"invalid.json");File.WriteAllText(export,json);Assert.NotEmpty(db.ImportJson(export).Errors);Assert.Equal(1,db.GetMedia(a.Id)!.Playback.Speed);
 }
}
