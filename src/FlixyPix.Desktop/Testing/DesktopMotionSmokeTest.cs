using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace FlixyPix.Desktop;

// The other smoke tests run with steady areas, so they can check places right after a change. This one lets everything
// move: the state must change at once, the picture must end exactly in place, and nothing may be left half done.
public partial class MainWindow
{
 internal async Task RunMotionSmokeTestAsync(string directory)
 {
  static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
  // Frames drawn in a span, and the longest gap between two of them.
  static async Task<(int Frames, double LongestMilliseconds)> Watch(int milliseconds)
  {
   var clock = Stopwatch.StartNew(); var last = TimeSpan.Zero; var longest = 0.0; var frames = 0;
   void Frame(object? sender, EventArgs e) { var now = clock.Elapsed; longest = Math.Max(longest, (now - last).TotalMilliseconds); last = now; frames++; }
   CompositionTarget.Rendering += Frame;
   await Task.Delay(milliseconds);
   CompositionTarget.Rendering -= Frame;
   return (frames, Math.Round(longest, 1));
  }
  void AtRest(FrameworkElement pane, string name) => Check(double.IsNaN(pane.Width) && pane.HorizontalAlignment == HorizontalAlignment.Stretch, name + " kept the width of its motion.");

  Motion.Steady = false;
  try
  {
   var smooth = Motion.Smooth; var measured = new Dictionary<string, object> { ["smooth"] = smooth, ["renderTier"] = RenderCapability.Tier >> 16 };
   await NavigateAreaAsync("all"); await _lastRefresh; await Task.Delay(400);

   // Only the current navigation entry carries its mark and its surface.
   foreach (var entry in Logicals<System.Windows.Controls.RadioButton>(NavItems))
   {
    if (entry.Template.FindName("bar", entry) is not FrameworkElement bar || entry.Template.FindName("chosen", entry) is not FrameworkElement chosen) continue;
    var height = bar.RenderTransform is ScaleTransform grown ? grown.ScaleY : -1;
    var shown = entry.IsChecked == true ? 1.0 : 0.0;
    Check(height == shown && chosen.Opacity == shown, $"Navigation entry {entry.Tag}: checked {entry.IsChecked}, mark {height} (wanted {Motion.GetGrowTo(bar)}, frozen {bar.RenderTransform.IsFrozen}), surface {chosen.Opacity} (wanted {Motion.GetFadeTo(chosen)}), loaded {entry.IsLoaded}, visible {entry.IsVisible}.");
   }
   MediaList.SelectedIndex = 0; await Task.Delay(1000);
   Check(DetailsExpanded && MediaList.Items.Count > 0, "The motion test needs tiles and open details.");
   measured["tilesInView"] = Tiles?.Realized ?? 0;

   // The state changes at once; the panel stays on screen until the column has closed.
   var sidebar = SidebarColumn.Width.Value; var savedSidebar = _settings.SidebarWidth; var savedDetails = _settings.DetailsWidth;
   ToggleSidebar();
   Check(!SidebarExpanded && SidebarRail.IsVisible && _settings.SidebarCollapsed, "Folding the sidebar did not change its state at once.");
   if (smooth) Check(Sidebar.IsVisible && SidebarColumn.Width.Value > RailWidth, "The sidebar did not glide shut.");
   await Task.Delay(30); Capture(RootContent, Path.Combine(directory, "motion-sidebar-closing.png"));
   measured["sidebarClose"] = await Watch(400);
   Check(SidebarColumn.Width.Value == RailWidth && !Sidebar.IsVisible, "The sidebar did not end as a rail.");
   AtRest(Sidebar, "The sidebar");

   // Changing one's mind in the middle ends in the last wish, with the exact width, and nothing mid-way gets saved.
   ToggleSidebar(); await Task.Delay(60); ToggleSidebar(); await Task.Delay(60); ToggleSidebar();
   measured["sidebarOpen"] = await Watch(400);
   Check(SidebarExpanded && Sidebar.IsVisible && !SidebarRail.IsVisible && SidebarColumn.Width.Value == sidebar, "Quick folding and unfolding ended in the wrong place.");
   Check(_settings.SidebarWidth == savedSidebar, $"A width from the middle of a motion was saved ({_settings.SidebarWidth}, not {savedSidebar}).");
   AtRest(Sidebar, "The sidebar");

   var details = DetailsColumn.Width.Value;
   ToggleDetails();
   Check(!DetailsExpanded && DetailsRail.IsVisible, "Folding the details did not change their state at once.");
   measured["detailsClose"] = await Watch(400);
   Check(DetailsColumn.Width.Value == RailWidth && !DetailsPane.IsVisible, "The details did not end as a rail.");
   ToggleDetails();
   await Task.Delay(30); Capture(RootContent, Path.Combine(directory, "motion-details-opening.png"));
   measured["detailsOpen"] = await Watch(400);
   Capture(RootContent, Path.Combine(directory, "motion-rest.png"));
   Check(DetailsExpanded && DetailsPane.IsVisible && DetailsColumn.Width.Value == details && _settings.DetailsWidth == savedDetails, "The details did not come back to their width.");
   AtRest(DetailsPane, "The details");

   // The last selection takes the details along.
   MediaList.SelectedItems.Clear();
   measured["detailsLeave"] = await Watch(500);
   Check(DetailsColumn.Width.Value == 0 && !DetailsPane.IsVisible && !DetailsRail.IsVisible, "The details did not leave with the selection.");
   AtRest(DetailsPane, "The details");

   // A resize during a motion wins, and nothing waits for it.
   ToggleSidebar(); SetSidebar(true); Check(SidebarColumn.Width.Value == sidebar && Sidebar.IsVisible, "A direct change did not replace the motion."); AtRest(Sidebar, "The sidebar"); await Task.Delay(300);
   Check(SidebarColumn.Width.Value == sidebar, "A replaced motion still changed the width.");
   _sidebarAutoFolded = false; SaveViewPreferences();

   // Pages arrive whole and upright.
   var watch = Stopwatch.StartNew();
   var lowest = 1.0; var deepest = 0.0; var moving = true;
   async Task Follow()
   {
    while (moving)
    {
     lowest = Math.Min(lowest, MediaPane.Opacity);
     if (MediaPane.RenderTransform is TranslateTransform place) deepest = Math.Max(deepest, place.Y);
     await Task.Delay(5);
    }
   }
   var following = Follow();
   var navigation = NavigateAreaAsync("Image"); var frames = Watch(900); await navigation; await _lastRefresh;
   measured["pageReadyMilliseconds"] = watch.ElapsedMilliseconds;
   measured["page"] = await frames;
   moving = false; await following;
   measured["pageLowestOpacity"] = Math.Round(lowest, 3); measured["pageDeepestRise"] = Math.Round(deepest, 2);
   // A fast page must still be seen to arrive; a quick load is the normal case.
   if (smooth) Check(lowest < .1 && deepest > 2, $"The page change did not show: lowest opacity {lowest:0.###}, rise {deepest:0.##}, ready after {measured["pageReadyMilliseconds"]} ms.");
   Check(MediaPane.Opacity == 1 && MediaPane.RenderTransform is not TranslateTransform { Y: not 0 }, "The page did not settle.");

   // Another order shows new results, and they are seen to arrive; the page around them stays.
   var results = 1.0; var sorting = true;
   async Task FollowResults() { while (sorting) { results = Math.Min(results, MediaArea.Opacity); await Task.Delay(5); } }
   var followingResults = FollowResults(); var sortBefore = SortChoice.SelectedIndex;
   SortChoice.SelectedIndex = sortBefore == 1 ? 0 : 1; await _lastRefresh; await Task.Delay(900); sorting = false; await followingResults;
   measured["resultsLowestOpacity"] = Math.Round(results, 3);
   if (smooth) Check(results < .1, $"New results did not show their arrival: lowest opacity {results:0.###}.");
   Check(MediaArea.Opacity == 1 && MediaPane.Opacity == 1 && MediaArea.CacheMode == null, "The results did not settle.");
   SortChoice.SelectedIndex = sortBefore; await _lastRefresh; await Task.Delay(900);

   // The wheel glides on a page that scrolls by pixels: it does not jump, further turns add up, and it ends exactly there.
   MediaList.SelectedIndex = 0; await Task.Delay(1000);
   if (smooth && DetailsScroll.ScrollableHeight > 200)
   {
    DetailsScroll.ScrollToTop(); await Task.Delay(200);
    void Wheel(int delta) => DetailsScroll.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = UIElement.MouseWheelEvent });
    Wheel(-120); Wheel(-120);
    var wanted = Math.Min(DetailsScroll.ScrollableHeight, 2 * SystemParameters.WheelScrollLines * 32d);
    Check(DetailsScroll.VerticalOffset == 0, "The wheel jumped instead of gliding.");
    await Task.Delay(900);
    Check(Math.Abs(DetailsScroll.VerticalOffset - wanted) < 1, $"The wheel ended at {DetailsScroll.VerticalOffset}, not {wanted}.");
    DetailsScroll.ScrollToTop();
   }
   MediaList.SelectedItems.Clear(); await Task.Delay(500);

