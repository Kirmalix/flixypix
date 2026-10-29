using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using FlixyPix.Core;
using FlixyPix.MediaTools;

namespace FlixyPix.Desktop;

/// <summary>Package P6 in the library: technical data read from ffprobe (on import, later on request), several files per
/// medium (combine, main file, split off, play a chosen version) and their place in details and media menu.</summary>
public partial class MainWindow
{
 /// <summary>What the catalog keeps of a probe. Audio files drop the "video" of an embedded cover picture.</summary>
 internal static TechnicalInfo? TechnicalOf(MediaKind kind, MediaProbe probe) =>
  probe.Technical is not { } technical ? null : kind == MediaKind.Audio ? technical with { VideoCodec = null, FrameRate = null, Hdr = false } : technical;

 /// <summary>"HEVC · AAC · 60 fps · 25 Mbit/s · HDR"; empty without technical data.</summary>
 internal static string TechnicalSummary(TechnicalInfo? technical) => technical is null ? "" : string.Join(" · ", new[]
 {
  TechnicalLabels.Codec(technical.VideoCodec), TechnicalLabels.Codec(technical.AudioCodec), TechnicalLabels.FrameRate(technical.FrameRate),
  TechnicalLabels.BitRate(technical.BitRate), technical.Hdr ? "HDR" : "",
 }.Where(part => part.Length > 0));

 /// <summary>One line for a file: resolution, technical data and size.</summary>
 internal static string FileFacts(MediaFile file) => string.Join(" · ", new[]
 {
  TechnicalLabels.Resolution(file.Width, file.Height), TechnicalSummary(file.Technical), file.FileSize is { } size ? MediaCard.SizeText(size) : "",
 }.Where(part => part.Length > 0));

 /// <summary>Reads the technical data of the given media again, or of all videos and audio files that have none yet.</summary>
 private async void ReadTechnicalData(IReadOnlyList<MediaItem>? items = null)
 {
  if (!ToolExists(_settings.Ffprobe, "ffprobe")) { Message.Show(this, T("ffprobe is missing. The path can be chosen in Settings › Tools."), T("Read technical data")); return; }
  await RunOperation(T("Read technical data"), async token =>
  {
   var (read, errors) = await ReadTechnicalDataAsync(items, token);
   Status.Text = Tn(read, "Technical data read for 1 item", "Technical data read for {0:N0} items");
   if (errors.Count > 0) Dialogs.Text(this, T("Read technical data"), string.Join(Environment.NewLine, errors.Take(30)));
  });
 }

