using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

// Explicit developer-only manual acceptance mode. Never injects input or records typed characters.
internal sealed record PlayerInputCounts(int NativeMouse=0,int NativeWheel=0,int WpfKeys=0,int ToolbarMouse=0,int Activations=0,int Menus=0);
internal sealed record PlayerInputSnapshot(DateTimeOffset At,bool Ready,string? Source,string? NativeSource,bool Fullscreen,bool Marks,bool UiPaused,bool? NativePaused,
 double? Seconds,double UiVolume,double? NativeVolume,AbLoopState UiLoop,AbLoopState? NativeLoop,int Bookmarks,int Scenes,PlayerInputCounts Inputs);
internal sealed record PlayerInputCase(string Id,string Instruction,bool FullscreenBefore=false)
{
 internal static bool SameSource(string? first,string? second)=>first!=null&&second!=null&&CatalogRepository.PathKey(first)==CatalogRepository.PathKey(second);
 internal bool Matches(PlayerInputSnapshot before,PlayerInputSnapshot after)
 {
  if(!before.Ready||!after.Ready||!SameSource(before.Source,before.NativeSource)||!SameSource(after.Source,after.NativeSource)||Id!="navigation"&&!SameSource(before.Source,after.Source))return false;
  var a=after.Inputs;var b=before.Inputs;
  var pauseChanged=after.UiPaused!=before.UiPaused&&after.NativePaused==after.UiPaused&&before.NativePaused==before.UiPaused;
  return Id switch
  {
   "native-click"=>a.NativeMouse>b.NativeMouse&&pauseChanged,
   "toolbar-pause"=>a.ToolbarMouse>b.ToolbarMouse&&pauseChanged,
   "keyboard-pause"=>a.WpfKeys>b.WpfKeys&&pauseChanged,
   "native-fullscreen"=>a.NativeMouse>b.NativeMouse&&!before.Fullscreen&&after.Fullscreen,
   "escape-fullscreen"=>a.WpfKeys>b.WpfKeys&&before.Fullscreen&&!after.Fullscreen,
   "context-menu"=>a.NativeMouse>b.NativeMouse&&a.Menus>b.Menus,
   "ctrl-wheel"=>a.NativeWheel>b.NativeWheel&&Math.Abs(after.UiVolume-before.UiVolume)>=4&&after.NativeVolume is {} volume&&Math.Abs(volume-after.UiVolume)<.1,
   "timeline"=>a.ToolbarMouse>b.ToolbarMouse&&before.NativePaused==true&&after.NativePaused==true&&before.Seconds is {} start&&after.Seconds is {} end&&Math.Abs(end-start)>2,
   "ab-loop"=>a.ToolbarMouse>b.ToolbarMouse&&after.UiLoop is {Start:not null,End:not null}&&after.UiLoop==after.NativeLoop&&after.UiLoop!=before.UiLoop,
   "marks"=>a.ToolbarMouse>b.ToolbarMouse&&after.Bookmarks>before.Bookmarks&&after.Scenes>before.Scenes,
   "fullscreen-marks"=>a.WpfKeys>b.WpfKeys&&before.Fullscreen&&after.Fullscreen&&before.Marks!=after.Marks,
   "focus-return"=>a.Activations>b.Activations&&a.WpfKeys>b.WpfKeys&&pauseChanged,
   "navigation"=>a.WpfKeys>b.WpfKeys&&!SameSource(after.Source,before.Source),
   _=>false
  };
 }
 internal static readonly PlayerInputCase[] All=
 [
  new("native-click","Click once in the native video picture. Wait one second; pause must toggle."),
  new("toolbar-pause","Click the WPF pause/play button once. The native decoder must change pause state."),
  new("keyboard-pause","Return to the player with Alt+Tab, without clicking the picture, then press Space once."),
  new("native-fullscreen","Double-click the video picture to enter fullscreen."),
  new("escape-fullscreen","Press Escape once in the fullscreen player. It must return to its normal window.",true),
  new("context-menu","Right-click the native picture. Keep its player menu open for at least one second, then dismiss it."),
  new("ctrl-wheel","Hold Ctrl over the native picture and turn the wheel down one notch. Native and visible volume must change together."),
  new("timeline","While still paused, click or drag the WPF timeline at least three seconds away from its initial position."),
  new("ab-loop","Click Set A, seek several seconds forward with the timeline, then click Set B. Both points must reach mpv."),
  new("marks","Use the visible controls to create a bookmark and a scene: Scene start, seek forward, Scene end, Create scene."),
  new("fullscreen-marks","Press L in fullscreen to toggle the jump-mark list; keep fullscreen active.",true),
  new("focus-return","Switch to another application and back to the player with Alt+Tab, then press Space once."),
  new("navigation","Press N in the player. The next synthetic library video must open with its different source path.")
 ];
}

