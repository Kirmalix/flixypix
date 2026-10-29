using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>The filters of the view (UX plan U4.2): one line above the tiles with the "Filter" button and the active filters
/// as chips that remove themselves on click. The button opens a flyout with all sections on the left (rating and type,
/// category, performers, studios and tags to include or exclude, duration, dates, properties, more) and the chosen
/// section on the right. Every change applies at once.</summary>
public partial class MainWindow
{
 private MediaFilter _filter = new();
 private bool _tagsMatchAny;
 private Popup? _filterPopup;

 /// <summary>What the main window shows now, as one state (saved searches, restore on start, back and forward).</summary>
 internal ViewState CaptureViewState() => new()
 {
  View = _view, PlaylistId = _playlist?.Id, SearchText = SearchBox.Text.Length == 0 ? null : SearchBox.Text, Tag = _tag, TagCategory = _tagGroup, Category = _category,
  Folder = _folder, RequiredTags = _requiredTags.ToList(), ExcludedTags = _excludedTags.ToList(), Expression = _advancedExpression, Filter = _filter,
  TagsMatchAny = _tagsMatchAny, MinimumStars = Math.Max(RatingFilter.SelectedIndex, 0), SortOrder = Math.Max(SortChoice.SelectedIndex, 0),
  ViewMode = ViewChoice.SelectedIndex, TileWidth = (int)Math.Round(TileSize.Value), Position = Math.Max(0, _visibleFirst),
  AnchorId = _visibleFirst >= _windowStart && _visibleFirst - _windowStart < MediaList.Items.Count ? ((MediaCard)MediaList.Items[_visibleFirst - _windowStart]).Item.Id : null,
  Selection = SelectedMedia.Select(m => m.Id).ToList(),
 };

 /// <summary>Shows a state: view, filters, sort and display; the caller decides about position and selection.</summary>
 internal void ApplyViewState(ViewState state, string? title = null)
 {
  var initializing = _initializingBrowsing; _initializingBrowsing = true;
  try
  {
   _view = state.View; _tag = state.Tag; _tagGroup = state.TagCategory; _category = state.Category; _folder = state.Folder;
   _playlist = state.PlaylistId is { } id ? _repository.GetPlaylists().FirstOrDefault(p => p.Id == id) : null;
   _requiredTags.Clear(); _requiredTags.AddRange(state.RequiredTags); _excludedTags.Clear(); _excludedTags.AddRange(state.ExcludedTags);
   // With fewer than two tags the combination does not matter yet; it starts from the library's choice.
   _advancedExpression = state.Expression; _filter = state.Filter; _tagsMatchAny = state.RequiredTags.Count > 1 ? state.TagsMatchAny : _repository.GetPreferences().General.TagsMatchAny;
   SearchBox.Text = state.SearchText ?? ""; _searchTimer.Stop();
   RatingFilter.SelectedIndex = Math.Clamp(state.MinimumStars, 0, 5);
   if (state.SortOrder >= 0 && state.SortOrder < SortChoice.Items.Count) SortChoice.SelectedIndex = state.SortOrder;
   if (state.ViewMode is >= 0 and <= 2) ViewChoice.SelectedIndex = state.ViewMode;
   if (state.TileWidth > 0) TileSize.Value = Math.Clamp(state.TileWidth, TileSize.Minimum, TileSize.Maximum);
  }
  finally { _initializingBrowsing = initializing; }
  ViewTitle.Text = title ?? (_playlist != null ? CollectionName(_playlist) : null) ?? (_requiredTags.Count > 0 ? string.Join(" + ", _requiredTags) : ViewName(_view));
  UpdateNavigation(); _anchor = 0;
  UpdateFilterBar(); RenderTagFilterChips(); RenderTagList(); Refresh();
 }

 /// <summary>An empty library invites the first import; an empty result says that the search or filters found nothing.</summary>
 private void ShowEmptyState(bool catalogEmpty)
 {
  EmptyTitle.Text = catalogEmpty ? T("Room for your memories") : T("No matches");
  EmptyText.Text = catalogEmpty ? T("Import a folder or drag files here.\nImages, videos and music stay where they are.")
   : T("Nothing matches the search and filters in this view.\nBroaden the filters or remove them.");
  EmptyReset.Visibility = catalogEmpty ? Visibility.Collapsed : Visibility.Visible;
  // The logo welcomes an empty library; an empty result shows a magnifier.
  EmptyLogo.Visibility = catalogEmpty ? Visibility.Visible : Visibility.Collapsed; EmptyGlyph.Visibility = catalogEmpty ? Visibility.Collapsed : Visibility.Visible;
 }

