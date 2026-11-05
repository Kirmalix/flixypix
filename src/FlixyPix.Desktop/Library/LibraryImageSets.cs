using System.IO;
using System.Windows;
using System.Windows.Controls;
using FlixyPix.Archives;
using FlixyPix.Core;
namespace FlixyPix.Desktop;
public partial class MainWindow
{
 private bool IsImageSetView => _browse.Kind == LibraryViewKind.ImageSets;
 private Grid? _setPage;
 private ListBox? _setList, _setMembers;
 private Guid? _selectedImageSet;
 private Image? _setCover;
 private TextBlock? _setCoverCaption;
 private readonly Dictionary<string,Button> _setActions=[];
 private bool _refreshingSets;
 private CancellationTokenSource? _setCoverWork;
 private Task _setCoverTask=Task.CompletedTask;
 private readonly Dictionary<UIElement, Visibility> _setCovered=[];
 private ImageSet? SelectedImageSet => _setList?.SelectedItem as ImageSet;
 private void HideImageSets()
 {
  if(_setPage!=null)_setPage.Visibility=Visibility.Collapsed;
  _setCoverWork?.Cancel();if(_setCover!=null)_setCover.Source=null;
  foreach(var (element,visibility) in _setCovered)element.Visibility=visibility;_setCovered.Clear();
 }
 private void RefreshImageSets()
 {
  RememberDetailsDraft();HideRoleView();
  if(_setPage==null)
  {
   _setList=new ListBox {DisplayMemberPath=nameof(ImageSet.Name),MinWidth=180,Margin=new Thickness(0,0,16,0)};
   _setMembers=new ListBox {DisplayMemberPath=nameof(MediaItem.Title)};
   _setList.SelectionChanged+=(_,_)=>{if(_refreshingSets)return;_selectedImageSet=SelectedImageSet?.Id;ShowSetMembers();if(IsImageSetView)RecordView();};
   _setMembers.SelectionChanged+=(_,_)=>UpdateSetActions();
   _setMembers.MouseDoubleClick+=async(_,_)=>await OpenSetImageFromUiAsync();
   var body=new Grid();body.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});body.ColumnDefinitions.Add(new(){Width=new GridLength(2,GridUnitType.Star)});
   _setCover=new Image {Height=150,Stretch=System.Windows.Media.Stretch.Uniform,Margin=new Thickness(0,0,0,8)};
   _setCoverCaption=new TextBlock {TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,8)};
   var members=new DockPanel();DockPanel.SetDock(_setCover,Dock.Top);members.Children.Add(_setCover);DockPanel.SetDock(_setCoverCaption,Dock.Top);members.Children.Add(_setCoverCaption);members.Children.Add(_setMembers);
   body.Children.Add(_setList);Grid.SetColumn(members,1);body.Children.Add(members);
   var frame=new WindowFrame(T("Image sets"),null,body,scroll:false,wrapActions:true);
   void Add(string key,Action action)=>AddAsync(key,()=>{action();return Task.CompletedTask;});
   // Awaited inside the handler, so failures after the first await are reported as well.
   void AddAsync(string key,Func<Task> action)=>_setActions[key]=frame.Add(T(key),async()=>{try{await action();}catch(Exception error) when(IsSetActionFailure(error)){MenuFailed(T(key),error);}});
   Add(TKey("New entry"),CreateImageSet);Add(TKey("Edit"),EditImageSet);
   AddAsync(TKey("Suggest image sets"),SuggestImageSetsAsync);
   Add(TKey("Publication groups"),AddImageSetToPublicationGroup);
   AddAsync(TKey("Open"),OpenSetImageFromUiAsync);
   Add(TKey("Move up"),()=>MoveSetMember(-1));Add(TKey("Move down"),()=>MoveSetMember(1));
   Add(TKey("Set as cover"),()=>{if(CurrentImageSet() is {} set&&_setMembers.SelectedItem is MediaItem media){_repository.UpsertImageSet(set with {CoverId=media.Id});RefreshImageSets();}});
   Add(TKey("Remove"),()=>{if(CurrentImageSet() is {} set&&_setMembers.SelectedItem is MediaItem media){_repository.UpsertImageSet(set with {MediaIds=set.MediaIds.Where(id=>id!=media.Id).ToList(),CoverId=set.CoverId==media.Id?null:set.CoverId});RefreshImageSets();}});
   Add(TKey("Delete"),()=>{if(CurrentImageSet() is {} set&&Message.Show(this,set.Name,T("Delete"),MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)==MessageBoxResult.Yes){_repository.DeleteImageSet(set.Id);RefreshImageSets();}});
   _setPage=new Grid();_setPage.SetResourceReference(BackgroundProperty,"WindowBackground");_setPage.Children.Add(frame);Grid.SetRow(_setPage,1);Grid.SetRowSpan(_setPage,4);Panel.SetZIndex(_setPage,20);MediaPane.Children.Add(_setPage);
  }
  foreach(UIElement element in MediaPane.Children)if(element!=_setPage&&Grid.GetRow(element)>=1){_setCovered.TryAdd(element,element.Visibility);element.Visibility=Visibility.Collapsed;}
  _setPage.Visibility=Visibility.Visible;
  var keep=_selectedImageSet;var member=(_setMembers!.SelectedItem as MediaItem)?.Id;var text=SearchBox.Text.Trim();
  _refreshingSets=true;
  try{
  var all=_repository.GetImageSets();
  _setList!.ItemsSource=all.Where(set=>text.Length==0||set.Name.Contains(text,StringComparison.CurrentCultureIgnoreCase)||set.Description?.Contains(text,StringComparison.CurrentCultureIgnoreCase)==true).ToArray();
  CountLabel.Text=T("{0:N0} of {1:N0} entries",_setList.Items.Count,all.Count);
  _setList.SelectedItem=_setList.Items.Cast<ImageSet>().FirstOrDefault(set=>set.Id==keep)??_setList.Items.Cast<ImageSet>().FirstOrDefault();
  }finally{_refreshingSets=false;}
  _selectedImageSet=SelectedImageSet?.Id;
  ShowSetMembers(SelectedImageSet?.Id==keep?member:null);ViewTitle.Text=T("Image sets");UpdateNavigation();AdaptToWidth();RecordView();
 }
 private ImageSet? CurrentImageSet()=>SelectedImageSet is {} set?_repository.GetImageSet(set.Id):null;
 private void ShowSetMembers(Guid? selected=null)
 {
  if(_setMembers==null)return;
  _setMembers.ItemsSource=SelectedImageSet?.MediaIds.Select(id=>_repository.GetMedia(id)).OfType<MediaItem>().ToArray()??[];
  _setMembers.SelectedItem=_setMembers.Items.Cast<MediaItem>().FirstOrDefault(m=>m.Id==selected)??_setMembers.Items.Cast<MediaItem>().FirstOrDefault();
  UpdateSetActions();_setCoverTask=LoadSetCoverAsync();
 }
 private void UpdateSetActions()
 {
  var set=SelectedImageSet;var index=_setMembers?.SelectedIndex??-1;var count=_setMembers?.Items.Count??0;
  foreach(var(key,button)in _setActions)button.IsEnabled=key switch
  {"New entry" or "Suggest image sets"=>true,"Edit" or "Delete" or "Publication groups"=>set!=null,"Open"=>count>0,"Move up"=>index>0,"Move down"=>index>=0&&index<count-1,_=>index>=0};
 }
 private async Task LoadSetCoverAsync()
 {
  _setCoverWork?.Cancel();_setCoverWork?.Dispose();var work=_setCoverWork=new CancellationTokenSource();var token=work.Token;
  _setCover!.Source=null;var set=SelectedImageSet;var id=set?.CoverId??set?.MediaIds.FirstOrDefault();var media=id is {} value?_repository.GetMedia(value):null;
  _setCoverCaption!.Text=media==null?T("Images")+": 0":T("Cover image")+": "+media.Title;
  if(media==null)return;
  try
  {
   // Preview existing cache only; archive materialization happens when opening the viewer.
   if(!File.Exists(media.Path)){_setCoverCaption.Text+="\n"+media.Path;return;}
   var bitmap=await DesktopImaging.LoadBitmapAsync(media.Path,320,token);
   if(!token.IsCancellationRequested&&IsImageSetView)_setCover.Source=bitmap;
  }
  catch(OperationCanceledException){}
  catch(Exception error) when(error is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or ArgumentException)
  {if(!token.IsCancellationRequested)_setCoverCaption.Text+="\n"+error.Message;}
 }
 private void CreateImageSet()
 {
  var fields=Dialogs.Fields(this,T("Image sets"),(T("Name"),""),(T("Description"),""));if(fields==null||string.IsNullOrWhiteSpace(fields[0]))return;
  _selectedImageSet=_repository.UpsertImageSet(new(){Name=fields[0],Description=fields[1]}).Id;RefreshImageSets();
 }
 private void EditImageSet()
 {
  if(CurrentImageSet() is not {} set)return;var fields=Dialogs.Fields(this,T("Image sets"),(T("Name"),set.Name),(T("Description"),set.Description??""));
  if(fields==null||string.IsNullOrWhiteSpace(fields[0]))return;
  if(_repository.GetImageSet(set.Id) is {} current)_repository.UpsertImageSet(current with {Name=fields[0],Description=fields[1]});RefreshImageSets();
 }
 private void MoveSetMember(int delta)
 {
  if(CurrentImageSet() is not {} set||_setMembers?.SelectedItem is not MediaItem image)return;
  var ids=set.MediaIds.ToList();var from=ids.IndexOf(image.Id);var to=from+delta;if(from<0||to<0||to>=ids.Count)return;(ids[from],ids[to])=(ids[to],ids[from]);
  _repository.UpsertImageSet(set with {MediaIds=ids});RefreshImageSets();_setMembers.SelectedIndex=to;
 }
 private async Task OpenSetImageAsync()
 {
  if(CurrentImageSet() is not {} set)return;
  await OpenImageSetAsync(set,(_setMembers?.SelectedItem as MediaItem)?.Id);
 }
 private async Task OpenImageSetAsync(ImageSet set,Guid? selectedId=null)
 {
  var selected=selectedId??set.CoverId??set.MediaIds.FirstOrDefault();if(selected==Guid.Empty)return;
  var repository=_repository;var images=await Task.Run(()=>set.MediaIds.Select(repository.GetMedia).OfType<MediaItem>().ToArray());
  if(images.Length==0||!ReferenceEquals(repository,_repository)||_closing)return;
  // Archive images are extracted only when shown; one inspection per archive serves the whole set.
  var inspected=new Dictionary<string,Task<IReadOnlyList<ArchiveImageEntry>>>();var resolved=new Task<MediaItem>?[images.Length];
  async Task<MediaItem?> ItemAt(int index)
  {
   if(index<0||index>=images.Length)return null;var task=resolved[index]??=EnsureArchiveImageAsync(images[index],default,inspected);
   try{return await task;}catch(Exception){if(ReferenceEquals(resolved[index],task))resolved[index]=null;throw;}
  }
  var initial=Math.Max(0,Array.FindIndex(images,image=>image.Id==selected));
  _viewer?.Close();var viewer=new ImageViewerWindow(this,images,images[initial].Id,ItemAt,images.Length,initial);_viewer=viewer;
  viewer.Closed+=(_,_)=>{if(ReferenceEquals(_viewer,viewer))_viewer=null;};viewer.Show();await viewer.ReadyTask;
 }
 private async Task OpenSetImageFromUiAsync()
 {
  try{await OpenSetImageAsync();}
  catch(Exception error) when(IsSetActionFailure(error))
  {MenuFailed(T("Open"),error);}
 }
 /// <summary>Expected failures of set and group actions; InvalidDataException covers damaged archives.</summary>
 private static bool IsSetActionFailure(Exception error)=>error is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or NotSupportedException or Microsoft.Data.Sqlite.SqliteException;
 private void AddImagesToSet(ImageSet set,IReadOnlyList<MediaItem> images)
 { _repository.AddImageSetMembers(set.Id,images.Where(i=>i.Kind==MediaKind.Image).Select(i=>i.Id)); }
 private async Task SuggestImageSetsAsync()
 {
  try
  {
   var repository=_repository;var proposals=await Task.Run(()=>repository.GetImageSetProposals());
   if(!ReferenceEquals(repository,_repository)||_closing)return;
   var window=new ImageSetProposalsWindow(this,proposals,repository.GetMedia,async choices=>
   {var created=await Task.Run(()=>repository.ApplyImageSetProposals(choices));_selectedImageSet=created.FirstOrDefault()?.Id;},repository.GetImageSet);
   if(window.ShowDialog()==true&&IsImageSetView)RefreshImageSets();
  }
  catch(Exception error)when(error is IOException or InvalidOperationException or ArgumentException or Microsoft.Data.Sqlite.SqliteException)
  {MenuFailed(T("Suggest image sets"),error);}
 }
}
