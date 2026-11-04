using System.Windows;
using FlixyPix.Core;
namespace FlixyPix.Desktop;

/// <summary>A filter such as "without thumbnail" is a list to work through. What an edit moves out of the filter stays in the
/// list, marked as done, so the result can be seen; "Update list" or another view removes it.</summary>
public partial class MainWindow
{
 private readonly HashSet<Guid> _keptMedia=[];
 private ViewState? _keptView,_shownView;
 private bool _dropKeptMedia,_ignoreLeavingOnce;

 private static bool IsWorklist(ViewState state)=>state.Filter.HasThumbnail is not null||state.Filter.UnratedOnly;
 private static bool SameList(ViewState? known,ViewState state)=>known!=null&&known.SameView(state with {SortOrder=known.SortOrder});

 /// <summary>Called before each query: media of the list on screen that no longer match are kept in it.</summary>
 private async Task UpdateKeptMediaAsync(ViewState state,MediaQuery matches)
 {
  var sameList=SameList(_keptView,state);var drop=_dropKeptMedia;var ignore=_ignoreLeavingOnce;
  _keptView=state;_dropKeptMedia=_ignoreLeavingOnce=false;
  if(!sameList||drop||!IsWorklist(state))_keptMedia.Clear();
  if(!sameList||drop||ignore||!IsWorklist(state)||!SameList(_shownView,state))return;
  var listed=(MediaList.ItemsSource as IEnumerable<MediaCard>??[]).Select(card=>card.Item.Id).Where(id=>!_keptMedia.Contains(id)).ToArray();
  if(listed.Length==0)return;
  var repository=_repository;
  var still=await Task.Run(()=>repository.MatchingMedia(matches,listed));
  _keptMedia.UnionWith(listed.Where(id=>!still.Contains(id)));
 }

 /// <summary>The kept media that still exist and no longer match.</summary>
 private HashSet<Guid> FinishedMedia(MediaQuery matches,IReadOnlyCollection<Guid> kept)
 {
  if(kept.Count==0)return [];
  var existing=_repository.MatchingMedia(new MediaQuery(),kept).ToHashSet();
  existing.ExceptWith(_repository.MatchingMedia(matches,kept));
  return existing;
 }

 private void ShowWorklistActions(ViewState state,int total,int finished)
 {
  UpdateListButton.Visibility=finished>0?Visibility.Visible:Visibility.Collapsed;
  CreateListThumbnailsButton.Visibility=state.Filter.HasThumbnail==false&&total>finished?Visibility.Visible:Visibility.Collapsed;
 }
 private void HideWorklistActions()=>UpdateListButton.Visibility=CreateListThumbnailsButton.Visibility=Visibility.Collapsed;

 private void UpdateListClicked(object sender,RoutedEventArgs e) {_dropKeptMedia=true;Refresh();}

 private async void CreateListThumbnailsClicked(object sender,RoutedEventArgs e)
 {
  var query=CaptureViewState().ToQuery(DateTimeOffset.Now);var members=_playlist?.MediaIds.ToHashSet();
  await RunOperation(T("Create thumbnails for this list"),async token=>
  {
   var videos=new List<MediaItem>();
   await foreach(var item in StreamCatalog(query,token))if(item.Kind==MediaKind.Video&&members?.Contains(item.Id)!=false)videos.Add(item);
   // The whole list stays visible, also the part beyond what is on screen.
   _keptMedia.UnionWith(videos.Select(item=>item.Id));
   await RegenerateThumbnailsAsync(videos,token);
  });
 }
}
