using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Input;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 internal async Task RunSelectionBandProbeAsync(string directory)
 {
  for(var i=0;i<6;i++) _repository.Upsert(new FlixyPix.Core.MediaItem { Path=System.IO.Path.Combine(directory,$"synthetic-{i}.mp4"), Title=$"Synthetic {i}", Kind=FlixyPix.Core.MediaKind.Video });
  ApplyViewState(new FlixyPix.Core.ViewState { View="Video" }); await RefreshAsync();
  await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
  await RunSelectionBandSmokeTestAsync();
 }

 /// <summary>A rectangle drawn from the middle of one tile to the middle of the next selects both and nothing else; with Ctrl
 /// a further rectangle adds to the selection; a rectangle in the gap between tiles takes none.</summary>
 private async Task RunSelectionBandSmokeTestAsync()
 {
  static void Require(bool check, string message) { if (!check) throw new InvalidOperationException(message); }
  async Task Idle() => await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
  var panel = Tiles ?? throw new InvalidOperationException("The tile panel is missing.");
  Require(MediaList.Items.Count >= 3, "The rectangle test needs three tiles.");
  MediaList.ScrollIntoView(MediaList.Items[0]); await Idle();
  Point Center(int index)
  {
   var container = (ListBoxItem)MediaList.ItemContainerGenerator.ContainerFromIndex(index);
   return container.TransformToAncestor(panel).Transform(new Point(container.ActualWidth / 2, container.ActualHeight / 2));
  }
  Point Content(Point point) => new(point.X, point.Y + panel.VerticalOffset);
  Guid Id(int index) => ((MediaCard)MediaList.Items[index]).Item.Id;
  async Task Draw(Point from, Point to, bool add)
  {
   Require(StartBand(Content(from), add, follow: false) && UpdateBand(to), "The rectangle did not start.");
   await EndBandAsync(); await Idle();
  }

  MediaList.SelectedItems.Clear(); await Idle();
  await Draw(Center(0), Center(1), add: false);
  var chosen = SelectedMedia.Select(item => item.Id).ToHashSet();
  Require(chosen.SetEquals([Id(0), Id(1)]), $"The rectangle from the first to the second tile selected {chosen.Count} items.");
  Require(AdornerLayer() is not { } layer || layer.GetAdorners(MediaArea) is null, "The frame stayed on screen.");

  await Draw(Center(2), Center(2) + new Vector(8, 8), add: true);
  chosen = SelectedMedia.Select(item => item.Id).ToHashSet();
  Require(chosen.SetEquals([Id(0), Id(1), Id(2)]), "Ctrl and a rectangle on the third tile did not add it to the selection.");

  // Between the first two tiles of a row, a short frame touches neither.
  if (panel.Columns > 1)
  {
   var gap = new Point(panel.TileWidth, Center(0).Y);
   await Draw(gap - new Vector(2, 0), gap + new Vector(2, 4), add: false);
   Require(SelectedMedia.Count == 0, "A rectangle in the gap between two tiles selected a tile.");
  }
  await Draw(new Point(-12, Center(0).Y), Center(0), add:false);
  Require(SelectedMedia.Any(item=>item.Id==Id(0)), "A rectangle from the sidebar gap missed the first tile.");
  Require(StartBand(Content(Center(0)), false, follow:false), "Cleanup fixture did not start.");
  CancelBand();
  Require(_band==null && _bandOrigin==null, "Cancelled rectangle retained its gesture.");
  var savedPosition=BandPosition; var savedPressed=BandPressed;
  var savedKeys=new byte[256]; GetKeyboardState(savedKeys);
  try
  {
   BandPressed=_=>true;
   async Task Gesture(UIElement source, Point from, Point to, ModifierKeys modifiers)
   {
    var keys=(byte[])savedKeys.Clone(); keys[0x12]=keys[0xA4]=(byte)(modifiers.HasFlag(ModifierKeys.Alt)?128:0); keys[0x11]=keys[0xA2]=(byte)(modifiers.HasFlag(ModifierKeys.Control)?128:0); keys[0xA5]=keys[0xA3]=keys[0x10]=keys[0xA0]=keys[0xA1]=0; SetKeyboardState(keys);
    var point=from; BandPosition=(_,relative)=>panel.TranslatePoint(point,(UIElement)relative);
    var down=new MouseButtonEventArgs(Mouse.PrimaryDevice,0,MouseButton.Left) { RoutedEvent=Mouse.PreviewMouseDownEvent };
    typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!.SetValue(down,1);
    source.RaiseEvent(down);
    Require(_bandOrigin!=null, $"Routed mouse down did not offer a rectangle ({modifiers}, actual {Keyboard.Modifiers}).");
    point=to; MediaArea.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice,0) { RoutedEvent=Mouse.PreviewMouseMoveEvent });
    Require(_band!=null, "Routed mouse move did not start a rectangle.");
    MediaArea.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,0,MouseButton.Left) { RoutedEvent=Mouse.PreviewMouseUpEvent });
    await Idle(); Require(_band==null && !MediaArea.IsMouseCaptured,"Routed mouse up retained capture or the frame.");
   }
   var gapHit=InputHitTest(panel.TranslatePoint(new Point(-12,Center(0).Y),this));
   Require(gapHit is DependencyObject hit && (ReferenceEquals(hit,MediaArea) || MediaArea.IsAncestorOf(hit)), "The sidebar gap is outside the selection hit surface.");
   await Gesture(MediaArea,new Point(-12,Center(0).Y),Center(0),ModifierKeys.None);
   Require(SelectedMedia.Count==1 && SelectedMedia[0].Id==Id(0),"The routed sidebar-gap gesture missed its tile.");
   var first=(ListBoxItem)MediaList.ItemContainerGenerator.ContainerFromIndex(0);
   await Gesture(first,Center(0),Center(1),ModifierKeys.Alt);
   Require(SelectedMedia.Count==2,"Alt dragging a selected tile did not draw a rectangle.");
   var third=(ListBoxItem)MediaList.ItemContainerGenerator.ContainerFromIndex(2);
   await Gesture(third,Center(2),Center(2)+new Vector(10,10),ModifierKeys.Control|ModifierKeys.Alt);
   Require(SelectedMedia.Count==3,"Ctrl+Alt did not retain the previous selection.");
  }
  finally { SetKeyboardState(savedKeys); BandPosition=savedPosition; BandPressed=savedPressed; CancelBand(); }
  MediaList.SelectedItems.Clear(); await Idle();
  System.Windows.Documents.AdornerLayer? AdornerLayer() => System.Windows.Documents.AdornerLayer.GetAdornerLayer(panel);
 }
}
