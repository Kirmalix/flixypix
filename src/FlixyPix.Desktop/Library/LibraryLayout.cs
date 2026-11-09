using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace FlixyPix.Desktop;

/// <summary>Both sidebars fold to a narrow rail of symbols instead of disappearing (UX plan U4.1): a symbol opens the bar
/// at its place. Their widths follow a drag on the inner edge; folding and widths are kept in appsettings.json. Narrow
/// windows fold them by themselves without changing the saved choice. The details show everything one below the other
/// or as tabs (details, tags, scenes), as the user switches.</summary>
public partial class MainWindow
{
 internal const double RailWidth = 44;
 private const double SidebarMinimum = 180, SidebarMaximum = 440, DetailsMinimum = 260, DetailsMaximum = 600;
 /// <summary>Below these window widths the sidebar, then the details fold by themselves.</summary>
 private const double SidebarFoldWidth = 980, DetailsFoldWidth = 1100;

 /// <summary>The bars were folded because the window got narrow, not by the user; they come back when the window widens.</summary>
 private bool _sidebarAutoFolded, _detailsAutoFolded;
 private string _detailsTab = "details";

 /// <summary>Pages of their own bring what they need, and the inspector has nothing to say without a selection.</summary>
 private bool DetailsNotNeeded => _view == "home" || IsRoleView || IsPageView && !IsDetailView || IsImageSetView || IsPublicationGroupView || SelectedMedia.Count == 0;
 internal bool SidebarExpanded => _sidebarOpen;
 internal bool DetailsExpanded => _detailsOpen;
 internal string DetailsTab => _detailsTab;
 /// <summary>What the bars are meant to be; while one glides shut, its panel is still on screen.</summary>
 private bool _sidebarOpen = true, _detailsOpen = true;

 private void ToggleSidebarClicked(object sender, RoutedEventArgs e) => ToggleSidebar();
 internal void ToggleSidebar() { _sidebarAutoFolded = false; SetSidebar(!SidebarExpanded, glide: true); SaveViewPreferences(); }
 private void ToggleDetailsClicked(object sender, RoutedEventArgs e) => ToggleDetails();
 internal void ToggleDetails()
 {
  if (DetailsNotNeeded) return;
  _detailsAutoFolded = false; SetDetails(!DetailsExpanded, glide: true); SaveViewPreferences();
 }

 /// <summary>A bar glides when the user or a selection asks for it; a page change or a resized window shows the result at once.</summary>
 private void SetSidebar(bool expanded, bool glide = false)
 {
  _sidebarOpen = expanded;
  SidebarRail.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
  SidebarResizer.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
  Motion.SlidePane(Sidebar, SidebarColumn, expanded ? Math.Clamp(_settings.SidebarWidth, SidebarMinimum, SidebarMaximum) : RailWidth, expanded, glide, expanded ? null : SidebarRail);
  SidebarToggle.ToolTip = (expanded ? T("Fold the sidebar") : T("Unfold the sidebar")) + " (" + KeyLabel("Ctrl+B") + ")";
 }

 private void SetDetails(bool expanded, bool glide = false)
 {
  if (DetailsNotNeeded)
  {
   // The media take their place.
   _detailsOpen = false;
   DetailsRail.Visibility = DetailsResizer.Visibility = Visibility.Collapsed;
   Motion.SlidePane(DetailsPane, DetailsColumn, 0, false, glide, null);
   return;
  }
  _detailsOpen = expanded;
  DetailsRail.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
  DetailsResizer.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
  Motion.SlidePane(DetailsPane, DetailsColumn, expanded ? Math.Clamp(_settings.DetailsWidth, DetailsMinimum, DetailsMaximum) : RailWidth, expanded, glide, expanded ? null : DetailsRail);
 }

