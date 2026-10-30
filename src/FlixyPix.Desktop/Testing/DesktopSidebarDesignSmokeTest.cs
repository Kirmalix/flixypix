using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using FlixyPix.Core;
namespace FlixyPix.Desktop;
public partial class MainWindow
{
 private async Task RunSidebarDesignSmokeTestAsync(string directory)
 {
  static void Check(bool condition,string message) {if(!condition)throw new InvalidOperationException(message);}
  async Task Idle()=>await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
  foreach(var key in new[]{"folders","collections","tags","categories"})Check(_sidebarSections[key].Template!=null,"Card template missing.");
  Check(SidebarCard.GetActions(_sidebarSections["collections"]) is Button,"Collection header action missing.");
  var expanded=_sidebarSections["tags"].IsExpanded;
  var action=(Button)SidebarCard.GetActions(_sidebarSections["tags"]);
  action.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));await Idle();
  if(_sidebarToolsMenu!=null)_sidebarToolsMenu.IsOpen=false;
  Check(_sidebarSections["tags"].IsExpanded==expanded,"Header tools changed collapse state.");
  var height=_folderDesiredHeight;
  FolderHeightDragging(this,new DragDeltaEventArgs(0,40));await Idle();SaveFolderViewportState();
  Check(_repository.GetPreferences().Sidebar.FolderHeight==height+40,"Folder height was not persisted.");
  FolderViewport.ScrollToHorizontalOffset(35);await Idle();SaveFolderViewportState();
  Check(FolderViewport.ScrollableWidth>0&&_repository.GetPreferences().Sidebar.FolderHorizontalOffset>0,"Folder horizontal scrolling was not persisted.");
  var width=SidebarColumn.Width;SidebarColumn.Width=new GridLength(300);await Idle();
  Capture(RootContent,Path.Combine(directory,"sidebar-cards.png"));
  SidebarColumn.Width=new GridLength(180);await Idle();Capture(RootContent,Path.Combine(directory,"sidebar-cards-narrow.png"));
  var scaled=new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(Sidebar.ActualWidth*1.5),(int)Math.Ceiling(Sidebar.ActualHeight*1.5),144,144,PixelFormats.Pbgra32);
  var drawing=new DrawingVisual();using(var context=drawing.RenderOpen())context.DrawRectangle(new VisualBrush(Sidebar),null,new Rect(0,0,Sidebar.ActualWidth,Sidebar.ActualHeight));scaled.Render(drawing);
  var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(scaled));using(var output=File.Create(Path.Combine(directory,"sidebar-cards-scaled.png")))encoder.Save(output);
  SidebarColumn.Width=width;_folderDesiredHeight=height;FolderViewport.Height=height;
  var page=new HomePage();var host=new Window {Owner=this,Width=960,Height=760,Content=page,ShowInTaskbar=false};
  var featured=new MediaItem {Kind=MediaKind.Video,Title="Featured",Path="featured.mp4"};
  var row=new MediaItem {Kind=MediaKind.Video,Title="Ordinary",Path="ordinary.mp4"};
  var requests=new List<Guid>();var tokens=new List<CancellationToken>();
  page.FeaturedVideoArtwork=(item,token)=>{requests.Add(item.Id);tokens.Add(token);return Task.FromResult<ImageSource?>(null);};
  var feed=new HomeFeed([new(featured)], [new(HomeRowKind.RecentlyAdded,"Recent",[new(row)],[])]);
  try {
   host.Show();await Idle();page.Show(feed,false);await Idle();
   Check(requests.Count>0&&requests.All(id=>id==featured.Id),"Ordinary cards requested high-resolution artwork.");
   var count=requests.Count;host.Height=430;await Idle();page.Show(feed,false);await Idle();
   Check(requests.Count==count,"Hidden banner requested high-resolution artwork.");
   Check(tokens.All(t=>t.IsCancellationRequested),"Hidden banner requests were not cancelled.");
  } finally {host.Close();}
 }
}
