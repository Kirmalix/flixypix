using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using FlixyPix.Core;
using FlixyPix.MediaTools;

namespace FlixyPix.Desktop;
public partial class MainWindow
{
 private async Task RunPlaybackPreferencesSmokeAsync(string directory)
 {
  if(!ToolExists(_settings.Ffmpeg,"ffmpeg")||!ToolExists(_settings.Mpv,"mpv"))return;
  static void Require(bool value,string text){if(!value)throw new InvalidOperationException(text);}
  var folder=Path.Combine(directory,"playback-preferences");Directory.CreateDirectory(folder);var first=Path.Combine(folder,"first.mkv");var second=Path.Combine(folder,"second.mkv");
  var start=new ProcessStartInfo(_settings.Ffmpeg??Path.Combine(AppContext.BaseDirectory,"tools","ffmpeg.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true};
  foreach(var arg in new[]{"-hide_banner","-loglevel","error","-nostdin","-n","-f","lavfi","-i","testsrc2=size=160x90:rate=10:duration=12","-f","lavfi","-i","sine=frequency=440:duration=12","-f","lavfi","-i","sine=frequency=880:duration=12","-map","0:v","-map","1:a","-map","2:a","-c:v","libx264","-preset","ultrafast","-c:a","aac",first})start.ArgumentList.Add(arg);
  using(var encoder=Process.Start(start)!){var error=encoder.StandardError.ReadToEndAsync();await encoder.WaitForExitAsync();Require(encoder.ExitCode==0,await error);}File.Copy(first,second);
  foreach(var encrypted in new[]{false,true})
  {
   var root=Path.Combine(folder,encrypted?"encrypted":"plain");Directory.CreateDirectory(root);var path=Path.Combine(root,"catalog.db");using(var empty=new CatalogRepository(path)){}
   if(encrypted)CatalogEncryption.EnableEncryption(path,"synthetic-player-preferences-password");using var key=encrypted?CatalogEncryption.Unlock(path,"synthetic-player-preferences-password"):null;using var db=new CatalogRepository(path,key);
   var media=db.Upsert(new(){Path=first,Kind=MediaKind.Video,DurationSeconds=80,Files=[new(){Path=second,DurationSeconds=90}]});
   db.SetMediaPlaybackSettings(media.Id,new(){Speed=1.5,AudioDelaySeconds=.3});
   foreach(var file in new[]{first,second})
   {
    var source=PlaybackSource.Resolve(media,file);db.SetVersionAudioTrack(source,new(){Mode=AudioTrackMode.Track,TrackId=file==first?1:2,SourceSize=source.Size,SourceModifiedAtUtc=source.ModifiedAtUtc});
   }
   async Task Check(string file,int expectedTrack,bool edit)
   {
    var current=db.GetMedia(media.Id)!;var window=new VideoPlayerWindow(this,current,db,PlaybackSource.Resolve(current,file));window.Show();
    MpvPlayer? player=null;
    try
    {
     var handle=await window.ReadyTask;player=await _tools.PlayAsync(file,0,handle);await player.PauseAsync(true);await window.AttachPlayerAsync(player);
     Require(window.Source?.Path==file&&window.Timeline.Maximum is >11 and <13,"Player did not use actual version path/duration.");
     Require(Math.Abs((await player.GetAudioDelayAsync())!.Value-current.Playback.AudioDelaySeconds)<.001,"Stored audio delay not applied.");
     Require((await player.GetTracksAsync()).Single(t=>t.Type=="audio"&&t.Selected).Id==expectedTrack,"Wrong version audio restored.");
     var rates=Descendants<ComboBox>((FrameworkElement)window.Content).Single(c=>c.Items.OfType<double>().Any());Require(Equals(rates.SelectedItem,current.Playback.Speed),"Stored speed not reflected in UI.");
     if(edit)
     {
      db.Upsert(db.GetMedia(media.Id)! with {Description="Concurrent retained metadata"});rates.SelectedItem=2d;await window.PreferenceUpdate;await window.ShiftAudioAsync(-1);
      var menu=window.ShowPlayerMenu();var tracks=menu.Items.OfType<MenuItem>().Single(i=>Equals(i.Header,T("Tracks")));
      tracks.Items.OfType<MenuItem>().Single(i=>Equals(i.Header,T("Audio off"))).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));await window.PreferenceUpdate;menu.IsOpen=false;
      Require(db.GetMedia(media.Id) is {Playback.Speed:2,AudioTrack.Mode:AudioTrackMode.Off,Description:"Concurrent retained metadata"},"Actual controls did not persist fields safely.");
      Require(!(await player.GetTracksAsync()).Any(t=>t.Type=="audio"&&t.Selected),"Audio-off did not reach decoder.");
      await RunAbLoopControlsSmokeAsync(window,player,db,media.Id,root);
      await RunFramePreviewSmokeAsync(window,root,db,media);
     }
     Capture((FrameworkElement)window.Content,Path.Combine(root,edit?"edited.png":"restored.png"));
    }
    finally{window.Close();if(player!=null)await player.DisposeAsync();}
   }
   await Check(first,1,true);await Check(second,2,false);
   var reopened=new VideoPlayerWindow(this,db.GetMedia(media.Id)!,db,PlaybackSource.Resolve(db.GetMedia(media.Id)!,first));reopened.Show();MpvPlayer? restored=null;
   try
   {
    restored=await _tools.PlayAsync(first,0,await reopened.ReadyTask);await restored.PauseAsync(true);await reopened.AttachPlayerAsync(restored);
    Require(!(await restored.GetTracksAsync()).Any(t=>t.Type=="audio"&&t.Selected)&&Math.Abs((await restored.GetAudioDelayAsync())!.Value-.2)<.001,"Fresh player lost off mode or changed delay.");
   }
   finally{reopened.Close();if(restored!=null)await restored.DisposeAsync();}
   var missingTrackSource=PlaybackSource.Resolve(db.GetMedia(media.Id)!,first);
   db.SetVersionAudioTrack(missingTrackSource,new(){Mode=AudioTrackMode.Track,TrackId=99,SourceSize=missingTrackSource.Size,SourceModifiedAtUtc=missingTrackSource.ModifiedAtUtc});
   await Check(first,1,false);Require(db.GetMedia(media.Id)!.AudioTrack.TrackId==99,"Missing-track fallback overwrote the stored preference.");
   db.SetVersionAudioTrack(missingTrackSource,new(){Mode=AudioTrackMode.Track,TrackId=2,SourceSize=missingTrackSource.Size,SourceModifiedAtUtc=missingTrackSource.ModifiedAtUtc});
   File.SetLastWriteTimeUtc(first,missingTrackSource.ModifiedAtUtc.UtcDateTime.AddSeconds(2));
   await Check(first,1,false);Require(db.GetMedia(media.Id)!.AudioTrack.TrackId==2,"Changed-source fallback overwrote the stored preference.");
   await RunContactSheetSeekingSmokeAsync(db,media.Id,first,root);
   File.WriteAllText(Path.Combine(root,"result.txt"),"PASS: actual mpv version audio, actual duration/path, UI speed/audio-off and delay persistence, fresh player and concurrent metadata retention.");
  }
 }
}
