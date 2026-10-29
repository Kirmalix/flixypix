using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>Metadata of the files (original "Import metadata"): reads XMP sidecars, EXIF/IPTC/XMP of images and the
/// Windows properties of videos with a preview, and writes the catalog's data into XMP sidecars.</summary>
public partial class MainWindow
{

 /// <summary>The selected images and videos, otherwise all of them, with the words for the preview title.</summary>
 private async Task<(IReadOnlyList<Guid> Ids, string Scope)> MetadataScopeAsync(IReadOnlyList<MediaItem> selection, CancellationToken token)
 {
  var chosen = selection.Where(i => i.Kind is MediaKind.Image or MediaKind.Video).Select(i => i.Id).ToList();
  if (chosen.Count > 0) return (chosen, Tn(chosen.Count, "1 selected item", "{0:N0} selected items"));
  var ids = new List<Guid>();
  foreach (var kind in new[] { MediaKind.Image, MediaKind.Video })
   await foreach (var item in StreamCatalog(new MediaQuery { Kind = kind }, token)) ids.Add(item.Id);
  return (ids, T("all images and videos in the library"));
 }

 private async void ImportFileMetadata()
 {
  var selection = SelectedMedia;
  await RunOperation(T("Read metadata"), async token =>
  {
   var (ids, scope) = await MetadataScopeAsync(selection, token);
   var plan = await Task.Run(() => _repository.PlanMetadataImport(ids, new DesktopMetadataReader(), token), token);
   var preview = new MetadataPreviewWindow(this, plan, scope);
   if (preview.ShowDialog() != true) return;
   var applied = await Task.Run(() => _repository.ApplyMetadataImport(preview.Chosen), token);
   Status.Text = Tn(applied, "Metadata imported for 1 item", "Metadata imported for {0:N0} items") + (applied < preview.Chosen.Count ? T(" · {0:N0} changed in the meantime, skipped", preview.Chosen.Count - applied) : "");
  });
 }

 private async void ExportSidecarFiles()
 {
  var selection = SelectedMedia;
  await RunOperation(T("Write XMP sidecars"), async token =>
  {
   var (ids, scope) = await MetadataScopeAsync(selection, token);
   if (ids.Count == 0) return;
   if (Message.Show(this, T("Write title, description, rating and tags of {0} into XMP sidecars next to the media files?\n\nExisting sidecars are added to, not replaced. The media files themselves stay unchanged.", scope),
    T("Write XMP sidecars"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
   var result = await Task.Run(() => _repository.ExportSidecars(ids, token), token);
   ShowSidecarResult(result);
  }, refresh: false);
 }

 private void ShowSidecarResult(SidecarExport result)
 {
  Status.Text = Tn(result.Written, "1 sidecar written", "{0:N0} sidecars written") + (result.Unchanged > 0 ? Tn(result.Unchanged, " · 1 was up to date", " · {0:N0} were up to date") : "")
   + (result.Problems.Count > 0 ? Tn(result.Problems.Count, " · 1 error", " · {0:N0} errors") : "");
  if (result.Problems.Count > 0)
   Message.Show(this, string.Join("\n", result.Problems.Take(20)) + (result.Problems.Count > 20 ? "\n…" : ""), T("Write XMP sidecars"), MessageBoxButton.OK, MessageBoxImage.Warning);
 }
}

/// <summary>Preview of the metadata changes; each row can be left out.</summary>
internal sealed class MetadataPreviewWindow : Window
{
 internal sealed class Row(MetadataChange change)
 {
  public MetadataChange Change { get; } = change;
  public bool Chosen { get; set; } = true;
  public string File => Path.GetFileName(Change.MediaPath);
  public string After => Change.Title ?? "–";
  public string TagText => Change.TagCount == 0 ? "–" : string.Join(" · ", Change.Tags.Select(p => (p.Key.Length == 0 ? "" : p.Key + ": ") + string.Join(", ", p.Value)));
  public string RatingText => Change.Rating is { } rating ? new string('★', rating / 2) : "–";
  public string DateText => Change.TakenAt is { } taken ? taken.ToLocalTime().ToString("g") : "–";
  public string DescriptionText => Change.Description == null ? "–" : T("new");
  public string Source => Change.Source;
 }
 private readonly List<Row> rows;
 internal IReadOnlyList<MetadataChange> Chosen => rows.Where(r => r.Chosen).Select(r => r.Change).ToList();
 internal int RowCount => rows.Count;

 internal MetadataPreviewWindow(Window owner, MetadataPlan plan, string scope)
 {
  Owner = owner; Title = T("Metadata · FlixyPix"); Width = 1200; Height = 700; MinWidth = 800; MinHeight = 450; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
  rows = plan.Changes.Select(c => new Row(c)).ToList();
  var list = new ListView { ItemsSource = rows, Background = Theme.Brush("WindowBackground") };
  var view = new GridView();
  var check = new FrameworkElementFactory(typeof(CheckBox));
  check.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(Row.Chosen)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
  check.SetValue(AutomationProperties.NameProperty, T("Apply"));
  view.Columns.Add(new GridViewColumn { Header = "", Width = 40, CellTemplate = new DataTemplate { VisualTree = check } });
  foreach (var (header, member, width) in new[] { (T("File"), nameof(Row.File), 180d), (T("New title"), nameof(Row.After), 180d), (T("New tags"), nameof(Row.TagText), 290d),
   (T("Rating"), nameof(Row.RatingText), 100d), (T("Date taken"), nameof(Row.DateText), 120d), (T("Description"), nameof(Row.DescriptionText), 110d), (T("Source"), nameof(Row.Source), 150d) })
   view.Columns.Add(new GridViewColumn { Header = header, DisplayMemberBinding = new Binding(member), Width = width });
  list.View = view;
  var hint = plan.Changes.Count == 0 ? T("No changes: the metadata found contains nothing new.")
   : T("Only missing information is filled in: a title still based on the file name, an empty description, or a missing rating. The date taken is used as the file date; tags are added. The files are only read.");
  var frame = new WindowFrame(T("Import metadata · ") + scope, hint, list, "", scroll: false);
  frame.Status.Text = Tn(plan.Checked, "1 item checked", "{0:N0} items checked") + Tn(plan.WithoutMetadata, " · 1 without metadata", " · {0:N0} without metadata")
   + Tn(plan.Changes.Count, " · 1 with changes", " · {0:N0} with changes") + (plan.Problems.Count > 0 ? Tn(plan.Problems.Count, " · 1 unreadable", " · {0:N0} unreadable") : "");
  frame.Add(T("Import selected"), () => DialogResult = true, primary: true).IsEnabled = rows.Count > 0; frame.Add(T("Cancel"), Close, cancel: true);
  Content = frame;
 }
}