internal sealed class PlayerInputCounters
{
 internal PlayerInputCounts Value=new();private bool menu;
 internal void Native(string input)=>Value=input.Contains("WHEEL",StringComparison.Ordinal)?Value with {NativeWheel=Value.NativeWheel+1}:Value with {NativeMouse=Value.NativeMouse+1};
 internal void Key(object sender,KeyEventArgs e)=>Value=Value with {WpfKeys=Value.WpfKeys+1};
 internal void Toolbar(object sender,MouseButtonEventArgs e)=>Value=Value with {ToolbarMouse=Value.ToolbarMouse+1};
 internal void Activated(object? sender,EventArgs e)=>Value=Value with {Activations=Value.Activations+1};
 internal void ObserveMenu(bool open){if(open&&!menu)Value=Value with {Menus=Value.Menus+1};menu=open;}
}

internal sealed partial class VideoPlayerWindow
{
 internal IDisposable ObserveAcceptanceInput(PlayerInputCounters counts)
 {
  host.Input+=counts.Native;AddHandler(Keyboard.PreviewKeyDownEvent,new KeyEventHandler(counts.Key),true);
  controlBar.AddHandler(PreviewMouseDownEvent,new MouseButtonEventHandler(counts.Toolbar),true);Activated+=counts.Activated;
  return new AcceptanceSubscription(()=>{host.Input-=counts.Native;RemoveHandler(Keyboard.PreviewKeyDownEvent,new KeyEventHandler(counts.Key));controlBar.RemoveHandler(PreviewMouseDownEvent,new MouseButtonEventHandler(counts.Toolbar));Activated-=counts.Activated;});
 }
 internal async Task PrepareAcceptanceAsync(bool fullscreen)
 {
  if(OpenPlayerMenu is {} menu)menu.IsOpen=false;
  if(IsFullscreen!=fullscreen)ToggleFullscreen();
  await ChangeAbLoopAsync(2);await SeekToAsync(10);if(!IsPaused)await TogglePauseAsync();
 }
 private sealed class AcceptanceSubscription(Action dispose):IDisposable{public void Dispose()=>dispose();}
}

