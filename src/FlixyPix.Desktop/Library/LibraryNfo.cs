using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>Reads Kodi-style .nfo files of the selected videos (or all videos) and takes over the chosen changes after a preview.</summary>
public partial class MainWindow
{
 private async void ImportNfoFiles()
 {
  var chosen = SelectedMedia.Where(i => i.Kind == MediaKind.Video).Select(i => i.Id).ToList();
  var scope = chosen.Count > 0 ? Tn(chosen.Count, "1 selected video", "{0:N0} selected videos") : T("all videos in the library");
  await RunOperation(T("Read NFO files"), async token =>
  {
   var ids = chosen.Count > 0 ? chosen : await CollectVideoIdsAsync(token);
   var plan = await Task.Run(() => _repository.PlanNfoImport(ids, token), token);
   var preview = new NfoPreviewWindow(this, plan, scope);
   if (preview.ShowDialog() != true) return;
   var applied = await Task.Run(() => _repository.ApplyNfoImport(preview.Chosen), token);
   foreach (var change in preview.Chosen.Where(c => c.Images.Any(p => !p.IsFanart))) await ShowStoredThumbnailAsync(change.MediaId, token);
   Refresh();
   Status.Text = Tn(applied, "NFO data imported for 1 video", "NFO data imported for {0:N0} videos") + (applied < preview.Chosen.Count ? T(" · {0:N0} changed in the meantime, skipped", preview.Chosen.Count - applied) : "");
  });
 }

 /// <summary>Shows the thumbnail a medium refers to by <c>thumbnail.assetId</c> when it has no thumbnail file yet.</summary>
 internal async Task ShowStoredThumbnailAsync(Guid mediaId, CancellationToken token = default)
 {
  if (_repository.GetMedia(mediaId) is not { ThumbnailPath: null } item || !item.Extra.TryGetValue("thumbnail.assetId", out var id) || !Guid.TryParse(id, out var assetId)) return;
  if (_repository.GetMediaAsset(assetId) is not { } asset || CatalogRepository.ImageExtension(asset.Data) == null) return;
  var path = await Task.Run(() => _repository.MaterializeMediaAsset(asset, Path.Combine(_settings.DataDirectory, "media-assets")), token);
  _repository.Upsert(item with { ThumbnailPath = path });
 }

 private async Task<List<Guid>> CollectVideoIdsAsync(CancellationToken token)
 {
  var ids = new List<Guid>();
  await foreach (var item in StreamCatalog(new MediaQuery { Kind = MediaKind.Video }, token)) ids.Add(item.Id);
  return ids;
 }
}

/// <summary>Preview of the NFO changes; each row can be left out.</summary>
internal sealed class NfoPreviewWindow : Window
{
 internal sealed class Row(NfoChange change)
 {
  public NfoChange Change { get; } = change;
  public bool Chosen { get; set; } = true;
  public string Before => Change.TitleBefore;
  public string After => Change.Title ?? "–";
  public string TagText => Change.TagCount == 0 ? "–" : string.Join(" · ", Change.Tags.Select(p => p.Key + ": " + string.Join(", ", p.Value)));
  public string DescriptionText => Change.Description == null ? "–" : T("new");
  public string PictureText => Change.Images.Count == 0 ? "–" : string.Join(" · ", new[] {
   Change.Images.Any(p => !p.IsFanart) ? T("Thumbnail") : null, Change.Images.Count(p => p.IsFanart) is > 0 and var fanart ? Tn(fanart, "1 fanart image", "{0} fanart images") : null }.OfType<string>());
  public string Source => Path.GetFileName(Change.NfoPath);
 }
 private readonly List<Row> rows;
 internal IReadOnlyList<NfoChange> Chosen => rows.Where(r => r.Chosen).Select(r => r.Change).ToList();
 internal int RowCount => rows.Count;

 internal NfoPreviewWindow(Window owner, NfoPlan plan, string scope)
 {
  Owner = owner; Title = T("NFO files · FlixyPix"); Width = 1100; Height = 700; MinWidth = 800; MinHeight = 450; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
  rows = plan.Changes.Select(c => new Row(c)).ToList();
  var list = new ListView { ItemsSource = rows, Background = Theme.Brush("WindowBackground") };
  var view = new GridView();
  var check = new FrameworkElementFactory(typeof(CheckBox));
  check.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(Row.Chosen)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
  check.SetValue(AutomationProperties.NameProperty, T("Apply"));
  view.Columns.Add(new GridViewColumn { Header = "", Width = 40, CellTemplate = new DataTemplate { VisualTree = check } });
  foreach (var (header, member, width) in new[] { (T("Previous title"), nameof(Row.Before), 200d), (T("Title from NFO"), nameof(Row.After), 200d), (T("New tags"), nameof(Row.TagText), 300d), (T("Description"), nameof(Row.DescriptionText), 100d), (T("Images"), nameof(Row.PictureText), 140d), (T("File"), nameof(Row.Source), 140d) })
   view.Columns.Add(new GridViewColumn { Header = header, DisplayMemberBinding = new Binding(member), Width = width });
  list.View = view;
  var hint = plan.Changes.Count == 0 ? T("No changes: the NFO files found contain nothing new.") : T("Existing tags are kept; new ones go into categories like Genre, Director or Performers. The NFO files are only read.");
  var frame = new WindowFrame(T("Import NFO data · ") + scope, hint, list, "", scroll: false);
  frame.Status.Text = Tn(plan.Checked, "1 video checked", "{0:N0} videos checked") + Tn(plan.WithoutNfo, " · 1 without NFO",
   " · {0:N0} without NFO") + Tn(plan.Changes.Count, " · 1 with changes",
   " · {0:N0} with changes") + (plan.Problems.Count > 0 ? Tn(plan.Problems.Count, " · 1 unreadable", " · {0:N0} unreadable") : "")
   + (plan.SkippedLinks > 0 ? Tn(plan.SkippedLinks, " · 1 image with a web address was not loaded", " · {0:N0} images with web addresses were not loaded") : "");
  frame.Add(T("Import selected"), () => DialogResult = true, primary: true).IsEnabled = rows.Count > 0; frame.Add(T("Cancel"), Close, cancel: true);
  Content = frame;
 }
}
