using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FlixyPix.Core;
namespace FlixyPix.Desktop;
public partial class MainWindow
{
 private async Task RunLibraryNavigationSmokeTestAsync(string directory)
 {
  static void Check(bool condition,string message) {if(!condition)throw new InvalidOperationException(message);}
  var healthIds=new List<Guid>();
  var before=_repository.GetPreferences();var state=CaptureViewState();
  try {
   Check(_sidebarSections.Count==4,"Sidebar sections were not discovered.");
   _sidebarSections["tags"].IsExpanded=false;Check(_repository.GetPreferences().Sidebar.CollapsedSections.Contains("tags"),"Collapsed section was not saved.");
   _sidebarSections["tags"].IsExpanded=true;
   var empty=Path.Combine(directory,"empty-root");
   _repository.SaveLibraryFolders([new(){Path=directory},new(){Path=empty}]);
   await RefreshAsync();var roots=((IEnumerable<BrowseNode>)Folders.ItemsSource).Single().Children;
   Check(roots.Any(n=>n.Path==directory)&&roots.Any(n=>n.Path==empty),"Registered and empty roots were not shown.");
   await RunSidebarDesignSmokeTestAsync(directory);
   var item=MediaList.Items.Cast<MediaCard>().First().Item;
   var slots=QuickTags.ToArray();AssignQuickTag(9,"stable-smoke");await RefreshAsync(item.Id);
   Check(QuickTags[8]=="stable-smoke","Fixed quick tag moved during refresh.");
   MediaList.SelectedItem=MediaList.Items.Cast<MediaCard>().Single(c=>c.Item.Id==item.Id);MediaList.Focus();var saved=new byte[256];GetKeyboardState(saved);var keyboard=(byte[])saved.Clone();keyboard[0x11]=128;keyboard[0xA2]=128;
   try {SetKeyboardState(keyboard);MediaList.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(MediaList)!,0,Key.NumPad9){RoutedEvent=Keyboard.PreviewKeyDownEvent});}
   finally {SetKeyboardState(saved);}
   await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);await _lastRefresh;
   // Route handler is async: wait for the data write, not only its first yield.
   for(int i=0;i<30&&!_repository.GetMedia(item.Id)!.Tags.Contains("stable-smoke");i++)await Task.Delay(20);
   Check(_repository.GetMedia(item.Id)!.Tags.Contains("stable-smoke"),"Ctrl+numpad shortcut did not tag the selection.");
   var quick=_repository.GetQuickCollection();var member=quick.MediaIds.Contains(item.Id);
   MediaList.UpdateLayout();var card=MediaList.Items.Cast<MediaCard>().Single(c=>c.Item.Id==item.Id);
   var container=(ListBoxItem)MediaList.ItemContainerGenerator.ContainerFromItem(card);
   var collectButton=Visuals<Button>(container).First(b=>b.ToolTip as string==card.QuickHint);
   collectButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));await _lastRefresh;
   Check(_repository.GetQuickCollection().MediaIds.Contains(item.Id)!=member,"Quick collection overlay action did not toggle membership.");ToggleQuickCollection(item);await _lastRefresh;
   var broken=Path.Combine(directory,"broken-thumbnail.jpg");File.WriteAllText(broken,"not an image");
   foreach(var path in new[]{item.ThumbnailPath??item.Path,broken,Path.Combine(directory,"missing.jpg")})
   {
    var media=_repository.Upsert(new(){Title="nav-health",Path=Path.Combine(directory,Guid.NewGuid()+".mp4"),Kind=MediaKind.Video,ThumbnailPath=path});healthIds.Add(media.Id);
   }
   ApplyViewState(new(){View="Video",SearchText="nav-health",Filter=new(){HasThumbnail=false,UnratedOnly=true}});await _lastRefresh;
   Check(MediaList.Items.Count==2,"Missing and corrupt thumbnails were not found.");
   Check(MediaList.Items.Cast<MediaCard>().All(c=>c.Item.Rating==0&&_repository.GetThumbnailHealth(c.Item.Id) is {Available:false}),"Missing thumbnail results are unchecked or rated.");
   Capture(RootContent,Path.Combine(directory,"desktop-navigation.png"));
  }
  finally {foreach(var id in healthIds)_repository.Delete(id);foreach(var media in _repository.Search().Where(m=>m.Tags.Contains("stable-smoke")))_repository.Upsert(media with {Tags=media.Tags.Where(t=>t!="stable-smoke").ToList()});_repository.SavePreferences(before);_requiredTags.Clear();ApplyViewState(state);await _lastRefresh;}
 }
}
