using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FlixyPix.Core;
using FlixyPix.MediaTools;

namespace FlixyPix.Desktop;

internal sealed partial class VideoPlayerWindow : Window
{
 internal Action<bool>? HelpRequested {get;set;}
 private readonly CatalogRepository repository;
 private readonly MediaItem media;
 /// <summary>Volume the player starts with (percent).</summary>
 internal double InitialVolume { get; set; } = 100;
 internal Guid MediaId => media.Id;
 private readonly NativeVideoHost host=new();
 private readonly TextBlock status=new() { Text=T("Starting the player …"),Margin=new Thickness(8),TextWrapping=TextWrapping.Wrap };
 private readonly Slider position=new() { Minimum=0,Maximum=1,IsEnabled=false,MinWidth=150,Margin=new Thickness(8) };
 private readonly Button pause=new() { Content=T("Pause") };
 private readonly ComboBox audio=new() { MinWidth=150,DisplayMemberPath="Label" };
 private readonly ComboBox subtitles=new() { MinWidth=150,DisplayMemberPath="Label" };
 private readonly DispatcherTimer timer=new() { Interval=TimeSpan.FromMilliseconds(200) };
 private readonly TaskCompletionSource<nint> ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
 private readonly PlaybackPreferences playback;
 private readonly ComboBox speed=new() { ItemsSource=new double[]{.25,.5,.75,1,1.25,1.5,2,3,4},SelectedItem=1d,MinWidth=65 };
 private readonly CheckBox transforms=new() { Content=T("Time-based view"),IsChecked=true,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8) };
 private MpvPlayer? player;
 private PlayerTransformTimeline timeline=new();
 private PlayerTransform? manualTransform;
 private bool polling,closing,paused,seeking,syncing,keyboardSeeking;
 private IDisposable? awake;
 internal bool KeepsAwake=>awake!=null;
 internal Task<nint> ReadyTask=>ready.Task;
 internal nint VideoHandle=>host.VideoHandle;
 internal nint VideoOuterHandle=>host.Handle;
 internal double LastPositionSeconds { get; private set; }
 internal bool HasPlayer=>player!=null;
 internal event Action<double>? PositionTick;
 private sealed record TrackChoice(int? Id,string Label);

 internal VideoPlayerWindow(Window owner,MediaItem media,CatalogRepository repository)
 {
  Owner=owner; this.media=media; this.repository=repository; Title=media.Title+T(" · Playback"); playback=repository.GetPreferences().Playback;
  ScreenshotFolder=playback.Screenshots.Folder??ScreenshotFolder; marksWanted=playback.ShowMarks;
  host.Input += QueueMouseInput;
  // Some Windows wheel settings deliver to the focused WPF window instead of the native child under the pointer.
  PreviewMouseWheel+=(_,e)=>
  {
   if(e.OriginalSource is DependencyObject source && IsInside(source,controlBar))return;
   var point=e.GetPosition(host);
   if(point.X>=0 && point.Y>=0 && point.X<host.ActualWidth && point.Y<host.ActualHeight)
   { e.Handled=true; host.Wheel(e.Delta,Keyboard.Modifiers); }
  };
  Width=1100; Height=760; MinWidth=650; MinHeight=460;
  WindowStartupLocation=WindowStartupLocation.CenterOwner;
  var root=new DockPanel(); var bar=BuildControlBar(); DockPanel.SetDock(bar,Dock.Bottom); root.Children.Add(bar);
  var panel=BuildMarkPanel(); DockPanel.SetDock(panel,Dock.Right); root.Children.Add(panel);
  // Track lists stay as the model of the track menu; the menu sets their selection.
  audio.SelectionChanged+=async (_,_)=>{ if(!syncing && audio.SelectedItem is TrackChoice track && player!=null)await Command(()=>player.SelectAudioTrackAsync(track.Id)); };
  subtitles.SelectionChanged+=async (_,_)=>{ if(!syncing && subtitles.SelectedItem is TrackChoice track && player!=null)await Command(()=>player.SelectSubtitleTrackAsync(track.Id)); };
  transforms.Checked+=(_,_)=>ApplyTransform(); transforms.Unchecked+=(_,_)=>ApplyTransform();
  root.Children.Add(host); Content=root;
  // From pressing the time line until release the poll must not move it back: a click jumps to the clicked point
  // (IsMoveToPointEnabled), dragging follows the thumb, release seeks to where it ended.
  // The slider marks these events handled itself (IsMoveToPointEnabled), so they are subscribed with handledEventsToo.
  position.AddHandler(PreviewMouseLeftButtonDownEvent,new MouseButtonEventHandler((_,_)=>{ if(position.IsEnabled)seeking=true; }),true);
  position.ValueChanged+=(_,_)=>{ if(seeking && PointerPressed())Scrub(position.Value); };
  position.PreviewKeyDown+=(_,e)=>{if(e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)keyboardSeeking=seeking=true;};
  position.PreviewKeyUp+=async(_,e)=>{if(keyboardSeeking){keyboardSeeking=seeking=false;if(player!=null)await Command(()=>SeekToAsync(position.Value));}};
  position.AddHandler(PreviewMouseLeftButtonUpEvent,new MouseButtonEventHandler(async (_,_)=>{ if(!seeking)return; seeking=false; if(player!=null)await Command(()=>SeekToAsync(position.Value)); }),true);
  timer.Tick+=async (_,_)=>await PollAsync();
  Loaded+=(_,_)=>{ try { timeline=repository.GetPlayerTransforms(media.Id); transforms.IsChecked=timeline.Enabled; RefreshMarks();
   ready.TrySetResult(host.VideoHandle); if(playback.AlwaysFullscreen && WindowStyle!=WindowStyle.None)ToggleFullscreen();
   } catch(Exception error) { ready.TrySetException(error); status.Text=error.Message; } };
  Closed+=(_,_)=>{ closing=true; if(editingMark)InlineEdit.Cancel(); clickTimer?.Stop(); mouseInputs.Clear(); timer.Stop(); player=null; overlay.IsOpen=flash.IsOpen=seekPreview.IsOpen=false; seekPreviewWork?.Cancel(); ready.TrySetCanceled(); awake?.Dispose(); awake=null; };
  host.SizeChanged+=(_,_)=>{ ApplyTransform(); if(overlay.IsOpen)ShowOverlay(); };
  Deactivated+=(_,_)=>{ overlay.IsOpen=false; HideSeekPreview(); };
  PreviewKeyDown+=async (_,e)=>
  {
   if(e.Key==Key.F1) {e.Handled=true;if(HelpRequested!=null)HelpRequested(Keyboard.Modifiers==ModifierKeys.Control);else new HelpWindow(this,T("Help"),HelpWindow.Guide()).ShowDialog();return;}
   if(e.Key==Key.Tab) {ShowOverlay();return;}
   if(e.Key==Key.Escape&&Keyboard.FocusedElement is ComboBox {IsDropDownOpen:true})return;
   if(e.Key==Key.Escape&&Keyboard.FocusedElement is ComboBox {IsDropDownOpen:false}) {e.Handled=true;if(WindowStyle==WindowStyle.None)ToggleFullscreen();else Close();return;}
   if(MainWindow.TextEntry(e.OriginalSource))return;
   if(Keyboard.FocusedElement is Slider or ButtonBase or CheckBox && e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown or Key.Enter or Key.Space)return;
   if(markList.IsKeyboardFocusWithin && e.Key is Key.Up or Key.Down or Key.Left or Key.Right)return;
   if(markList.IsKeyboardFocusWithin && e.Key==Key.Enter && markList.SelectedItem is MarkRow markRow) {e.Handled=true;await SeekToMarkAsync(markRow.Mark);return;}
   if(e.Key==Key.F2 && markList.IsKeyboardFocusWithin) { e.Handled=BeginRenameSelectedMark(); if(e.Handled)return; }
   var key=e.Key==Key.System?e.SystemKey:e.Key;
   if(key==Key.Escape) { e.Handled=true; if(WindowStyle==WindowStyle.None)ToggleFullscreen(); else Close(); return; }
   if(key==Key.F11) { e.Handled=true; ToggleFullscreen(); return; }
   // Without shortcuts only the media keys of keyboards and headsets keep working (their own setting).
   if(!playback.KeyboardShortcuts) { if(playback.MediaKeys && MediaKeyAction(key) is { } pressed) { e.Handled=true; if(pressed.Length==0)Close(); else await Command(()=>RunActionAsync(pressed)); } return; }
   if(PlayerHotkeys.Match(playback.Hotkeys,key,Keyboard.Modifiers) is { } action) { e.Handled=true; await Command(()=>RunActionAsync(action)); return; }
   if(playback.CustomJumps.FirstOrDefault(j=>PlayerHotkeys.TryParse(j.Key,out var m,out var k)&&k==key&&m==Keyboard.Modifiers) is { } jump) { e.Handled=true; await Command(()=>JumpByAsync(jump.Amount,jump.Unit)); return; }
   if(playback.MediaKeys && MediaKeyAction(key) is { } media) { e.Handled=true; if(media.Length==0)Close(); else await Command(()=>RunActionAsync(media)); return; }
   // Digits jump to the n-th mark unless the user bound a digit to an action.
   if(Keyboard.Modifiers==ModifierKeys.None && DigitOf(key) is { } number)
   { e.Handled=true; if(JumpMarks.Numbered(Marks,number) is { } mark)await Command(()=>SeekToMarkAsync(mark)); }
  };
 }
 internal async Task AttachPlayerAsync(MpvPlayer player)
 {
  if(closing)return; this.player=player;
  volumeSlider.Value=Math.Clamp(InitialVolume,0,100); if(InitialVolume!=100)await player.SetVolumeAsync(volumeSlider.Value);
  if(playback.PreventSleep)awake??=SleepGuard.Acquire();
  position.Maximum=Math.Max(1,media.DurationSeconds??1); position.IsEnabled=media.DurationSeconds>0; RenderMarkers();
  pause.Content=paused?"":"";
  await Command(() => RestoreSubtitlesAsync(player)); await RefreshTracksAsync(); await PollAsync(); timer.Start();
 }
 /// <summary>Whether the left mouse button is down; the smoke test replaces it because it cannot press a real button.</summary>
 internal Func<bool> PointerPressed { get; set; } = ()=>Mouse.LeftButton==MouseButtonState.Pressed;
 private bool scrubbing; private double? scrubTarget;
 /// <summary>Seeks along while the time line is dragged; requests during a running seek collapse into the latest one.</summary>
 private async void Scrub(double seconds)
 {
  scrubTarget=seconds; if(scrubbing || player==null)return; scrubbing=true;
  try { while(scrubTarget is { } target) { scrubTarget=null; await Command(()=>SeekToAsync(target)); } }
  finally { scrubbing=false; }
 }
 internal async Task SeekToAsync(double seconds)
 {
  if(media.DurationSeconds is >0 and var duration)seconds=Math.Min(seconds,duration);
  await RequirePlayer().SeekAsync(Math.Max(0,seconds)); LastPositionSeconds=seconds; ApplyTransform();
 }
 internal void SetManualTransform(PlayerTransform transform) { PlayerTransformTimeline.Validate(new() { Steps=[transform] }); manualTransform=transform; ApplyTransform(); }
 internal (int X,int Y,int Width,int Height) VideoBounds=>host.LastBounds;
 private MpvPlayer RequirePlayer()=>player??throw new InvalidOperationException(T("The player is not ready yet."));
 private async Task RefreshTracksAsync()
 {
  var tracks=await RequirePlayer().GetTracksAsync(); syncing=true;
  try
  {
   var audioChoices=tracks.Where(t=>t.Type=="audio").Select(t=>new TrackChoice(t.Id,T("Audio {0} · {1}", t.Id, t.Title??t.Language??T("Default")))).Prepend(new(null,T("Audio off"))).ToArray();
   var subtitleChoices=tracks.Where(t=>t.Type=="sub").Select(t=>new TrackChoice(t.Id,T("Subtitle {0} · {1}", t.Id, t.Title??t.Language??T("Default")))).Prepend(new(null,T("Subtitles off"))).ToArray();
   audio.ItemsSource=audioChoices; audio.SelectedItem=audioChoices.FirstOrDefault(c=>c.Id==tracks.FirstOrDefault(t=>t.Type=="audio" && t.Selected)?.Id)??audioChoices[0];
   subtitles.ItemsSource=subtitleChoices; subtitles.SelectedItem=subtitleChoices.FirstOrDefault(c=>c.Id==tracks.FirstOrDefault(t=>t.Type=="sub" && t.Selected)?.Id)??subtitleChoices[0];
  }
  finally { syncing=false; }
 }
 private async Task PollAsync()
 {
  if(polling || player==null || closing)return; polling=true;
  try
  {
   var seconds=await player.GetPositionAsync(); if(closing || seconds==null || processingMouse)return;
   // A release outside the time line does not reach it: the poll ends the press and seeks where it was left.
   if(seeking && !keyboardSeeking && !PointerPressed()) { seeking=false; _=Command(()=>SeekToAsync(position.Value)); }
   LastPositionSeconds=seconds.Value; if(!seeking)position.Value=Math.Clamp(seconds.Value,0,position.Maximum);
   status.Text=notice is { } n && n.Until>DateTime.UtcNow ? n.Text : media.Title+CurrentMarkLabel();
   if(!paused)Coverage.Observe(seconds.Value,speed.SelectedItem is double rate?rate:1);
   ApplyTransform(); UpdateCurrentMark(); UpdateControls(seconds.Value); PositionTick?.Invoke(seconds.Value);
  }
  catch(Exception error) when(error is IOException or InvalidOperationException or OperationCanceledException or ObjectDisposedException) { timer.Stop(); if(!closing)status.Text=T("Playback finished · ")+error.Message; }
  finally { polling=false; }
 }
 private void ApplyTransform() { if(host.VideoHandle!=0)host.Apply(transforms.IsChecked==true?(manualTransform??timeline.At(LastPositionSeconds)):PlayerTransformTimeline.Identity); }
 private Task SaveTransformsEnabledAsync()=>Command(()=>
 {
  if(media.Kind==MediaKind.Video)timeline=repository.SavePlayerTransforms(media.Id,timeline with { Enabled=transforms.IsChecked==true });
  ApplyTransform(); return Task.CompletedTask;
 });
 private async Task Command(Func<Task> action)
 {
  try { await action(); }
  catch(Exception error) when(error is IOException or InvalidDataException or InvalidOperationException or ArgumentException or OperationCanceledException or ObjectDisposedException)
  { if(!closing)status.Text=error.Message; }
 }
 internal async Task TogglePauseAsync() { paused=!paused; await RequirePlayer().PauseAsync(paused); ShowPlayState(); }
 /// <summary>Pauses for the boss key or the lock; the play button then shows the pause.</summary>
 internal async Task PauseForPrivacyAsync() { if(!paused&&player!=null)await TogglePauseAsync(); }
 private Task JumpAsync(double seconds)=>SeekToAsync(Math.Max(0,LastPositionSeconds+seconds));
 private async Task RunActionAsync(string action)
 {
  switch(action)
  {
   case PlayerActions.PlayPause: await TogglePauseAsync(); break;
   case PlayerActions.Fullscreen: ToggleFullscreen(); break;
   case PlayerActions.SmallForward: await JumpAsync(playback.SmallJumpForwardSeconds); break;
   case PlayerActions.SmallBackward: await JumpAsync(-playback.SmallJumpBackwardSeconds); break;
   case PlayerActions.LargeForward: await JumpAsync(playback.LargeJumpForwardSeconds); break;
   case PlayerActions.LargeBackward: await JumpAsync(-playback.LargeJumpBackwardSeconds); break;
   case PlayerActions.SpeedUp: speed.SelectedIndex=Math.Min(speed.SelectedIndex+1,speed.Items.Count-1); break;
   case PlayerActions.SpeedDown: speed.SelectedIndex=Math.Max(speed.SelectedIndex-1,0); break;
   case PlayerActions.FrameForward or PlayerActions.FrameBackward:
    await RequirePlayer().FrameStepAsync(action==PlayerActions.FrameForward); paused=true; pause.Content=""; break;
   case PlayerActions.CreateBookmark:
    var created=repository.UpsertBookmark(new Bookmark { MediaId=media.Id,Title=T("Bookmark ")+TimeSpan.FromSeconds(LastPositionSeconds).ToString(@"h\:mm\:ss"),PositionSeconds=LastPositionSeconds });
    Notify(T("Bookmark set · ")+TimeSpan.FromSeconds(LastPositionSeconds).ToString(@"h\:mm\:ss")); RefreshMarks();
    if(playback.NameNewBookmarks)NameBookmark(created);
    break;
   case PlayerActions.NextBookmark or PlayerActions.PreviousBookmark:
    // Follows the time line, not the display order arranged in the details panel.
    var target=action==PlayerActions.NextBookmark ? JumpMarks.Next(Marks,LastPositionSeconds,wrap:true) : JumpMarks.Previous(Marks,LastPositionSeconds,wrap:true);
    if(target!=null)await SeekToMarkAsync(target); break;
   case PlayerActions.NextVideo or PlayerActions.PreviousVideo:
    if(StepRequested!=null)await StepRequested(action==PlayerActions.NextVideo?1:-1); else Notify(T("Back and forward navigation is only available from a library view.")); break;
   case PlayerActions.ToggleBookmark: ToggleBookmarkHere(); break;
   case PlayerActions.ClipStart: ClipStart=LastPositionSeconds; Notify(T("Scene start · ")+Time(LastPositionSeconds)+ClipHint()); break;
   case PlayerActions.ClipEnd: ClipEnd=LastPositionSeconds; Notify(T("Scene end · ")+Time(LastPositionSeconds)+ClipHint()); break;
   case PlayerActions.ClipCreate: CreateSceneFromMarks(); break;
   case PlayerActions.Screenshot: await SaveScreenshotAsync(); break;
   case PlayerActions.SlowMotion: ToggleSlowMotion(); break;
   case PlayerActions.SwitchWheel: SwitchWheel(); break;
   case PlayerActions.AddO: ChangeO(add: true); break;
   case PlayerActions.UndoO: ChangeO(add: false); break;
   case PlayerActions.Mute: ToggleMute(); break;
   case PlayerActions.ToggleMarks: ToggleMarkPanel(); break;
  }
 }

 // Scene marks (original hotkeyclipstart/hotkeyclipend/hotkeyclipcreate): start and end, then one key creates the scene.
 internal double? ClipStart { get; private set; }
 internal double? ClipEnd { get; private set; }
 private string ClipHint()=>ClipStart is { } start && ClipEnd is { } end && end>start ? T(" · Scene {0}–{1} ready", Time(start), Time(end))+KeyHint(PlayerActions.ClipCreate) : "";
 /// <summary>Runs a player action as its shortcut would (used by the smoke test for actions without their own entry point).</summary>
 internal void Act(string action)
 {
  switch(action) { case PlayerActions.ClipStart: ClipStart=LastPositionSeconds; break; case PlayerActions.ClipEnd: ClipEnd=LastPositionSeconds; break; case PlayerActions.ClipCreate: CreateSceneFromMarks(); break; }
 }
 internal void CreateSceneFromMarks()
 {
  if(ClipStart is not { } start || ClipEnd is not { } end || end<=start) { Notify(T("Mark the scene start first and then a later scene end.")+KeyHint(PlayerActions.ClipStart)); return; }
  repository.UpsertScene(new Scene { MediaId=media.Id,Title=T("Scene {0}–{1}", Time(start), Time(end)),StartSeconds=start,EndSeconds=end });
  ClipStart=ClipEnd=null; RefreshMarks(); Notify(T("Scene created · {0}–{1}", Time(start), Time(end)));
 }
 /// <summary>Removes a bookmark close to the current position (within 1 s, or 1 % of a short video), otherwise sets one there.</summary>
 internal void ToggleBookmarkHere()
 {
  var tolerance=Math.Min(1,Math.Max(.05,(media.DurationSeconds??100)*.01));
  var near=repository.GetBookmarks(media.Id).Where(b=>b.Kind==BookmarkKind.Bookmark && Math.Abs(b.PositionSeconds-LastPositionSeconds)<=tolerance).OrderBy(b=>Math.Abs(b.PositionSeconds-LastPositionSeconds)).FirstOrDefault();
  if(near!=null) { repository.DeleteBookmark(near.Id); Notify(T("Bookmark removed · ")+Time(near.PositionSeconds)); }
  else { repository.UpsertBookmark(new Bookmark { MediaId=media.Id,Title=T("Bookmark {0}", Time(LastPositionSeconds)),PositionSeconds=LastPositionSeconds }); Notify(T("Bookmark added · {0}", Time(LastPositionSeconds))); }
  RefreshMarks();
 }
 internal string ScreenshotFolder { get; set; }=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),"FlixyPix");
 /// <summary>Saves the current frame in the folder, format and naming of the settings (original "Save screenshot", "videoscreenshots-*").</summary>
 internal async Task<string> SaveScreenshotAsync()
 {
  var folder=ScreenshotFolder; Directory.CreateDirectory(folder);
  var path=playback.Screenshots.NextPath(folder,media.Title,LastPositionSeconds);
  await RequirePlayer().ScreenshotAsync(path); Notify(T("Screenshot saved · ")+path); return path;
 }
 private (string Text,DateTime Until)? notice;
 /// <summary>A message in the status line for a few seconds; afterwards the line shows title and current mark again.</summary>
 private void Notify(string text) { if(!playback.ShowMessages)return; notice=(text,DateTime.UtcNow.AddSeconds(4)); status.Text=text; }
 private double speedBeforeSlowMotion=1;
 internal bool SlowMotion=>speed.SelectedItem is double value && value<=.25;
 /// <summary>Switches to quarter speed and back to the previous speed (original "Toggle slow motion").</summary>
 internal void ToggleSlowMotion()
 {
  if(SlowMotion) speed.SelectedItem=speedBeforeSlowMotion;
  else { speedBeforeSlowMotion=speed.SelectedItem is double value?value:1; speed.SelectedItem=.25; }
  Notify(SlowMotion?T("Slow motion · 0.25×"):T("Normal speed"));
 }
 /// <summary>Media keys of keyboards and headsets: play/pause, previous/next jump mark, stop closes the player (empty action).</summary>
 internal static string? MediaKeyAction(Key key)=>key switch
 {
  Key.MediaPlayPause or Key.Play or Key.Pause=>PlayerActions.PlayPause,
  Key.MediaNextTrack=>PlayerActions.NextBookmark,
  Key.MediaPreviousTrack=>PlayerActions.PreviousBookmark,
  Key.MediaStop=>"",
  _=>null,
 };
 internal bool IsFullscreen=>WindowStyle==WindowStyle.None;
 internal bool IsPaused=>paused;
 internal Slider Timeline=>position;
 internal void ToggleFullscreen()
 {
  if(IsFullscreen) { WindowStyle=WindowStyle.SingleBorderWindow; WindowState=WindowState.Normal; LeaveOverlayMode(); }
  else { WindowStyle=WindowStyle.None; WindowState=WindowState.Maximized; Dispatcher.BeginInvoke(EnterOverlayMode,DispatcherPriority.Loaded); }
 }
 private sealed class NativeVideoHost : HwndHost
 {
  internal event Action<string>? Input;
  private const string ClassName="FlixyPix.VideoInput";
  private static readonly Dictionary<nint,NativeVideoHost> owners=new();
  private delegate nint WindowProc(nint hwnd,uint message,nint wParam,nint lParam);
  private static readonly WindowProc procedure=ProcessMessage;
  private static bool registered;
  [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct WindowClass
  {
   public uint Style; public WindowProc Procedure; public int ClassExtra,WindowExtra;
   public nint Instance,Icon,Cursor,Background; public string? Menu; public string Name;
  }
  private static void EnsureClass()
  {
   if(registered)return;
   var cls=new WindowClass { Style=8,Procedure=procedure,Instance=GetModuleHandle(null),Name=ClassName,Cursor=LoadCursor(0,32512) };
   if(RegisterClass(ref cls)==0)throw new Win32Exception(Marshal.GetLastWin32Error()); registered=true;
  }
  private int wheelRemainder;
  private ModifierKeys wheelModifiers;
  internal void Wheel(int delta,ModifierKeys modifiers)
  {
   if(modifiers!=wheelModifiers) { wheelRemainder=0; wheelModifiers=modifiers; }
   wheelRemainder+=delta;
   while(Math.Abs(wheelRemainder)>=120)
   {
    int step=Math.Sign(wheelRemainder)*120; wheelRemainder-=step;
    Input?.Invoke(WheelInput(step,modifiers));
   }
  }
  private static nint ProcessMessage(nint hwnd,uint message,nint wParam,nint lParam)
  {
   if(owners.TryGetValue(hwnd,out var owner))
   {
    string? input=message switch { 0x0202=>"MBTN_LEFT",0x0203=>"MBTN_LEFT_DBL",0x0205=>"MBTN_RIGHT",0x0208=>"MBTN_MID",
     0x020C=>((wParam.ToInt64()>>16)&0xffff)==1?"MBTN_BACK":"MBTN_FORWARD",_=>null };
    // mpv disables its child in embedding mode. Both application-owned parents handle input instead.
    if(message==0x0084)return 1; // HTCLIENT, including margins around a transformed picture
    if(message==0x020A)
    {
     var flags=wParam.ToInt64();
     var modifiers=((flags&8)!=0?ModifierKeys.Control:ModifierKeys.None)|((flags&4)!=0?ModifierKeys.Shift:ModifierKeys.None)
      |(GetKeyState(0x12)<0?ModifierKeys.Alt:ModifierKeys.None);
     owner.Wheel(unchecked((short)(flags>>16)),modifiers);
     return 0;
    }
    if(input!=null) { owner.Input?.Invoke(input); return message==0x020C?1:0; }
   }
   return DefWindowProc(hwnd,message,wParam,lParam);
  }
  [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern ushort RegisterClass(ref WindowClass cls);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern nint DefWindowProc(nint hwnd,uint message,nint wParam,nint lParam);
  [DllImport("user32.dll")] private static extern nint LoadCursor(nint instance,int name);
  [DllImport("user32.dll")] private static extern short GetKeyState(int key);
  private nint outer;
  internal nint VideoHandle { get; private set; }
  internal (int X,int Y,int Width,int Height) LastBounds { get; private set; }
  private PlayerTransform geometry=PlayerTransformTimeline.Identity;
  protected override HandleRef BuildWindowCore(HandleRef parent)
  {
   const int style=0x40000000|0x10000000|0x02000000|0x04000000;
   EnsureClass();
   outer=CreateWindowEx(0,ClassName,"",style,0,0,1,1,parent.Handle,0,0,0);
   if(outer==0)throw new Win32Exception(Marshal.GetLastWin32Error());
   owners[outer]=this;
   VideoHandle=CreateWindowEx(0,ClassName,"",style,0,0,1,1,outer,0,0,0);
   if(VideoHandle==0) { var error=Marshal.GetLastWin32Error(); owners.Remove(outer); DestroyWindow(outer); outer=0; throw new Win32Exception(error); }
   owners[VideoHandle]=this; return new(this,outer);
  }
  protected override void DestroyWindowCore(HandleRef hwnd) { owners.Remove(VideoHandle); owners.Remove(outer); if(VideoHandle!=0)DestroyWindow(VideoHandle); if(outer!=0)DestroyWindow(outer); VideoHandle=outer=0; }
  internal void Apply(PlayerTransform transform)
  {
   geometry=transform; if(outer==0 || VideoHandle==0)return; if(!GetClientRect(outer,out var rect))return;
   var width=Math.Max(1,rect.Right); var height=Math.Max(1,rect.Bottom);
   var bounds=((int)Math.Round(transform.X*width),(int)Math.Round(transform.Y*height),Math.Max(1,(int)Math.Round(transform.Width*width)),Math.Max(1,(int)Math.Round(transform.Height*height)));
   SetWindowPos(VideoHandle,0,bounds.Item1,bounds.Item2,bounds.Item3,bounds.Item4,0x0014); LastBounds=bounds;
  }
  protected override void OnWindowPositionChanged(Rect rectangle) { base.OnWindowPositionChanged(rectangle); Apply(geometry); }
  [StructLayout(LayoutKind.Sequential)] private struct NativeRect { internal int Left,Top,Right,Bottom; }
  [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern nint CreateWindowEx(int exStyle,string className,string title,int style,int x,int y,int width,int height,nint parent,nint menu,nint instance,nint parameter);
  [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
  [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd,out NativeRect rectangle);
  [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd,nint insertAfter,int x,int y,int width,int height,uint flags);
 }
}
