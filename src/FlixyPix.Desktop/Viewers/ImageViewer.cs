using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FlixyPix.Core;
using FlixyPix.Imaging;

namespace FlixyPix.Desktop;

/// <summary>Image viewer (the original's frmMaxPic): symbol bar, zoom and rotation, pages and animation frames, a film strip of
/// the neighbouring images, details over the picture and a slideshow with the original's options. It is a part of a window:
/// the loupe of the main window, in place of the tiles, or the content of an <see cref="ImageViewerWindow"/>.</summary>
internal sealed partial class ImageViewer : ContentControl
{
 private const int StripRadius = 7;
 /// <summary>Longest side of a RAW development for the fitted picture; one size, so a development is found again.</summary>
 internal const int DevelopedFit = 3200;
 private readonly List<MediaItem> _items;
 private Func<int,Task<MediaItem?>>? _itemAt;
 /// <summary>The item at a position without side effects: unlike <see cref="_itemAt"/> it never extracts an archive image.</summary>
 private Func<int,Task<MediaItem?>>? _peekAt;
 /// <summary>The loupe: keys and ends of the list follow the tiles it stands for.</summary>
 private readonly bool _embedded;
 internal bool Embedded => _embedded;
 private int _itemCount;
 private MediaItem? _currentItem;
 private readonly Image _image = new() { Stretch=Stretch.None, RenderTransformOrigin=new Point(.5,.5) };
 // No scroll bars: a zoomed picture is moved by dragging it.
 private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility=ScrollBarVisibility.Hidden, VerticalScrollBarVisibility=ScrollBarVisibility.Hidden, Background=Brushes.Black, Focusable=false };
 private readonly TextBlock _label = new() { VerticalAlignment=VerticalAlignment.Center, Margin=new Thickness(12,0,8,0), FontSize=13 };
 /// <summary>Camera, lens and exposure of the shot, where the file tells them.</summary>
 private readonly TextBlock _capture = new() { VerticalAlignment=VerticalAlignment.Center, HorizontalAlignment=HorizontalAlignment.Right, Margin=new Thickness(12,0,12,0), FontSize=12, TextTrimming=TextTrimming.CharacterEllipsis };
 private readonly Button _play = Symbol("",T("Pause animation (Space)"));
 private readonly Button _slideshow = Symbol("",T("Start slideshow (S)"));
 private readonly StackPanel _frameButtons = new() { Orientation=Orientation.Horizontal };
 private readonly CheckBox _infoToggle = Toggle("",T("Show image details (I)")), _stripToggle = Toggle("",T("Show film strip (T)"));
 private readonly Border _infoPanel = new() { Background=new SolidColorBrush(Color.FromArgb(205,10,12,16)), CornerRadius=new CornerRadius(8),
  Padding=new Thickness(14,10,14,12), Margin=new Thickness(16), HorizontalAlignment=HorizontalAlignment.Left, VerticalAlignment=VerticalAlignment.Top,
  Visibility=Visibility.Collapsed, IsHitTestVisible=false, MaxWidth=420 };
 private readonly TextBlock _infoText = new() { Foreground=Brushes.White, TextWrapping=TextWrapping.Wrap, FontSize=13, LineHeight=20 };
 private readonly StackPanel _strip = new() { Orientation=Orientation.Horizontal, HorizontalAlignment=HorizontalAlignment.Center, Margin=new Thickness(8,6,8,8) };
 private readonly Border _stripPanel = new() { Visibility=Visibility.Collapsed };
 private readonly DispatcherTimer _animation = new(), _slideTimer = new();
 private readonly Random _random = new();
 private CancellationTokenSource? _load, _stripLoad;
 private ImageInfo? _info;
 /// <summary>The current image came through the Windows decoder; another size of it is decoded the same way.</summary>
 private bool _windowsDecoded;
 /// <summary>The next and the previous image, decoded beside the window so turning the page shows them at once.</summary>
 private readonly Dictionary<(string Path, int Bound), (Task<FastImageDecoder.Result?> Work, long Length, DateTime Modified)> _prefetched = new();
 private readonly CancellationTokenSource _prefetching = new();
 internal bool WindowsDecoded => _windowsDecoded;
 internal int PrefetchedCount => _prefetched.Count;
 private int _index, _rotation, _frame, _runInOrder, _direction=1, _prefetchRound, _moves;
 private uint _animationLoops;
 private double _zoom=1;
 private bool _fit=true, _playing=true, _closed, _started, _placeholder;
 /// <summary>Counts the changes of the image, so a late answer about an earlier one is told apart.</summary>
 internal int Moves => _moves;
 /// <summary>The picture on show is only the thumbnail, while the image itself is read.</summary>
 internal bool ShowsPlaceholder => _placeholder;
 internal bool Fitted => _fit;
 internal string Title { get; private set; } = "";
 internal event EventHandler? TitleChanged;
 /// <summary>Another image is on show; its item is known.</summary>
 internal event Action<MediaItem>? CurrentChanged;
 internal event EventHandler? Closed;
 internal int CurrentIndex => _index;
 internal int Count => _itemCount;
 internal int CurrentFrame => _frame;
 internal int FrameCount => _info?.FrameCount ?? 0;
 internal bool IsAnimated => _info?.IsAnimated == true;
 internal bool HasImage => _image.Source != null;
 internal int Rotation => _rotation;
 internal double ZoomFactor => _zoom;
 internal Task ReadyTask { get; private set; } = Task.CompletedTask;
 internal Task StripTask { get; private set; } = Task.CompletedTask;
 internal bool InfoShown => _infoPanel.Visibility == Visibility.Visible;
 internal string InfoText => _infoText.Text;
 internal int StripCount => _strip.Children.Count;
 internal bool SlideshowRunning => _slideTimer.IsEnabled;
 /// <summary>Mouse wheel without Ctrl, as the original's image viewer setting; Ctrl+wheel always zooms.</summary>
 internal ImageWheelAction WheelAction { get; set; } = ImageWheelAction.Navigate;
 /// <summary>Moving to another image cross-fades (library setting "Bildwechsel überblenden").</summary>
 internal bool FadeOnChange { get; set; }
 private bool _fadeNext;
 internal bool LastChangeFaded { get; private set; }
 internal bool MediaKeys { get; set; } = true;
 internal SlideshowPreferences Slideshow { get; set; } = new();

 internal ImageViewer(IReadOnlyList<MediaItem> items, Guid selectedId, Func<int,Task<MediaItem?>>? itemAt=null, int? itemCount=null, int? initialIndex=null, Func<int,Task<MediaItem?>>? peekAt=null, bool embedded=false)
 {
  _embedded=embedded; _items=[.. items]; _itemAt=itemAt; _peekAt=peekAt; _itemCount=itemCount??items.Count; _index=initialIndex??Math.Max(0,items.ToList().FindIndex(i=>i.Id==selectedId));
  Focusable=true; IsTabStop=true; FocusVisualStyle=null; HorizontalContentAlignment=HorizontalAlignment.Stretch; VerticalContentAlignment=VerticalAlignment.Stretch;
  var toolbar=new DockPanel { Margin=new Thickness(8,6,8,6), LastChildFill=true };
  var left=new StackPanel { Orientation=Orientation.Horizontal };
  if(embedded) { left.Children.Add(Command("",T("Back to the tiles (G)"),()=>Close())); left.Children.Add(Gap()); }
  left.Children.Add(Command("",T("Previous image (←)"),()=>Navigate(-1))); left.Children.Add(Command("",T("Next image (→)"),()=>Navigate(1)));
  left.Children.Add(Gap());
  left.Children.Add(Command("",T("Zoom out (−)"),()=>ChangeZoom(1/1.25))); left.Children.Add(Command("",T("Zoom in (+)"),()=>ChangeZoom(1.25)));
  left.Children.Add(Command("",T("Fit (F)"),Fit)); left.Children.Add(Command("",T("Original size 1:1 (Z)"),()=>OriginalSize()));
  left.Children.Add(Command("",T("Rotate (R)"),Rotate));
  _frameButtons.Children.Add(Command("",T("Previous page or previous frame of the animation (Page↑)"),()=>ChangeFrame(-1))); _frameButtons.Children.Add(_play);
  _frameButtons.Children.Add(Command("",T("Next page or next frame of the animation (Page↓)"),()=>ChangeFrame(1)));
  _play.Click+=(_,_)=>ToggleAnimation(); left.Children.Add(_frameButtons); left.Children.Add(BuildRawSwitch()); left.Children.Add(_label);
  DockPanel.SetDock(left,Dock.Left); toolbar.Children.Add(left);
  var right=new StackPanel { Orientation=Orientation.Horizontal };
  _infoToggle.Click+=(_,_)=>SetInfo(_infoToggle.IsChecked==true); _stripToggle.Click+=(_,_)=>SetStrip(_stripToggle.IsChecked==true);
  _slideshow.Click+=(_,_)=>ToggleSlideshow();
  var more=Symbol("\uE712",T("More actions")); more.Click+=(_,_)=>ShowActions(more);
  right.Children.Add(_infoToggle); right.Children.Add(_stripToggle); right.Children.Add(_slideshow); right.Children.Add(more);
  DockPanel.SetDock(right,Dock.Right); toolbar.Children.Add(right);
  _capture.SetResourceReference(TextBlock.ForegroundProperty,"Muted"); toolbar.Children.Add(_capture);
  _infoPanel.Child=_infoText;
  _stripPanel.SetResourceReference(Border.BackgroundProperty,"ChromeBackground");
  _stripPanel.Child=new ScrollViewer { Content=_strip, HorizontalScrollBarVisibility=ScrollBarVisibility.Auto, VerticalScrollBarVisibility=ScrollBarVisibility.Disabled };
  var stage=new Grid(); _scroll.Content=_image; stage.Children.Add(_scroll); stage.Children.Add(_infoPanel);
  var layout=new DockPanel(); DockPanel.SetDock(toolbar,Dock.Top); DockPanel.SetDock(_stripPanel,Dock.Bottom);
  layout.Children.Add(toolbar); layout.Children.Add(_stripPanel); layout.Children.Add(stage); Content=layout;
  _scroll.SizeChanged+=(_,_)=>{ if(_fit) Fit(); };
  _scroll.MouseRightButtonUp+=(_,e)=>{ e.Handled=true; ShowActions(null); };
  _scroll.PreviewMouseLeftButtonDown+=StagePressed; _scroll.PreviewMouseMove+=StageMoved; _scroll.PreviewMouseLeftButtonUp+=StageReleased;
  _scroll.LostMouseCapture+=(_,_)=>_press=null;
  Loaded+=(_,_)=>
  {
   if(_started || _closed) return; _started=true;
   SetInfo(Slideshow.ShowInfo); if(_embedded) { _stripToggle.IsChecked=true; _stripPanel.Visibility=Visibility.Visible; }
   ReadyTask=LoadCurrentAsync();
  };
  _animation.Tick+=async (_,_)=>
  {
   _animation.Stop(); if(!_playing || _info?.IsAnimated!=true) return;
   var next=(_frame+1)%_info.FrameCount;
   if(next==0 && ++_animationLoops>=_info.AnimationIterations && _info.AnimationIterations>0) { _playing=false; UpdatePlayButton(); return; }
   _frame=next; ReadyTask=ShowFrameAsync(_load?.Token??default); await ReadyTask;
  };
  _slideTimer.Tick+=(_,_)=>ShowNextSlide();
  PreviewMouseWheel+=(_,e)=>{ if(HandleWheel(e.Delta,(Keyboard.Modifiers&ModifierKeys.Control)!=0)) e.Handled=true; };
 }

 /// <summary>Ends the viewer: its work stops and the window or the loupe that holds it closes.</summary>
 internal void Close()
 {
  if(_closed) return; _closed=true;
  _animation.Stop(); _slideTimer.Stop(); _load?.Cancel(); _load?.Dispose(); _stripLoad?.Cancel(); _prefetching.Cancel(); _prefetched.Clear();
  Closed?.Invoke(this,EventArgs.Empty);
 }

 /// <summary>The keys of the viewer. Its window or the main window passes on what reaches it; true when the key was one
 /// of them. As the loupe it leaves the keys of the tiles alone (rating, favorite, tag mode, removing) and Esc, G, Enter
 /// or the space bar lead back to the tiles.</summary>
 internal bool HandleKey(KeyEventArgs e)
 {
  if(_closed) return false;
  if(e.Key==Key.F1 && !_embedded) { if(Window.GetWindow(this) is { } owner) new HelpWindow(owner,T("Help"),HelpWindow.Guide()).ShowDialog(); return true; }
  if(MainWindow.TextEntry(e.OriginalSource))return false;
  if(Keyboard.FocusedElement is System.Windows.Controls.Primitives.ButtonBase && e.Key is Key.Enter or Key.Space)return false;
  if(_embedded)
  {
   var modifiers=Keyboard.Modifiers;
   if(e.Key==Key.C && modifiers==ModifierKeys.Control) { CopyImage(); return true; }
   if(e.Key==Key.C && modifiers==(ModifierKeys.Control|ModifierKeys.Shift)) { CopyPath(); return true; }
   if(modifiers!=ModifierKeys.None) return false;
   switch(e.Key)
   {
    case Key.Escape: if(SlideshowRunning) StopSlideshow(); else Close(); return true;
    case Key.G: case Key.Enter: Close(); return true;
    case Key.Space: if(SlideshowRunning) StopSlideshow(); else if(IsAnimated) ToggleAnimation(); else Close(); return true;
    case Key.Home: StopSlideshow(); GoTo(0); return true; case Key.End: StopSlideshow(); GoTo(_itemCount-1); return true;
    case Key.Z: ToggleZoom(); return true;
    case Key.H: Flip(true); return true; case Key.V: Flip(false); return true;
    case Key.F: case Key.T: case Key.Delete: return false;
   }
  }
  else if(HandleActionKey(e)) return true;
  switch(e.Key)
  {
   case Key.Escape: if(SlideshowRunning) StopSlideshow(); else Close(); break;
   case Key.Right: StopSlideshow(); Navigate(1); break; case Key.Left: StopSlideshow(); Navigate(-1); break;
   case Key.Add: case Key.OemPlus: ChangeZoom(1.25); break; case Key.Subtract: case Key.OemMinus: ChangeZoom(1/1.25); break;
   case Key.F: Fit(); break; case Key.R: Rotate(); break; case Key.D: ToggleDevelop(); break;
   case Key.Z: ToggleZoom(); break;
   case Key.Space: if(SlideshowRunning) StopSlideshow(); else ToggleAnimation(); break;
   case Key.S: ToggleSlideshow(); break; case Key.I: SetInfo(!InfoShown); break; case Key.T: SetStrip(_stripPanel.Visibility!=Visibility.Visible); break;
   case Key.PageDown: ChangeFrame(1); break; case Key.PageUp: ChangeFrame(-1); break;
   case Key.MediaNextTrack when MediaKeys: Navigate(1); break; case Key.MediaPreviousTrack when MediaKeys: Navigate(-1); break;
   case Key.MediaPlayPause when MediaKeys: ToggleAnimation(); break; case Key.MediaStop when MediaKeys: Close(); break; default:return false;
  }
  return true;
 }

 // The mouse on the picture: a click changes between the fitted picture and its pixels at that place; a zoomed picture
 // is moved by dragging it.
 private Point? _press; private Point _pressOffset; private bool _dragged;
 private void StagePressed(object sender,MouseButtonEventArgs e)
 {
  Focus(); if(_image.Source==null) return;
  _press=e.GetPosition(_scroll); _pressOffset=new(_scroll.HorizontalOffset,_scroll.VerticalOffset); _dragged=false; _scroll.CaptureMouse(); e.Handled=true;
 }
 private void StageMoved(object sender,MouseEventArgs e)
 {
  if(_press is not { } from || e.LeftButton!=MouseButtonState.Pressed) return;
  var delta=e.GetPosition(_scroll)-from;
  if(!_dragged && Math.Abs(delta.X)<4 && Math.Abs(delta.Y)<4) return;
  _dragged=true; _hold=null; _scroll.ScrollToHorizontalOffset(_pressOffset.X-delta.X); _scroll.ScrollToVerticalOffset(_pressOffset.Y-delta.Y);
 }
 private void StageReleased(object sender,MouseButtonEventArgs e)
 {
  if(_press is not { } at) return;
  _press=null; _scroll.ReleaseMouseCapture(); e.Handled=true;
  if(!_dragged) { StopSlideshow(); ToggleZoom(at); }
 }

 /// <summary>A place of the picture, as a share of its width and height, and the place of the stage it is to stay at
 /// while the zoom changes or a larger picture replaces the one on show.</summary>
 private (Point Share, Point At)? _hold;
 private void Hold(Point? at)
 {
  _hold=null; if(_image.Source==null || _image.ActualWidth<=0 || _image.ActualHeight<=0 || _scroll.ActualWidth<=0) return;
  var point=at ?? new Point(_scroll.ActualWidth/2,_scroll.ActualHeight/2);
  var inside=_scroll.TranslatePoint(point,_image);
  _hold=(new(Math.Clamp(inside.X/_image.ActualWidth,0,1),Math.Clamp(inside.Y/_image.ActualHeight,0,1)),point);
 }
 private void Settle()
 {
  if(_hold is not { } hold || _fit) return;
  _scroll.UpdateLayout();
  var now=_image.TranslatePoint(new(hold.Share.X*_image.ActualWidth,hold.Share.Y*_image.ActualHeight),_scroll);
  _scroll.ScrollToHorizontalOffset(_scroll.HorizontalOffset+now.X-hold.At.X); _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset+now.Y-hold.At.Y);
 }
 /// <summary>From the fitted picture to its pixels and back; <paramref name="at"/> is the place of the stage that stays.</summary>
 internal void ToggleZoom(Point? at=null) { if(_image.Source==null) return; if(_fit) OriginalSize(at); else Fit(); }

 private static Button Symbol(string glyph,string tip)
 {
  var button=new Button { Content=glyph, Style=(Style)Application.Current.Resources["IconButton"], ToolTip=tip, Margin=new Thickness(1,0,1,0) };
  AutomationProperties.SetName(button,tip); return button;
 }
 private static Button Command(string glyph,string tip,Action action) { var button=Symbol(glyph,tip); button.Click+=(_,_)=>action(); return button; }
 private static CheckBox Toggle(string glyph,string tip)
 {
  var toggle=new CheckBox { Content=glyph, Tag=glyph, Style=(Style)Application.Current.Resources["IconToggle"], ToolTip=tip, Margin=new Thickness(2,0,2,0) };
  AutomationProperties.SetName(toggle,tip); return toggle;
 }
 private static Border Gap() { var gap=new Border { Width=1, Height=20, Margin=new Thickness(8,0,8,0) }; gap.SetResourceReference(Border.BackgroundProperty,"Divider"); return gap; }

 internal bool HandleWheel(int delta,bool control)
 {
  if(control || WheelAction==ImageWheelAction.Zoom) { ChangeZoom(delta>0?1.15:1/1.15,_scroll.IsMouseOver?Mouse.GetPosition(_scroll):null); return true; }
  if(WheelAction==ImageWheelAction.Navigate) { Navigate(delta>0?-1:1); return true; }
  return false;
 }
 /// <summary>To a neighbouring image. The window goes round from the last to the first; the loupe ends where the tiles end.</summary>
 internal void Navigate(int delta)
 {
  if(_embedded && delta!=0 && (_index+delta<0 || _index+delta>=_itemCount)) return;
  GoTo(_index+delta);
 }
 private void GoTo(int index)
 {
  if(_itemCount==0)return;
  var target=((index%_itemCount)+_itemCount)%_itemCount;
  if(target!=_index) _direction=target>_index?1:-1;
  _index=target; _moves++; _rotation=0; _flipX=_flipY=false; _fit=true; _hold=null; _playing=true; _fadeNext=FadeOnChange && !SlideshowRunning; ReadyTask=LoadCurrentAsync();
 }

 /// <summary>The list behind the viewer changed (a filter, a rating, a removed item): it goes on with the new one. With
 /// <paramref name="index"/> the image on show is still in it and stays; without, the image at the same place follows.</summary>
 internal async Task RebaseAsync(Func<int,Task<MediaItem?>> itemAt,Func<int,Task<MediaItem?>>? peekAt,int count,int? index,int moves)
 {
  if(_closed) return;
  _itemAt=itemAt; _peekAt=peekAt; _itemCount=count; _prefetchRound++;
  if(count==0) { Close(); return; }
  // Moved on meanwhile: the answer was about an earlier image.
  if(moves!=_moves) { _index=Math.Clamp(_index,0,count-1); return; }
  if(index is not { } place) { _index=Math.Clamp(_index,0,count-1); _moves++; _fit=true; _hold=null; ReadyTask=LoadCurrentAsync(); await ReadyTask; return; }
  _index=place;
  try { if(await ItemAtAsync(place) is { } fresh && moves==_moves && !_closed) { _currentItem=fresh; UpdateInfo(); } }
  catch(Exception error) when(error is OperationCanceledException or IOException or InvalidOperationException) { }
  if(_closed) return;
  if(_image.Source!=null) Apply();
  if(_stripPanel.Visibility==Visibility.Visible) StripTask=LoadStripAsync();
 }
 internal void ChangeFrame(int delta)
 {
  if(_info==null || _info.FrameCount<=1)return;
  _playing=false; _animationLoops=0; _animation.Stop(); _frame=(_frame+delta+_info.FrameCount)%_info.FrameCount;
  _load?.Cancel(); _load?.Dispose(); _load=new(); ReadyTask=ShowFrameAsync(_load.Token);
 }
 internal void ToggleAnimation()
 {
  if(_info?.IsAnimated!=true)return;
  _playing=!_playing; if(_playing)_animationLoops=0; UpdatePlayButton();
  if(_playing) ScheduleFrame(); else _animation.Stop();
 }
 private void UpdatePlayButton() { _play.Content=_playing?"":""; _play.ToolTip=_playing?T("Pause animation (Space)"):T("Play animation (Space)"); }

 private async Task LoadCurrentAsync()
 {
  _animation.Stop(); _load?.Cancel(); _load?.Dispose(); _load=new(); var token=_load.Token;
  _info=null; _windowsDecoded=false; _frame=0; _animationLoops=0; _label.Text=$"{_index+1:N0} / {_itemCount:N0}";
  _develop=_developing=_keepPreview=_keepSize=false; _path=_rawPath=null; _capture.Text=""; UpdateRawSwitch();
  // The stage is never empty between two images: the one before stays until the thumbnail of the next is there, and
  // that one until the image itself is read.
  if(_itemCount==0) { _image.Source=null; return; }
  try
  {
   var item=await ItemAtAsync(_index); token.ThrowIfCancellationRequested();
   _currentItem=item??throw new IOException(T("The image is no longer in the catalog."));
   Title=item.Title+T(" · Image viewer"); TitleChanged?.Invoke(this,EventArgs.Empty);
   CurrentChanged?.Invoke(item);
   if(_stripPanel.Visibility==Visibility.Visible) StripTask=LoadStripAsync();
   // Common still images come through the Windows decoder in a fraction of the time; everything else through ImageMagick.
   // A pair of RAW file and camera picture shows the picture, and the RAW file once the picture is gone.
   var path=_path=await Task.Run(()=>RawPairs.DisplayPath(item),token);
   _rawPath=RawPairs.RawOf(item);
   var bound=Bound(); var raw=_rawPath;
   var waiting=!(_prefetched.TryGetValue((path,bound),out var ready) && ready.Work.IsCompletedSuccessfully);
   if(waiting && !_fadeNext && !SlideshowRunning && await ThumbnailAsync(item,token) is { } small)
   {
    ShowPlaceholder(small);
    // Leafing through quickly shows thumbnails only: an image passed at once is not read.
    await Task.Delay(40,token);
   }
   // A RAW file that was developed before shows that development at once.
   var developed=raw==null ? null : await Task.Run(()=>DesktopImaging.Service.FindDeveloped(raw,DevelopedFit),token);
   var finished=developed==null ? null : FastImageDecoder.DecodeAsync(developed,DevelopedFit,token);
   var fast=await DecodeFastAsync(path,bound,token); if(token.IsCancellationRequested)return;
   if(fast!=null)
   {
    _windowsDecoded=true; _info=fast.Info;
    if(finished!=null && await finished is { } picture && !token.IsCancellationRequested) { _develop=true; Present(picture.Bitmap); }
    else Present(fast.Bitmap);
    if(token.IsCancellationRequested)return;
    Prefetch(); _ = ShowCaptureAsync(path,raw,token);
    await DevelopByItselfAsync(token); return;
   }
   _info=await DesktopImaging.Service.GetInfoAsync(path,token); if(token.IsCancellationRequested)return; await ShowFrameAsync(token);
   if(!token.IsCancellationRequested) _ = ShowCaptureAsync(path,raw,token);
  }
  catch(OperationCanceledException) { }
  catch(Exception error) when(error is IOException or InvalidDataException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
  { if(!token.IsCancellationRequested) { _image.Source=null; _placeholder=false; _label.Text=T("The image cannot be shown: ")+error.Message; } }
 }

 /// <summary>Thumbnails of the catalog, kept for the stage and the film strip while leafing through.</summary>
 private readonly Dictionary<string,BitmapSource> _thumbnails = new(StringComparer.OrdinalIgnoreCase);
 private async Task<BitmapSource?> ThumbnailAsync(MediaItem item,CancellationToken token)
 {
  if(item.ThumbnailPath is not { Length: >0 } path) return null;
  if(_thumbnails.TryGetValue(path,out var known)) return known;
  var loaded=await Task.Run(()=>MediaCard.LoadBitmap(path,0),token);
  if(loaded==null) return null;
  if(_thumbnails.Count>=60) _thumbnails.Clear();
  return _thumbnails[path]=loaded;
 }
 private void ShowPlaceholder(BitmapSource small)
 {
  _placeholder=true; _image.BeginAnimation(OpacityProperty,null); _image.Opacity=1;
  _image.Source=small; _image.Width=small.PixelWidth; _image.Height=small.PixelHeight; Fit();
 }

 /// <summary>While the RAW switch is on, a RAW file is developed when it is looked at; not while leafing past it and
 /// not in a slideshow.</summary>
 private async Task DevelopByItselfAsync(CancellationToken token)
 {
  if(!DevelopRaw || _develop || SlideshowRunning || _rawPath==null || _info==null || _info is { IsRaw: true, ShownByPreview: false }) return;
  await Task.Delay(300,token);
  if(_closed || _develop || _developing || _keepPreview || !File.Exists(_rawPath)) return;
  _develop=_developing=true; _keepSize=!_fit; UpdateRawSwitch();
  await ShowFrameAsync(token);
  if(!token.IsCancellationRequested) { _developing=false; UpdateRawSwitch(); UpdateInfo(); }
 }

 private readonly Dictionary<string,string> _captures = new(StringComparer.OrdinalIgnoreCase);
 /// <summary>Camera, lens and exposure beside the symbols; read from the file after the picture is there. A pair tells
 /// them from its picture, and from its RAW file when the picture has none.</summary>
 private async Task ShowCaptureAsync(string path,string? raw,CancellationToken token)
 {
  try
  {
   if(_info is { FrameCount: >1 }) return;
   if(!_captures.TryGetValue(path,out var line))
   {
    line=await Task.Run(()=>CaptureFacts.Read(path,token)?.Line ?? (raw!=null && raw!=path ? CaptureFacts.Read(raw,token)?.Line : null) ?? "",token);
    if(_captures.Count>=500) _captures.Clear(); _captures[path]=line;
   }
   if(token.IsCancellationRequested || _closed || path!=_path) return;
   _capture.Text=line; _capture.ToolTip=line.Length>0?line:null; UpdateInfo();
  }
  catch(Exception error) when(error is OperationCanceledException or IOException or InvalidDataException or UnauthorizedAccessException) { }
 }
 internal string CaptureLine => _capture.Text;
 private Task<MediaItem?> ItemAtAsync(int index)=>_itemAt==null?Task.FromResult<MediaItem?>(index>=0 && index<_items.Count?_items[index]:null):_itemAt(index);

 private async Task ShowFrameAsync(CancellationToken token)
 {
  var develop=_develop && _rawPath!=null;
  try
  {
   if(_currentItem==null || _path is not { } path)return; var frame=_frame; var bound=Bound();
   BitmapSource bitmap;
   if(develop) { var size=_fit?DevelopedFit:0; bitmap=DesktopImaging.Bitmap((await DesktopImaging.Service.DecodeAsync(_rawPath!,0,size,size,token,develop:true)).PngBytes); }
   else if(_windowsDecoded && frame==0 && await DecodeFastAsync(path,bound,token) is { } fast) bitmap=fast.Bitmap;
   else bitmap=DesktopImaging.Bitmap((await DesktopImaging.Service.DecodeAsync(path,frame,bound,bound,token)).PngBytes);
   if(_closed || token.IsCancellationRequested || path!=_path || frame!=_frame || develop!=_develop)return;
   Present(bitmap);
  }
  catch(OperationCanceledException) { }
  catch(Exception error) when(error is IOException or InvalidDataException or ArgumentException or InvalidOperationException or UnauthorizedAccessException) { if(!token.IsCancellationRequested)
   {
    _playing=false; _label.Text=T("The image cannot be shown: ")+error.Message;
    // A RAW file that cannot be developed keeps its picture on show, and zooming does not try again.
    if(develop && _develop) { _develop=_developing=_keepSize=false; _keepPreview=true; UpdateRawSwitch(); }
   } }
 }
 /// <summary>Longest side to decode: twice the window when fitting, so zooming in a little stays sharp; full size otherwise.</summary>
 private int Bound()=>_fit?Math.Max(1600,(int)Math.Max(_scroll.ActualWidth,_scroll.ActualHeight)*2):0;

 private void Present(BitmapSource bitmap)
 {
  // Preview and developed picture differ in size: what was on screen keeps its size when one replaces the other.
  if(_keepSize && !_fit && _image.Source is BitmapSource before) _zoom=Math.Clamp(_zoom*Math.Max(before.PixelWidth,before.PixelHeight)/Math.Max(bitmap.PixelWidth,bitmap.PixelHeight),.02,16);
  _keepSize=false; _placeholder=false;
  _image.Source=bitmap; _image.Width=bitmap.PixelWidth; _image.Height=bitmap.PixelHeight;
  if(_fit)Fit(); else { Apply(); Settle(); }
  _frameButtons.Visibility=_info?.FrameCount>1?Visibility.Visible:Visibility.Collapsed; _play.Visibility=_info?.IsAnimated==true?Visibility.Visible:Visibility.Collapsed; UpdatePlayButton();
  UpdateInfo(); UpdateRawSwitch(); ScheduleFrame();
  if(SlideshowRunning) Transition();
  else if(_fadeNext) { _fadeNext=false; LastChangeFaded=true; _image.BeginAnimation(OpacityProperty,new DoubleAnimation(.15,1,TimeSpan.FromMilliseconds(280))); }
  else LastChangeFaded=false;
 }

 /// <summary>A prefetched image is used when the file is still the same; otherwise the Windows decoder reads it now.</summary>
 private async Task<FastImageDecoder.Result?> DecodeFastAsync(string path,int bound,CancellationToken token)
 {
  if(_prefetched.Remove((path,bound),out var ready))
  {
   var file=new FileInfo(path);
   if(file.Exists && file.Length==ready.Length && file.LastWriteTimeUtc==ready.Modified)
    try { if(await ready.Work.WaitAsync(token) is { } done) return done; }
    catch(OperationCanceledException) when(!token.IsCancellationRequested) { }
  }
  return await FastImageDecoder.DecodeAsync(path,bound,token);
 }

 /// <summary>Reads ahead: the next two images in the direction of travel and the one just left. A large image costs
 /// tens of megabytes, so what is no longer near is let go.</summary>
 private async void Prefetch()
 {
  if(_closed || _itemCount<2 || !_fit) return;
  var bound=Bound(); var round=++_prefetchRound; var step=_direction<0?-1:1;
  var near=new[] { _index+step, _index+2*step, _index-step }.Where(i=>!_embedded || i>=0 && i<_itemCount)
   .Select(i=>((i%_itemCount)+_itemCount)%_itemCount).Distinct().Where(i=>i!=_index).ToArray();
  try
  {
   var wanted=new List<(string Path,int Bound)>();
   foreach(var index in near)
   {
    // Only a look: an image still inside an archive is not extracted for a page the user may never turn.
    var peeked=_peekAt!=null ? await _peekAt(index) : _itemAt==null || _items.Count==_itemCount ? (index<_items.Count ? _items[index] : null) : null;
    if(_closed || round!=_prefetchRound) return;
    if(peeked is not { Kind: MediaKind.Image } item) continue;
    var path=await Task.Run(()=>RawPairs.DisplayPath(item));
    if(_closed || round!=_prefetchRound) return;
    if(FastImageDecoder.Handles(path)) wanted.Add((path,bound));
   }
   foreach(var key in _prefetched.Keys.Where(key=>!wanted.Contains(key)).ToList()) _prefetched.Remove(key);
   foreach(var key in wanted)
   {
    if(_prefetched.ContainsKey(key)) continue;
    var file=new FileInfo(key.Path); if(!file.Exists) continue;
    _prefetched[key]=(FastImageDecoder.DecodeAsync(key.Path,bound,_prefetching.Token),file.Length,file.LastWriteTimeUtc);
   }
  }
  catch(Exception error) when(error is OperationCanceledException or IOException or UnauthorizedAccessException or InvalidOperationException) { }
 }

 private void ScheduleFrame()
 {
  if(!_closed && _playing && _info?.IsAnimated==true) { _animation.Interval=_info.FrameDurations[_frame]; _animation.Start(); }
 }
 internal void Rotate() { _rotation=(_rotation+90)%360; if(_fit)Fit(); else Apply(); }
 /// <param name="at">The place of the stage that stays where it is, as under the pointer; its middle otherwise.</param>
 internal void ChangeZoom(double multiplier,Point? at=null)
 {
  Hold(at); _fit=false; _zoom=Math.Clamp(_zoom*multiplier,.02,16); Apply(); Settle(); _hold=null; DevelopWhenPreviewRunsOut(false);
 }
 /// <summary>One pixel of the image per pixel of the screen; for a RAW file with a smaller preview that is the sensor
 /// image. The picture on show is enlarged at once and replaced when all its pixels are read.</summary>
 internal void OriginalSize(Point? at=null)
 {
  // Not on the thumbnail that stands in while the image is read.
  if(_placeholder || _info==null) return;
  Hold(at); _fit=false;
  var plain=!_develop && _info is { IsRaw: false } && _image.Source is BitmapSource;
  if(plain && _image.Source is BitmapSource shown)
  {
   // What is on show was read smaller than the file: it takes the size the whole image will have.
   var have=Math.Max(shown.PixelWidth,shown.PixelHeight); var whole=Math.Max(_info!.Width,_info.Height);
   _zoom=Math.Clamp(whole/(double)Math.Max(1,have),1,16); Apply(); Settle();
   if(have>=whole) { _hold=null; return; }
   _keepSize=true; ReadyTask=ShowFrameAsync(_load?.Token??default); return;
  }
  _zoom=1; if(_image.Source!=null) { Apply(); Settle(); }
  if(!DevelopWhenPreviewRunsOut(true)) ReadyTask=ShowFrameAsync(_load?.Token??default);
 }
 internal void Fit()
 {
  _fit=true; _hold=null; if(_image.Source is not BitmapSource source)return;
  var swap=_rotation is 90 or 270; var width=swap?source.PixelHeight:source.PixelWidth; var height=swap?source.PixelWidth:source.PixelHeight;
  _zoom=Math.Clamp(Math.Min(Math.Max(_scroll.ActualWidth-22,1)/width,Math.Max(_scroll.ActualHeight-22,1)/height),.02,16); Apply();
 }
 private void Apply()
 {
  _image.LayoutTransform=new TransformGroup { Children=[new RotateTransform(_rotation),new ScaleTransform(_flipX?-_zoom:_zoom,_flipY?-_zoom:_zoom)] };
  _label.Text=$"{_index+1:N0} / {_itemCount:N0} · {_zoom:P0}"+(_rotation!=0?$" · {_rotation}°":"")+(_flipX||_flipY?T(" · mirrored"):"")+(_info?.FrameCount>1?$" · {(_info.IsAnimated?"Bild":T("Page"))} {_frame+1}/{_info.FrameCount}":"");
 }

 // Details over the picture (original "Show file details").
 internal void SetInfo(bool shown) { _infoToggle.IsChecked=shown; _infoPanel.Visibility=shown?Visibility.Visible:Visibility.Collapsed; UpdateInfo(); }
 private void UpdateInfo()
 {
  if(_currentItem is not { } item) return;
  // The developed RAW file of a pair is another file than the picture: its size, date and place are told then.
  var rawOfPair=_develop && _info is { IsRaw: false } && _rawPath!=null ? item.Files.FirstOrDefault(f=>string.Equals(f.Path,_rawPath,StringComparison.OrdinalIgnoreCase)) : null;
  var shownPath=rawOfPair?.Path??_path??item.Path; var file=new FileInfo(shownPath);
  var lines=new List<string> { item.Title };
  var facts=new[] { rawOfPair!=null ? (rawOfPair is { Width: >0, Height: >0 } ? $"{rawOfPair.Width} × {rawOfPair.Height}" : null) : _info is { } info ? $"{info.Width} × {info.Height}" : null, file.Exists ? MediaCard.SizeText(file.Length) : null, file.Exists ? file.LastWriteTime.ToString("d. MMMM yyyy, HH:mm") : null }.OfType<string>();
  lines.Add(string.Join(" · ",facts));
  if(RawLine() is { Length: >0 } raw) lines.Add(raw);
  if(_capture.Text.Length>0) lines.Add(_capture.Text);
  else if(item.Extra.GetValueOrDefault(MediaCard.CameraKey) is { Length: >0 } camera) lines.Add(camera);
  if(item.Rating>0) lines.Add(new string('★',item.Rating/2)+(item.Rating%2==1?"½":""));
  if(item.Tags.Count>0) lines.Add(string.Join(", ",item.Tags));
  lines.Add(shownPath);
  _infoText.Text=string.Join("\n",lines.Where(l=>l.Length>0));
 }

 // Camera RAW files: the camera's preview is the picture; the sensor data is developed on request (D). The request
 // lasts: while the switch is on, every RAW file looked at is developed, and a development made once is always shown.
 /// <summary>The RAW switch of the library: RAW files are developed when they are looked at.</summary>
 internal bool DevelopRaw { get; set; }
 /// <summary>Set by the library: keeps the RAW switch.</summary>
 internal Action<bool>? DevelopRawChanged { get; set; }
 private readonly Button _raw = new() { Style=(Style)Application.Current.Resources["Quiet"], Padding=new Thickness(2), Margin=new Thickness(8,0,0,0), Visibility=Visibility.Collapsed, VerticalAlignment=VerticalAlignment.Center };
 private readonly Border _rawPill = new() { CornerRadius=new CornerRadius(6), Padding=new Thickness(9,4,9,5) };
 private readonly TextBlock _rawText = new() { FontSize=12, FontWeight=FontWeights.SemiBold };
 private string? _path, _rawPath;
 private bool _develop, _developing;
 /// <summary>The file on show: the main file of the item, or its RAW file when the picture of a pair is gone.</summary>
 internal string? ShownPath => _path;
 /// <summary>The sensor data of the RAW file is shown instead of a finished picture.</summary>
 internal bool Developed => _develop || _info is { IsRaw: true, ShownByPreview: false };
 /// <summary>Pixels of the picture on show.</summary>
 internal (int Width, int Height) ShownSize => _image.Source is BitmapSource shown ? (shown.PixelWidth, shown.PixelHeight) : (0, 0);
 internal bool RawSwitchShown => _raw.Visibility==Visibility.Visible;
 internal string RawSwitchText => _rawText.Text;

 private Button BuildRawSwitch()
 {
  _rawPill.Child=_rawText; _raw.Content=_rawPill; _raw.Click+=(_,_)=>ToggleDevelop();
  return _raw;
 }

 /// <summary>Develops the RAW file of the image on show, or goes back to the finished picture.</summary>
 internal void ToggleDevelop()
 {
  if(_currentItem==null || _rawPath==null || _info==null || _developing || _info is { IsRaw: true, ShownByPreview: false } || !File.Exists(_rawPath)) return;
  StopSlideshow();
  // Going back to the camera's picture by hand holds until the next image, also when zooming in.
  _keepPreview=_develop;
  // The switch stays as it was left: on, the RAW files that follow are developed by themselves; off, none is.
  var on=!_develop; if(DevelopRaw!=on) { DevelopRaw=on; DevelopRawChanged?.Invoke(on); }
  SwitchDevelop(on, keepSize: true);
 }

 private void SwitchDevelop(bool develop, bool keepSize)
 {
  _develop=develop; _developing=develop; _keepSize=keepSize && !_fit; UpdateRawSwitch();
  _load?.Cancel(); _load?.Dispose(); _load=new(); var token=_load.Token;
  ReadyTask=Run();
  async Task Run() { await ShowFrameAsync(token); if(!token.IsCancellationRequested) { _developing=false; UpdateRawSwitch(); UpdateInfo(); } }
 }

 private bool _keepPreview, _keepSize;
 /// <summary>The camera's preview is clearly smaller than the sensor image (Olympus, Panasonic, Sony and others).</summary>
 private bool PreviewIsSmaller => _info is { IsRaw: true, ShownByPreview: true, PreviewWidth: { } width, PreviewHeight: { } height } && Math.Max(width,height)<Math.Max(_info.Width,_info.Height)*9/10;

 /// <summary>A preview smaller than the sensor image has no more to show beyond its own pixels: zooming past them, or
 /// asking for the original size, develops the sensor data in the background and swaps the picture.</summary>
 /// <param name="original">The original size was asked for; otherwise what is on screen keeps its size.</param>
 private bool DevelopWhenPreviewRunsOut(bool original)
 {
  if(_fit || _develop || _developing || _keepPreview || !PreviewIsSmaller || _rawPath==null || _image.Source is not BitmapSource shown || !File.Exists(_rawPath)) return false;
  var preview=Math.Max(_info!.PreviewWidth!.Value,_info.PreviewHeight!.Value);
  if(!original && Math.Max(shown.PixelWidth,shown.PixelHeight)*_zoom<preview*.999) return false;
  StopSlideshow();
  SwitchDevelop(true, keepSize: !original);
  return true;
 }

 private void UpdateRawSwitch()
 {
  var item=_currentItem; var raw=_info?.IsRaw==true; var pair=!raw && _rawPath!=null && _info!=null;
  if(item==null || _info==null || !raw && !pair) { _raw.Visibility=Visibility.Collapsed; return; }
  _raw.Visibility=Visibility.Visible;
  var always=raw && !_info.ShownByPreview; var active=_develop || always;
  _rawText.Text=_developing ? T("RAW · developing …") : active ? T("RAW · developed") : pair ? T("JPEG + RAW") : "RAW";
  _rawPill.SetResourceReference(Border.BackgroundProperty,active?"SelectionBackground":"ButtonBackground");
  _rawText.SetResourceReference(TextBlock.ForegroundProperty,active?"Accent":"Muted");
  var tip=always ? T("This RAW file holds no preview of the camera, so its sensor data is developed.")
   : _develop ? T("Developed from the sensor data of the RAW file. Click for the finished picture again (D).")
   : pair ? T("The camera's picture of this shot is shown. Click to develop the RAW file beside it instead: that takes a moment and looks a little different (D).")
   : _info.PreviewWidth is { } width && _info.PreviewHeight is { } height && Math.Max(width,height)<Math.Max(_info.Width,_info.Height)*9/10
    ? T("The camera's preview inside the RAW file is shown ({0} × {1}). Click to develop the sensor data in full resolution ({2} × {3}): that takes a moment and looks a little different (D).", width, height, _info.Width, _info.Height)
    : T("The camera's preview inside the RAW file is shown. Click to develop the sensor data instead: that takes a moment and looks a little different (D).");
  if(!always) tip+="\n\n"+(DevelopRaw ? T("The RAW switch is on: every RAW file is developed when you look at it.") : T("The RAW switch is off: nothing is developed by itself. A RAW file developed before still shows its development."));
  _raw.ToolTip=tip; AutomationProperties.SetName(_raw,_rawText.Text); AutomationProperties.SetHelpText(_raw,tip);
 }

 /// <summary>What the details over the picture say about the RAW side of the image.</summary>
 private string RawLine()
 {
  if(_info==null) return "";
  if(Developed) return T("RAW, developed from the sensor data");
  if(_info.IsRaw) return _info.PreviewWidth is { } width && _info.PreviewHeight is { } height ? T("RAW, shown by the camera's preview ({0} × {1})", width, height) : "RAW";
  return _rawPath is { } path ? T("RAW file: {0}", Path.GetFileName(path)) : "";
 }

 // Film strip: the neighbouring images; a click opens one.
 internal void SetStrip(bool shown)
 {
  _stripToggle.IsChecked=shown; _stripPanel.Visibility=shown?Visibility.Visible:Visibility.Collapsed;
  if(shown) StripTask=LoadStripAsync();
 }
 private async Task LoadStripAsync()
 {
  _stripLoad?.Cancel(); var work=_stripLoad=new CancellationTokenSource(); var token=work.Token;
  var first=Math.Max(0,Math.Min(_index-StripRadius,_itemCount-1-2*StripRadius)); var last=Math.Min(_itemCount-1,first+2*StripRadius);
  _strip.Children.Clear();
  for(var index=first;index<=last;index++)
  {
   var position=index; var current=index==_index;
   var image=new Image { Height=60, Stretch=Stretch.Uniform };
   var frame=new Border { Child=image, BorderThickness=new Thickness(current?3:0), CornerRadius=new CornerRadius(3), Padding=new Thickness(current?0:3) };
   frame.SetResourceReference(Border.BorderBrushProperty,"Accent");
   var cell=new Button { Content=frame, Padding=new Thickness(2), Margin=new Thickness(3,0,3,0), MinWidth=72, Height=74, ToolTip=T("Image {0:N0}", index+1), Style=(Style)Application.Current.Resources["Quiet"] };
   AutomationProperties.SetName(cell,T("Image {0:N0}", index+1));
   cell.Click+=(_,_)=>{ StopSlideshow(); GoTo(position); };
   _strip.Children.Add(cell);
   _ = FillAsync(image,position,token);
  }
  if(_strip.Children.Count>0 && _index-first is var local && local>=0 && local<_strip.Children.Count) ((FrameworkElement)_strip.Children[local]).BringIntoView();
  await Task.CompletedTask;
 }
 private readonly Dictionary<string,BitmapSource> _stripPictures = new(StringComparer.OrdinalIgnoreCase);
 private async Task FillAsync(Image image,int index,CancellationToken token)
 {
  try
  {
   if(await ItemAtAsync(index) is not { } item || token.IsCancellationRequested) return;
   var source=item.ThumbnailPath is { } thumbnail && File.Exists(thumbnail) ? thumbnail : RawPairs.DisplayPath(item);
   // Kept while the viewer is open: leafing through builds the strip anew at every image.
   if(!_stripPictures.TryGetValue(source,out var bitmap))
   {
    bitmap=await FastImageDecoder.DecodeAsync(source,160,token) is { } fast ? fast.Bitmap : await DesktopImaging.LoadBitmapAsync(source,160,token);
    if(_stripPictures.Count>=400) _stripPictures.Clear();
    if(bitmap!=null) _stripPictures[source]=bitmap;
   }
   if(!token.IsCancellationRequested) image.Source=bitmap;
  }
  catch(Exception error) when(error is OperationCanceledException or IOException or InvalidDataException or InvalidOperationException or ArgumentException or NotSupportedException or UnauthorizedAccessException) { }
 }

 // Slideshow with the original's options: interval, order, fade, pan and zoom, pace.
 internal void ToggleSlideshow() { if(SlideshowRunning) StopSlideshow(); else StartSlideshow(); }
 internal void StartSlideshow()
 {
  if(_itemCount<2) return;
  _runInOrder=0; _slideTimer.Interval=TimeSpan.FromSeconds(Math.Clamp(Slideshow.IntervalSeconds,SlideshowPreferences.MinimumInterval,SlideshowPreferences.MaximumInterval)); _slideTimer.Start();
  _slideshow.Content=""; _slideshow.ToolTip=T("End slideshow (S or Esc)"); Transition();
 }
 internal void StopSlideshow()
 {
  if(!SlideshowRunning) return;
  _slideTimer.Stop(); _slideshow.Content=""; _slideshow.ToolTip=T("Start slideshow (S)");
  _image.BeginAnimation(OpacityProperty,null); _image.Opacity=1; _image.RenderTransform=Transform.Identity;
 }
 /// <summary>Index of the next slide: in order, at random, or "less random" (a random place, then a few in order).</summary>
 internal int NextSlideIndex()
 {
  switch(Slideshow.Order)
  {
   case SlideshowOrder.Random: return Other();
   case SlideshowOrder.LessRandom when ++_runInOrder>=5: _runInOrder=0; return Other();
   default: return (_index+1)%_itemCount;
  }
  int Other() { var next=_random.Next(_itemCount-1); return next>=_index?next+1:next; }
 }
 private void ShowNextSlide() => GoTo(NextSlideIndex());
 private void Transition()
 {
  var pace=Slideshow.Speed switch { HoverSpeed.Slow=>1.6, HoverSpeed.Fast=>.5, _=>1.0 };
  if(Slideshow.Fade) _image.BeginAnimation(OpacityProperty,new DoubleAnimation(0,1,TimeSpan.FromSeconds(.5*pace)));
  if(Slideshow.PanAndZoom)
  {
   var scale=new ScaleTransform(1,1); var shift=new TranslateTransform();
   _image.RenderTransform=new TransformGroup { Children=[scale,shift] };
   var span=TimeSpan.FromSeconds(Math.Max(1,_slideTimer.Interval.TotalSeconds*pace)); var direction=_random.Next(2)==0?-1:1;
   scale.BeginAnimation(ScaleTransform.ScaleXProperty,new DoubleAnimation(1,1.12,span)); scale.BeginAnimation(ScaleTransform.ScaleYProperty,new DoubleAnimation(1,1.12,span));
   shift.BeginAnimation(TranslateTransform.XProperty,new DoubleAnimation(0,direction*_image.ActualWidth*.03,span));
  }
  else _image.RenderTransform=Transform.Identity;
 }
}
