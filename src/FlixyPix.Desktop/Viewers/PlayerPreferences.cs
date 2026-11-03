using FlixyPix.Core;

namespace FlixyPix.Desktop;

internal sealed partial class VideoPlayerWindow
{
 internal PlaybackSource? Source {get;private set;}
 private double? durationSeconds;
 private AudioTrackMode audioMode;
 private readonly SemaphoreSlim preferenceGate=new(1,1);
 internal double AudioDelay {get;private set;}
 internal Task PreferenceUpdate {get;private set;}=Task.CompletedTask;
 private Task TrackPreference(Func<Task> action)=>PreferenceUpdate=Command(action);
 private async Task RestorePlaybackSettingsAsync()
 {
  var current=repository.GetMedia(media.Id)??throw new InvalidOperationException(T("The item no longer exists."));
  syncing=true;
  try
  {
   var rates=speed.Items.OfType<double>().Append(current.Playback.Speed).Distinct().Order().ToArray();speed.ItemsSource=rates;speed.SelectedItem=current.Playback.Speed;
   await RequirePlayer().SetSpeedAsync(current.Playback.Speed);AudioDelay=current.Playback.AudioDelaySeconds;await RequirePlayer().SetAudioDelayAsync(AudioDelay);
   if(Source is not {} source)return;
   var preference=MediaVersions.All(current).FirstOrDefault(f=>CatalogRepository.PathKey(f.Path)==CatalogRepository.PathKey(source.Path))?.AudioTrack??new();
   var tracks=await RequirePlayer().GetTracksAsync();
   if(!preference.Matches(source.Size,source.ModifiedAtUtc)||preference.Mode==AudioTrackMode.Track&&!tracks.Any(t=>t.Type=="audio"&&t.Id==preference.TrackId))
   {audioMode=AudioTrackMode.Automatic;await RequirePlayer().SelectAutomaticAudioTrackAsync();Notify(T("The saved audio track is unavailable. Automatic selection is used."));}
   else
   {
    audioMode=preference.Mode;
    if(audioMode==AudioTrackMode.Automatic)await RequirePlayer().SelectAutomaticAudioTrackAsync();
    else await RequirePlayer().SelectAudioTrackAsync(audioMode==AudioTrackMode.Off?null:preference.TrackId);
   }
  }
  finally{syncing=false;}
 }
 private async Task SaveSpeedAsync(double value)
 {
  await preferenceGate.WaitAsync();
  try{if(closing||player==null)return;await player.SetSpeedAsync(value);repository.ChangeMediaPlaybackSettings(media.Id,speed:value);}
  finally{preferenceGate.Release();}
 }
 private async Task SelectAudioAsync(TrackChoice choice)
 {
  await preferenceGate.WaitAsync();
  try
  {
   if(closing||player==null)return;
   if(choice.Automatic)await player.SelectAutomaticAudioTrackAsync();else await player.SelectAudioTrackAsync(choice.Id);
   audioMode=choice.Automatic?AudioTrackMode.Automatic:choice.Id.HasValue?AudioTrackMode.Track:AudioTrackMode.Off;
   if(Source is {} source)repository.SetVersionAudioTrack(source,new(){Mode=audioMode,TrackId=choice.Id,SourceSize=source.Size,SourceModifiedAtUtc=source.ModifiedAtUtc});
  }
  finally{preferenceGate.Release();}
 }
 internal async Task ShiftAudioAsync(int steps)
 {
  await preferenceGate.WaitAsync();
  try
  {
   if(closing||player==null)return;var delay=steps==0?0:Math.Round(Math.Clamp(AudioDelay+steps*.1,-600,600),1);
   await player.SetAudioDelayAsync(delay);repository.ChangeMediaPlaybackSettings(media.Id,audioDelaySeconds:delay);AudioDelay=delay;
  }
  finally{preferenceGate.Release();}
 }
}
