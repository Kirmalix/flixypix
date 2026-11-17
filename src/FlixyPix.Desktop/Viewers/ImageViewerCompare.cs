using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FlixyPix.Core;
using FlixyPix.Imaging;

namespace FlixyPix.Desktop;

/// <summary>Comparing in the loupe: a second viewer holds the reference image beside the one on show, and the two
/// follow each other's zoom and place, measured in pixels of the whole image, so a detail is seen in both at once.</summary>
internal sealed partial class ImageViewer
{
 /// <summary>Zoom and place of the picture: fitted, or the screen pixels per pixel of the whole image and the place
 /// of the picture in the middle of the stage, as a share of its width and height.</summary>
 internal readonly record struct ViewPlace(bool Fit,double Scale,Point Center);

 private bool _reference;
 private DockPanel? _toolbar;
 private readonly TextBlock _referenceCaption = new() { Foreground=Brushes.White, FontSize=12, TextTrimming=TextTrimming.CharacterEllipsis };
 private Button? _compareButton;
 /// <summary>Set by the library: keeps the image on show as the reference, or ends the comparison.</summary>
 internal Action? CompareRequested { get; set; }
 /// <summary>The user changed zoom or place; the other side of a comparison follows.</summary>
 internal event Action<ImageViewer>? ViewChanged;
 internal bool IsReference => _reference;
 private bool _viewPending;
 private ViewPlace? _followAfterLoad;

 /// <summary>The left side of a comparison: one image, no symbol bar and no film strip, its name over the picture.</summary>
 internal static ImageViewer Reference(MediaItem item)
 {
  var viewer=new ImageViewer([item],item.Id,embedded:true) { _reference=true };
  if(viewer._toolbar!=null) viewer._toolbar.Visibility=Visibility.Collapsed;
  viewer._referenceCaption.Text=T("Reference")+" · "+item.Title;
  var caption=new Border { Background=new SolidColorBrush(Color.FromArgb(190,10,12,16)), CornerRadius=new CornerRadius(6), Padding=new Thickness(10,4,10,5),
   Margin=new Thickness(12), HorizontalAlignment=HorizontalAlignment.Left, VerticalAlignment=VerticalAlignment.Top, IsHitTestVisible=false, Child=viewer._referenceCaption };
  if(viewer._scroll.Parent is Grid stage) stage.Children.Add(caption);
  return viewer;
 }

 private Button CompareButton()
 {
  _compareButton=Symbol(((char)0xE89A).ToString(),T("Compare: this image stays on the left as the reference (C)"));
  _compareButton.Click+=(_,_)=>CompareRequested?.Invoke();
  return _compareButton;
 }
 /// <summary>The symbol tells whether a comparison is running.</summary>
 internal void ShowComparing(bool comparing)
 {
  if(_compareButton==null) return;
  var tip=comparing?T("End the comparison (C)"):T("Compare: this image stays on the left as the reference (C)");
  _compareButton.ToolTip=tip; System.Windows.Automation.AutomationProperties.SetName(_compareButton,tip);
  if(comparing) _compareButton.SetResourceReference(ForegroundProperty,"Accent"); else _compareButton.ClearValue(ForegroundProperty);
 }

 /// <summary>Tells the other side once the layout settled, so a drag sends one change per frame, not one per move.</summary>
 private void UserView()
 {
  if(ViewChanged==null || _viewPending) return; _viewPending=true;
  Dispatcher.BeginInvoke(()=>{ _viewPending=false; if(!_closed) ViewChanged?.Invoke(this); },DispatcherPriority.Background);
 }

 internal ViewPlace? Place
 {
  get
  {
   if(_image.Source is not BitmapSource source || _info is not { Width: >0 } info || _placeholder) return null;
   if(_fit) return new(true,0,new(.5,.5));
   _scroll.UpdateLayout();
   var inside=_scroll.TranslatePoint(new(_scroll.ActualWidth/2,_scroll.ActualHeight/2),_image);
   return new(false,_zoom*source.PixelWidth/WholeWidth(info),new(Math.Clamp(inside.X/Math.Max(1,_image.ActualWidth),0,1),Math.Clamp(inside.Y/Math.Max(1,_image.ActualHeight),0,1)));
  }
 }
 /// <summary>Width of the whole image; a developed RAW file is as wide as its sensor image.</summary>
 private static double WholeWidth(ImageInfo info)=>Math.Max(1,info.Width);

 /// <summary>Takes zoom and place of the other side of a comparison. A picture read smaller than the image is read
 /// whole once it is enlarged past its pixels, as with the original size.</summary>
 internal void Follow(ViewPlace place)
 {
  if(_closed) return;
  if(place.Fit) { _followAfterLoad=null; if(!_fit) Fit(); return; }
  if(_image.Source is not BitmapSource source || _info is not { Width: >0 } info || _placeholder) { _followAfterLoad=place; return; }
  _followAfterLoad=null; _fit=false;
  _zoom=Math.Clamp(place.Scale*WholeWidth(info)/source.PixelWidth,.02,16); Apply();
  _hold=(place.Center,new Point(_scroll.ActualWidth/2,_scroll.ActualHeight/2)); Settle();
  var have=Math.Max(source.PixelWidth,source.PixelHeight); var whole=Math.Max(info.Width,info.Height);
  if(have<whole && _zoom>1.01 && !_develop) { _keepSize=true; ReadyTask=ShowFrameAsync(_load?.Token??default); return; }
  _hold=null;
 }
 /// <summary>A comparison asked for a place before the picture was read: it is taken now.</summary>
 private void FollowAfterLoad() { if(_followAfterLoad is { } place && !_placeholder) Follow(place); }
}