 /// <summary>After each refresh and resize: the details show only when needed; narrow windows fold the bars by themselves.</summary>
 private void AdaptToWidth(bool glideDetails = false)
 {
  // Both limits were chosen at 100 %; a larger interface has less room in the same window.
  var roomWidth = ActualWidth / InterfaceScaling.Current;
  var sidebarNarrow = roomWidth < SidebarFoldWidth;
  if (sidebarNarrow && !_sidebarAutoFolded && SidebarExpanded) { _sidebarAutoFolded = true; SetSidebar(false); }
  else if (!sidebarNarrow && _sidebarAutoFolded) { _sidebarAutoFolded = false; SetSidebar(!_settings.SidebarCollapsed); }
  var detailsNarrow = roomWidth < DetailsFoldWidth;
  if (DetailsNotNeeded) SetDetails(false, glideDetails);
  else if (detailsNarrow && !_detailsAutoFolded && !_settings.DetailsCollapsed) { _detailsAutoFolded = true; SetDetails(false, glideDetails); }
  else if (!detailsNarrow && _detailsAutoFolded) { _detailsAutoFolded = false; SetDetails(!_settings.DetailsCollapsed, glideDetails); }
  else SetDetails(_detailsAutoFolded ? false : !_settings.DetailsCollapsed, glideDetails);
 }

 private bool _detailsWanted;
 private DispatcherTimer? _detailsAppear;

 /// <summary>The inspector comes with the first selection and goes with the last. Its width moves the tiles, so after a
 /// click it waits until a second click of a double click has landed on the tile that was meant.</summary>
 private void UpdateDetailsNeed()
 {
  var wanted = SelectedMedia.Count > 0;
  if (wanted == _detailsWanted) return;
  _detailsWanted = wanted;
  _detailsAppear?.Stop();
  if (!wanted || InputManager.Current.MostRecentInputDevice is not MouseDevice) { ShowDetailsNeed(clicked: false); return; }
  if (_detailsAppear == null)
  {
   _detailsAppear = new() { Interval = TimeSpan.FromMilliseconds(GetDoubleClickTime()) };
   _detailsAppear.Tick += (_, _) => { _detailsAppear.Stop(); if (!_closing) ShowDetailsNeed(clicked: true); };
  }
  _detailsAppear.Start();
 }
 private void ShowDetailsNeed(bool clicked)
 {
  var first = _visibleFirst;
  AdaptToWidth(glideDetails: true);
  // The columns changed: a clicked tile stays in sight, otherwise the view keeps its place. Both wait for the final width.
  if (clicked) { if (MediaList.SelectedItem is { } card) Motion.WhenSettled(DetailsColumn, () => Dispatcher.BeginInvoke(() => { if (!_closing && MediaList.SelectedItem == card) MediaList.ScrollIntoView(card); }, DispatcherPriority.Loaded)); return; }
  if (first <= 0 || _view == "home" || !MediaList.IsVisible) return;
  Motion.WhenSettled(DetailsColumn, () =>
  {
   if (_closing || !MediaList.IsVisible) return;
   MediaList.UpdateLayout();
   Tiles?.ScrollToIndex(first);
  });
 }

 // ----- Widths -----

 private void SidebarResizing(object sender, DragDeltaEventArgs e) =>
  SidebarColumn.Width = new GridLength(Math.Clamp(SidebarColumn.ActualWidth + e.HorizontalChange, SidebarMinimum, SidebarMaximum));
 private void DetailsResizing(object sender, DragDeltaEventArgs e) =>
  DetailsColumn.Width = new GridLength(Math.Clamp(DetailsColumn.ActualWidth - e.HorizontalChange, DetailsMinimum, DetailsMaximum));
 private void PaneResized(object sender, DragCompletedEventArgs e) => SaveViewPreferences();
 /// <summary>Widths as the user dragged them; a folded bar keeps its last width.</summary>
 private (int Sidebar, int Details) PaneWidths() => (
  SidebarExpanded ? (int)Math.Round(Motion.WidthOf(SidebarColumn)) : _settings.SidebarWidth,
  DetailsExpanded ? (int)Math.Round(Motion.WidthOf(DetailsColumn)) : _settings.DetailsWidth);

 // ----- Rails -----

