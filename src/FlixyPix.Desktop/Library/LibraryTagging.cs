using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

// Tag sidebar with counts and combinable filters, chip editor for one or many media,
// explainable suggestions and typical combinations.
public partial class MainWindow
{
 private const string MediaDragFormat = "FlixyPix.MediaIds";
 private readonly List<string> _requiredTags = [];
 private readonly List<string> _excludedTags = [];
 private IReadOnlyList<TagListEntry> _tagEntries = [];
 private bool _tagsByName;
 private long _tagAssistVersion;
 private Task _tagAssistReady = Task.CompletedTask;
 private Point? _dragStart;
 private ListBoxItem? _pendingSingleSelection;
 private static Brush IncludedTagBrush => Theme.Brush("TagIncluded");
 private static Brush ExcludedTagBrush => Theme.Brush("TagExcluded");
 private static Brush UnusedTagBrush => Theme.Brush("TagUnused");
 private static Brush TagBrush => Theme.Brush("TextSoft");
 private static Brush FullChipBrush => Theme.Brush("ChipFull");
 private static Brush PartialChipBrush => Theme.Brush("ChipPartial");
 private static Brush ExcludedChipBrush => Theme.Brush("ChipExcluded");
 private static Brush SuggestionChipBrush => Theme.Brush("ChipSuggestion");

 internal sealed record TagListEntry(string Tag, int Count, bool IsFavorite);
 private sealed record TagRow(string Tag, string Label, string CountLabel, Brush Foreground, string Shortcut = "")
 { public Visibility ShortcutVisibility => Shortcut.Length>0?Visibility.Visible:Visibility.Collapsed; public string ShortcutHint => T("Assign to selected items")+" ("+Shortcut+")"; }

 private string? CurrentTag => _tag ?? _requiredTags.LastOrDefault();
 private IReadOnlyList<MediaItem> SelectedMedia => MediaList.SelectedItems.Cast<MediaCard>().Select(card => card.Item).Concat(_offWindowSelection).DistinctBy(item => item.Id).ToArray();

 private static TagListEntry[] TagEntries(IReadOnlyList<TagUsage> usage, CatalogPreferences preferences)
 {
  var favorites = new HashSet<string>(preferences.TagVocabulary.Where(t => t.IsFavorite).Select(t => t.Tag), StringComparer.OrdinalIgnoreCase);
  var counts = usage.ToDictionary(u => u.Tag, u => u.MediaCount, StringComparer.OrdinalIgnoreCase);
  return usage.Select(u => u.Tag).Concat(preferences.TagVocabulary.Select(t => t.Tag.Trim())).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
   .Select(tag => new TagListEntry(tag, counts.GetValueOrDefault(tag), favorites.Contains(tag))).ToArray();
 }

 /// <summary>Tags for Ctrl+1 … Ctrl+9: favourite tags, then the most used ones, each group alphabetical so the numbers do not
 /// move when a tag is used.</summary>
 internal IReadOnlyList<string> QuickTags
 {
  get
  {
   var preferences=_repository.GetPreferences();
   if(preferences.QuickTags is null)
   {
    var initial=_tagEntries.OrderByDescending(e=>e.IsFavorite).ThenByDescending(e=>e.Count).ThenBy(e=>e.Tag,StringComparer.OrdinalIgnoreCase).Where(e=>e.IsFavorite||e.Count>0).Take(9).OrderByDescending(e=>e.IsFavorite).ThenBy(e=>e.Tag,StringComparer.OrdinalIgnoreCase).Select(e=>(string?)e.Tag).ToList();
    if(_tagEntries.Count==0) return [];
    while(initial.Count<9) initial.Add(null);
    _repository.SavePreferences(preferences with {QuickTags=initial}); preferences=_repository.GetPreferences();
   }
   return preferences.QuickTags!.Select(t=>t??"").ToArray();
  }
 }