   // A view that takes long keeps the old page back and says that it loads; if none follows, both end by themselves.
   _pageChanging = true; Motion.LeavePage(MediaPane); ExpectLoading(_browse.CaptureRequest(SelectedMedia.Select(i => i.Id)));
   await Task.Delay(2200);
   if (smooth) Check(MediaPane.Opacity < .5, "The page came back while the next one was still loading.");
   Check(LoadingHint.Visibility == Visibility.Visible, "A view that takes long did not say that it loads.");
   Capture(RootContent, Path.Combine(directory, "motion-loading.png"));
   _browse.Invalidate(); await Task.Delay(1500);
   Check(LoadingHint.Visibility != Visibility.Visible, "The loading hint stayed although nothing was loading.");
   Check(MediaPane.Opacity == 1, "A page stayed dimmed although no other followed.");

   await NavigateAreaAsync("all"); await _lastRefresh; await Task.Delay(300);

   // What a gliding column costs in a full grid, apart from what this desktop may draw: frames without any motion for
   // comparison, then the layout of one step of a glide, with as many tiles as a large window shows.
   measured["idle"] = await Watch(400);
   var fixture = ((IEnumerable<MediaCard>)MediaList.ItemsSource).First().Item;
   var (windowWidth, windowHeight, windowState, tileWidth) = (Width, Height, WindowState, TileSize.Value);
   WindowState = WindowState.Normal; Width = 1920; Height = 1080; TileSize.Value = 200;
   MediaList.ItemsSource = Enumerable.Range(0, 400).Select(_ => new MediaCard(fixture, false, context: _cardContext)).ToList();
   VirtualizingTilePanel.SetTotalCount(MediaList, 400); VirtualizingTilePanel.SetWindowStart(MediaList, 0);
   MediaList.UpdateLayout(); await Task.Delay(300);
   measured["fullGridTiles"] = Tiles?.Realized ?? 0;
   var steps = new List<double>(); var column = DetailsColumn.Width;
   for (var step = 0; step < 40; step++)
   {
    DetailsColumn.Width = new GridLength(step % 2 == 0 ? 8 + step * 8 : 330 - step * 8);
    var layout = Stopwatch.StartNew(); UpdateLayout(); steps.Add(layout.Elapsed.TotalMilliseconds);
   }
   DetailsColumn.Width = column;
   steps.Sort();
   measured["layoutStepMedianMilliseconds"] = Math.Round(steps[steps.Count / 2], 2);
   measured["layoutStepLongestMilliseconds"] = Math.Round(steps[^1], 2);
   WindowState = windowState; Width = windowWidth; Height = windowHeight; TileSize.Value = tileWidth;
   await NavigateAreaAsync("all"); await _lastRefresh;

   File.WriteAllText(Path.Combine(directory, "motion.json"), JsonSerializer.Serialize(measured.ToDictionary(p => p.Key, p => p.Value is ValueTuple<int, double> t ? new { frames = t.Item1, longestMilliseconds = t.Item2 } : p.Value), new JsonSerializerOptions { WriteIndented = true }));
  }
  finally { Motion.Steady = true; }
 }
}