 /// <summary>Probes the main file and every version; files that are unavailable are left as they are.</summary>
 internal async Task<(int Read, List<string> Errors)> ReadTechnicalDataAsync(IReadOnlyList<MediaItem>? items, CancellationToken token)
 {
  var errors = new List<string>(); var read = 0;
  var source = items?.Where(i => i.Kind != MediaKind.Image).ToList() ?? [];
  if (items is null)
   // Without technical data the query table has no HDR value, not even "no".
   await foreach (var item in StreamCatalog(new MediaQuery
   {
    Expression = SearchExpression.All(SearchExpression.Not(SearchExpression.Where(new() { Field = SearchField.Kind, Comparison = SearchComparison.Equals, Text = nameof(MediaKind.Image) })),
     SearchExpression.Where(new() { Field = SearchField.Hdr, Comparison = SearchComparison.IsUnknown })),
   }, token)) source.Add(item);
  var processes = _repository.GetPreferences().Previews.GeneratorProcesses;
  await Parallel.ForEachAsync(source, new ParallelOptions { MaxDegreeOfParallelism = processes, CancellationToken = token }, async (listed, itemToken) =>
  {
   try
   {
    async Task<MediaFile> Probe(MediaFile file)
    {
     if (!File.Exists(file.Path)) return file;
     var probe = await _tools.ProbeAsync(file.Path, itemToken);
     return file with { Technical = TechnicalOf(listed.Kind, probe), Width = file.Width ?? probe.Width, Height = file.Height ?? probe.Height,
      DurationSeconds = file.DurationSeconds ?? probe.DurationSeconds, FileSize = file.FileSize ?? new FileInfo(file.Path).Length };
    }
    var main = await Probe(MediaVersions.Main(listed));
    var versions = new List<MediaFile>();
    foreach (var file in listed.Files) versions.Add(await Probe(file));
    if (_repository.GetMedia(listed.Id) is not { } current) return;
    _repository.Upsert(current with
    {
     Technical = main.Technical ?? current.Technical, Width = current.Width ?? main.Width, Height = current.Height ?? main.Height,
     DurationSeconds = current.DurationSeconds ?? main.DurationSeconds,
     Files = [.. current.Files.Select(file => versions.FirstOrDefault(v => string.Equals(v.Path, file.Path, StringComparison.OrdinalIgnoreCase)) ?? file)],
    });
    var number = Interlocked.Increment(ref read);
    _ = Dispatcher.InvokeAsync(() => Status.Text = T("Technical data · {0} · {1}", number, listed.Title));
   }
   catch (OperationCanceledException) { throw; }
   catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or System.Text.Json.JsonException)
   { lock (errors) errors.Add(Path.GetFileName(listed.Path) + ": " + error.Message); }
  });
  return (read, errors);
 }

 /// <summary>Combines the selected videos (or audio files) into one medium; the user picks the main file, the best is offered first.</summary>
 private void CombineAsVersions(IReadOnlyList<MediaItem> items)
 {
  if (items.Count < 2 || items[0].Kind == MediaKind.Image || items.Any(i => i.Kind != items[0].Kind))
  { Message.Show(this, T("Choose two or more videos, or two or more audio files."), T("Combine as versions")); return; }
  var ordered = items.OrderByDescending(i => MediaVersions.Best(i, _ => true) is { } best ? Math.Min(best.Width ?? 0, best.Height ?? 0) : 0)
   .ThenByDescending(i => i.Technical?.BitRate ?? 0).ThenByDescending(i => i.FileSize ?? 0).ToList();
  var dialog = new ChoiceDialog<MediaItem>(this, T("Combine as versions"),
   T("Choose the main file. Title, description and thumbnail of the main item stay; tags, ratings and counters of all items are combined, bookmarks, scenes and collection places move to it. No file is changed."),
   text => ordered.Where(i => text.Length == 0 || i.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase))
    .Select(i => (i, i.Title + " · " + FileFacts(MediaVersions.Main(i)))).ToList());
  dialog.ShowDialog();
  if (dialog.Chosen is not { } main) return;
  CombineAsVersions(main, items);
 }

 internal MediaItem? CombineAsVersions(MediaItem main, IReadOnlyList<MediaItem> items)
 {
  try
  {
   var merged = _repository.MergeVersions(main.Id, items.Select(i => i.Id).ToList());
   Refresh(merged.Id);
   Status.Text = Tn(merged.Files.Count + 1, "1 file in one item", "{0:N0} files combined in one item");
   return merged;
  }
  catch (ArgumentException error) { Message.Show(this, error.Message, T("Combine as versions"), MessageBoxButton.OK, MessageBoxImage.Warning); return null; }
 }

 internal void MakeMainVersion(MediaItem item, MediaFile file)
 {
  try { Refresh(_repository.SetMainVersion(item.Id, file.Path).Id); Status.Text = T("“{0}” is now the main file", Path.GetFileName(file.Path)); }
  catch (ArgumentException error) { Message.Show(this, error.Message, T("Main file"), MessageBoxButton.OK, MessageBoxImage.Warning); }
 }

 internal async Task<MediaItem?> SplitVersionAsync(MediaItem item, MediaFile file)
 {
  try
  {
   var created = _repository.SplitVersion(item.Id, file.Path);
   Refresh(created.Id); Status.Text = T("“{0}” is an item of its own again", Path.GetFileName(file.Path));
   // The new item needs a thumbnail of its own.
   if (File.Exists(created.Path) && ToolExists(_settings.Ffmpeg, "ffmpeg"))
    _repository.Upsert(await PrepareItemAsync(created, _repository.GetPreferences().Previews, ToolExists(_settings.Ffprobe, "ffprobe"), true, CancellationToken.None));
   return created;
  }
  catch (Exception error) when (error is ArgumentException or IOException or InvalidOperationException or InvalidDataException)
  { Message.Show(this, error.Message, T("Split off"), MessageBoxButton.OK, MessageBoxImage.Warning); return null; }
 }

 /// <summary>Menu entries for the files of one medium, or "Combine as versions" for several.</summary>
 private IEnumerable<MenuSpec> VersionEntries(IReadOnlyList<MediaItem> items, bool outside)
 {
  if (items.Count > 1)
  {
   var combinable = items[0].Kind != MediaKind.Image && items.All(i => i.Kind == items[0].Kind) && !outside;
   yield return MenuSpec.Command(T("Combine as versions …"), () => CombineAsVersions(items), enabled: combinable);
   yield break;
  }
  var item = items[0];
  if (item.Kind != MediaKind.Image) yield return MenuSpec.Command(T("Read technical data"), () => ReadTechnicalData([item]));
  if (item.Files.Count == 0) yield break;
  yield return MenuSpec.Separator;
  foreach (var file in MediaVersions.All(item))
  {
   var chosen = file;
   yield return MenuSpec.Command(T("Play: {0}", VersionLabel(chosen)), async () => { StopSlideshow(); await Play(item, file: chosen.Path); });
  }
  yield return MenuSpec.Separator;
  yield return MenuSpec.Submenu(T("Make main file"), item.Files.Select(file => MenuSpec.Command(VersionLabel(file), () => MakeMainVersion(item, file))).ToList());
  yield return MenuSpec.Submenu(T("Split off as own item"), item.Files.Select(file => MenuSpec.Command(VersionLabel(file), async () => await SplitVersionAsync(item, file))).ToList());
 }

 private static string VersionLabel(MediaFile file) => Path.GetFileName(file.Path) + (FileFacts(file) is { Length: > 0 } facts ? " · " + facts : "");

 /// <summary>The files of the medium in the details: the main file first, each with what was measured and its own actions.</summary>
 private void ShowVersions(MediaItem? item)
 {
  VersionRows.Children.Clear();
  if (item == null || item.Files.Count == 0) { VersionsSection.Visibility = Visibility.Collapsed; return; }
  VersionsSection.Visibility = Visibility.Visible;
  var best = _repository.GetPreferences().Playback.PlayBestVersion ? MediaVersions.Best(item, File.Exists)?.Path : null;
  var files = MediaVersions.All(item);
  for (var index = 0; index < files.Count; index++)
  {
   var file = files[index]; var main = index == 0;
   var title = new TextBlock { Text = Path.GetFileName(file.Path), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = file.Path };
   var facts = new TextBlock { FontSize = 12, Foreground = SettingsUi.Muted, TextWrapping = TextWrapping.Wrap,
    Text = string.Join(" · ", new[] { main ? T("main file") : "", string.Equals(file.Path, best, StringComparison.OrdinalIgnoreCase) ? T("plays") : "", FileFacts(file),
     File.Exists(file.Path) ? "" : T("unavailable") }.Where(part => part.Length > 0)) };
   var play = SettingsUi.SmallButton(T("Play")); var chosen = file;
   play.Click += async (_, _) => { StopSlideshow(); await Play(item, file: chosen.Path); };
   var buttons = SettingsUi.Buttons(play);
   if (!main)
   {
    var makeMain = SettingsUi.SmallButton(T("Main file"), T("Makes this file the main file; the former one becomes a version.")); makeMain.Click += (_, _) => MakeMainVersion(item, chosen);
    var split = SettingsUi.SmallButton(T("Split off"), T("Makes this file an item of its own again.")); split.Click += async (_, _) => await SplitVersionAsync(item, chosen);
    buttons.Children.Add(makeMain); buttons.Children.Add(split);
   }
   foreach (var button in buttons.Children.OfType<Button>()) button.Margin = new Thickness(0, 4, 6, 0);
   VersionRows.Children.Add(new StackPanel { Margin = new Thickness(0, 4, 0, 8), Children = { title, facts, buttons } });
  }
 }
}
