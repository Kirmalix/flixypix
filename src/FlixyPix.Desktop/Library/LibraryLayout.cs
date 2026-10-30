using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

 internal bool SidebarExpanded => Sidebar.Visibility == Visibility.Visible;
 internal bool DetailsExpanded => DetailsPane.Visibility == Visibility.Visible;
 internal string DetailsTab => _detailsTab;

 private void ToggleSidebarClicked(object sender, RoutedEventArgs e) => ToggleSidebar();
 internal void ToggleSidebar() { _sidebarAutoFolded = false; SetSidebar(!SidebarExpanded); SaveViewPreferences(); }
 private void ToggleDetailsClicked(object sender, RoutedEventArgs e) => ToggleDetails();
 internal void ToggleDetails()
 {
  if (_view == "home") return;
  _detailsAutoFolded = false; SetDetails(!DetailsExpanded); SaveViewPreferences();
 }

 private void SetSidebar(bool expanded)
 {
  Sidebar.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
  SidebarRail.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
  SidebarResizer.Visibility = Sidebar.Visibility;
  SidebarColumn.Width = new GridLength(expanded ? Math.Clamp(_settings.SidebarWidth, SidebarMinimum, SidebarMaximum) : RailWidth);
  SidebarToggle.ToolTip = (expanded ? T("Fold the sidebar") : T("Unfold the sidebar")) + " (" + KeyLabel("Ctrl+B") + ")";
 }

 private void SetDetails(bool expanded)
 {
  if (_view == "home")
  {
   // The start page needs no details and takes their place.
   DetailsPane.Visibility = DetailsRail.Visibility = DetailsResizer.Visibility = Visibility.Collapsed;
   DetailsColumn.Width = new GridLength(0); return;
  }
  DetailsPane.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
  DetailsRail.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
  DetailsResizer.Visibility = DetailsPane.Visibility;
  DetailsColumn.Width = new GridLength(expanded ? Math.Clamp(_settings.DetailsWidth, DetailsMinimum, DetailsMaximum) : RailWidth);
 }

 /// <summary>After each refresh and resize: the start page hides the details; narrow windows fold the bars by themselves.</summary>
 private void AdaptToWidth()
 {
  var sidebarNarrow = ActualWidth < SidebarFoldWidth;
  if (sidebarNarrow && !_sidebarAutoFolded && SidebarExpanded) { _sidebarAutoFolded = true; SetSidebar(false); }
  else if (!sidebarNarrow && _sidebarAutoFolded) { _sidebarAutoFolded = false; SetSidebar(!_settings.SidebarCollapsed); }
  var detailsNarrow = ActualWidth < DetailsFoldWidth;
  if (_view == "home") SetDetails(false);
  else if (detailsNarrow && !_detailsAutoFolded && !_settings.DetailsCollapsed) { _detailsAutoFolded = true; SetDetails(false); }
  else if (!detailsNarrow && _detailsAutoFolded) { _detailsAutoFolded = false; SetDetails(!_settings.DetailsCollapsed); }
  else SetDetails(_detailsAutoFolded ? false : !_settings.DetailsCollapsed);
 }

 // ----- Widths -----

 private void SidebarResizing(object sender, DragDeltaEventArgs e) =>
  SidebarColumn.Width = new GridLength(Math.Clamp(SidebarColumn.ActualWidth + e.HorizontalChange, SidebarMinimum, SidebarMaximum));
 private void DetailsResizing(object sender, DragDeltaEventArgs e) =>
  DetailsColumn.Width = new GridLength(Math.Clamp(DetailsColumn.ActualWidth - e.HorizontalChange, DetailsMinimum, DetailsMaximum));
 private void PaneResized(object sender, DragCompletedEventArgs e) => SaveViewPreferences();
 /// <summary>Widths as the user dragged them; a folded bar keeps its last width.</summary>
 private (int Sidebar, int Details) PaneWidths() => (
  SidebarExpanded ? (int)Math.Round(SidebarColumn.Width.Value) : _settings.SidebarWidth,
  DetailsExpanded ? (int)Math.Round(DetailsColumn.Width.Value) : _settings.DetailsWidth);

 // ----- Rails -----

 /// <summary>A symbol of the folded sidebar opens it at that section.</summary>
 private void SidebarRailClicked(object sender, RoutedEventArgs e) => OpenSidebarAt((string)((FrameworkElement)sender).Tag);
 internal void OpenSidebarAt(string section)
 {
  _sidebarAutoFolded = false; SetSidebar(true); SaveViewPreferences();
  if(_sidebarSections.TryGetValue(section,out var expander))expander.IsExpanded=true;
  FrameworkElement target = section switch
  {
   "collections" or "searches" => CollectionsHeader, "tags" => TagsHeader, "folders" => FoldersHeader, _ => LibraryHeader,
  };
  Dispatcher.BeginInvoke(() => target.BringIntoView(new Rect(0, 0, target.ActualWidth, 160)), DispatcherPriority.Loaded);
 }

 private void DetailsRailClicked(object sender, RoutedEventArgs e) => OpenDetailsAt((string)((FrameworkElement)sender).Tag);
 /// <summary>Opens the details at a part: with tabs the tab, otherwise the place in the long column.</summary>
 internal void OpenDetailsAt(string part)
 {
  if (_view == "home") return;
  if (!DetailsExpanded) { _detailsAutoFolded = false; SetDetails(true); SaveViewPreferences(); }
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