internal sealed class PlayerInputAcceptanceWindow:Window
{
 private readonly string folder;private readonly bool encrypted,selfTest;
 private readonly Func<PlayerInputCase,Task<PlayerInputSnapshot>> prepare;
 private readonly Func<Task<PlayerInputSnapshot>> snapshot;
 private readonly Action sample;
 private readonly ComboBox cases=new(){ItemsSource=PlayerInputCase.All.Select(c=>c.Id).ToArray(),SelectedIndex=0,Margin=new Thickness(0,8,0,12)};
 private readonly TextBlock instruction=new(){TextWrapping=TextWrapping.Wrap,FontSize=16,Margin=new Thickness(0,0,0,14)};
 private readonly TextBlock status=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,8)};
 private readonly CheckBox attest=new(){Content=new TextBlock{Text="I performed this check with a physical mouse/keyboard, without injected or replayed input.",TextWrapping=TextWrapping.Wrap},HorizontalContentAlignment=HorizontalAlignment.Stretch,Margin=new Thickness(0,12,0,12)};
 private readonly Button start=new(){Content="1. Prepare check",MinWidth=140,Margin=new Thickness(0,0,8,0)};
 private readonly Button verify=new(){Content="2. Verify native result",MinWidth=170,IsEnabled=false};
 private readonly Button finish=new(){Content="Finish acceptance",MinWidth=140,IsEnabled=false,Margin=new Thickness(8,0,0,0)};
 private readonly Dictionary<string,object> results=new();private readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromMilliseconds(100)};
 private readonly TaskCompletionSource<bool> closed=new(TaskCreationOptions.RunContinuationsAsynchronously);
 private PlayerInputSnapshot? baseline;private PlayerInputCase? active;private bool busy,completed;
 internal Task<bool> Completion=>closed.Task;
 internal bool CanVerify=>verify.IsEnabled;
 internal PlayerInputAcceptanceWindow(Window owner,string folder,bool encrypted,bool selfTest,Func<PlayerInputCase,Task<PlayerInputSnapshot>> prepare,Func<Task<PlayerInputSnapshot>> snapshot,Action sample)
 {
  Owner=owner;this.folder=folder;this.encrypted=encrypted;this.selfTest=selfTest;this.prepare=prepare;this.snapshot=snapshot;this.sample=sample;
  Title=selfTest?"FlixyPix · input harness self-test":"FlixyPix · physical input acceptance";Width=740;Height=500;MinWidth=600;MinHeight=440;WindowStartupLocation=WindowStartupLocation.CenterOwner;
  var panel=new StackPanel();panel.Children.Add(new TextBlock{Text=(encrypted?"Encrypted":"Plain")+" isolated synthetic catalog. Prepare each check, perform the instruction in the player, then return here with Alt+Tab and verify. A successful state check alone does not prove physical input.",TextWrapping=TextWrapping.Wrap});
  panel.Children.Add(cases);panel.Children.Add(instruction);panel.Children.Add(attest);
  var buttons=new WrapPanel();buttons.Children.Add(start);buttons.Children.Add(verify);buttons.Children.Add(finish);panel.Children.Add(buttons);panel.Children.Add(status);
  Content=new WindowFrame(Title,selfTest?"SELF-TEST ONLY · no physical input was performed · no acceptance can be recorded.":"Manual acceptance only · no input is generated · only aggregate input counts are recorded.",panel,scroll:true);
  cases.SelectionChanged+=(_,_)=>{baseline=null;active=null;verify.IsEnabled=false;attest.IsChecked=false;instruction.Text=PlayerInputCase.All[cases.SelectedIndex].Instruction;};instruction.Text=PlayerInputCase.All[0].Instruction;
  start.Click+=async(_,_)=>await PrepareAsync();verify.Click+=async(_,_)=>await VerifyAsync();
  finish.Click+=(_,_)=>{if(results.Count!=PlayerInputCase.All.Length||selfTest)return;completed=true;WriteReport();Close();};
  timer.Tick+=(_,_)=>sample();timer.Start();
  Closing+=(_,e)=>{if(busy)e.Cancel=true;};
  Closed+=(_,_)=>{timer.Stop();WriteReport();closed.TrySetResult(completed);};
  WriteReport();
 }
 private async Task PrepareAsync()
 {
  if(busy)return;busy=true;start.IsEnabled=verify.IsEnabled=cases.IsEnabled=false;
  try{active=PlayerInputCase.All[cases.SelectedIndex];results.Remove(active.Id);finish.IsEnabled=false;baseline=null;WriteReport();baseline=await prepare(active);attest.IsChecked=false;verify.IsEnabled=baseline.Ready;status.Text="Baseline recorded. Perform the instruction in the player; return here to verify.";}
  catch(Exception error){baseline=null;status.Text=error.Message;}
  finally{busy=false;start.IsEnabled=cases.IsEnabled=true;}
 }
 private async Task VerifyAsync()
 {
  if(busy||baseline==null||active==null)return;
  if(attest.IsChecked!=true){status.Text="Physical input has not been attested. No acceptance result was recorded.";return;}
  var check=active;var before=baseline;busy=true;verify.IsEnabled=start.IsEnabled=cases.IsEnabled=finish.IsEnabled=false;
  try
  {
   var after=await snapshot();
   if(!check.Matches(before,after)){status.Text="The required native/input change is not demonstrated. Retry this check; no successful result was recorded.";return;}
   if(selfTest){status.Text="Self-test mode cannot record manual acceptance.";return;}
   results[check.Id]=new {Before=before,After=after,HardwareAttested=true,Instruction=check.Instruction};WriteReport();
   status.Text=$"Recorded {results.Count}/{PlayerInputCase.All.Length}. Choose the next check and prepare it.";baseline=null;finish.IsEnabled=results.Count==PlayerInputCase.All.Length;
  }
  catch(Exception error){status.Text=error.Message;}
  finally{busy=false;start.IsEnabled=cases.IsEnabled=true;verify.IsEnabled=baseline!=null;finish.IsEnabled=results.Count==PlayerInputCase.All.Length&&!selfTest;}
 }
 private void WriteReport()
 {
  var dpi=VisualTreeHelper.GetDpi(this);var report=new {Format="flixypix-player-input-acceptance",Version=1,Mode=encrypted?"encrypted":"plain",Status=selfTest?"self-test-only":completed?"manual-checks-recorded":"incomplete",AutomaticHardwareProof=false,DpiX=dpi.PixelsPerInchX,DpiY=dpi.PixelsPerInchY,CurrentCase=active?.Id,Baseline=baseline,Cases=results,Missing=PlayerInputCase.All.Where(c=>!results.ContainsKey(c.Id)).Select(c=>c.Id).ToArray()};
  var path=Path.Combine(folder,"player-input-result.json");var temporary=path+".tmp";File.WriteAllText(temporary,JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));File.Move(temporary,path,true);
 }
 internal async Task CheckGuardsAsync()
 {
  await PrepareAsync();await VerifyAsync();
  if(baseline is not {Ready:true,NativePaused:true,UiPaused:true}||!PlayerInputCase.SameSource(baseline.Source,baseline.NativeSource))throw new InvalidOperationException("The baseline does not describe the actual paused native source.");
  if(results.Count!=0||!CanVerify||completed)throw new InvalidOperationException("A missing physical-input attestation was accepted.");
  attest.IsChecked=true;await VerifyAsync();
  if(results.Count!=0||completed)throw new InvalidOperationException("Unchanged native state was accepted.");
  foreach(var check in PlayerInputCase.All)if(baseline!=null&&check.Matches(baseline,baseline))throw new InvalidOperationException("An unchanged snapshot satisfied "+check.Id);
  attest.IsChecked=false;
 }
}

