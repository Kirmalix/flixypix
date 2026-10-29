using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

// Measures browsing on a large catalog copy. Only timings and counts are written,
// never titles, tags, paths or pictures, so private catalogs can be probed safely.
public partial class MainWindow
{
 internal async Task RunPerfProbeAsync(string directory, TimeSpan construction, int rounds = 3)
 {
  var samples = new Dictionary<string, List<(double Milliseconds, double Stall)>>();
  var heartbeat = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(15) };
  var clock = Stopwatch.StartNew(); var lastTick = clock.Elapsed; var longestStall = TimeSpan.Zero;
  heartbeat.Tick += (_, _) => { var now = clock.Elapsed; if (now - lastTick > longestStall) longestStall = now - lastTick; lastTick = now; };
  heartbeat.Start();
  async Task Idle() => await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
  async Task Step(string name, Func<Task> action)
  {
   await Idle(); lastTick = clock.Elapsed; longestStall = TimeSpan.Zero;
   var watch = Stopwatch.StartNew();
   await action(); await _lastRefresh; await _previewReady; await _tagAssistReady; await Idle();
   watch.Stop(); var stall = Math.Max(longestStall.TotalMilliseconds, (clock.Elapsed - lastTick).TotalMilliseconds);
   if (!samples.TryGetValue(name, out var list)) samples[name] = list = [];
   list.Add((watch.Elapsed.TotalMilliseconds, stall));
  }
  async Task Refreshed(Action change) { change(); _searchTimer.Stop(); Refresh(); await _lastRefresh; }
  long Time(Action action) { var watch = Stopwatch.StartNew(); action(); return watch.ElapsedMilliseconds; }