 private void ResetFiltersClicked(object sender, RoutedEventArgs e) =>
  ApplyViewState(CaptureViewState() with { SearchText = null, Tag = null, TagCategory = null, Category = null, Folder = null, RequiredTags = [], ExcludedTags = [], Expression = null, Filter = new(), MinimumStars = 0 });

 private void ApplyFilter(MediaFilter filter)
 {
  _filter = filter; _anchor = 0;
  UpdateFilterBar(); RenderTagFilterChips(); Refresh();
 }

 private string FilterDescription() => _filter.IsEmpty ? "" : " · " + string.Join(" · ", _filter.Describe());

 // ----- Bar -----

 private void UpdateFilterBar()
 {
  int Count(TagRole role) => _filter.Include.Count(f => f.Role == role) + _filter.Exclude.Count(f => f.Role == role);
  Pill("\uE734", T("Rating and type"), RatingFilter.SelectedIndex > 0 ? RatingLabel(RatingFilter.SelectedIndex) : "", RatingPanel);
  Pill("\uE8EC", T("Category"), _category ?? "", CategoryPanel);
  string Range(double? min, double? max, string unit) => (min, max) switch
  {
   ({ } a, { } b) => $"{a:0.##}–{b:0.##} {unit}", ({ } a, null) => T("from {0:0.##} {1}", a, unit), (null, { } b) => T("up to {0:0.##} {1}", b, unit), _ => "",
  };
  Pill("", T("Performers"), Count(TagRole.Performer) is > 0 and var p ? p.ToString(CultureInfo.CurrentCulture) : "", owner => FacetPanel(owner, TagRole.Performer));
  Pill("", T("Studios"), Count(TagRole.Studio) is > 0 and var s ? s.ToString(CultureInfo.CurrentCulture) : "", owner => FacetPanel(owner, TagRole.Studio));
  Pill("", T("Tags"), _requiredTags.Count + _excludedTags.Count + Count(TagRole.Tag) is > 0 and var t ? t.ToString(CultureInfo.CurrentCulture) : "", owner => FacetPanel(owner, TagRole.Tag));
  Pill("", T("Duration"), Range(_filter.MinMinutes, _filter.MaxMinutes, "min"), DurationPanel);
  Pill("", T("File date"), _filter.FileDate.Describe(), owner => DatePanel(owner, DateKind.File));
  Pill("", T("Import date"), _filter.Imported.Describe(), owner => DatePanel(owner, DateKind.Imported));
  Pill("", T("Release date"), _filter.Released.Describe(), owner => DatePanel(owner, DateKind.Released));
  Pill("", T("Properties"), _filter.Profiles.Count > 0 ? _filter.Profiles.Count.ToString(CultureInfo.CurrentCulture) : "", ProfilePanel);
  Pill("", T("Jump marks"), _filter.MarkTags.Count > 0 ? _filter.MarkTags.Count.ToString(CultureInfo.CurrentCulture) : "", MarkTagPanel);
  var technical = _filter.VideoCodecs.Count + (_filter.Hdr is null ? 0 : 1) + (_filter.MinFrameRate is null ? 0 : 1) + (_filter.MultipleFiles is null ? 0 : 1);
  Pill("", T("Technology"), technical > 0 ? technical.ToString(CultureInfo.CurrentCulture) : "", TechnicalPanel);
  var more = new[] { _filter.Resolution != ResolutionClass.Any ? 1 : 0, _filter.Watched is null ? 0 : 1, _filter.MinMegabytes is null && _filter.MaxMegabytes is null ? 0 : 1 }.Sum();
  Pill("", T("More"), more > 0 ? more.ToString(CultureInfo.CurrentCulture) : "", MorePanel);
 }

