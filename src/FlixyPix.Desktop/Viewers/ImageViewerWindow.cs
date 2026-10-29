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
/// the neighbouring images, details over the picture and a slideshow with the original's options.</summary>
internal sealed partial class ImageViewerWindow : Window
{
 private const int StripRadius = 7;
 private readonly List<MediaItem> _items;
 private readonly Func<int,Task<MediaItem?>>? _itemAt;
 private int _itemCount;
 private MediaItem? _currentItem;
 private readonly Image _image = new() { Stretch=Stretch.None, RenderTransformOrigin=new Point(.5,.5) };
 private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility=ScrollBarVisibility.Auto, VerticalScrollBarVisibility=ScrollBarVisibility.Auto, Background=Brushes.Black };
 private readonly TextBlock _label = new() { VerticalAlignment=VerticalAlignment.Center, Margin=new Thickness(12,0,8,0), FontSize=13 };
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
 private int _index, _rotation, _frame, _runInOrder;
 private uint _animationLoops;
 private double _zoom=1;
 private bool _fit=true, _playing=true, _closed;
 internal int CurrentIndex => _index;
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

 internal ImageViewerWindow(Window owner, IReadOnlyList<MediaItem> items, Guid selectedId, Func<int,Task<MediaItem?>>? itemAt=null, int? itemCount=null, int? initialIndex=null)
 {
  Owner=owner; _items=[.. items]; _itemAt=itemAt; _itemCount=itemCount??items.Count; _index=initialIndex??Math.Max(0,items.ToList().FindIndex(i=>i.Id==selectedId));
  FontSize=14; Width=1150; Height=800; MinWidth=600; MinHeight=400; WindowStartupLocation=WindowStartupLocation.CenterOwner;
  var toolbar=new DockPanel { Margin=new Thickness(8,6,8,6), LastChildFill=false };
  var left=new StackPanel { Orientation=Orientation.Horizontal };
  left.Children.Add(Command("",T("Previous image (←)"),()=>Navigate(-1))); left.Children.Add(Command("",T("Next image (→)"),()=>Navigate(1)));
  left.Children.Add(Gap());
  left.Children.Add(Command("",T("Zoom out (−)"),()=>ChangeZoom(1/1.25))); left.Children.Add(Command("",T("Zoom in (+)"),()=>ChangeZoom(1.25)));
  left.Children.Add(Command("",T("Fit (F)"),Fit)); left.Children.Add(Command("",T("Original size 1:1"),()=>{ _fit=false; _zoom=1; ReadyTask=ShowFrameAsync(_load?.Token??default); }));
  left.Children.Add(Command("",T("Rotate (R)"),Rotate));
  _frameButtons.Children.Add(Command("",T("Previous page or previous frame of the animation (Page↑)"),()=>ChangeFrame(-1))); _frameButtons.Children.Add(_play);
  _frameButtons.Children.Add(Command("",T("Next page or next frame of the animation (Page↓)"),()=>ChangeFrame(1)));
  _play.Click+=(_,_)=>ToggleAnimation(); left.Children.Add(_frameButtons); left.Children.Add(_label);
  DockPanel.SetDock(left,Dock.Left); toolbar.Children.Add(left);
  var right=new StackPanel { Orientation=Orientation.Horizontal };
  _infoToggle.Click+=(_,_)=>SetInfo(_infoToggle.IsChecked==true); _stripToggle.Click+=(_,_)=>SetStrip(_stripToggle.IsChecked==true);
  _slideshow.Click+=(_,_)=>ToggleSlideshow();
  var more=Symbol("\uE712",T("More actions")); more.Click+=(_,_)=>ShowActions(more);
  right.Children.Add(_infoToggle); right.Children.Add(_stripToggle); right.Children.Add(_slideshow); right.Children.Add(more);
  DockPanel.SetDock(right,Dock.Right); toolbar.Children.Add(right);
  _infoPanel.Child=_infoText;
  _stripPanel.SetResourceReference(Border.BackgroundProperty,"ChromeBackground");
  _stripPanel.Child=new ScrollViewer { Content=_strip, HorizontalScrollBarVisibility=ScrollBarVisibility.Auto, VerticalScrollBarVisibility=ScrollBarVisibility.Disabled };
  var stage=new Grid(); _scroll.Content=_image; stage.Children.Add(_scroll); stage.Children.Add(_infoPanel);
  var layout=new DockPanel(); DockPanel.SetDock(toolbar,Dock.Top); DockPanel.SetDock(_stripPanel,Dock.Bottom);
  layout.Children.Add(toolbar); layout.Children.Add(_stripPanel); layout.Children.Add(stage); Content=layout;
  _scroll.SizeChanged+=(_,_)=>{ if(_fit) Fit(); };
  _scroll.MouseRightButtonUp+=(_,e)=>{ e.Handled=true; ShowActions(null); };
  Loaded+=(_,_)=>{ SetInfo(Slideshow.ShowInfo); ReadyTask=LoadCurrentAsync(); };
  Closed+=(_,_)=>{ _closed=true; _animation.Stop(); _slideTimer.Stop(); _load?.Cancel(); _load?.Dispose(); _stripLoad?.Cancel(); };
  _animation.Tick+=async (_,_)=>
  {
   _animation.Stop(); if(!_playing || _info?.IsAnimated!=true) return;
   var next=(_frame+1)%_info.FrameCount;
   if(next==0 && ++_animationLoops>=_info.AnimationIterations && _info.AnimationIterations>0) { _playing=false; UpdatePlayButton(); return; }
   _frame=next; ReadyTask=ShowFrameAsync(_load?.Token??default); await ReadyTask;
  };
  _slideTimer.Tick+=(_,_)=>ShowNextSlide();
  PreviewKeyDown+=(_,e)=>
  {
   if(HandleActionKey(e)) { e.Handled=true; return; }
   switch(e.Key)
   {
    case Key.Escape: if(SlideshowRunning) StopSlideshow(); else Close(); break;
    case Key.Right: StopSlideshow(); Navigate(1); break; case Key.Left: StopSlideshow(); Navigate(-1); break;
    case Key.Add: case Key.OemPlus: ChangeZoom(1.25); break; case Key.Subtract: case Key.OemMinus: ChangeZoom(1/1.25); break;
    case Key.F: Fit(); break; case Key.R: Rotate(); break;
    case Key.Space: if(SlideshowRunning) StopSlideshow(); else ToggleAnimation(); break;
    case Key.S: ToggleSlideshow(); break; case Key.I: SetInfo(!InfoShown); break; case Key.T: SetStrip(_stripPanel.Visibility!=Visibility.Visible); break;
    case Key.PageDown: ChangeFrame(1); break; case Key.PageUp: ChangeFrame(-1); break;
    case Key.MediaNextTrack when MediaKeys: Navigate(1); break; case Key.MediaPreviousTrack when MediaKeys: Navigate(-1); break;
    case Key.MediaPlayPause when MediaKeys: ToggleAnimation(); break; case Key.MediaStop when MediaKeys: Close(); break; default:return;
   }
   e.Handled=true;
  };
  PreviewMouseWheel+=(_,e)=>{ if(HandleWheel(e.Delta,(Keyboard.Modifiers&ModifierKeys.Control)!=0)) e.Handled=true; };
 }

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
  if(control || WheelAction==ImageWheelAction.Zoom) { ChangeZoom(delta>0?1.15:1/1.15); return true; }
  if(WheelAction==ImageWheelAction.Navigate) { Navigate(delta>0?-1:1); return true; }
  return false;
 }
 internal void Navigate(int delta) => GoTo(_index+delta);
 private void GoTo(int index)
 {
  if(_itemCount==0)return;
  _index=((index%_itemCount)+_itemCount)%_itemCount; _rotation=0; _flipX=_flipY=false; _fit=true; _playing=true; _fadeNext=FadeOnChange && !SlideshowRunning; ReadyTask=LoadCurrentAsync();
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
  _info=null; _frame=0; _animationLoops=0; _label.Text=T("Reading image …");
  if(!(SlideshowRunning && Slideshow.Fade) && !_fadeNext) _image.Source=null;
  if(_itemCount==0)return;
  try
  {
   var item=await ItemAtAsync(_index); token.ThrowIfCancellationRequested();
   _currentItem=item??throw new IOException(T("The image is no longer in the catalog."));
   Title=item.Title+T(" · Image viewer");
   if(_stripPanel.Visibility==Visibility.Visible) StripTask=LoadStripAsync();
   _info=await DesktopImaging.Service.GetInfoAsync(item.Path,token); if(token.IsCancellationRequested)return; await ShowFrameAsync(token);
  }
  catch(OperationCanceledException) { }
  catch(Exception error) when(error is IOException or InvalidDataException or ArgumentException or InvalidOperationException or UnauthorizedAccessException) { if(!token.IsCancellationRequested) _label.Text=T("The image cannot be shown: ")+error.Message; }
 }
 private Task<MediaItem?> ItemAtAsync(int index)=>_itemAt==null?Task.FromResult<MediaItem?>(index>=0 && index<_items.Count?_items[index]:null):_itemAt(index);

 private async Task ShowFrameAsync(CancellationToken token)
 {
  try
  {
   if(_currentItem==null)return; var frame=_frame; var path=_currentItem.Path;
   var bound=_fit?Math.Max(1600,(int)Math.Max(_scroll.ActualWidth,_scroll.ActualHeight)*2):0;
   var decoded=await DesktopImaging.Service.DecodeAsync(path,frame,bound,bound,token);
   if(_closed || token.IsCancellationRequested || path!=_currentItem?.Path || frame!=_frame)return;
   var bitmap=DesktopImaging.Bitmap(decoded.PngBytes); _image.Source=bitmap; _image.Width=bitmap.PixelWidth; _image.Height=bitmap.PixelHeight;
   if(_fit)Fit(); else Apply();
   _frameButtons.Visibility=_info?.FrameCount>1?Visibility.Visible:Visibility.Collapsed; _play.Visibility=_info?.IsAnimated==true?Visibility.Visible:Visibility.Collapsed; UpdatePlayButton();
   UpdateInfo(); ScheduleFrame();
   if(SlideshowRunning) Transition();
   else if(_fadeNext) { _fadeNext=false; LastChangeFaded=true; _image.BeginAnimation(OpacityProperty,new DoubleAnimation(.15,1,TimeSpan.FromMilliseconds(280))); }
   else LastChangeFaded=false;
  }
  catch(OperationCanceledException) { }
  catch(Exception error) when(error is IOException or InvalidDataException or ArgumentException or InvalidOperationException or UnauthorizedAccessException) { if(!token.IsCancellationRequested) { _playing=false; _label.Text=T("The image cannot be shown: ")+error.Message; } }
 }
 private void ScheduleFrame()
 {
  if(!_closed && _playing && _info?.IsAnimated==true) { _animation.Interval=_info.FrameDurations[_frame]; _animation.Start(); }
 }
 internal void Rotate() { _rotation=(_rotation+90)%360; if(_fit)Fit(); else Apply(); }
 internal void ChangeZoom(double multiplier) { _fit=false; _zoom=Math.Clamp(_zoom*multiplier,.02,16); Apply(); }
 internal void Fit()
 {
  _fit=true; if(_image.Source is not BitmapSource source)return;
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
  var file=new FileInfo(item.Path);
  var lines=new List<string> { item.Title };
  var facts=new[] { _info is { } info ? $"{info.Width} × {info.Height}" : null, file.Exists ? MediaCard.SizeText(file.Length) : null, file.Exists ? file.LastWriteTime.ToString("d. MMMM yyyy, HH:mm") : null }.OfType<string>();
  lines.Add(string.Join(" · ",facts));
  if(item.Rating>0) lines.Add(new string('★',item.Rating/2)+(item.Rating%2==1?"½":""));
  if(item.Tags.Count>0) lines.Add(string.Join(", ",item.Tags));
  lines.Add(item.Path);
  _infoText.Text=string.Join("\n",lines.Where(l=>l.Length>0));
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
 private async Task FillAsync(Image image,int index,CancellationToken token)
 {
  try
  {
   if(await ItemAtAsync(index) is not { } item || token.IsCancellationRequested) return;
   var source=item.ThumbnailPath is { } thumbnail && File.Exists(thumbnail) ? thumbnail : item.Path;
   var bitmap=await DesktopImaging.LoadBitmapAsync(source,160,token);
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
