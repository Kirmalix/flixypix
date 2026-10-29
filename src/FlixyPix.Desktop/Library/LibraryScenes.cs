using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using FlixyPix.Core;
using FlixyPix.MediaTools;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 private void EditSelectedScene(object sender, RoutedEventArgs e)
 {
  if (Selected is not { } item || Scenes.SelectedItem is not Scene scene) return;
  var fields = Dialogs.Fields(this,T("Edit scene"),("Name",scene.Title),(T("Description"),scene.Description ?? ""),
   (T("Start in seconds"),scene.StartSeconds.ToString(CultureInfo.InvariantCulture)),(T("End in seconds"),scene.EndSeconds.ToString(CultureInfo.InvariantCulture)),(T("Scene tags, separated by semicolons"),string.Join("; ",scene.Tags)));
  if (fields == null) return;
  if (!TrySeconds(fields[2],out var start) || !TrySeconds(fields[3],out var end) || start < 0 || end <= start || item.DurationSeconds is { } duration && end > duration)
  { Message.Show(this,T("Start and end must form a valid range within the item.")); return; }
  List<string> tags;
  try { tags=CatalogRepository.NormalizeSceneTags(fields[4].Split(';',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries)); }
  catch(ArgumentException error) { Message.Show(this,error.Message,T("Scene tags")); return; }
  _repository.UpsertScene(scene with { Title = fields[0], Description = fields[1], StartSeconds = start, EndSeconds = end,Tags=tags });
  ShowDetails();
 }

 private void EditSelectedBookmark(object sender, RoutedEventArgs e)
 {
  if (Selected is not { } item || Bookmarks.SelectedItem is not Bookmark bookmark) return;
  var fields = Dialogs.Fields(this,T("Edit bookmark"),("Name",bookmark.Title),(T("Position in seconds"),bookmark.PositionSeconds.ToString(CultureInfo.InvariantCulture)),(T("Description"),bookmark.Description??""),
   (T("Tags, separated by semicolons"),string.Join("; ",bookmark.Tags)));
  if (fields == null) return;
  if (!TrySeconds(fields[1],out var position) || position < 0 || item.DurationSeconds is { } duration && position > duration)
  { Message.Show(this,T("The position must lie within the item.")); return; }
  List<string> tags;
  try { tags=CatalogRepository.NormalizeSceneTags(fields[3].Split(';',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries)); }
  catch(ArgumentException error) { Message.Show(this,error.Message,T("Tags")); return; }
  _repository.UpsertBookmark(bookmark with { Title = fields[0], PositionSeconds = position,Description=fields[2],Tags=tags }); ShowDetails();
 }

 private void MoveSelectedSceneUp(object sender,RoutedEventArgs e)=>MoveSelectedScene(-1);
 private void MoveSelectedSceneDown(object sender,RoutedEventArgs e)=>MoveSelectedScene(1);
 private void MoveSelectedBookmarkUp(object sender,RoutedEventArgs e)=>MoveSelectedBookmark(-1);
 private void MoveSelectedBookmarkDown(object sender,RoutedEventArgs e)=>MoveSelectedBookmark(1);
 private void MoveSelectedScene(int delta)
 { if(Scenes.SelectedItem is not Scene scene)return; _repository.MoveScene(scene.Id,delta); ShowDetails(); Scenes.SelectedItem=Scenes.Items.Cast<Scene>().FirstOrDefault(s=>s.Id==scene.Id); }
 private void MoveSelectedBookmark(int delta)
 { if(Bookmarks.SelectedItem is not Bookmark bookmark)return; _repository.MoveBookmark(bookmark.Id,delta); ShowDetails(); Bookmarks.SelectedItem=Bookmarks.Items.Cast<Bookmark>().FirstOrDefault(s=>s.Id==bookmark.Id); }
 private void SetSelectedScenePreview(object sender,RoutedEventArgs e)
 {
  if(Selected is not {} media || Scenes.SelectedItem is not Scene scene)return;
  var selected=ChoosePreviewAsset(media,scene.PreviewAssetId); if(!selected.Accepted)return;
  _repository.SetScenePreview(scene.Id,selected.AssetId); ShowDetails();
 }
 private void SetSelectedBookmarkPreview(object sender,RoutedEventArgs e)
 {
  if(Selected is not {} media || Bookmarks.SelectedItem is not Bookmark bookmark)return;
  var selected=ChoosePreviewAsset(media,bookmark.PreviewAssetId); if(!selected.Accepted)return;
  _repository.SetBookmarkPreview(bookmark.Id,selected.AssetId); ShowDetails();
 }
 private (bool Accepted,Guid? AssetId) ChoosePreviewAsset(MediaItem media,Guid? selectedId)
 {
  var choices=_repository.GetMediaAssetSummaries(media.Id).Select(asset=>new PreviewChoice(asset.Id,asset.Label+(asset.PositionSeconds is {} seconds?$" · {seconds:0.###} s":""))).Prepend(new(null,T("No assignment"))).ToArray();
  var list=new ListBox { ItemsSource=choices,DisplayMemberPath="Label",MinHeight=150 };
  list.SelectedItem=choices.FirstOrDefault(c=>c.Id==selectedId)??choices[0];
  var panel=new DockPanel { Margin=new Thickness(20) }; var buttons=new WrapPanel { HorizontalAlignment=HorizontalAlignment.Right }; DockPanel.SetDock(buttons,Dock.Bottom); panel.Children.Add(buttons); panel.Children.Add(list);
  var dialog=new Window { Owner=this,ShowInTaskbar=false,Title=T("Assign thumbnail"),Content=panel,Width=600,Height=410,WindowStartupLocation=WindowStartupLocation.CenterOwner };
  var save=new Button { Content=T("Assign"),IsDefault=true }; save.Click+=(_,_)=>dialog.DialogResult=true; buttons.Children.Add(save); buttons.Children.Add(new Button { Content=T("Cancel"),IsCancel=true });
  var accepted=dialog.ShowDialog()==true; return (accepted,(list.SelectedItem as PreviewChoice)?.Id);
 }
 private sealed record PreviewChoice(Guid? Id,string Label);
 private async void ScenePreviewSelected(object sender,SelectionChangedEventArgs e)
 {
  var assetId=sender==Scenes?(Scenes.SelectedItem as Scene)?.PreviewAssetId:(Bookmarks.SelectedItem as Bookmark)?.PreviewAssetId;
  if(assetId==null)return;
  var version=++_previewVersion;
  try
  {
   var asset=await Task.Run(()=>_repository.GetMediaAsset(assetId.Value));
   if(asset==null) { Status.Text=T("The assigned thumbnail is missing. Please assign a new image."); return; }
   var path=_repository.MaterializeMediaAsset(asset,Path.Combine(_settings.DataDirectory,"media-assets"));
   var bitmap=await DesktopImaging.LoadBitmapAsync(path,800);
   if(version==_previewVersion && !_closing)Preview.Source=bitmap;
  }
  catch(Exception error) when(error is IOException or InvalidDataException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
  { if(version==_previewVersion && !_closing)Status.Text=T("Scene/bookmark preview: ")+error.Message; }
 }

 private void GenerateScenePreviews()
 {
  if (Selected is not { Kind: MediaKind.Video } item) { Message.Show(this,T("Please select a video first.")); return; }
  var scene = Scenes.SelectedItem as Scene;
  var values = Dialogs.Fields(this,T("Create scene images"),(T("Start in seconds"),(scene?.StartSeconds ?? 0).ToString(CultureInfo.InvariantCulture)),
   (T("End in seconds"),(scene?.EndSeconds ?? item.DurationSeconds ?? 60).ToString(CultureInfo.InvariantCulture)),
   (T("Mode: Count or Interval"),T("Count")),(T("Number of images or interval in seconds"),"12"),
   (T("Replace existing generated images: yes/no"),T("no")));
  if (values == null) return;
  try
  {
   if (!TrySeconds(values[0],out var start) || !TrySeconds(values[1],out var end)) throw new ArgumentException(T("Invalid time range."));
   int? count = null; double? interval = null;
   if (values[2].Trim().Equals(T("Count"),StringComparison.OrdinalIgnoreCase) && int.TryParse(values[3],out var number)) count = number;
   else if (values[2].Trim().Equals(T("Interval"),StringComparison.OrdinalIgnoreCase) && TrySeconds(values[3],out var seconds)) interval = seconds;
   else throw new ArgumentException(T("Enter Count or Interval as the mode and a matching value."));
   var id = Guid.NewGuid(); var request = new ScenePreviewRequest(id,start,end,count,interval); request.Validate();
   if (item.DurationSeconds is { } duration && end > duration) throw new ArgumentException(T("The range lies outside the video."));
   if (!values[4].Trim().Equals(T("yes"),StringComparison.OrdinalIgnoreCase) && !values[4].Trim().Equals(T("no"),StringComparison.OrdinalIgnoreCase)) throw new ArgumentException(T("Replace: enter yes or no."));
   var replace = values[4].Trim().Equals(T("yes"),StringComparison.OrdinalIgnoreCase)
    ? _repository.GetMediaAssets(item.Id).Where(a => a.Kind == MediaAssetKind.ScenePreview && a.Extra.ContainsKey("generation.id") && a.PositionSeconds >= start && a.PositionSeconds < end).Select(a => a.Id).ToList() : [];
   OpenQueue();
   _queueWindow?.AddPreparedJob(new MediaJob { Id = id, Kind = MediaJobKind.ScenePreviews, Input = item.Path, MediaId = item.Id,
    Output = Path.Combine(_settings.DataDirectory,"scene-generations",id.ToString("N")), StartSeconds = start, EndSeconds = end,
    PreviewCount = count, PreviewIntervalSeconds = interval, ReplaceAssetIds = replace });
   Status.Text = T("Scene images are queued. Click Start in Media jobs.");
  }
  catch (Exception ex) { Message.Show(this,ex.Message,T("Scene images"),MessageBoxButton.OK,MessageBoxImage.Warning); }
 }

 private void ApplyScenePreviewResults(MediaJob job)
 {
  if (job.Kind != MediaJobKind.ScenePreviews || job.MediaId is not { } mediaId) return;
  var manifest = MediaToolService.ReadScenePreviewManifest(job.Output);
  if (manifest.Request != ScenePreviewRequest.FromJob(job)) throw new InvalidDataException(T("The previews belong to another job."));
  _repository.ApplyGeneratedScenePreviews(mediaId,job.Id,
   manifest.Frames.Select(frame => new GeneratedScenePreview(frame.Seconds,File.ReadAllBytes(Path.Combine(job.Output,frame.FileName)))),job.ReplaceAssetIds,manifest.SourceSha256);
  if (Selected?.Id == mediaId) ShowDetails();
  Status.Text = Tn(manifest.Frames.Count, "1 scene image saved in the catalog.", "{0} scene images saved in the catalog.");
 }
}
