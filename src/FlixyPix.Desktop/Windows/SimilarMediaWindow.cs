using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FlixyPix.Core;
using FlixyPix.MediaTools;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 /// <summary>Opens "Ähnliche Medien"; removed versions leave the catalog, and with "Datei löschen" also the disk. With
 /// <paramref name="only"/> the search starts by itself and shows only the groups of that medium.</summary>
 internal void ShowSimilarMedia(MediaItem? only = null)
 {
  var window = new SimilarMediaWindow(this, _repository, ToolExists(_settings.Ffmpeg, "ffmpeg") ? _tools : null, RemoveDuplicate, only) { Combine = CombineAsVersions };
  window.ShowDialog();
  if (window.Changed) Refresh();
 }

 /// <summary>Removes one version of a duplicate: from the catalog, and with <paramref name="deleteFile"/> the file as well
 /// (recycle bin or for good, as the library says).</summary>
 private void RemoveDuplicate(MediaItem item, bool deleteFile)
 {
  if (deleteFile && File.Exists(item.Path)) { FileAccessPolicy.Current.EnsureWritable(item.Path); DeleteFromDisk(item.Path, _repository.GetPreferences().General.DeleteToRecycleBin); }
  _repository.Delete(item.Id); ForgetInLibraryFolders(item.Path);
 }
}

