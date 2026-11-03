namespace FlixyPix.Core;

public sealed record MediaPlaybackSettings
{
 public double Speed {get;init;}=1;
 public double AudioDelaySeconds {get;init;}
}
public enum AudioTrackMode { Automatic, Off, Track }
public sealed record AudioTrackPreference
{
 public AudioTrackMode Mode {get;init;}
 public int? TrackId {get;init;}
 public long? SourceSize {get;init;}
 public DateTimeOffset? SourceModifiedAtUtc {get;init;}
 public bool Matches(long size,DateTimeOffset modified)=>Mode!=AudioTrackMode.Track||SourceSize==size&&SourceModifiedAtUtc==modified;
}
public sealed record PlaybackSource(Guid MediaId,string Path,double? DurationSeconds,long Size,DateTimeOffset ModifiedAtUtc)
{
 public static PlaybackSource Resolve(MediaItem item,string path)
 {
  var file=MediaVersions.All(item).SingleOrDefault(f=>CatalogRepository.PathKey(f.Path)==CatalogRepository.PathKey(path))??throw new ArgumentException("The playback file does not belong to this medium.");
  var info=new FileInfo(file.Path);return new(item.Id,file.Path,file.DurationSeconds,info.Length,info.LastWriteTimeUtc);
 }
}
public sealed partial class CatalogRepository
{
 private static void ValidatePlaybackSettings(MediaPlaybackSettings settings)
 {
  ArgumentNullException.ThrowIfNull(settings);
  if(!double.IsFinite(settings.Speed)||settings.Speed is <.25 or >4||!double.IsFinite(settings.AudioDelaySeconds)||Math.Abs(settings.AudioDelaySeconds)>600)throw new ArgumentException("Invalid playback speed or audio delay.");
 }
 private static void ValidateAudioTrack(AudioTrackPreference preference)
 {
  ArgumentNullException.ThrowIfNull(preference);
  if(!Enum.IsDefined(preference.Mode)||preference.SourceSize is <0||
   (preference.Mode==AudioTrackMode.Track?(preference.TrackId is not >0||preference.SourceSize==null||preference.SourceModifiedAtUtc==null):preference.TrackId!=null))throw new ArgumentException("Invalid audio track preference.");
 }
 /// <summary>Changes only player fields of the latest media document, retaining concurrent metadata edits.</summary>
 public MediaItem SetMediaPlaybackSettings(Guid mediaId,MediaPlaybackSettings settings)
 => UpdateMediaPlaybackSettings(mediaId,_=>settings);
 public MediaItem ChangeMediaPlaybackSettings(Guid mediaId,double? speed=null,double? audioDelaySeconds=null)
 => UpdateMediaPlaybackSettings(mediaId,current=>current with {Speed=speed??current.Speed,AudioDelaySeconds=audioDelaySeconds??current.AudioDelaySeconds});
 private MediaItem UpdateMediaPlaybackSettings(Guid mediaId,Func<MediaPlaybackSettings,MediaPlaybackSettings> update)
 {
  lock(gate)
  {
   ThrowIfDisposed();using var transaction=connection.BeginTransaction();
   var item=ReadMediaDocument(mediaId,transaction)??throw new ArgumentException("Unknown media.");
   var settings=update(item.Playback);ValidatePlaybackSettings(settings);
   var saved=UpsertCore(item with {Playback=settings},transaction);transaction.Commit();return saved;
  }
 }
 public MediaItem SetVersionAudioTrack(PlaybackSource source,AudioTrackPreference preference)
 {
  ValidateAudioTrack(preference);
  if(preference.Mode==AudioTrackMode.Track&&(preference.SourceSize!=source.Size||preference.SourceModifiedAtUtc!=source.ModifiedAtUtc))throw new ArgumentException("Audio preference does not match its playback source.");
  lock(gate)
  {
   ThrowIfDisposed();using var transaction=connection.BeginTransaction();
   var item=ReadMediaDocument(source.MediaId,transaction)??throw new ArgumentException("Unknown media.");
   var pathKey=PathKey(source.Path);
   if(!MediaVersions.All(item).Any(f=>PathKey(f.Path)==pathKey))throw new ArgumentException("The playback version no longer belongs to this medium.");
   var info=new FileInfo(source.Path);
   if(info.Length!=source.Size||new DateTimeOffset(info.LastWriteTimeUtc)!=source.ModifiedAtUtc)throw new InvalidOperationException("The playback source changed.");
   var updated=PathKey(item.Path)==pathKey?item with {AudioTrack=preference}:item with {Files=item.Files.Select(f=>PathKey(f.Path)==pathKey?f with {AudioTrack=preference}:f).ToList()};
   var saved=UpsertCore(updated,transaction);transaction.Commit();return saved;
  }
 }
}
