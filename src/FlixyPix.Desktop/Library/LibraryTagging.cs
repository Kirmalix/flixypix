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
    private IReadOnlyList<string> _requiredTags => _browse.State.RequiredTags;
    private IReadOnlyList<string> _excludedTags => _browse.State.ExcludedTags;
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
    private static Brush FullChipBrush => Theme.Brush("ChipApplied");
    private static Brush PartialChipBrush => Theme.Brush("ChipPartial");
    private static Brush ExcludedChipBrush => Theme.Brush("ChipExcluded");
    private static Brush SuggestionChipBrush => Theme.Brush("ChipSuggestion");

    internal sealed record TagListEntry(string Tag, int Count, bool IsFavorite);
    private sealed record TagRow(string Tag, string Label, string CountLabel, Brush Foreground);

    private string? CurrentTag => _tag ?? _requiredTags.LastOrDefault();
    private IReadOnlyList<MediaItem> SelectedMedia => MediaList.SelectedItems.Cast<MediaCard>().Select(card => card.Item).Concat(_offWindowSelection).DistinctBy(item => item.Id).ToArray();

    private static TagListEntry[] TagEntries(IReadOnlyList<TagUsage> usage, CatalogPreferences preferences)
    {
        var favorites = new HashSet<string>(preferences.TagVocabulary.Where(t => t.IsFavorite).Select(t => t.Tag), StringComparer.OrdinalIgnoreCase);
        var counts = usage.ToDictionary(u => u.Tag, u => u.MediaCount, StringComparer.OrdinalIgnoreCase);
        return usage.Select(u => u.Tag).Concat(preferences.TagVocabulary.Select(t => t.Tag.Trim())).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
         .Select(tag => new TagListEntry(tag, counts.GetValueOrDefault(tag), favorites.Contains(tag))).ToArray();
    }

    /// <summary>The pinned tags of the tag mode (1–9): favourite tags, then the most used ones, each group alphabetical so the numbers do not
    /// move when a tag is used.</summary>
    internal IReadOnlyList<string> QuickTags
    {
        get
        {
            var preferences = _repository.GetPreferences();
            if (preferences.QuickTags is null)
            {
                var initial = _tagEntries.OrderByDescending(e => e.IsFavorite).ThenByDescending(e => e.Count).ThenBy(e => e.Tag, StringComparer.OrdinalIgnoreCase).Where(e => e.IsFavorite || e.Count > 0).Take(9).OrderByDescending(e => e.IsFavorite).ThenBy(e => e.Tag, StringComparer.OrdinalIgnoreCase).Select(e => (string?)e.Tag).ToList();
                if (_tagEntries.Count == 0) return [];
                while (initial.Count < 9) initial.Add(null);
                _repository.SavePreferences(preferences with { QuickTags = initial }); preferences = _repository.GetPreferences();
            }
            return preferences.QuickTags!.Select(t => t ?? "").ToArray();
        }
    }

    /// <summary>The n-th pinned tag is added to the selection, or removed when every selected medium has it.</summary>
    internal async Task ToggleQuickTagAsync(int number)
    {
        var tags = QuickTags; var items = SelectedMedia;
        if (number < 1 || number > tags.Count || string.IsNullOrEmpty(tags[number - 1])) { Status.Text = T("Pin {0} is free · right-click a tag to pin it", number); return; }
        if (items.Count == 0) { Status.Text = T("Please select items first."); return; }
        var tag = tags[number - 1]; var ids = items.Select(i => i.Id).ToArray();
        if (items.All(i => i.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))) await ChangeTagsAsync(ids, [], [tag]); else await ChangeTagsAsync(ids, [tag], []);
    }

    private void RenderTagList()
    {
        var filter = TagFilterBox.Text.Trim();
        bool Active(string tag) => _requiredTags.Contains(tag, StringComparer.OrdinalIgnoreCase) || _excludedTags.Contains(tag, StringComparer.OrdinalIgnoreCase);
        bool OnTop(string tag) => _tagRowsOnTop?.Contains(tag) ?? Active(tag);
        var entries = _tagEntries.Where(e => (ShowUnusedTags.IsChecked == true || e.Count > 0 || Active(e.Tag)) && (filter.Length == 0 || e.Tag.Contains(filter, StringComparison.OrdinalIgnoreCase)));
        var ordered = entries.OrderByDescending(e => OnTop(e.Tag)).ThenByDescending(e => e.IsFavorite);
        ordered = _tagsByName ? ordered.ThenBy(e => e.Tag, StringComparer.OrdinalIgnoreCase) : ordered.ThenByDescending(e => e.Count).ThenBy(e => e.Tag, StringComparer.OrdinalIgnoreCase);
        Tags.ItemsSource = ordered.Select(e =>
        {
            var included = _requiredTags.Contains(e.Tag, StringComparer.OrdinalIgnoreCase); var excluded = _excludedTags.Contains(e.Tag, StringComparer.OrdinalIgnoreCase);
            var label = (included ? "✓ " : excluded ? "⊘ " : "") + (e.IsFavorite ? "★ " : "") + e.Tag;
            return new TagRow(e.Tag, label, e.Count > 0 ? e.Count.ToString("N0") : "–", included ? IncludedTagBrush : excluded ? ExcludedTagBrush : e.Count == 0 ? UnusedTagBrush : TagBrush);
        }).ToArray();
    }
    private void TagFilterChanged(object sender, TextChangedEventArgs e) { if (_repository != null) RenderTagList(); }
    private void TagListOptionsChanged(object sender, RoutedEventArgs e) { if (_repository != null) { SaveSidebar(s => s with { ShowUnusedTags = ShowUnusedTags.IsChecked == true }); RenderTagList(); } }
    private void TagSortClicked(object sender, RoutedEventArgs e) { _tagsByName = !_tagsByName; SaveSidebar(s => s with { TagsByName = _tagsByName }); TagSortButton.Content = TagSortLabel(); RenderTagList(); }

    private void TagItemClicked(object sender, MouseButtonEventArgs e)
    {
        if ((sender as ListBoxItem)?.DataContext is not TagRow row) return;
        e.Handled = true;
        // The row stays under the pointer, so a second click hits the same tag; the list sorts again once the pointer leaves it.
        _tagRowsOnTop ??= new HashSet<string>(_requiredTags.Concat(_excludedTags), StringComparer.OrdinalIgnoreCase);
        ToggleTagFilter(row.Tag, (Keyboard.Modifiers & ModifierKeys.Control) != 0);
    }
    private HashSet<string>? _tagRowsOnTop;
    private void TagListLeft(object sender, MouseEventArgs e) { if (_tagRowsOnTop == null) return; _tagRowsOnTop = null; RenderTagList(); }
    private void TagItemRightClicked(object sender, MouseButtonEventArgs e)
    {
        if ((sender as ListBoxItem)?.DataContext is not TagRow row) return;
        e.Handled = true; OpenTagMenu(row, sender as UIElement);
    }
    private void OpenTagMenu(TagRow row, UIElement? target)
    {
        var tag = row.Tag; var ids = SelectedMedia.Select(i => i.Id).ToArray();
        var actions = new List<MenuSpec> { MenuSpec.Command(T("Filter (all chosen tags)"), () => ToggleTagFilter(tag, false)), MenuSpec.Command(T("Exclude"), () => ToggleTagFilter(tag, true)) };
        if (ids.Length > 0) { actions.Add(MenuSpec.Command(T("Assign to selected items ({0})", ids.Length), () => _ = ChangeTagsAsync(ids, [tag], []))); actions.Add(MenuSpec.Command(T("Remove from selected items ({0})", ids.Length), () => _ = ChangeTagsAsync(ids, [], [tag]))); }
        actions.Add(MenuSpec.Separator); actions.Add(MenuSpec.Submenu(T("Search in browser"), Specs(WebSearchEntries(tag)), "\uE774"));
        actions.Add(MenuSpec.Separator); actions.Add(MenuSpec.Command(T("Rename / merge …"), () => RenameGlobalTag(tag))); actions.Add(MenuSpec.Command(T("Remove from all items …"), () => DeleteGlobalTag(tag)));
        actions.Add(MenuSpec.Submenu(T("Pin at"), Enumerable.Range(1, 9).Select(n => MenuSpec.Command(n + " · " + (QuickTags.ElementAtOrDefault(n - 1) ?? ""), () => AssignQuickTag(n, tag)))));
        if (QuickTags.Contains(tag, StringComparer.OrdinalIgnoreCase)) actions.Add(MenuSpec.Command(T("Unpin"), () => AssignQuickTag(0, tag)));
        Menus.Open(target, actions, MenuFailed);
    }

    private void ToggleTagFilter(string tag, bool exclude)
    {
        _browse.ToggleTag(tag, exclude);
        _playlist = null; _anchor = 0; _advancedExpression = null;
        ApplyTagFilterChanges();
    }
    private void ClearTagFilters() { _browse.ClearTags(); _tagsMatchAny = _repository.GetPreferences().General.TagsMatchAny; RenderTagFilterChips(); }
    private void ApplyTagFilterChanges()
    {
        if (_requiredTags.Count > 0) ViewTitle.Text = string.Join(" + ", _requiredTags);
        else if (_view == FolderView && _tag == null) ViewTitle.Text = FolderTitle();
        else if (_view == "all" && _tag == null && _folder == null) ViewTitle.Text = T("All items");
        RenderTagFilterChips(); RenderTagList(); UpdateFilterBar(); Refresh();
    }
    private void RenderTagFilterChips()
    {
        ActiveTagFilters.Children.Clear();
        var folderIsFilter = _folder != null && _view != FolderView; var kindChip = _view == FolderView ? _kindFilter : null;
        var active = (RatingFilter.SelectedIndex > 0 ? 1 : 0) + (_category != null ? 1 : 0) + (folderIsFilter ? 1 : 0) + (kindChip != null ? 1 : 0) + _requiredTags.Count + _excludedTags.Count + FilterChips().Count();
        FilterButtonText.Text = active > 0 ? T("Filter · {0:N0}", active) : T("Filter");
        FilterButton.SetResourceReference(BackgroundProperty, active > 0 ? "ChipFull" : "ButtonBackground");
        Button Chip(string text, Brush background, string tooltip, Action remove) { var chip = new Button { Content = text, Background = background, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 3, 4, 3), FontSize = 12, ToolTip = tooltip }; chip.Click += (_, _) => remove(); return chip; }
        if (RatingFilter.SelectedIndex > 0) ActiveTagFilters.Children.Add(Chip(RatingLabel(RatingFilter.SelectedIndex) + "  ×", FullChipBrush, T("Remove filter"), () => RatingFilter.SelectedIndex = 0));
        if (kindChip is { } kind) ActiveTagFilters.Children.Add(Chip(ViewName(kind.ToString()) + "  ×", FullChipBrush, T("Remove filter"), () => { _kindFilter = null; _anchor = 0; ApplyTagFilterChanges(); }));
        if (folderIsFilter && _folder is { } folder) ActiveTagFilters.Children.Add(Chip(T("Folder: {0}", System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(folder)) is { Length: > 0 } folderName ? folderName : folder) + "  ×", FullChipBrush, T("Remove folder filter"), ClearFolderFilter));
        if (_category != null) ActiveTagFilters.Children.Add(Chip(_category + "  ×", FullChipBrush, T("Remove filter"), () => CategoryFilter.SelectedIndex = 0));
        foreach (var tag in _requiredTags.ToArray()) ActiveTagFilters.Children.Add(Chip(tag + "  ×", FullChipBrush, T("Remove tag filter"), () => { _browse.RemoveIncludedTag(tag); _anchor = 0; ApplyTagFilterChanges(); }));
        foreach (var tag in _excludedTags.ToArray()) ActiveTagFilters.Children.Add(Chip(T("without {0}", tag) + "  ×", ExcludedChipBrush, T("Remove exclusion"), () => { _browse.RemoveExcludedTag(tag); _anchor = 0; ApplyTagFilterChanges(); }));
        foreach (var (label, excluded, remove) in FilterChips().ToList()) ActiveTagFilters.Children.Add(Chip(label + "  ×", excluded ? ExcludedChipBrush : FullChipBrush, T("Remove filter"), remove));
        if (_requiredTags.Count > 1 && _tagsMatchAny) ActiveTagFilters.Children.Insert(0, new TextBlock { Text = T("at least one:"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 6), Foreground = Theme.Brush("Muted"), FontSize = 12 });
        if (active > 1)
            ActiveTagFilters.Children.Add(Chip(T("Remove all filters"), Brushes.Transparent, T("Remove all tag and filter-bar filters"), () => { _browse.ClearTags(); _filter = new(); _kindFilter = null; UpdateFilterBar(); ClearFolderFilter(); }));
        if (FilterPanelShown && FilterMoreTab.Visibility == Visibility.Visible) RenderFilterMore();
    }
    /// <summary>Ends the folder filter; the view shows its whole content again under its own name.</summary>
    private void ClearFolderFilter()
    {
        if (_view != FolderView) _folder = null;
        _anchor = 0;
        if (_tag == null && _playlist == null) ViewTitle.Text = _view == FolderView ? FolderTitle() : ViewName(_view);
        ApplyTagFilterChanges();
    }
    private string TagFilterDescription() =>
     (_tag != null && EntityRole == null ? T(" · Tag: {0}", _tag) : "") + (_requiredTags.Count > 0 ? T(" · Tags: {0}", string.Join(" + ", _requiredTags)) : "") + (_excludedTags.Count > 0 ? T(" · without {0}", string.Join(", ", _excludedTags)) : "");

    /// <summary>Saved searches keep the sidebar tag filters as an explicit expression.</summary>
    private SearchExpression? WithTagFilters(SearchExpression? expression)
    {
        static SearchExpression Has(string tag) => SearchExpression.Where(new() { Field = SearchField.Tag, Comparison = SearchComparison.Equals, Text = tag });
        var parts = new List<SearchExpression>(); if (expression != null) parts.Add(expression);
        parts.AddRange(_requiredTags.Select(Has)); parts.AddRange(_excludedTags.Select(t => SearchExpression.Not(Has(t))));
        return parts.Count switch { 0 => null, 1 => parts[0], _ => SearchExpression.All(parts.ToArray()) };
    }

    private Task ChangeTagsAsync(IReadOnlyCollection<Guid> ids, IReadOnlyCollection<string> add, IReadOnlyCollection<string> remove)
     => _libraryTasks.Run(async token =>
    {
        if (ids.Count == 0 || add.Count + remove.Count == 0) return;
        if (add.Count > 0) { var tagged = ids.ToArray(); await Task.Run(() => _repository.NoteTagged(tagged), token); }
        _assistEngaged = true;
        try
        {
            var capturedIds = ids.ToArray(); var capturedAdd = add.ToArray(); var capturedRemove = remove.ToArray();
            var undo = await Task.Run(() => _repository.UpdateTagsWithUndo(capturedIds, capturedAdd, capturedRemove), token);
            if (_closing) return;
            var changed = undo.Rows.Count;
            Status.Text = (add.Count > 0 ? T("“{0}” assigned", string.Join(T("”, “"), add)) : T("“{0}” removed", string.Join(T("”, “"), remove))) + Tn(changed, " · 1 item changed", " · {0:N0} items changed");
            RememberImmediateUndo(undo, Status.Text);
            if (ViewDependsOnTags) await RefreshAsync(); else await RefreshTagsInPlaceAsync(ids);
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception ex) { if (!_closing) Message.Show(this, ex.Message, "Tags", MessageBoxButton.OK, MessageBoxImage.Warning); }
    });

    /// <summary>Whether a tag edit can change which media the view shows (tag filters, text search over tags, saved expressions).</summary>
    private bool ViewDependsOnTags => _tag != null || _tagGroup != null || _requiredTags.Count + _excludedTags.Count > 0 || _advancedExpression != null || SearchBox.Text.Trim().Length > 0
     || _filter.UntaggedOnly || _filter.Include.Count + _filter.Exclude.Count + _filter.Profiles.Count > 0;

    /// <summary>After a tag edit in a view that does not filter by tags: tiles show no tags, so only the edited media, the tag
    /// list and the tag editor change. The loaded window, its thumbnails and the scroll position stay.</summary>
    private Task RefreshTagsInPlaceAsync(IReadOnlyCollection<Guid> ids)
     => _libraryTasks.Run(async token =>
    {
        var cards = (MediaList.ItemsSource as IEnumerable<MediaCard> ?? []).Where(c => ids.Contains(c.Item.Id)).ToList();
        var request = _browse.CaptureRequest(SelectedMedia.Select(i => i.Id));
        var offWindow = _offWindowSelection.ToArray();
        var snapshot = await Task.Run(() => (
      Items: cards.Select(c => _repository.GetMedia(c.Item.Id)).ToList(),
      Browse: PrepareBrowsing(_repository.GetFacets(token), _repository.GetPreferences(), _repository.GetTagUsage(), _repository.GetNavigationCounts()),
      OffWindow: offWindow.Select(i => ids.Contains(i.Id) ? _repository.GetMedia(i.Id) ?? i : i).ToList()), token);
        if (_closing || token.IsCancellationRequested) return;
        _browse.TryPublishSelection(request.Complete(snapshot), SelectedMedia.Select(i => i.Id), result =>
     {
         var (items, browse, updatedOffWindow) = result;
         for (var i = 0; i < cards.Count; i++) if (items[i] is { } item) cards[i].Replace(item);
         _offWindowSelection = updatedOffWindow;
         _refreshing = true; try { ApplyBrowsing(browse); } finally { _refreshing = false; }
         UpdateTagEditor(SelectedMedia);
     });
    });

    private void UpdateTagEditor(IReadOnlyList<MediaItem> items)
    {
        TagChips.Children.Clear();
        RenderPinnedTags(items);
        TagInput.IsEnabled = items.Count > 0; TagCompletionPopup.IsOpen = false;
        TagEditorHint.Text = items.Count > 1 ? Tn(items.Count, "for 1 item", "for {0:N0} items") : "";
        var ids = items.Select(i => i.Id).ToArray();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in items.SelectMany(i => i.Tags.Distinct(StringComparer.OrdinalIgnoreCase))) counts[tag] = counts.GetValueOrDefault(tag) + 1;
        // Tags on every medium first, each group alphabetical, so a tag is found at the same place on every medium.
        foreach (var tag in counts.Keys.OrderByDescending(t => counts[t]).ThenBy(t => t, StringComparer.CurrentCultureIgnoreCase))
        {
            if (counts[tag] == items.Count) { TagChips.Children.Add(FullTagChip(tag, ids)); continue; }
            var chip = new Button
            {
                Content = $"{tag} · {counts[tag]}/{items.Count}",
                Background = PartialChipBrush,
                Padding = new Thickness(9, 4, 9, 4),
                FontSize = 12,
                Focusable = false,
                ToolTip = T("On {0} of {1} items · click: assign to all · right-click: remove from all", counts[tag], items.Count)
            };
            chip.Click += async (_, _) => await ChangeTagsAsync(ids, [tag], []);
            chip.MouseRightButtonUp += async (_, args) => { args.Handled = true; await ChangeTagsAsync(ids, [], [tag]); };
            TagChips.Children.Add(chip);
        }
        _tagAssistReady = LoadTagAssistAsync(items);
        UpdateRoleEditors(items);
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
        if (_cardContext.Display.ClickPlays && Keyboard.Modifiers == ModifierKeys.None && e.ClickCount <= 1 && item.DataContext is MediaCard card && OnPicture((DependencyObject)e.OriginalSource, item)) _clickToPlay = card;
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
