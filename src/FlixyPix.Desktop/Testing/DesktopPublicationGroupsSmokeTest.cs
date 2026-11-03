using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 private async Task RunPublicationGroupsSmokeAsync(string directory)
 {
  static void Require(bool condition,string text){if(!condition)throw new InvalidOperationException(text);}
  static void Click(Button button)=>button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
  var before=CaptureViewState();var images=new List<MediaItem>();var videos=new List<MediaItem>();ImageSet? set=null;PublicationGroup? group=null;
  try
  {
   for(var i=0;i<2;i++)
   {
    var path=Path.Combine(directory,"group-image-"+i+".png");using(var image=new ImageMagick.MagickImage(new ImageMagick.MagickColor(i==0?"blue":"orange"),64,64))image.Write(path,ImageMagick.MagickFormat.Png);
    images.Add(_repository.Upsert(new(){Path=path,Title="Group image "+i,Kind=MediaKind.Image}));
    videos.Add(_repository.Upsert(new(){Path=Path.Combine(directory,"group-video-"+i+".mp4"),Title="Group video "+i,Kind=MediaKind.Video}));
   }
   set=_repository.UpsertImageSet(new(){Name="Group ordered set",MediaIds=[images[1].Id,images[0].Id],CoverId=images[0].Id});
   group=_repository.UpsertPublicationGroup(new(){Name="Synthetic publication",Members=[new(PublicationGroupMemberKind.Video,videos[0].Id)]});
   var videoMenu=Menus.Create(MediaMenu([videos[1]]),(_,error)=>throw error);
   var groupMenu=videoMenu.Items.OfType<MenuItem>().Single(m=>Equals(m.Header,T("Publication groups")));
   groupMenu.Items.OfType<MenuItem>().Single(m=>Equals(m.Header,group.Name)).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
   ApplyViewState(new(){View="image-sets",ImageSetId=set.Id});await _lastRefresh;
   var setMenu=Menus.Create(ImageSetPublicationGroupChoices(set),(_,error)=>throw error);
   setMenu.Items.OfType<MenuItem>().Single(m=>Equals(m.Header,group.Name)).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
   ApplyViewState(new(){View="publication-groups",PublicationGroupId=group.Id});await _lastRefresh;
   await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
   Require(_publicationPage!.IsVisible&&!MediaList.IsVisible&&_setPage?.IsVisible!=true&&SelectedPublicationGroup?.Id==group.Id,"Publication route/lifecycle did not replace media/set page.");
   Require(SelectedPublicationGroup!.Members.SequenceEqual(new[]{new PublicationGroupMember(PublicationGroupMemberKind.Video,videos[0].Id),new(PublicationGroupMemberKind.Video,videos[1].Id),new(PublicationGroupMemberKind.ImageSet,set.Id)}),"Context additions changed typed membership/order.");
   _publicationMembers!.SelectedIndex=2;Click(_publicationActions["Move up"]);Click(_publicationActions["Move up"]);
   Require(SelectedPublicationGroup!.Members[0]==new PublicationGroupMember(PublicationGroupMemberKind.ImageSet,set.Id)&&!_publicationActions["Move up"].IsEnabled,"Reorder lost selection/boundary.");
   Click(_publicationActions["Open"]);await _publicationOpenTask;
   Require(_viewer is ImageViewerWindow{CurrentIndex:1},"Group did not open set viewer in saved order at its cover.");_viewer!.Close();_viewer=null;
   Require(CaptureViewState().PublicationGroupId==group.Id&&KeyboardRegions().Contains(_publicationPage),"Group view state/keyboard region missing.");
   _toasts.Children.Clear();Capture(this,Path.Combine(directory,"publication-groups-main.png"));
   ApplyViewState(new(){View="all"});await _lastRefresh;Require(MediaList.IsVisible&&!_publicationPage.IsVisible,"Media view did not return after groups.");
   await WalkHistoryAsync(-1);Require(IsPublicationGroupView&&SelectedPublicationGroup?.Id==group.Id,"Back lost selected group.");
   await WalkHistoryAsync(1);Require(!IsPublicationGroupView&&MediaList.IsVisible,"Forward did not restore media view.");
   ApplyViewState(new(){View="publication-groups",PublicationGroupId=group.Id});await _lastRefresh;
   SearchBox.Text="#literal";_searchTimer.Stop();SearchKeyDown(SearchBox,new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(SearchBox)!,0,Key.Enter){RoutedEvent=Keyboard.KeyDownEvent});await RefreshAsync();
   Require(IsPublicationGroupView&&SearchBox.Text=="#literal"&&_requiredTags.Count==0,"Group search shorthand changed media filters.");
  }
  finally
  {
   if(group!=null)_repository.DeletePublicationGroup(group.Id);if(set!=null)_repository.DeleteImageSet(set.Id);
   foreach(var item in videos.Concat(images))_repository.Delete(item.Id);ApplyViewState(before);await _lastRefresh;
  }
 }
}