/// <summary>"Ähnliche Medien": computes missing fingerprints in the background (can be cancelled and goes on where it
/// stopped), groups media that look alike and shows each group side by side with the version suggested for keeping. The
/// other versions leave the catalog or go to the recycle bin; tags, rating and counters can move to the kept one first.
/// "Kein Duplikat" keeps a group apart in later searches.</summary>
internal sealed class SimilarMediaWindow : Window
{
 private readonly CatalogRepository repository;
 private readonly MediaToolService? tools;
 private readonly Action<MediaItem, bool> remove;
 /// <summary>Combines the group into one medium with the chosen one as main file (P6); null when that failed.</summary>
 internal Func<MediaItem, IReadOnlyList<MediaItem>, MediaItem?>? Combine { get; set; }
 private readonly WindowFrame frame;
 private readonly RadioButton[] kinds = [new() { Content = T("All") }, new() { Content = T("Videos") }, new() { Content = T("Images") }];
 private readonly RadioButton[] levels = [new() { Content = T("Exact") }, new() { Content = T("Similar") }, new() { Content = T("Loose") }];
 private readonly ListBox groupList = new() { Width = 300, Margin = new Thickness(0, 0, 16, 0) };
 private readonly WrapPanel cards = new();
 private readonly TextBlock groupInfo = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
 private readonly CheckBox carry = new() { Content = T("Carry tags, rating and counters over to the kept version"), IsChecked = true, Margin = new Thickness(0, 10, 0, 6) };
 private readonly StackPanel actions = new() { Visibility = Visibility.Collapsed };
 private readonly ProgressBar progress = new() { Height = 4, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
 private readonly Button search, cancel;
 private CancellationTokenSource? running;
 private List<SimilarGroup> groups = [];
 private Guid keep;

 internal bool Changed { get; private set; }
 internal IReadOnlyList<SimilarGroup> Groups => groups;
 internal Task Searching { get; private set; } = Task.CompletedTask;
 private int computed;
 internal int Computed => Volatile.Read(ref computed);

 private readonly MediaItem? only;

 internal SimilarMediaWindow(Window owner, CatalogRepository repository, MediaToolService? tools, Action<MediaItem, bool> remove, MediaItem? only = null)
 {
  Owner = owner; this.repository = repository; this.tools = tools; this.remove = remove; this.only = only;
  Title = T("Similar items · FlixyPix"); Width = 1180; Height = 760; MinWidth = 860; MinHeight = 520; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
  StackPanel Segments(RadioButton[] options, int chosen)
  {
   var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 0) };
   var group = "g" + Guid.NewGuid().ToString("N");
   for (var i = 0; i < options.Length; i++) { options[i].Style = (Style)Application.Current.Resources["Segment"]; options[i].GroupName = group; options[i].IsChecked = i == chosen; options[i].Padding = new Thickness(12, 5, 12, 5); bar.Children.Add(options[i]); }
   return bar;
  }
  levels[0].ToolTip = T("Only re-encoded or rescaled copies of the same picture.");
  levels[1].ToolTip = T("Also copies with other quality, small crops or a logo.");
  levels[2].ToolTip = T("Finds more, including some that only look alike; check before removing.");
  search = SettingsUi.SmallButton(T("Search")); search.Style = (Style)Application.Current.Resources["Primary"]; search.Click += (_, _) => Searching = SearchAsync();
  cancel = SettingsUi.SmallButton(T("Cancel")); cancel.Visibility = Visibility.Collapsed; cancel.Click += (_, _) => running?.Cancel();
  var top = new WrapPanel();
  top.Children.Add(Segments(kinds, 0)); top.Children.Add(Segments(levels, 1)); top.Children.Add(search); top.Children.Add(cancel);
  var header = new StackPanel(); header.Children.Add(top); header.Children.Add(progress);
  groupList.SelectionChanged += (_, _) => ShowGroup();
  var keepAndRemove = new Button { Content = T("Keep the chosen one, remove the others from the catalog"), Style = (Style)Application.Current.Resources["Primary"] };
  keepAndRemove.Click += (_, _) => Resolve(deleteFiles: false);
  var keepAndDelete = new Button { Content = T("… and delete their files") };
  keepAndDelete.Click += (_, _) => Resolve(deleteFiles: true);
  var different = new Button { Content = T("No duplicates") , ToolTip = T("Keeps these items apart in later searches.") };
  different.Click += (_, _) => MarkDifferent();
  var combine = new Button { Content = T("Combine as versions"), ToolTip = T("Keeps all files as versions of one item, with the chosen one as main file. Videos only.") };
  combine.Click += (_, _) => CombineVersions();
  actions.Children.Add(carry); actions.Children.Add(SettingsUi.Buttons(keepAndRemove, keepAndDelete, combine, different));
  var detail = new DockPanel(); DockPanel.SetDock(groupInfo, Dock.Top); DockPanel.SetDock(actions, Dock.Bottom);
  detail.Children.Add(groupInfo); detail.Children.Add(actions);
  detail.Children.Add(new ScrollViewer { Content = cards, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
  var body = new DockPanel(); DockPanel.SetDock(header, Dock.Top); header.Margin = new Thickness(0, 0, 0, 12);
  body.Children.Add(header); DockPanel.SetDock(groupList, Dock.Left); body.Children.Add(groupList); body.Children.Add(detail);
  frame = new WindowFrame(T("Similar items"), T("Finds videos and images that look alike, also in another resolution or quality. Fingerprints are computed once and kept; files are only read."),
   body, "", scroll: false);
  frame.Add(T("Close"), Close, primary: true, cancel: true);
  Content = frame;
  Closing += (_, _) => running?.Cancel();
  frame.Status.Text = T("Choose media type and sharpness, then start the search.");
  if (only != null)
  {
   Title = T("Similar to “{0}” · FlixyPix", only.Title);
   kinds[only.Kind == MediaKind.Image ? 2 : 1].IsChecked = true;
   Loaded += (_, _) => Searching = SearchAsync();
  }
 }

 /// <summary>For tests: media type and sharpness as a user would choose them, and the group to show.</summary>
 internal void Choose(MediaKind? kind, SimilarityLevel level)
 {
  kinds[kind switch { MediaKind.Video => 1, MediaKind.Image => 2, _ => 0 }].IsChecked = true; levels[(int)level].IsChecked = true;
 }
 internal void ShowGroup(SimilarGroup group) => groupList.SelectedIndex = groups.IndexOf(group);
 internal int CardCount => cards.Children.Count;
 private readonly List<Task> pictures = [];
 internal Task PicturesReady => Task.WhenAll(pictures);

 private MediaKind? Kind => kinds[1].IsChecked == true ? MediaKind.Video : kinds[2].IsChecked == true ? MediaKind.Image : null;
 private SimilarityLevel Level => levels[0].IsChecked == true ? SimilarityLevel.Exact : levels[2].IsChecked == true ? SimilarityLevel.Loose : SimilarityLevel.Similar;

 /// <summary>Computes what is missing or outdated, then groups.</summary>
 internal async Task SearchAsync()
 {
  if (running != null) return;
  using var source = running = new CancellationTokenSource();
  var token = source.Token;
  search.IsEnabled = false; cancel.Visibility = progress.Visibility = Visibility.Visible; progress.Value = 0;
  var kind = Kind; var level = Level;
  try
  {
   frame.Status.Text = T("Checking which fingerprints are missing …");
   var run = new FingerprintRun(repository, tools);
   var work = await Task.Run(() => run.Missing(kind, token), token);
   if (work.Count > 0) await ComputeAsync(run, work, token);
   frame.Status.Text = T("Comparing …");
   groups = (await Task.Run(() => repository.FindSimilarMedia(level, kind, token), token)).Where(g => only == null || g.Items.Any(i => i.Id == only.Id)).ToList();
   Fill();
   frame.Status.Text = groups.Count == 0 ? T("No similar items found.") : Tn(groups.Count, "1 group of similar items", "{0:N0} groups of similar items")
    + (tools == null && kind != MediaKind.Image ? T(" · videos skipped: FFmpeg is missing") : "");
  }
  catch (OperationCanceledException) { frame.Status.Text = T("Cancelled. Fingerprints computed so far are kept; the next search goes on from there."); }
  finally { running = null; search.IsEnabled = true; cancel.Visibility = progress.Visibility = Visibility.Collapsed; }
 }

 private async Task ComputeAsync(FingerprintRun run, List<MediaItem> work, CancellationToken token)
 {
  var started = DateTime.UtcNow; var before = run.Done;
  void Report() { progress.Value = 100.0 * run.Done / work.Count; frame.Status.Text = run.Progress(started); }
  var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
  timer.Tick += (_, _) => Report(); timer.Start();
  try { await run.ComputeAsync(work, token); }
  finally { timer.Stop(); Interlocked.Add(ref computed, run.Done - before); }
  Report();
 }

 private void Fill()
 {
  groupList.ItemsSource = groups.Select(g => new ListBoxItem
  {
   Content = new TextBlock
   {
    Text = (g.Kind == MediaKind.Video ? Tn(g.Items.Count, "1 video", "{0:N0} videos") : Tn(g.Items.Count, "1 image", "{0:N0} images")) + " · " +
     T("difference {0}", g.Distance.ToString("0.#", CultureInfo.CurrentCulture)) + "\n" + DisplayName(g.Items.First(i => i.Id == g.Keep)),
    TextTrimming = TextTrimming.CharacterEllipsis,
   },
   Padding = new Thickness(8, 6, 8, 6),
  }).ToList();
  groupList.SelectedIndex = groups.Count > 0 ? 0 : -1;
  ShowGroup();
 }

 private static string DisplayName(MediaItem item) => string.IsNullOrWhiteSpace(item.Title) ? Path.GetFileName(item.Path) : item.Title;

 private SimilarGroup? Current => groupList.SelectedIndex >= 0 && groupList.SelectedIndex < groups.Count ? groups[groupList.SelectedIndex] : null;

 private void ShowGroup()
 {
  cards.Children.Clear(); pictures.Clear();
  if (Current is not { } group) { groupInfo.Text = ""; actions.Visibility = Visibility.Collapsed; return; }
  keep = group.Keep;
  groupInfo.Text = T("The suggestion keeps the version with the most pixels, then the highest bit rate. Choose another one if you prefer it.");
  actions.Visibility = Visibility.Visible;
  var name = "keep" + Guid.NewGuid().ToString("N");
  foreach (var item in group.Items.OrderByDescending(i => i.Id == group.Keep)) cards.Children.Add(Card(item, name));
 }

 private Border Card(MediaItem item, string radioGroup)
 {
  var image = new Image { Stretch = Stretch.Uniform, Height = 150 };
  Privacy.SetBlur(image, true);
  var picture = new Border { Height = 150, Background = SettingsUi.CardBorder, CornerRadius = new CornerRadius(6), ClipToBounds = true, Child = image, Margin = new Thickness(0, 0, 0, 8) };
  pictures.Add(LoadAsync());
  async Task LoadAsync()
  {
   var path = item.Kind == MediaKind.Image ? item.Path : item.ThumbnailPath;
   if (path == null || !File.Exists(path)) return;
   try { image.Source = await DesktopImaging.LoadBitmapAsync(path, 320); }
   catch (Exception error) when (error is IOException or NotSupportedException or InvalidOperationException or ArgumentException or UnauthorizedAccessException) { }
  }
  var keepThis = new RadioButton { Content = Tc("similar", "Keep"), GroupName = radioGroup, IsChecked = item.Id == keep, FontWeight = FontWeights.SemiBold };
  keepThis.SetResourceReference(ForegroundProperty, "Text");
  keepThis.Checked += (_, _) => keep = item.Id;
  var facts = new List<string>();
  if (item.Width is { } width && item.Height is { } height) facts.Add($"{width} × {height}");
  if (item.DurationSeconds is > 0 and var duration) facts.Add(MediaCard.DurationText(duration));
  if (item.FileSize is { } size) facts.Add(size < 1024 * 1024 ? (size / 1024d).ToString("N0", CultureInfo.CurrentCulture) + " KB" : (size / 1024d / 1024d).ToString("N1", CultureInfo.CurrentCulture) + " MB");
  if (item.DurationSeconds is > 0 and var seconds && item.FileSize is { } bytes) facts.Add((bytes * 8 / seconds / 1000).ToString("N0", CultureInfo.CurrentCulture) + " kbit/s");
  var details = new List<string> { string.Join(" · ", facts) };
  if (item.Rating > 0) details.Add(T("Rating {0} of 10", item.Rating));
  var tags = item.Tags.Count; if (tags > 0) details.Add(Tn(tags, "1 tag", "{0:N0} tags"));
  if (item.PlayCount is > 0 and var plays) details.Add(Tn((int)plays, "played once", "played {0:N0} times"));
  details.Add(T("imported {0:d}", item.AddedAt.LocalDateTime));
  var stack = new StackPanel();
  stack.Children.Add(picture); stack.Children.Add(keepThis);
  stack.Children.Add(new TextBlock { Text = DisplayName(item), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 2), ToolTip = DisplayName(item) });
  foreach (var line in details) stack.Children.Add(new TextBlock { Text = line, FontSize = 12, Foreground = SettingsUi.Muted, TextWrapping = TextWrapping.Wrap });
  stack.Children.Add(new TextBlock { Text = item.Path, FontSize = 11, Foreground = SettingsUi.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
  if (!File.Exists(item.Path)) stack.Children.Add(new TextBlock { Text = T("File is currently unavailable"), FontSize = 11, Foreground = Theme.Brush("Error") });
  return new Border { Width = 250, Margin = new Thickness(0, 0, 12, 12), Padding = new Thickness(12), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
   Background = SettingsUi.CardBackground, BorderBrush = SettingsUi.CardBorder, Child = stack };
 }

 /// <summary>Keeps the chosen version and removes the others (also for tests, without the question).</summary>
 internal void Resolve(bool deleteFiles, bool ask = true)
 {
  if (Current is not { } group) return;
  var others = group.Items.Where(i => i.Id != keep).ToList();
  if (ask)
  {
   var recycle = repository.GetPreferences().General.DeleteToRecycleBin;
   var question = deleteFiles
    ? (recycle ? Tn(others.Count, "Move 1 file to the Recycle Bin and remove it from the catalog?", "Move {0:N0} files to the Recycle Bin and remove them from the catalog?")
     : Tn(others.Count, "Permanently delete 1 file and remove it from the catalog? This cannot be undone.", "Permanently delete {0:N0} files and remove them from the catalog? This cannot be undone."))
    : Tn(others.Count, "Remove 1 item from the catalog? The file stays on the disk.", "Remove {0:N0} items from the catalog? The files stay on the disk.");
   if (Message.Show(this, question + "\n\n" + string.Join("\n", others.Select(o => o.Path)), T("Similar items"), MessageBoxButton.YesNo,
    deleteFiles ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
  }
  try
  {
   if (carry.IsChecked == true) repository.MergeDuplicateMetadata(keep, others.Select(o => o.Id).ToList());
   foreach (var other in others) remove(other, deleteFiles);
  }
  catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
  { Message.Show(this, error.Message, T("Similar items"), MessageBoxButton.OK, MessageBoxImage.Warning); }
  Changed = true; Forget(group);
  frame.Status.Text = deleteFiles ? Tn(others.Count, "1 file deleted", "{0:N0} files deleted") : Tn(others.Count, "1 item removed from the catalog", "{0:N0} items removed from the catalog");
 }

 /// <summary>Keeps every file of the group as a version of the chosen medium; images stay apart.</summary>
 internal void CombineVersions()
 {
  if (Current is not { } group || Combine is null) return;
  if (group.Items.Any(i => i.Kind != MediaKind.Video)) { frame.Status.Text = T("Only videos can become versions of one item."); return; }
  var main = group.Items.First(i => i.Id == keep);
  if (Combine(main, group.Items) is null) return;
  Changed = true; Forget(group);
  frame.Status.Text = Tn(group.Items.Count, "1 file in one item", "{0:N0} files combined in one item");
 }

 internal void MarkDifferent()
 {
  if (Current is not { } group) return;
  repository.MarkNotDuplicates(group.Items.Select(i => i.Id).ToList());
  Forget(group); frame.Status.Text = T("Marked as no duplicates");
 }

 private void Forget(SimilarGroup group)
 {
  var index = groups.IndexOf(group);
  groups.Remove(group); Fill();
  if (groups.Count > 0) groupList.SelectedIndex = Math.Min(index, groups.Count - 1);
 }
}
