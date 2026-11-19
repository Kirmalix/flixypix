using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 private async Task RunStackViewportRegressionAsync(string directory,List<Guid> cleanup)
 {
  static void Check(bool value,string message){if(!value)throw new InvalidOperationException("Stack viewport: "+message);}
  async Task Settle(){await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);await Task.Delay(380);}
  var picture=Path.Combine(directory,"grid-thumbnail.png");
  var drawing=new DrawingVisual();using(var dc=drawing.RenderOpen())dc.DrawRectangle(Brushes.SteelBlue,null,new Rect(0,0,320,180));
  var bitmap=new RenderTargetBitmap(320,180,96,96,PixelFormats.Pbgra32);bitmap.Render(drawing);
  using(var file=File.Create(picture)){var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));png.Save(file);}
  var videos=Enumerable.Range(0,100).Select(i=>_repository.Upsert(new MediaItem{Path=Path.Combine(directory,$"viewport-{i:000}.mp4"),Title=$"Viewport {i:000}",Kind=MediaKind.Video,Tags=["stack-viewport"],ThumbnailPath=picture})).ToArray();
  cleanup.AddRange(videos.Select(i=>i.Id));
  var stack=_repository.SaveVideoStack(new VideoStack(Guid.NewGuid(),"Viewport",videos[30].Id,videos.Skip(30).Take(5).Select(i=>i.Id).ToArray()));
  var originalSteady=Motion.Steady;Motion.Steady=false;
  try
  {
   ApplyViewState(new ViewState{View="Video",Tag="stack-viewport"});await _lastRefresh;await Settle();
   MediaList.SelectedItem=MediaList.Items.Cast<MediaCard>().Single(c=>c.Item.Id==videos[0].Id);await Settle();
   await ScrollToAsync(28);await Settle();
   var source=MediaList.ItemsSource;
   var master=MediaList.Items.Cast<MediaCard>().Single(c=>c.Item.Id==stack.MasterId);
   var tile=(ListBoxItem)MediaList.ItemContainerGenerator.ContainerFromItem(master);
   await master.EnsureLoaded();Check(master.Thumbnail!=null,"synthetic thumbnail did not load");
   var thumbnail=master.Thumbnail;var offset=Tiles!.VerticalOffset;var origin=tile.TranslatePoint(new Point(),MediaList);
   var events=0;SelectionChangedEventHandler selectionChanged=(_,_)=>events++;MediaList.SelectionChanged+=selectionChanged;
   _gridTransition?.Clear();_gridTransition=new MediaGridTransition(MediaList,()=>true);
   try
   {
    var badge=Visuals<Button>(tile).Single(b=>Equals(b.Tag,master.StackLabel));
    badge.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,badge));await _lastRefresh;
    Check(ReferenceEquals(source,MediaList.ItemsSource),"expansion replaced the entire item source");
    Check(ReferenceEquals(tile,MediaList.ItemContainerGenerator.ContainerFromItem(master)),"expansion rebuilt the master container");
    Check(ReferenceEquals(thumbnail,master.Thumbnail),"expansion reloaded the master thumbnail");
    Check(events==0 && SelectedMedia.Single().Id==videos[0].Id,"expansion disturbed the unrelated selection");
    Check(Math.Abs(Tiles.VerticalOffset-offset)<.5,"expansion scrolled to the unrelated selection");
    Check((tile.TranslatePoint(new Point(),MediaList)-origin).Length<.5,"master moved during expansion");
    Check(_gridTransition.MovingDistance>1,"expansion has no displacement before its first frame");
    await Task.Delay(60);Check(_gridTransition.MovingDistance>0,"expansion was cancelled before its intermediate frame");
    Capture((FrameworkElement)RootContent,Path.Combine(directory,"stack-expand-intermediate.png"));await Settle();
    await SetVideoStackExpandedAsync(stack,stack.MasterId);
    Check(ReferenceEquals(tile,MediaList.ItemContainerGenerator.ContainerFromItem(master)),"collapse rebuilt the master container");
    Check(events==0 && Math.Abs(Tiles.VerticalOffset-offset)<.5,"collapse disturbed selection or viewport");
    Check(_gridTransition.MovingDistance>1,"collapse did not animate the remaining tiles");await Settle();
   }
   finally{MediaList.SelectionChanged-=selectionChanged;}
   // Remove a visible non-stack tile: its successor must retain its container and move into the gap.
   var removed=MediaList.Items.Cast<MediaCard>().Single(c=>c.Item.Id==videos[29].Id);
   RemoveMedia([removed.Item],false);await _lastRefresh;
   Check(ReferenceEquals(tile,MediaList.ItemContainerGenerator.ContainerFromItem(master)),"removal rebuilt the surviving container");
   Check(_gridTransition.MovingDistance>1,"removal did not produce any reflow");
   await Task.Delay(60);Check(_gridTransition.MovingDistance>0,"removal reflow was cancelled");
   Capture((FrameworkElement)RootContent,Path.Combine(directory,"delete-intermediate.png"));await Settle();
   Check(!_gridTransition.HasMotion,"removal left animation visuals behind");
   // The normal motion policy must also work on WPF's software renderer, rather than requiring tier 2.
   var steady=Motion.Steady;Motion.Steady=false;_gridTransition=new MediaGridTransition(MediaList);
   try
   {
    await SetVideoStackExpandedAsync(stack,stack.MasterId);
    Check(!Motion.Enabled || _gridTransition.MovingDistance>1,"default motion policy suppressed lightweight reflow");await Settle();
   }
   finally{Motion.Steady=steady;}
   _gridTransition=new MediaGridTransition(MediaList,()=>true);
   var selected=MediaList.Items.Cast<MediaCard>().Single(c=>c.Item.Id==videos[28].Id);
   MediaList.SelectedItems.Clear();MediaList.SelectedItem=selected;await Settle();
   RemoveMedia([selected.Item],false);await _lastRefresh;
   Check(SelectedMedia.Count==0,"the removed video survived as an off-window selection");
   Check(_gridTransition.MovingDistance>1,"removing the selected tile did not start reflow");
   await Task.Delay(60);Check(_gridTransition.MovingDistance>0,"inspector collapse cancelled the selected-tile reflow");await Settle();
   var member=MediaList.Items.Cast<MediaCard>().Single(c=>c.Item.Id==videos[31].Id);
   MediaList.SelectedItem=member;await Settle();
   ((ListBoxItem)MediaList.ItemContainerGenerator.ContainerFromItem(member)).Focus();
   await SetVideoStackExpandedAsync(stack,stack.MasterId);await Settle();
   Check(SelectedMedia.Single().Id==stack.MasterId && MediaList.IsKeyboardFocusWithin,"collapse lost keyboard ownership of the hidden selected member");
   await SetVideoStackExpandedAsync(stack,stack.MasterId);await Settle();
   stack=_repository.SaveVideoStack(stack with{MasterId=videos[34].Id});await RefreshAsync();await Settle();
   foreach(var card in MediaList.Items.Cast<MediaCard>())
    if(MediaList.ItemContainerGenerator.ContainerFromItem(card) is ListBoxItem realized)
     Check(ReferenceEquals(realized.DataContext,card),"a moved container retained the wrong media identity");
   var repeated=new[]{new MediaCard(videos[0],false,context:_cardContext),new MediaCard(videos[0],false,context:_cardContext),new MediaCard(videos[1],false,context:_cardContext)};
   MediaGridItems.Publish(MediaList,repeated,false);
   MediaGridItems.Publish(MediaList,[new MediaCard(videos[0],false,context:_cardContext),new MediaCard(videos[1],false,context:_cardContext),new MediaCard(videos[0],false,context:_cardContext)],true);
   Check(MediaList.Items.Count==3 && ReferenceEquals(MediaList.Items[2],repeated[1]),"reconciliation lost a repeated media occurrence");
   await RefreshAsync();await Settle();
   File.WriteAllText(Path.Combine(directory,"stack-viewport-result.txt"),$"PASS: stable source, containers, thumbnail and offscreen selection; fixed viewport on expand/collapse; intermediate reflow on removal; default animation policy (render tier {RenderCapability.Tier>>16}, Windows animation {Motion.Enabled}).");
  }
  finally{Motion.Steady=originalSteady;_expandedVideoStacks.Remove(stack.Id);_repository.DeleteVideoStack(stack.Id,stack.Revision);}
 }
}