public partial class MainWindow
{
 internal async Task RunPhysicalInputAcceptanceAsync(string directory,bool selfTest)
 {
  if(!ToolExists(_settings.Ffmpeg,"ffmpeg")||!ToolExists(_settings.Mpv,"mpv"))throw new InvalidOperationException("The manual input fixture requires bundled FFmpeg and mpv.");
  var mediaFolder=Path.Combine(directory,"fixtures");Directory.CreateDirectory(mediaFolder);var first=Path.Combine(mediaFolder,"first.mkv");var next=Path.Combine(mediaFolder,"next.mkv");
  var encode=new ProcessStartInfo(_settings.Ffmpeg??Path.Combine(AppContext.BaseDirectory,"tools","ffmpeg.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true};
  foreach(var arg in new[]{"-hide_banner","-loglevel","error","-nostdin","-n","-f","lavfi","-i","testsrc2=size=320x180:rate=10:duration=120","-f","lavfi","-i","sine=frequency=440:duration=120","-f","lavfi","-i","sine=frequency=880:duration=120","-map","0:v","-map","1:a","-map","2:a","-c:v","libx264","-preset","ultrafast","-c:a","aac",first})encode.ArgumentList.Add(arg);
  using(var process=Process.Start(encode)!){var error=process.StandardError.ReadToEndAsync();await process.WaitForExitAsync();if(process.ExitCode!=0)throw new IOException(await error);}File.Copy(first,next,false);
  static object Fingerprint(string path){using var input=File.OpenRead(path);return new {Path=path,Size=input.Length,Sha256=Convert.ToHexStringLower(SHA256.HashData(input))};}
  var tools=new[]{"ffmpeg.exe","mpv.exe","mpv-2.dll"}.Select(name=>Path.Combine(AppContext.BaseDirectory,"tools",name)).Where(File.Exists).Select(Fingerprint).ToArray();
  File.WriteAllText(Path.Combine(directory,"fixture-manifest.json"),JsonSerializer.Serialize(new {Format="flixypix-player-input-fixture",Version=1,Encrypted=_repository.IsEncrypted,App=Fingerprint(typeof(MainWindow).Assembly.Location),Media=new[]{Fingerprint(first),Fingerprint(next)},Tools=tools},new JsonSerializerOptions{WriteIndented=true}));
  var playback=new PlaybackPreferences{StartVolume=50,RememberVolume=false,ResumePlayback=false,ShowMarks=true,NameNewBookmarks=false,Wheel=PlayerWheelAction.SeekAndVolume,MouseRevision=PlaybackPreferences.CurrentMouseRevision,WheelRevision=PlaybackPreferences.CurrentWheelRevision};
  _repository.SavePlaybackPreferences(playback);
  var media=_repository.Upsert(new(){Path=first,Kind=MediaKind.Video,DurationSeconds=120,Title="Input fixture 1"});_repository.Upsert(new(){Path=next,Kind=MediaKind.Video,DurationSeconds=120,Title="Input fixture 2"});
  _repository.UpsertBookmark(new(){MediaId=media.Id,PositionSeconds=15,Title="Reference mark"});
  ApplyViewState(new(){View="all"});await RefreshAsync(media.Id);await Play(media,10,first);
  var counts=new PlayerInputCounters();VideoPlayerWindow? observed=null;IDisposable? observation=null;
  void Sample()
  {
   if(!ReferenceEquals(observed,_videoWindow)){observation?.Dispose();observed=_videoWindow;observation=observed?.ObserveAcceptanceInput(counts);}
   counts.ObserveMenu(observed?.OpenPlayerMenu?.IsOpen==true);
  }
  async Task<PlayerInputSnapshot> Snapshot()
  {
   Sample();var window=_videoWindow;var player=_player;var id=window?.MediaId;
   return new(DateTimeOffset.UtcNow,window is {HasPlayer:true,IsLoaded:true}&&player!=null,window?.Source?.Path,player==null?null:await player.GetTextPropertyAsync("path"),window?.IsFullscreen==true,window?.MarkPanelVisible==true,window?.IsPaused==true,
    player==null?null:await player.GetPausedAsync(),player==null?null:await player.GetPositionAsync(),window?.Volume??0,player==null?null:await player.GetVolumeAsync(),window?.AbLoop??new(),player==null?null:await player.GetAbLoopAsync(),
    id.HasValue?_repository.GetBookmarks(id.Value).Count:0,id.HasValue?_repository.GetScenes(id.Value).Count:0,counts.Value);
  }
  async Task<PlayerInputSnapshot> Prepare(PlayerInputCase check)
  {
   if(_videoWindow?.IsLoaded!=true||_videoWindow.Source?.Path!=first)await Play(_repository.GetMedia(media.Id)!,10,first);
   var window=_videoWindow??throw new InvalidOperationException("The player did not open.");await window.PrepareAcceptanceAsync(check.FullscreenBefore);Sample();window.Activate();return await Snapshot();
  }
  var report=new PlayerInputAcceptanceWindow(this,directory,_repository.IsEncrypted,selfTest,Prepare,Snapshot,Sample);report.Show();
  try
  {
   if(selfTest){await report.CheckGuardsAsync();Capture((FrameworkElement)report.Content,Path.Combine(directory,"input-harness.png"));report.Close();File.WriteAllText(Path.Combine(directory,"result.txt"),"SELFTEST ONLY: real decoder, baseline sampling, missing attestation/unchanged-state guards and report lifecycle; no physical input acceptance.");}
   else await report.Completion;
  }
  finally{report.Close();observation?.Dispose();await StopPlayer();}
 }
}