 /// <summary>A symbol of the folded bar goes to that area; collections live in the bar, so their symbol unfolds it.</summary>
 private void SidebarRailClicked(object sender, RoutedEventArgs e)
 {
  var target = (string)((FrameworkElement)sender).Tag;
  if (target == "collections") OpenSidebarAt(target); else _ = NavigateAreaAsync(target);
 }
 internal void OpenSidebarAt(string section)
 {
  _sidebarAutoFolded = false; SetSidebar(true, glide: true); SaveViewPreferences();
  if(_sidebarSections.TryGetValue(section,out var expander))expander.IsExpanded=true;
  FrameworkElement target = section switch
  {
   "collections" or "searches" => CollectionsHeader, _ => LibraryHeader,
  };
  Dispatcher.BeginInvoke(() => target.BringIntoView(new Rect(0, 0, target.ActualWidth, 160)), DispatcherPriority.Loaded);
 }

 private void DetailsRailClicked(object sender, RoutedEventArgs e) => OpenDetailsAt((string)((FrameworkElement)sender).Tag);
 /// <summary>Opens the details at a part: with tabs the tab, otherwise the place in the long column.</summary>
 internal void OpenDetailsAt(string part)
 {
  if (DetailsNotNeeded) return;
  if (!DetailsExpanded) { _detailsAutoFolded = false; SetDetails(true, glide: true); SaveViewPreferences(); }
  if (_settings.DetailsTabs) { ShowDetailsTab(part); return; }
  FrameworkElement target = part switch { "tags" => DetailsTagsPart, "scenes" => DetailsScenesPart, _ => DetailsFieldsPart };
  Dispatcher.BeginInvoke(() => { if (target.IsVisible) target.BringIntoView(new Rect(0, 0, target.ActualWidth, 120)); }, DispatcherPriority.Loaded);
 }

 // ----- Details layout -----

 private void DetailsLayoutClicked(object sender, RoutedEventArgs e) => SetDetailsTabs(!_settings.DetailsTabs);
 internal void SetDetailsTabs(bool tabs)
 {
  _settings = _settings with { DetailsTabs = tabs };
  SaveViewPreferences(); ApplyDetailsLayout();
 }
 private void DetailsTabChecked(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { Tag: string tab }) ShowDetailsTab(tab); }
 internal void ShowDetailsTab(string tab)
 {
  _detailsTab = tab is "tags" or "scenes" ? tab : "details";
  var radio = _detailsTab switch { "tags" => DetailsTabTags, "scenes" => DetailsTabScenes, _ => DetailsTabMain };
  if (radio.IsChecked != true) radio.IsChecked = true;
  ApplyDetailsLayout();
  DetailsScroll.ScrollToTop();
 }
 /// <summary>Everything one below the other, or only the part of the chosen tab; the top (picture, title, actions,
 /// rating) always stays.</summary>
 private void ApplyDetailsLayout()
 {
  var tabs = _settings.DetailsTabs;
  DetailsTabBar.Visibility = tabs ? Visibility.Visible : Visibility.Collapsed;
  DetailsLayoutButton.Content = tabs ? "" : "";
  DetailsLayoutButton.ToolTip = tabs ? T("Show everything one below the other") : T("Show details, tags and scenes as tabs");
  System.Windows.Automation.AutomationProperties.SetName(DetailsLayoutButton, (string)DetailsLayoutButton.ToolTip);
  Visibility Shown(string tab) => !tabs || _detailsTab == tab ? Visibility.Visible : Visibility.Collapsed;
  DetailsTagsPart.Visibility = Shown("tags");
  DetailsFieldsPart.Visibility = DetailsFilePart.Visibility = Shown("details");
  DetailsScenesPart.Visibility = Shown("scenes");
 }

 /// <summary>The tag field needs the tags part in view: opens the details and, with tabs, the tags tab.</summary>
 private void RevealTagInput()
 {
  if (!DetailsExpanded || _settings.DetailsTabs && _detailsTab != "tags") OpenDetailsAt("tags");
 }
}