  var firstWatch = Stopwatch.StartNew(); var firstRender = new Dictionary<string, long>();
  await _lastRefresh; firstRender["refresh"] = firstWatch.ElapsedMilliseconds;
  await _previewReady; firstRender["preview"] = firstWatch.ElapsedMilliseconds;
  await _tagAssistReady; firstRender["tagAssist"] = firstWatch.ElapsedMilliseconds;
  await Idle(); firstRender["idle"] = firstWatch.ElapsedMilliseconds;
  // The search index builds in the background after the first refresh; searches before that use the row-by-row comparison.
  await _searchIndexWarmup; firstRender["searchIndexReady"] = firstWatch.ElapsedMilliseconds;
  await Step("first-render", () => Task.CompletedTask);
  var core = new
  {
   facetsMilliseconds = await Task.Run(() => Time(() => _repository.GetFacets())),
   firstPageMilliseconds = await Task.Run(() => Time(() => _repository.QueryPage(new() { Limit = WindowSize }))),
   playlistsMilliseconds = await Task.Run(() => Time(() => _repository.GetPlaylists())),
   preferencesMilliseconds = await Task.Run(() => Time(() => _repository.GetPreferences())),
   fullSearchMilliseconds = await Task.Run(() => Time(() => _repository.Search())),
  };
  var facets = _repository.GetFacets();
  var tagCore = new Dictionary<string, List<long>>();
  var folders = facets.Folders.Where(f => !string.IsNullOrWhiteSpace(f)).ToArray();
  for (var round = 0; round < rounds; round++)
  {
  Navigate(new Button { Tag = "all" }, new RoutedEventArgs()); await _lastRefresh;
  // Endless scrolling: jumps across the whole result load the window around the new place.
  await Step("scroll-screen", () => ScrollToAsync(_visibleLast + 1));
  await Step("scroll-middle", () => ScrollToAsync(_total / 2));
  await Step("scroll-end", () => ScrollToAsync(_total - 1));
  await Step("scroll-start", () => ScrollToAsync(0));
  foreach (var term in new[] { "a", "er", "2019", "zzqxv" })
   await Step("search-" + term.Length + "-chars" + (term == "zzqxv" ? "-no-hit" : ""), () => Refreshed(() => SearchBox.Text = term));
  await Step("search-clear", () => Refreshed(() => SearchBox.Text = ""));
  foreach (var view in new[] { "Video", "Image", "Audio", "favorites", "watchlater", "all" })
   await Step("navigate-" + view, () => { Navigate(new Button { Tag = view }, new RoutedEventArgs()); return Task.CompletedTask; });
  for (var index = 1; index <= 5; index++) { var sort = index; await Step("sort-" + sort, () => { SortChoice.SelectedIndex = sort; return Task.CompletedTask; }); }
  SortChoice.SelectedIndex = 0; await _lastRefresh;
  await Step("rating-filter-4", () => { RatingFilter.SelectedIndex = 4; return Task.CompletedTask; });
  RatingFilter.SelectedIndex = 0; await _lastRefresh;
  if (CategoryFilter.Items.Count > 1)
  {
   await Step("category", () => { CategoryFilter.SelectedIndex = 1; return Task.CompletedTask; });
   CategoryFilter.SelectedIndex = 0; await _lastRefresh;
  }
  if (folders.Length > 0)
  {
   await Step("folder", () => Refreshed(() => _folder = folders[folders.Length / 2]));
   await Step("folder-clear", () => Refreshed(() => _folder = null));
  }
  await Step("view-list", () => { ViewChoice.SelectedIndex = 1; return Task.CompletedTask; });
  await Step("view-grid", () => { ViewChoice.SelectedIndex = 0; return Task.CompletedTask; });
  await Step("select-first", () => { MediaList.SelectedIndex = 0; return Task.CompletedTask; });
  await Step("select-last-on-page", () => { MediaList.SelectedIndex = MediaList.Items.Count - 1; return Task.CompletedTask; });
  if (_tagEntries.FirstOrDefault(t => t.Count > 0) is { } top && _repository.QueryPage(new() { RequiredTags = [top.Tag], Limit = 1 }).Items.FirstOrDefault() is { } tagged)
  {
   await Step("tag-filter", () => { ToggleTagFilter(top.Tag, false); return Task.CompletedTask; });
   await Step("tag-filter-clear", () => { ToggleTagFilter(top.Tag, false); return Task.CompletedTask; });
   await Step("select-tagged-with-suggestions", () => RefreshAsync(tagged.Id));
   await Step("tag-add", () => ChangeTagsAsync([tagged.Id], ["flixypix-probe"], []));
   await Step("tag-remove", () => ChangeTagsAsync([tagged.Id], [], ["flixypix-probe"]));
   // Where a tag change spends its time in the core: the write, then what the next refresh recomputes.
   foreach (var (name, add, remove) in new[] { ("add", new[] { "flixypix-probe" }, Array.Empty<string>()), ("remove", Array.Empty<string>(), new[] { "flixypix-probe" }) })
   {
    var parts = new Dictionary<string, long>
    {
     ["write"] = await Task.Run(() => Time(() => _repository.UpdateTags([tagged.Id], add, remove))),
     ["facets"] = await Task.Run(() => Time(() => _repository.GetFacets())),
     ["usage"] = await Task.Run(() => Time(() => _repository.GetTagUsage())),
     ["suggestions"] = await Task.Run(() => Time(() => _repository.SuggestTags([tagged.Id], 10))),
     ["combinations"] = await Task.Run(() => Time(() => _repository.GetTagCombinations(null, 4))),
     ["window"] = await Task.Run(() => Time(() => _repository.QueryPage(new() { Limit = WindowSize }))),
    };
    foreach (var (part, milliseconds) in parts) { if (!tagCore.TryGetValue(name + "-" + part, out var list)) tagCore[name + "-" + part] = list = []; list.Add(milliseconds); }
   }
  }
  }
  heartbeat.Stop();
  int Nodes(IEnumerable<BrowseNode> nodes) => nodes.Sum(n => 1 + Nodes(n.Children));
  var result = new
  {
   constructionMilliseconds = Math.Round(construction.TotalMilliseconds),
   catalogBytes = new FileInfo(Path.Combine(_settings.DataDirectory, "catalog.db")).Length,
   media = _repository.QueryPage(new() { Limit = 1 }).TotalCount,
   tagsInUse = _tagEntries.Count(t => t.Count > 0), combinations = _repository.GetTagCombinations().Count,
   counts = new { tags = Tags.Items.Count, categories = facets.Categories.Count, tagCategories = facets.CategorizedTags.Count, folders = folders.Length, folderTreeNodes = Folders.ItemsSource is IEnumerable<BrowseNode> tree ? Nodes(tree) : 0, playlists = CollectionNodes().Count() },
   core,
   tagCoreMedianMilliseconds = tagCore.ToDictionary(p => p.Key, p => p.Value.Order().ElementAt(p.Value.Count / 2)),
   rounds, firstRender,
   steps = samples.Select(pair => new { name = pair.Key, medianMilliseconds = Math.Round(pair.Value.Select(v =>
    v.Milliseconds).Order().ElementAt(pair.Value.Count / 2)), maxMilliseconds = Math.Round(pair.Value.Max(v => v.Milliseconds)),
    longestUiStallMilliseconds = Math.Round(pair.Value.Max(v => v.Stall)) }),
   workingSetMiB = Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024,
   managedHeapMiB = GC.GetTotalMemory(false) / 1024 / 1024,
  };
  File.WriteAllText(Path.Combine(directory, "perf.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
 }
}