 private void Pill(string glyph, string label, string value, Func<Button, FrameworkElement> panel)
 {
  var active = value.Length > 0;
  // Pills stay and only change text and colour, so an open panel keeps its place under its pill.
  if (_pills.TryGetValue(label, out var existing))
  {
   existing.Text.Text = active ? $"{label}: {value}" : label;
   existing.Button.SetResourceReference(BackgroundProperty, active ? "ChipFull" : "ButtonBackground");
   return;
  }
  // The button centres its content; a fixed width keeps the label on the left.
  var content = new StackPanel { Orientation = Orientation.Horizontal, Width = 184 };
  content.Children.Add(new TextBlock { Text = glyph, FontFamily = SettingsUi.Icons, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 7, 0) });
  var text = new TextBlock { Text = active ? $"{label}: {value}" : label, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 160, TextTrimming = TextTrimming.CharacterEllipsis };
  content.Children.Add(text);
  // In the flyout the sections stand one below the other; the open one gets a frame.
  var button = new Button { Content = content, Padding = new Thickness(11, 7, 11, 7), Margin = new Thickness(0, 0, 0, 4), FontSize = 12, ToolTip = label,
   HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, BorderThickness = new Thickness(1), Tag = label };
  button.SetResourceReference(BackgroundProperty, active ? "ChipFull" : "ButtonBackground");
  button.BorderBrush = Brushes.Transparent;
  System.Windows.Automation.AutomationProperties.SetName(button, label);
  button.Click += (_, _) => OpenFilterPopup(button, panel(button));
  FilterBar.Children.Add(button);
  _pills[label] = (button, text);
 }
 private readonly Dictionary<string, (Button Button, TextBlock Text)> _pills = new(StringComparer.Ordinal);
 internal IReadOnlyDictionary<string, (Button Button, TextBlock Text)> FilterPills => _pills;

 /// <summary>Shows a section on the right of the filter flyout and opens the flyout if needed.</summary>
 private void OpenFilterPopup(FrameworkElement owner, FrameworkElement content)
 {
  _filterPopup = FilterFlyout;
  if (!FilterFlyout.IsOpen) OpenFilterFlyout();
  FilterSectionHost.Child = content;
  _filterSection = (owner as FrameworkElement)?.Tag as string;
  foreach (var (button, _) in _pills.Values) button.FontWeight = (string?)button.Tag == _filterSection ? FontWeights.SemiBold : FontWeights.Normal;
 }
 private string? _filterSection;

 private void FilterButtonClicked(object sender, RoutedEventArgs e)
 {
  if (FilterFlyout.IsOpen) { FilterFlyout.IsOpen = false; return; }
  // Opens at the section shown last; the first time at rating and type.
  var label = _filterSection != null && _pills.ContainsKey(_filterSection) ? _filterSection : _pills.Keys.First();
  var (button, _) = _pills[label];
  button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
 }
 private void OpenFilterFlyout()
 {
  if (!_filterFlyoutReady)
  {
   _filterFlyoutReady = true;
   FilterFlyoutFrame.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = Theme.IsDark ? .5 : .2 };
   FilterFlyoutFrame.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { FilterFlyout.IsOpen = false; e.Handled = true; } };
  }
  FilterFlyout.IsOpen = true;
 }
 private bool _filterFlyoutReady;
 private void FilterFlyoutDone(object sender, RoutedEventArgs e) => FilterFlyout.IsOpen = false;

 /// <summary>"Ansicht ▾": appearance, tile size and sorting, aligned to the right edge of its button.</summary>
 private void ViewMenuClicked(object sender, RoutedEventArgs e)
 {
  if (ViewPopup.IsOpen) { ViewPopup.IsOpen = false; return; }
  ViewPopupFrame.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
  ViewPopup.HorizontalOffset = ViewButton.ActualWidth - ViewPopupFrame.DesiredSize.Width + 12;
  ViewPopup.IsOpen = true;
 }

 private string RatingLabel(int index) => (RatingFilter.Items[index] as ComboBoxItem)?.Content as string ?? "";

 /// <summary>Minimum rating and media type; the type is the view of the sidebar (all, videos, images, audio).</summary>
 private FrameworkElement RatingPanel(Button owner)
 {
  var panel = new StackPanel();
  panel.Children.Add(Heading(T("Minimum rating")));
  var ratings = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) };
  for (var index = 0; index < RatingFilter.Items.Count; index++)
  {
   var value = index;
   var option = new RadioButton { Content = RatingLabel(index), IsChecked = RatingFilter.SelectedIndex == index, GroupName = "rating", Style = (Style)FindResource("Segment"), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 4, 4) };
   option.Checked += (_, _) => RatingFilter.SelectedIndex = value;
   ratings.Children.Add(option);
  }
  panel.Children.Add(ratings);
  panel.Children.Add(Heading(T("Type")));
  var kinds = new WrapPanel();
  foreach (var view in new[] { "all", "Video", "Image", "Audio" })
  {
   var option = new RadioButton { Content = ViewName(view), IsChecked = _view == view, GroupName = "kind", Style = (Style)FindResource("Segment"), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 4, 4) };
   option.Checked += (_, _) => ShowKind(view);
   kinds.Children.Add(option);
  }
  panel.Children.Add(kinds);
  panel.Children.Add(SettingsUi.Hint(T("The type is the same choice as videos, images and audio in the sidebar; the other filters stay.")));
  return panel;
 }
 /// <summary>Switches the media type and keeps search and filters, unlike a click in the sidebar.</summary>
 private void ShowKind(string view)
 {
  if (_view == view) return;
  var wasTitle = ViewTitle.Text == ViewName(_view);
  _view = view; _anchor = 0;
  if (wasTitle) ViewTitle.Text = ViewName(view);
  UpdateNavigation(); SaveViewPreferences(); Refresh();
 }

 private FrameworkElement CategoryPanel(Button owner)
 {
  var panel = new StackPanel();
  panel.Children.Add(Heading(T("Media category")));
  var rows = new StackPanel();
  foreach (var option in CategoryFilter.Items.Cast<object>())
  {
   var choice = option;
   var radio = new RadioButton { Content = (choice as CategoryOption)?.Label ?? choice.ToString(), IsChecked = ReferenceEquals(CategoryFilter.SelectedItem, choice), GroupName = "category", Margin = new Thickness(0, 3, 0, 3) };
   radio.SetResourceReference(ForegroundProperty, "Text");
   radio.Checked += (_, _) => CategoryFilter.SelectedItem = choice;
   rows.Children.Add(radio);
  }
  panel.Children.Add(new ScrollViewer { Content = rows, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
  return panel;
 }

 // ----- Panels -----

 private static TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };

 /// <summary>Entries of a role with a search; a click cycles include → exclude → off. Tags without category filter by name.</summary>
 private FrameworkElement FacetPanel(Button owner, TagRole role)
 {
  var panel = new StackPanel { Width = 300 };
  var title = role switch { TagRole.Performer => T("Performers"), TagRole.Studio => T("Studios"), _ => T("Tags") };
  panel.Children.Add(Heading(title));
  var any = role == TagRole.Tag ? _tagsMatchAny : _filter.AnyOf.Contains(role);
  var mode = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
  foreach (var (label, value) in new[] { (T("all"), false), (T("at least one"), true) })
  {
   var option = new RadioButton { Content = label, IsChecked = any == value, GroupName = "match" + role, Style = (Style)FindResource("Segment"), Padding = new Thickness(10, 4, 10, 4) };
   option.Checked += (_, _) =>
   {
    if (role == TagRole.Tag) { _tagsMatchAny = value; _anchor = 0; RenderTagFilterChips(); Refresh(); return; }
    ApplyFilter(_filter with { AnyOf = value ? [.. _filter.AnyOf.Where(r => r != role), role] : _filter.AnyOf.Where(r => r != role).ToList() });
   };
   mode.Children.Add(option);
  }
  panel.Children.Add(mode);
  if (role == TagRole.Studio)
  {
   var children = new CheckBox { Content = T("Include sub-studios"), IsChecked = _filter.IncludeSubStudios, Margin = new Thickness(0, 0, 0, 8),
    ToolTip = T("A studio also finds the items of the studios that belong to it (directory: “Belongs to”).") };
   children.SetResourceReference(ForegroundProperty, "Text");
   children.Click += (_, _) => ApplyFilter(_filter with { IncludeSubStudios = children.IsChecked == true });
   panel.Children.Add(children);
  }
  var search = new TextBox { Padding = new Thickness(6, 5, 6, 5), ToolTip = T("Name or alias") };
  System.Windows.Automation.AutomationProperties.SetName(search, T("Search {0}", title));
  var placeholder = new TextBlock { Text = T("Search {0} …", title), Foreground = SettingsUi.Muted, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 0, 0, 0) };
  search.TextChanged += (_, _) => placeholder.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
  var searchBox = new Grid { Margin = new Thickness(0, 0, 0, 6) }; searchBox.Children.Add(search); searchBox.Children.Add(placeholder);
  panel.Children.Add(searchBox);
  var rows = new StackPanel();
  panel.Children.Add(new ScrollViewer { Content = rows, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
  var hint = SettingsUi.Hint(T("Click: include · again: exclude · again: off")); panel.Children.Add(hint);
  IReadOnlyList<DirectoryEntry> entries = [];
  void Fill()
  {
   rows.Children.Clear();
   var text = search.Text.Trim();
   var shown = entries.Where(e => text.Length == 0 || e.Tag.Contains(text, StringComparison.CurrentCultureIgnoreCase) || e.AliasList.Any(a => a.Contains(text, StringComparison.CurrentCultureIgnoreCase)))
    .OrderByDescending(e => State(e) != 0).ThenByDescending(e => e.MediaCount).ThenBy(e => e.Tag, StringComparer.CurrentCultureIgnoreCase).Take(200).ToList();
   foreach (var entry in shown) rows.Children.Add(FacetRow(entry, role, Fill));
   if (shown.Count == 0) rows.Children.Add(SettingsUi.Hint(entries.Count == 0 ? T("No entries in this role yet (directory).") : T("Nothing found.")));
  }
  search.TextChanged += (_, _) => Fill();
  panel.Loaded += async (_, _) =>
  {
   var all = await Task.Run(() => _repository.GetDirectory());
   entries = role == TagRole.Tag
    ? all.Where(e => e.Role == TagRole.Tag).GroupBy(e => e.Tag, StringComparer.OrdinalIgnoreCase).Select(g => g.First() with { MediaCount = g.Max(e => e.MediaCount) }).ToList()
    : all.Where(e => e.Role == role).ToList();
   Fill(); search.Focus();
  };
  return panel;
 }

 /// <summary>0 off, 1 included, -1 excluded.</summary>
 private int State(DirectoryEntry entry)
 {
  if (entry.Role == TagRole.Tag)
   return _requiredTags.Contains(entry.Tag, StringComparer.OrdinalIgnoreCase) ? 1 : _excludedTags.Contains(entry.Tag, StringComparer.OrdinalIgnoreCase) ? -1 : 0;
  var facet = Facet(entry);
  return _filter.Include.Contains(facet) ? 1 : _filter.Exclude.Contains(facet) ? -1 : 0;
 }

 private static TagFacet Facet(DirectoryEntry entry) => new(entry.Category, entry.Tag, entry.Role);

 private Button FacetRow(DirectoryEntry entry, TagRole role, Action refill)
 {
  var state = State(entry);
  // The quiet button centres its content; a fixed width keeps name left and count right.
  var row = new DockPanel { Width = 258 };
  var mark = new TextBlock { Text = state switch { 1 => "", -1 => "", _ => "" }, FontFamily = SettingsUi.Icons, FontSize = 12, Width = 22, VerticalAlignment = VerticalAlignment.Center };
  mark.SetResourceReference(TextBlock.ForegroundProperty, state == -1 ? "TagExcluded" : "TagIncluded");
  var count = new TextBlock { Text = entry.MediaCount.ToString("N0", CultureInfo.CurrentCulture), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
  count.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
  DockPanel.SetDock(mark, Dock.Left); DockPanel.SetDock(count, Dock.Right);
  row.Children.Add(mark); row.Children.Add(count);
  row.Children.Add(new TextBlock { Text = entry.Tag, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
   ToolTip = entry.Category.Length > 0 ? entry.Category + (entry.AliasList.Count > 0 ? " · " + string.Join(", ", entry.AliasList) : "") : null });
  var button = new Button { Content = row, Style = (Style)FindResource("Quiet"), HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(6, 5, 6, 5), Margin = new Thickness(0, 0, 0, 1) };
  if (state != 0) button.SetResourceReference(BackgroundProperty, state == 1 ? "ChipFull" : "ChipExcluded");
  button.SetResourceReference(ForegroundProperty, "Text");
  System.Windows.Automation.AutomationProperties.SetName(button, entry.Tag + (state switch { 1 => " (eingeschlossen)", -1 => " (ausgeschlossen)", _ => "" }));
  button.Click += (_, _) => { CycleFacet(entry, role); refill(); };
  return button;
 }

 internal void CycleFacet(DirectoryEntry entry, TagRole role)
 {
  var next = State(entry) switch { 0 => 1, 1 => -1, _ => 0 };
  if (role == TagRole.Tag)
  {
   _requiredTags.RemoveAll(t => string.Equals(t, entry.Tag, StringComparison.OrdinalIgnoreCase)); _excludedTags.RemoveAll(t => string.Equals(t, entry.Tag, StringComparison.OrdinalIgnoreCase));
   if (next == 1) _requiredTags.Add(entry.Tag); else if (next == -1) _excludedTags.Add(entry.Tag);
   _anchor = 0; UpdateFilterBar(); ApplyTagFilterChanges(); return;
  }
  var facet = Facet(entry);
  var include = _filter.Include.Where(f => f != facet).ToList(); var exclude = _filter.Exclude.Where(f => f != facet).ToList();
  if (next == 1) include.Add(facet); else if (next == -1) exclude.Add(facet);
  ApplyFilter(_filter with { Include = include, Exclude = exclude });
 }

 private FrameworkElement DurationPanel(Button owner)
 {
  var panel = new StackPanel { Width = 280 };
  panel.Children.Add(Heading(T("Duration")));
  var presets = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
  foreach (var (label, min, max) in new (string, double?, double?)[] { (T("any"), null, null), (T("up to 5 min"), null, 5), (T("5–20 min"), 5, 20), (T("20–60 min"), 20, 60), (T("over 1 h"), 60, null) })
  {
   var preset = SettingsUi.SmallButton(label); preset.Margin = new Thickness(0, 0, 6, 6);
   preset.Click += (_, _) => ApplyFilter(_filter with { MinMinutes = min, MaxMinutes = max });
   presets.Children.Add(preset);
  }
  panel.Children.Add(presets);
  panel.Children.Add(RangeFields(T("Minutes"), _filter.MinMinutes, _filter.MaxMinutes, (min, max) => ApplyFilter(_filter with { MinMinutes = min, MaxMinutes = max })));
  return panel;
 }

 /// <summary>Two number fields "von" and "bis"; Enter or leaving a field applies, an empty field means no limit.</summary>
 private static FrameworkElement RangeFields(string unit, double? min, double? max, Action<double?, double?> apply)
 {
  var from = new TextBox { Text = min?.ToString("0.##", CultureInfo.CurrentCulture) ?? "", Width = 72, Padding = new Thickness(6, 4, 6, 4) };
  var to = new TextBox { Text = max?.ToString("0.##", CultureInfo.CurrentCulture) ?? "", Width = 72, Padding = new Thickness(6, 4, 6, 4) };
  System.Windows.Automation.AutomationProperties.SetName(from, unit + " von"); System.Windows.Automation.AutomationProperties.SetName(to, unit + " bis");
  static double? Parse(string text) => double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var value) && value >= 0 ? value : null;
  var last = (min, max);
  void Commit() { var next = (Parse(from.Text), Parse(to.Text)); if (next == last) return; last = next; apply(next.Item1, next.Item2); }
  foreach (var box in new[] { from, to }) { box.LostKeyboardFocus += (_, _) => Commit(); box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); }; }
  var row = new StackPanel { Orientation = Orientation.Horizontal };
  row.Children.Add(new TextBlock { Text = T("of"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) }); row.Children.Add(from);
  row.Children.Add(new TextBlock { Text = T("to"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0) }); row.Children.Add(to);
  row.Children.Add(new TextBlock { Text = unit, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Foreground = SettingsUi.Muted });
  return row;
 }

 private enum DateKind { File, Imported, Released }

 private FrameworkElement DatePanel(Button owner, DateKind kind)
 {
  var current = kind switch { DateKind.File => _filter.FileDate, DateKind.Imported => _filter.Imported, _ => _filter.Released };
  MediaFilter With(DateFilter value) => kind switch
  {
   DateKind.File => _filter with { FileDate = value }, DateKind.Imported => _filter with { Imported = value }, _ => _filter with { Released = value },
  };
  var file = kind == DateKind.File;
  var panel = new StackPanel { Width = 290 };
  panel.Children.Add(Heading(kind switch { DateKind.File => T("File date (age of the item)"), DateKind.Imported => T("Import date"), _ => T("Release date") }));
  foreach (var (label, window) in new[] { (T("any"), DateWindow.Any), (T("last 7 days"), DateWindow.Last7Days), (T("last 30 days"), DateWindow.Last30Days), (T("last year"), DateWindow.LastYear),
   (T("older than 1 year"), DateWindow.OlderThanYear), (T("older than 2 years"), DateWindow.OlderThan2Years), (T("older than 5 years"), DateWindow.OlderThan5Years) })
  {
   var option = new RadioButton { Content = label, IsChecked = current.Window == window, GroupName = "date" + kind, Margin = new Thickness(0, 2, 0, 2) };
   option.SetResourceReference(ForegroundProperty, "Text");
   option.Checked += (_, _) => ApplyFilter(With(new DateFilter(window)));
   panel.Children.Add(option);
  }
  panel.Children.Add(new TextBlock { Text = T("Period"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) });
  var from = new TextBox { Text = current.Window == DateWindow.Custom ? current.From?.ToString("d", CultureInfo.CurrentCulture) ?? "" : "", Width = 100, Padding = new Thickness(6, 4, 6, 4) };
  var to = new TextBox { Text = current.Window == DateWindow.Custom ? current.To?.ToString("d", CultureInfo.CurrentCulture) ?? "" : "", Width = 100, Padding = new Thickness(6, 4, 6, 4) };
  System.Windows.Automation.AutomationProperties.SetName(from, T("From date")); System.Windows.Automation.AutomationProperties.SetName(to, T("To date"));
  static DateTimeOffset? Parse(string text) => DateTime.TryParse(text.Trim(), CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var date) ? new DateTimeOffset(date.Date) : null;
  void Commit()
  {
   var (a, b) = (Parse(from.Text), Parse(to.Text));
   var next = a is null && b is null ? new DateFilter() : new DateFilter(DateWindow.Custom, a, b);
   if (next != current) { current = next; ApplyFilter(With(next)); }
  }
  foreach (var box in new[] { from, to }) { box.LostKeyboardFocus += (_, _) => Commit(); box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); }; }
  var row = new StackPanel { Orientation = Orientation.Horizontal };
  row.Children.Add(from); row.Children.Add(new TextBlock { Text = "–", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) }); row.Children.Add(to);
  panel.Children.Add(row);
  panel.Children.Add(SettingsUi.Hint(T("Date such as {0:d}; leave the field empty for no limit.", DateTime.Today)));
  return panel;
 }

 private FrameworkElement MorePanel(Button owner)
 {
  var panel = new StackPanel { Width = 320 };
  StackPanel Choices<T>(string group, IEnumerable<(string Label, T Value)> options, T current, Func<T, MediaFilter> apply)
  {
   var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
   foreach (var (label, value) in options)
   {
    var option = new RadioButton { Content = label, IsChecked = Equals(value, current), GroupName = group, Style = (Style)FindResource("Segment"), Padding = new Thickness(9, 4, 9, 4) };
    option.Checked += (_, _) => ApplyFilter(apply(value));
    bar.Children.Add(option);
   }
   return bar;
  }
  panel.Children.Add(Heading(T("Minimum resolution")));
  panel.Children.Add(Choices("resolution", [(T("any"), ResolutionClass.Any), ("HD", ResolutionClass.HD), (T("Full HD"), ResolutionClass.FullHD), ("4K", ResolutionClass.UHD), ("8K", ResolutionClass.EightK)],
   _filter.Resolution, value => _filter with { Resolution = value }));
  panel.Children.Add(Heading(T("Watched")));
  panel.Children.Add(Choices("watched", [(T("any"), (bool?)null), (T("already watched"), true), (T("never watched"), false)], _filter.Watched, value => _filter with { Watched = value }));
  panel.Children.Add(Heading(T("File size")));
  panel.Children.Add(RangeFields("MB", _filter.MinMegabytes, _filter.MaxMegabytes, (min, max) => ApplyFilter(_filter with { MinMegabytes = min, MaxMegabytes = max })));
  return panel;
 }

 /// <summary>Technical data (P6): video codecs present in the library, HDR, frame rate and several files.</summary>
 private FrameworkElement TechnicalPanel(Button owner)
 {
  var panel = new StackPanel { Width = 320 };
  StackPanel Choices<TValue>(string group, IEnumerable<(string Label, TValue Value)> options, TValue current, Func<TValue, MediaFilter> apply)
  {
   var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
   foreach (var (label, value) in options)
   {
    var option = new RadioButton { Content = label, IsChecked = Equals(value, current), GroupName = group, Style = (Style)FindResource("Segment"), Padding = new Thickness(9, 4, 9, 4) };
    option.Checked += (_, _) => ApplyFilter(apply(value));
    bar.Children.Add(option);
   }
   return bar;
  }
  panel.Children.Add(Heading(T("Video codec")));
  var codecs = _repository.GetVideoCodecs();
  var rows = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
  foreach (var (codec, count) in codecs.Concat(_filter.VideoCodecs.Where(c => !codecs.Any(k => k.Codec == c)).Select(c => (c, 0L))))
  {
   var box = new CheckBox { Content = count > 0 ? $"{TechnicalLabels.Codec(codec)}  ({count:N0})" : TechnicalLabels.Codec(codec), Margin = new Thickness(0, 3, 0, 3),
    IsChecked = _filter.VideoCodecs.Contains(codec) };
   box.SetResourceReference(ForegroundProperty, "Text");
   box.Click += (_, _) => ApplyFilter(_filter with { VideoCodecs = box.IsChecked == true ? [.. _filter.VideoCodecs, codec] : _filter.VideoCodecs.Where(c => c != codec).ToList() });
   rows.Children.Add(box);
  }
  if (rows.Children.Count == 0) panel.Children.Add(SettingsUi.Hint(T("No technical data read yet. New imports read it; for the library use Organize ▾ → Read technical data.")));
  else panel.Children.Add(rows);
  panel.Children.Add(Heading("HDR"));
  panel.Children.Add(Choices("hdr", [(T("any"), (bool?)null), ("HDR", true), (T("without HDR"), false)], _filter.Hdr, value => _filter with { Hdr = value }));
  panel.Children.Add(Heading(T("Frame rate")));
  panel.Children.Add(Choices("fps", [(T("any"), (double?)null), (T("from 30 fps"), 30d), (T("from 50 fps"), 50d), (T("from 60 fps"), 60d)], _filter.MinFrameRate, value => _filter with { MinFrameRate = value }));
  panel.Children.Add(Heading(T("Files")));
  panel.Children.Add(Choices("files", [(T("any"), (bool?)null), (T("several versions"), true), (T("one file"), false)], _filter.MultipleFiles, value => _filter with { MultipleFiles = value }));
  return panel;
 }

 /// <summary>Tags of bookmarks and scenes; a medium needs a jump mark with each ticked tag.</summary>
 private FrameworkElement MarkTagPanel(Button owner)
 {
  var panel = new StackPanel { Width = 300 };
  panel.Children.Add(Heading(T("Jump mark with tag")));
  var tags = _repository.GetMarkTags();
  var rows = new StackPanel();
  foreach (var tag in _filter.MarkTags.Where(t => !tags.Any(m => string.Equals(m.Tag, t, StringComparison.OrdinalIgnoreCase))).Select(t => new MarkTagCount(t, 0)).Concat(tags))
  {
   var box = new CheckBox { Content = tag.Count > 0 ? $"{tag.Tag}  ({tag.Count:N0})" : tag.Tag, Margin = new Thickness(0, 3, 0, 3),
    IsChecked = _filter.MarkTags.Contains(tag.Tag, StringComparer.OrdinalIgnoreCase) };
   box.SetResourceReference(ForegroundProperty, "Text");
   box.Click += (_, _) => ApplyFilter(_filter with { MarkTags = box.IsChecked == true ? [.. _filter.MarkTags, tag.Tag]
    : _filter.MarkTags.Where(t => !string.Equals(t, tag.Tag, StringComparison.OrdinalIgnoreCase)).ToList() });
   rows.Children.Add(box);
  }
  if (rows.Children.Count == 0) panel.Children.Add(SettingsUi.Hint(T("No bookmark or scene has a tag yet. Tags are given in the details, in the lists of bookmarks and scenes.")));
  else panel.Children.Add(new ScrollViewer { Content = rows, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
  panel.Children.Add(SettingsUi.Hint(T("Finds items with a bookmark or scene carrying the tag. Short form in the search box: @tag")));
  return panel;
 }

 /// <summary>Chips for the active filter bar parts, each removing itself; used by the tag chip row.</summary>
 private IEnumerable<(string Label, bool Excluded, Action Remove)> FilterChips()
 {
  foreach (var facet in _filter.Include.ToList()) yield return (facet.Tag, false, () => ApplyFilter(_filter with { Include = _filter.Include.Where(f => f != facet).ToList() }));
  foreach (var facet in _filter.Exclude.ToList()) yield return (T("without {0}", facet.Tag), true, () => ApplyFilter(_filter with { Exclude = _filter.Exclude.Where(f => f != facet).ToList() }));
  foreach (var profile in _filter.Profiles.ToList()) yield return (MediaFilter.DescribeProfile(profile), false, () => ApplyFilter(_filter with { Profiles = _filter.Profiles.Where(p => p != profile).ToList() }));
  foreach (var tag in _filter.MarkTags.ToList()) yield return (T("Jump mark: {0}", tag), false, () => ApplyFilter(_filter with { MarkTags = _filter.MarkTags.Where(t => t != tag).ToList() }));
  var rest = _filter with { Include = [], Exclude = [], Profiles = [], MarkTags = [] };
  if (rest.MinMinutes is not null || rest.MaxMinutes is not null) yield return (rest.Describe().First(), false, () => ApplyFilter(_filter with { MinMinutes = null, MaxMinutes = null }));
  var size = _filter with { Include = [], Exclude = [], Profiles = [], MarkTags = [], MinMinutes = null, MaxMinutes = null, Resolution = ResolutionClass.Any, FileDate = new(), Imported = new(), Released = new(), Watched = null,
   VideoCodecs = [], Hdr = null, MinFrameRate = null, MultipleFiles = null };
  if (!size.IsEmpty) yield return (size.Describe().First(), false, () => ApplyFilter(_filter with { MinMegabytes = null, MaxMegabytes = null }));
  if (_filter.Resolution != ResolutionClass.Any) yield return ((new MediaFilter { Resolution = _filter.Resolution }).Describe().First(), false, () => ApplyFilter(_filter with { Resolution = ResolutionClass.Any }));
  if (_filter.FileDate.IsActive) yield return (T("File ") + _filter.FileDate.Describe(), false, () => ApplyFilter(_filter with { FileDate = new() }));
  if (_filter.Imported.IsActive) yield return (T("imported ") + _filter.Imported.Describe(), false, () => ApplyFilter(_filter with { Imported = new() }));
  if (_filter.Released.IsActive) yield return (T("released {0}", _filter.Released.Describe()), false, () => ApplyFilter(_filter with { Released = new() }));
  if (_filter.Watched is { } watched) yield return (watched ? T("watched") : T("never watched"), false, () => ApplyFilter(_filter with { Watched = null }));
  foreach (var codec in _filter.VideoCodecs.ToList()) yield return (TechnicalLabels.Codec(codec), false, () => ApplyFilter(_filter with { VideoCodecs = _filter.VideoCodecs.Where(c => c != codec).ToList() }));
  if (_filter.Hdr is { } hdr) yield return (hdr ? "HDR" : T("without HDR"), false, () => ApplyFilter(_filter with { Hdr = null }));
  if (_filter.MinFrameRate is { } rate) yield return (T("from {0} fps", rate.ToString("0.##", CultureInfo.CurrentCulture)), false, () => ApplyFilter(_filter with { MinFrameRate = null }));
  if (_filter.MultipleFiles is { } multiple) yield return (multiple ? T("several versions") : T("one file"), false, () => ApplyFilter(_filter with { MultipleFiles = null }));
 }
}
