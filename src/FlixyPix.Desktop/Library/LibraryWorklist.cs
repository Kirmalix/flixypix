using System.Windows;
using FlixyPix.Core;
namespace FlixyPix.Desktop;

/// <summary>A filter such as "without thumbnail" is a list to work through. What an edit moves out of the filter stays in the
/// list, marked as done, so the result can be seen; "Update list" or another view removes it.</summary>
public partial class MainWindow
{
 private WorklistState _worklist => _browse.Worklist;
 private HashSet<Guid> _keptMedia => _worklist.Kept;
 private ViewState? _shownView { get => _worklist.Shown; set => _worklist.Shown=value; }
 private bool _dropKeptMedia { get => _worklist.DropNext; set => _worklist.DropNext=value; }
 private bool _ignoreLeavingOnce { get => _worklist.IgnoreLeavingOnce; set => _worklist.IgnoreLeavingOnce=value; }

 /// <summary>Called before each query: media of the list on screen that no longer match are kept in it.</summary>
 private async Task UpdateKeptMediaAsync(ViewState state,MediaQuery matches,long generation)
 {
  var request=_worklist.Begin(state,(MediaList.ItemsSource as IEnumerable<MediaCard>??[]).Select(card=>card.Item.Id),generation);
  var repository=_repository;
  var still=request.Listed.Length==0 ? new HashSet<Guid>() : await Task.Run(()=>repository.MatchingMedia(matches,request.Listed));
  if(generation!=_refreshVersion||_closing)return;
  _worklist.Commit(request,still);
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