 /// <summary>Keyboard tagging: the n-th quick tag is added to the selection, or removed when every selected medium has it.</summary>
 internal async Task ToggleQuickTagAsync(int number)
 {
  var tags = QuickTags; var items = SelectedMedia;
  if (number < 1 || number > tags.Count || string.IsNullOrEmpty(tags[number - 1])) { Status.Text = T("Ctrl+{0} is not assigned to a tag yet · favorite tags (★) come first", number); return; }
  if (items.Count == 0) { Status.Text = T("Please select items first."); return; }
  var tag = tags[number - 1]; var ids = items.Select(i => i.Id).ToArray();
  if (items.All(i => i.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))) await ChangeTagsAsync(ids, [], [tag]); else await ChangeTagsAsync(ids, [tag], []);
 }

 private void RenderTagList()
 {
  var quick = QuickTags.Select((tag, index) => (tag, index)).Where(p=>p.tag.Length>0).ToDictionary(p => p.tag, p => p.index + 1, StringComparer.OrdinalIgnoreCase);
  var filter = TagFilterBox.Text.Trim();
  bool Active(string tag) => _requiredTags.Contains(tag, StringComparer.OrdinalIgnoreCase) || _excludedTags.Contains(tag, StringComparer.OrdinalIgnoreCase);
  var entries = _tagEntries.Where(e => (ShowUnusedTags.IsChecked == true || e.Count > 0 || Active(e.Tag)) && (filter.Length == 0 || e.Tag.Contains(filter, StringComparison.OrdinalIgnoreCase)));
  var ordered = entries.OrderByDescending(e => Active(e.Tag)).ThenByDescending(e => e.IsFavorite);
  ordered = _tagsByName ? ordered.ThenBy(e => e.Tag, StringComparer.OrdinalIgnoreCase) : ordered.ThenByDescending(e => e.Count).ThenBy(e => e.Tag, StringComparer.OrdinalIgnoreCase);
  Tags.ItemsSource = ordered.Select(e =>
  {
   var included = _requiredTags.Contains(e.Tag, StringComparer.OrdinalIgnoreCase); var excluded = _excludedTags.Contains(e.Tag, StringComparer.OrdinalIgnoreCase);
   var label = (included ? "✓ " : excluded ? "⊘ " : "") + (e.IsFavorite ? "★ " : "") + e.Tag;
   return new TagRow(e.Tag, label, e.Count > 0 ? e.Count.ToString("N0") : "–", included ? IncludedTagBrush : excluded ? ExcludedTagBrush : e.Count == 0 ? UnusedTagBrush : TagBrush, quick.TryGetValue(e.Tag,out var key)?KeyLabel("Ctrl+"+key):"");
  }).ToArray();
 }
 private void TagFilterChanged(object sender, TextChangedEventArgs e) { if (_repository != null) RenderTagList(); }
 private void TagListOptionsChanged(object sender, RoutedEventArgs e) { if (_repository != null) { SaveSidebar(s=>s with {ShowUnusedTags=ShowUnusedTags.IsChecked==true});RenderTagList(); } }
 private void TagSortClicked(object sender, RoutedEventArgs e) { _tagsByName = !_tagsByName; SaveSidebar(s=>s with {TagsByName=_tagsByName}); TagSortButton.Content = _tagsByName ? "Name" : T("Frequency"); RenderTagList(); }

 private void TagItemClicked(object sender, MouseButtonEventArgs e)
 {
  if ((sender as ListBoxItem)?.DataContext is not TagRow row) return;
  if (IsQuickBadge(e.OriginalSource)) return;
  e.Handled = true; ToggleTagFilter(row.Tag, (Keyboard.Modifiers & ModifierKeys.Control) != 0);
 }
 private void TagItemRightClicked(object sender, MouseButtonEventArgs e)
 {
  if ((sender as ListBoxItem)?.DataContext is not TagRow row) return;
  e.Handled = true; OpenTagMenu(row,sender as UIElement);
 }
 private void OpenTagMenu(TagRow row,UIElement? target)
 {
  var tag = row.Tag; var ids = SelectedMedia.Select(i => i.Id).ToArray();
  var actions = new List<MenuSpec> { MenuSpec.Command(T("Filter (all chosen tags)"), () => ToggleTagFilter(tag, false)), MenuSpec.Command(T("Exclude"), () => ToggleTagFilter(tag, true)) };
  if (ids.Length > 0) { actions.Add(MenuSpec.Command(T("Assign to selected items ({0})", ids.Length), () => _ = ChangeTagsAsync(ids, [tag], []))); actions.Add(MenuSpec.Command(T("Remove from selected items ({0})", ids.Length), () => _ = ChangeTagsAsync(ids, [], [tag]))); }
  actions.Add(MenuSpec.Separator); actions.Add(MenuSpec.Submenu(T("Search in browser"), Specs(WebSearchEntries(tag)), "\uE774"));
  actions.Add(MenuSpec.Separator); actions.Add(MenuSpec.Command(T("Rename / merge …"), () => RenameGlobalTag(tag))); actions.Add(MenuSpec.Command(T("Remove from all items …"), () => DeleteGlobalTag(tag)));
  actions.Add(MenuSpec.Submenu(T("Assign shortcut"), Enumerable.Range(1,9).Select(n=>MenuSpec.Command(KeyLabel("Ctrl+"+n)+" · "+(QuickTags.ElementAtOrDefault(n-1)??""),()=>AssignQuickTag(n,tag)))));
  actions.Add(MenuSpec.Command(T("Remove shortcut"),()=>AssignQuickTag(0,tag)));
  Menus.Open(target, actions, MenuFailed);
 }

 private void ToggleTagFilter(string tag, bool exclude)
 {
  var wasIncluded = _requiredTags.RemoveAll(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)) > 0;
  var wasExcluded = _excludedTags.RemoveAll(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)) > 0;
  if (exclude ? !wasExcluded : !wasIncluded) (exclude ? _excludedTags : _requiredTags).Add(tag);
  _playlist = null; _anchor = 0; _advancedExpression = null;
  ApplyTagFilterChanges();
 }
 private void ClearTagFilters() { _requiredTags.Clear(); _excludedTags.Clear(); _tagsMatchAny = _repository.GetPreferences().General.TagsMatchAny; RenderTagFilterChips(); }
 private void ApplyTagFilterChanges()
 {
  if (_requiredTags.Count > 0) ViewTitle.Text = string.Join(" + ", _requiredTags);
  else if (_view == "all" && _tag == null && _folder == null) ViewTitle.Text = T("All items");
  RenderTagFilterChips(); RenderTagList(); UpdateFilterBar(); Refresh();
 }
 private void RenderTagFilterChips()
 {
  ActiveTagFilters.Children.Clear();
  var active = (RatingFilter.SelectedIndex > 0 ? 1 : 0) + (_category != null ? 1 : 0) + _requiredTags.Count + _excludedTags.Count + FilterChips().Count();
  FilterButtonText.Text = active > 0 ? T("Filter · {0:N0}", active) : T("Filter");
  FilterButton.SetResourceReference(BackgroundProperty, active > 0 ? "ChipFull" : "ButtonBackground");
  Button Chip(string text, Brush background, string tooltip, Action remove) { var chip = new Button { Content = text, Background = background, Padding = new Thickness(10, 4, 10, 4), FontSize = 12, ToolTip = tooltip }; chip.Click += (_, _) => remove(); return chip; }
  if (RatingFilter.SelectedIndex > 0) ActiveTagFilters.Children.Add(Chip(RatingLabel(RatingFilter.SelectedIndex) + "  ×", FullChipBrush, T("Remove filter"), () => RatingFilter.SelectedIndex = 0));
  if (_category != null) ActiveTagFilters.Children.Add(Chip(_category + "  ×", FullChipBrush, T("Remove filter"), () => CategoryFilter.SelectedIndex = 0));
  foreach (var tag in _requiredTags.ToArray()) ActiveTagFilters.Children.Add(Chip(tag + "  ×", FullChipBrush, T("Remove tag filter"), () => { _requiredTags.Remove(tag); _anchor = 0; ApplyTagFilterChanges(); }));
  foreach (var tag in _excludedTags.ToArray()) ActiveTagFilters.Children.Add(Chip(T("without {0}", tag) + "  ×", ExcludedChipBrush, T("Remove exclusion"), () => { _excludedTags.Remove(tag); _anchor = 0; ApplyTagFilterChanges(); }));
  foreach (var (label, excluded, remove) in FilterChips().ToList()) ActiveTagFilters.Children.Add(Chip(label + "  ×", excluded ? ExcludedChipBrush : FullChipBrush, T("Remove filter"), remove));
  if (_requiredTags.Count > 1 && _tagsMatchAny) ActiveTagFilters.Children.Insert(0, new TextBlock { Text = T("at least one:"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 6), Foreground = Theme.Brush("Muted"), FontSize = 12 });
  if (active > 1)
   ActiveTagFilters.Children.Add(Chip(T("Remove all filters"), Brushes.Transparent, T("Remove all tag and filter-bar filters"), () => { _requiredTags.Clear(); _excludedTags.Clear(); _filter = new(); _anchor = 0; UpdateFilterBar(); ApplyTagFilterChanges(); }));
 }
 private string TagFilterDescription() =>
  (_tag != null ? T(" · Tag: {0}", _tag) : "") + (_requiredTags.Count > 0 ? T(" · Tags: {0}", string.Join(" + ", _requiredTags)) : "") + (_excludedTags.Count > 0 ? T(" · without {0}", string.Join(", ", _excludedTags)) : "");

 /// <summary>Saved searches keep the sidebar tag filters as an explicit expression.</summary>
 private SearchExpression? WithTagFilters(SearchExpression? expression)
 {
  static SearchExpression Has(string tag) => SearchExpression.Where(new() { Field = SearchField.Tag, Comparison = SearchComparison.Equals, Text = tag });
  var parts = new List<SearchExpression>(); if (expression != null) parts.Add(expression);
  parts.AddRange(_requiredTags.Select(Has)); parts.AddRange(_excludedTags.Select(t => SearchExpression.Not(Has(t))));
  return parts.Count switch { 0 => null, 1 => parts[0], _ => SearchExpression.All(parts.ToArray()) };
 }

 private async Task ChangeTagsAsync(IReadOnlyCollection<Guid> ids, IReadOnlyCollection<string> add, IReadOnlyCollection<string> remove)
 {
  if (ids.Count == 0 || add.Count + remove.Count == 0) return;
  try
  {
   var changed = await Task.Run(() => _repository.UpdateTags(ids, add, remove));
   Status.Text = (add.Count > 0 ? T("“{0}” assigned", string.Join(T("”, “"), add)) : T("“{0}” removed", string.Join(T("”, “"), remove))) + Tn(changed, " · 1 item changed", " · {0:N0} items changed");
   if (ViewDependsOnTags) await RefreshAsync(); else await RefreshTagsInPlaceAsync(ids);
  }
  catch (Exception ex) { Message.Show(this, ex.Message, "Tags", MessageBoxButton.OK, MessageBoxImage.Warning); }
 }

 /// <summary>Whether a tag edit can change which media the view shows (tag filters, text search over tags, saved expressions).</summary>
 private bool ViewDependsOnTags => _tag != null || _tagGroup != null || _requiredTags.Count + _excludedTags.Count > 0 || _advancedExpression != null || SearchBox.Text.Trim().Length > 0
  || _filter.Include.Count + _filter.Exclude.Count + _filter.Profiles.Count > 0;

 /// <summary>After a tag edit in a view that does not filter by tags: tiles show no tags, so only the edited media, the tag
 /// list and the tag editor change. The loaded window, its thumbnails and the scroll position stay.</summary>
 private async Task RefreshTagsInPlaceAsync(IReadOnlyCollection<Guid> ids)
 {
  var cards = (MediaList.ItemsSource as IEnumerable<MediaCard> ?? []).Where(c => ids.Contains(c.Item.Id)).ToList();
  var (items, browse) = await Task.Run(() => (cards.Select(c => _repository.GetMedia(c.Item.Id)).ToList(), PrepareBrowsing(_repository.GetFacets(), _repository.GetPreferences(), _repository.GetTagUsage())));
  if (_closing) return;
  for (var i = 0; i < cards.Count; i++) if (items[i] is { } item) cards[i].Replace(item);
  if (_offWindowSelection.Any(i => ids.Contains(i.Id)))
   _offWindowSelection = await Task.Run(() => _offWindowSelection.Select(i => ids.Contains(i.Id) ? _repository.GetMedia(i.Id) ?? i : i).ToList());
  _refreshing = true; try { ApplyBrowsing(browse); } finally { _refreshing = false; }
  UpdateTagEditor(SelectedMedia);
 }

 private void UpdateTagEditor(IReadOnlyList<MediaItem> items)
 {
  TagChips.Children.Clear(); QuickTagButtons.Children.Clear();
  foreach(var slot in QuickTags.Select((tag,index)=>(tag,index)).Where(p=>p.tag.Length>0))
  {
   var quick=new Button {Content=KeyLabel("Ctrl+"+(slot.index+1))+" · "+slot.tag,Padding=new Thickness(6,3,6,3),FontSize=11,IsEnabled=items.Count>0,ToolTip=T("Assign to selected items")};
   quick.Click+=async(_,_)=>await ToggleQuickTagAsync(slot.index+1);QuickTagButtons.Children.Add(quick);
  }
 TagInput.IsEnabled = items.Count > 0; TagCompletionPopup.IsOpen = false;
  TagEditorHint.Text = items.Count > 1 ? Tn(items.Count, "for 1 item", "for {0:N0} items") : "";
  var ids = items.Select(i => i.Id).ToArray();
  var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); var order = new List<string>();
  foreach (var tag in items.SelectMany(i => i.Tags.Distinct(StringComparer.OrdinalIgnoreCase))) { if (!counts.ContainsKey(tag)) order.Add(tag); counts[tag] = counts.GetValueOrDefault(tag) + 1; }
  foreach (var tag in order.OrderByDescending(t => counts[t]).ThenBy(t => order.IndexOf(t)))
  {
   var full = counts[tag] == items.Count;
   var chip = new Button { Content = full ? tag + "  ×" : $"{tag} · {counts[tag]}/{items.Count}", Background = full ? FullChipBrush : PartialChipBrush, Padding = new Thickness(9, 4, 9, 4), FontSize = 12,
    ToolTip = full ? T("Remove tag") : T("On {0} of {1} items · click: assign to all · right-click: remove from all", counts[tag], items.Count) };
   chip.Click += async (_, _) => await (full ? ChangeTagsAsync(ids, [], [tag]) : ChangeTagsAsync(ids, [tag], []));
   if (!full) chip.MouseRightButtonUp += async (_, args) => { args.Handled = true; await ChangeTagsAsync(ids, [], [tag]); };
   TagChips.Children.Add(chip);
  }
  _tagAssistReady = LoadTagAssistAsync(items);
  UpdateRoleEditors(items);
 }

 private async Task LoadTagAssistAsync(IReadOnlyList<MediaItem> items)
 {
  var version = ++_tagAssistVersion;
  TagSuggestionChips.Children.Clear(); TagCombinationButtons.Children.Clear();
  TagSuggestionPanel.Visibility = TagCombinationPanel.Visibility = Visibility.Collapsed;
  if (items.Count == 0) return;
  var ids = items.Select(i => i.Id).ToArray(); var present = items.SelectMany(i => i.Tags).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
  var onAll = new HashSet<string>(items[0].Tags, StringComparer.OrdinalIgnoreCase); foreach (var item in items.Skip(1)) onAll.IntersectWith(item.Tags);
  (IReadOnlyList<TagSuggestion> Suggestions, IReadOnlyList<TagCombination> Combinations) assist;
  try { assist = await Task.Run(() => (_repository.SuggestTags(ids, 10), _repository.GetTagCombinations(present.Length > 0 ? present : null, 4))); }
  catch (ObjectDisposedException) { return; }
  if (version != _tagAssistVersion || _closing) return;
  foreach (var suggestion in assist.Suggestions)
  {
   var chip = new Button { Content = "+ " + suggestion.Tag, Background = SuggestionChipBrush, Padding = new Thickness(9, 4, 9, 4), FontSize = 12, ToolTip = suggestion.Reason };
   chip.Click += async (_, _) => await ChangeTagsAsync(ids, [suggestion.Tag], []);
   TagSuggestionChips.Children.Add(chip);
  }
  foreach (var combination in assist.Combinations)
  {
   var missing = combination.Tags.Where(t => !onAll.Contains(t)).ToArray(); if (missing.Length == 0) continue;
   var button = new Button { Content = string.Join(" · ", combination.Tags) + $"   {combination.MediaCount:N0}×", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 5, 10, 5), FontSize = 12,
    ToolTip = Tn(combination.MediaCount, "Shared by 1 item · click adds: {1}", "Shared by {0:N0} items · click adds: {1}", string.Join(", ", missing)) };
   button.Click += async (_, _) => await ChangeTagsAsync(ids, missing, []);
   TagCombinationButtons.Children.Add(button);
  }
  TagSuggestionPanel.Visibility = TagSuggestionChips.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
  TagCombinationPanel.Visibility = TagCombinationButtons.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
 }

 private void TagInputChanged(object sender, TextChangedEventArgs e)
 {
  var text = TagInput.Text.Trim();
  if (text.Length == 0) { TagCompletionPopup.IsOpen = false; return; }
  var items = SelectedMedia; var onAll = new HashSet<string>(items.FirstOrDefault()?.Tags ?? [], StringComparer.OrdinalIgnoreCase); foreach (var item in items.Skip(1)) onAll.IntersectWith(item.Tags);
  var aliasNames = _repository.GetPreferences().TagVocabulary.Where(v => (v.Aliases ?? []).Any(a => a.Contains(text, StringComparison.OrdinalIgnoreCase))).Select(v => v.Tag).ToHashSet(StringComparer.OrdinalIgnoreCase);
  var matches = _tagEntries.Where(t => !onAll.Contains(t.Tag) && (t.Tag.Contains(text, StringComparison.OrdinalIgnoreCase) || aliasNames.Contains(t.Tag)))
   .OrderByDescending(t => t.Tag.StartsWith(text, StringComparison.OrdinalIgnoreCase)).ThenByDescending(t => t.IsFavorite).ThenByDescending(t => t.Count).ThenBy(t => t.Tag, StringComparer.OrdinalIgnoreCase)
   .Take(8).Select(t => new TagRow(t.Tag, (t.IsFavorite ? "★ " : "") + t.Tag, t.Count > 0 ? t.Count.ToString("N0") : T("Vocabulary"), TagBrush)).ToArray();
  TagCompletions.ItemsSource = matches; TagCompletions.SelectedIndex = -1;
  TagCompletionPopup.IsOpen = matches.Length > 0 && TagInput.IsKeyboardFocused;
 }
 private async void TagInputKeyDown(object sender, KeyEventArgs e)
 {
  var open = TagCompletionPopup.IsOpen && TagCompletions.Items.Count > 0;
  if (e.Key == Key.Down && open) { TagCompletions.SelectedIndex = Math.Min(TagCompletions.SelectedIndex + 1, TagCompletions.Items.Count - 1); TagCompletions.ScrollIntoView(TagCompletions.SelectedItem); e.Handled = true; }
  else if (e.Key == Key.Up && open) { TagCompletions.SelectedIndex = Math.Max(TagCompletions.SelectedIndex - 1, 0); TagCompletions.ScrollIntoView(TagCompletions.SelectedItem); e.Handled = true; }
  else if (e.Key == Key.Tab && open) { TagInput.Text = ((TagCompletions.SelectedItem ?? TagCompletions.Items[0]) as TagRow)!.Tag; TagInput.CaretIndex = TagInput.Text.Length; e.Handled = true; }
  else if (e.Key == Key.Escape) { TagCompletionPopup.IsOpen = false; e.Handled = true; }
  else if (e.Key is Key.Enter or Key.OemComma) { e.Handled = true; await AddTagFromInputAsync((TagCompletions.SelectedItem as TagRow)?.Tag ?? TagInput.Text); }
 }
 private void TagInputLostFocus(object sender, KeyboardFocusChangedEventArgs e) { if (e.NewFocus is not DependencyObject target || !TagCompletions.IsAncestorOf(target)) TagCompletionPopup.IsOpen = false; }
 private async void TagCompletionClicked(object sender, MouseButtonEventArgs e)
 {
  if (ItemsControl.ContainerFromElement(TagCompletions, (DependencyObject)e.OriginalSource) is ListBoxItem { DataContext: TagRow row }) { e.Handled = true; await AddTagFromInputAsync(row.Tag); }
 }
 private async Task AddTagFromInputAsync(string text)
 {
  var tag = text.Trim().TrimEnd(',').Trim();
  TagInput.Text = ""; TagCompletionPopup.IsOpen = false; TagInput.Focus();
  if (tag.Length > 0 && !_tagEntries.Any(e => e.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase)))
  {
   var targets = _repository.GetPreferences().TagVocabulary.Where(v => (v.Aliases ?? []).Any(a => a.Equals(tag, StringComparison.OrdinalIgnoreCase))).Select(v => v.Tag).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
   if (targets.Length > 1) { Status.Text = T("Ambiguous alias; choose a tag from the suggestions."); return; }
   if (targets.Length == 1) tag = targets[0];
  }
  if (tag.Length > 0) await ChangeTagsAsync(SelectedMedia.Select(i => i.Id).ToArray(), [tag], []);
 }

 private void MediaListMouseDown(object sender, MouseButtonEventArgs e)
 {
  _dragStart = null; _pendingSingleSelection = null; _clickToPlay = null;
  if (ItemsControl.ContainerFromElement(MediaList, (DependencyObject)e.OriginalSource) is not ListBoxItem item) return;
  _dragStart = e.GetPosition(MediaList);
  // "Klick auf das Bild spielt ab": a plain click on the picture (not on its buttons) plays once the button is released without dragging.
  if (MediaCard.Display.ClickPlays && Keyboard.Modifiers == ModifierKeys.None && e.ClickCount <= 1 && item.DataContext is MediaCard card && OnPicture((DependencyObject)e.OriginalSource, item)) _clickToPlay = card;
  // Keep a multi-selection alive so it can be dragged; a plain click without drag still selects only this item.
  if (item.IsSelected && MediaList.SelectedItems.Count > 1 && Keyboard.Modifiers == ModifierKeys.None && e.ClickCount == 1) { _pendingSingleSelection = item; e.Handled = true; item.Focus(); }
 }
 private void MediaListMouseMove(object sender, MouseEventArgs e)
 {
  if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
  var offset = e.GetPosition(MediaList) - start;
  if (Math.Abs(offset.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(offset.Y) < SystemParameters.MinimumVerticalDragDistance) return;
  _dragStart = null; _pendingSingleSelection = null; _clickToPlay = null;
  var ids = SelectedMedia.Select(i => i.Id.ToString()).ToArray(); if (ids.Length == 0) return;
  DragDrop.DoDragDrop(MediaList, new DataObject(MediaDragFormat, ids), DragDropEffects.Copy);
 }
 private void MediaListMouseUp(object sender, MouseButtonEventArgs e)
 {
  if (_pendingSingleSelection is { } item) { MediaList.SelectedItems.Clear(); item.IsSelected = true; }
  var play = _dragStart != null ? _clickToPlay : null;
  _pendingSingleSelection = null; _dragStart = null; _clickToPlay = null;
  if (play != null) { StopSlideshow(); _ = Play(play.Item); }
 }
 private MediaCard? _clickToPlay;
 /// <summary>Whether the point lies on a tile's picture and not on one of its buttons.</summary>
 private static bool OnPicture(DependencyObject source, ListBoxItem item)
 {
  for (var node = source; node != null && node != item; node = VisualTreeHelper.GetParent(node))
  {
   if (node is ButtonBase) return false;
   if (node is AspectBox) return true;
  }
  return false;
 }
 private void TagItemDragOver(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent(MediaDragFormat) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
 private async void TagItemDrop(object sender, DragEventArgs e)
 {
  if ((sender as ListBoxItem)?.DataContext is not TagRow row || e.Data.GetData(MediaDragFormat) is not string[] ids) return;
  e.Handled = true; await ChangeTagsAsync(ids.Select(Guid.Parse).ToArray(), [row.Tag], []);
 }
}
