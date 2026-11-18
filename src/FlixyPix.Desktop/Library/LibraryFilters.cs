using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>The state of the view's filters: capturing and applying view states, the chips of active filters and the parts the
/// filter bar (LibraryFilterBar.cs) shares. Every change applies at once.</summary>
public partial class MainWindow
{
    private IReadOnlyList<ReviewState> _reviewStates { get => _browse.State.ReviewStates; set => _browse.Change(state => state with { ReviewStates = value.ToList() }); }
    private MediaFilter _filter { get => _browse.State.Filter; set => _browse.Change(state => state with { Filter = value }); }
    private bool _tagsMatchAny { get => _browse.State.TagsMatchAny; set => _browse.Change(state => state with { TagsMatchAny = value }); }
    private MediaKind? _kindFilter { get => _browse.State.KindFilter; set => _browse.Change(state => state with { KindFilter = value }); }

    /// <summary>What the main window shows now, as one state (saved searches, restore on start, back and forward).</summary>
    internal ViewState CaptureViewState() => _browse.Capture(new ViewState()
    {
        ImageSetId = IsImageSetView ? _selectedImageSet : null,
        PublicationGroupId = IsPublicationGroupView ? _selectedPublicationGroup : null,
        Directory = IsRoleView ? _roleBrowser?.CaptureDirectoryState() : null,
        ReviewStates = _reviewStates.ToList(),
        View = _view,
        KindFilter = _kindFilter,
        DetailId = _browse.State.DetailId,
        PlaylistId = _playlist?.Id,
        SearchText = SearchBox.Text.Length == 0 ? null : SearchBox.Text,
        Tag = _tag,
        TagCategory = _tagGroup,
        Category = _category,
        Folder = _folder,
        IncludeSubfolders = IncludeFolderChildren.IsChecked == true,
        RequiredTags = _requiredTags.ToList(),
        ExcludedTags = _excludedTags.ToList(),
        Expression = _advancedExpression,
        Filter = _filter,
        TagsMatchAny = _tagsMatchAny,
        MinimumStars = _filter.UnratedOnly ? 0 : Math.Max(RatingFilter.SelectedIndex, 0),
        SortOrder = Math.Max(SortChoice.SelectedIndex, 0),
        ViewMode = ViewChoice.SelectedIndex,
        TileWidth = TileWidthSetting,
        Position = Math.Max(0, _visibleFirst),
        AnchorId = _visibleFirst >= _windowStart && _visibleFirst - _windowStart < MediaList.Items.Count ? ((MediaCard)MediaList.Items[_visibleFirst - _windowStart]).Item.Id : null,
        Selection = SelectedMedia.Select(m => m.Id).ToList(),
    });

    /// <summary>Shows a state: view, filters, sort and display; the caller decides about position and selection.</summary>
    internal bool ApplyViewState(ViewState state, string? title = null)
    {
        if (!LeaveRoleView()) { UpdateNavigation(); return false; }
        // Another place: the loupe gives the tiles back without looking for its image among them.
        CloseLoupe(reselect: false);
        if (IsRoleView) RecordView();
        RememberCurrentArea();
        var before = _startupReady && Motion.Smooth ? CaptureViewState() : null;
        state = _browse.ApplyTransition(state);
        if (before != null && !SamePage(before, state)) { _pageChanging = true; Motion.LeavePage(MediaPane); }
        PresentViewState(state, title);
        Refresh();
        return true;
    }

    /// <summary>A new place moves as a page; another filter or sort order within a place only changes the tiles.</summary>
    private static bool SamePage(ViewState a, ViewState b) => a.View == b.View && a.PlaylistId == b.PlaylistId && a.DetailId == b.DetailId
        && a.ImageSetId == b.ImageSetId && a.PublicationGroupId == b.PublicationGroupId && a.Folder == b.Folder
        && (a.TagCategory != null ? a.Tag : null) == (b.TagCategory != null ? b.Tag : null);

    private void PresentViewState(ViewState state, string? title = null)
    {
        _pendingDirectoryState = state.Directory;
        var initializing = _initializingBrowsing; var refreshing = _refreshing; _initializingBrowsing = true; _refreshing = true;
        try
        {
            IncludeFolderChildren.IsChecked = state.Folder == null ? _repository.GetPreferences().Sidebar.IncludeSubfolders : state.IncludeSubfolders;
            _selectedImageSet = state.ImageSetId;
            _selectedPublicationGroup = state.PublicationGroupId;
            _reviewStates = state.ReviewStates.ToList();
            _view = state.View; _tag = state.Tag; _tagGroup = state.TagCategory; _category = state.Category; _folder = state.Folder;
            _playlist = state.PlaylistId is { } id ? _repository.GetPlaylists().FirstOrDefault(p => p.Id == id) : null;

            // With fewer than two tags the combination does not matter yet; it starts from the library's choice.
            _advancedExpression = state.Expression; _filter = state.Filter; _tagsMatchAny = state.RequiredTags.Count > 1 ? state.TagsMatchAny : _repository.GetPreferences().General.TagsMatchAny;
            SearchBox.Text = state.SearchText ?? ""; _searchTimer.Stop();
            RatingFilter.SelectedIndex = Math.Clamp(state.MinimumStars, 0, 5);
            if (state.SortOrder >= 0 && state.SortOrder < SortChoice.Items.Count) SortChoice.SelectedIndex = state.SortOrder;
            if (state.ViewMode is >= 0 and <= 2) ViewChoice.SelectedIndex = state.ViewMode;
            if (state.TileWidth > 0) SetTileWidth(state.TileWidth);
        }
        finally { _initializingBrowsing = initializing; _refreshing = refreshing; }
        ViewTitle.Text = title ?? (_playlist != null ? CollectionName(_playlist) : null) ?? (_requiredTags.Count > 0 ? string.Join(" + ", _requiredTags) : EntityRole != null ? _tag : _view == FolderView ? FolderTitle() : ViewName(_view));
        UpdateNavigation(); _anchor = 0;
        UpdateFilterBar(); RenderTagFilterChips(); RenderTagList();
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
        EmptyRelax.Children.Clear(); EmptyRelax.Visibility = Visibility.Collapsed;
        if (!catalogEmpty) _ = ShowRelaxSuggestionsAsync();
    }

    /// <summary>With no hits: which single filter, left out, brings back the most items. Counted off the UI thread.</summary>
    private Task ShowRelaxSuggestionsAsync()
    {
        if (_playlist != null || !ShowsMediaGrid) return Task.CompletedTask;
        var state = CaptureViewState();
        var atoms = FilterAtoms(state).Take(12).ToList();
        if (atoms.Count < 2) return Task.CompletedTask;
        return _libraryTasks.Run(async token =>
        {
            var now = DateTimeOffset.Now;
            var counted = await Task.Run(() => atoms.Select(a => (a.Label, a.Without, Count: _repository.CountMedia(a.Without.ToQuery(now), token))).Where(a => a.Count > 0).OrderByDescending(a => a.Count).Take(3).ToList(), token);
            if (_closing || FilterKey(CaptureViewState()) != FilterKey(state) || counted.Count == 0) return;
            EmptyRelax.Children.Clear();
            var intro = new TextBlock { Text = T("One filter less helps:"), Width = 560, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, 8) };
            intro.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            EmptyRelax.Children.Add(intro);
            foreach (var (label, without, count) in counted)
            {
                var button = new Button { Style = (Style)FindResource("FilterChip"), Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(4, 0, 4, 6),
                    Content = T("without “{0}” → {1:N0}", label, count) };
                var target = without with { Position = 0, AnchorId = null, Selection = [] };
                button.Click += (_, _) => ApplyViewState(target);
                EmptyRelax.Children.Add(button);
            }
            EmptyRelax.Visibility = Visibility.Visible;
        });
    }

    /// <summary>Each active filter of a state, with the state that leaves it out.</summary>
    private IEnumerable<(string Label, ViewState Without)> FilterAtoms(ViewState s)
    {
        var f = s.Filter;
        if (!string.IsNullOrWhiteSpace(s.SearchText)) yield return (s.SearchText!, s with { SearchText = null });
        foreach (var tag in s.RequiredTags) yield return (tag, s with { RequiredTags = s.RequiredTags.Where(t => t != tag).ToList() });
        foreach (var tag in s.ExcludedTags) yield return (T("without {0}", tag), s with { ExcludedTags = s.ExcludedTags.Where(t => t != tag).ToList() });
        foreach (var facet in f.Include) yield return (facet.Tag, s with { Filter = f with { Include = f.Include.Where(x => x != facet).ToList() } });
        foreach (var facet in f.Exclude) yield return (T("without {0}", facet.Tag), s with { Filter = f with { Exclude = f.Exclude.Where(x => x != facet).ToList() } });
        if (s.MinimumStars > 0) yield return (RatingLabel(Math.Clamp(s.MinimumStars, 0, 5)), s with { MinimumStars = 0 });
        if (s.Category != null) yield return (s.Category, s with { Category = null });
        if (s.Folder != null && s.View != FolderView) yield return (T("Folder: {0}", System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(s.Folder))), s with { Folder = null });
        if (s.View == FolderView && s.KindFilter is { } kind) yield return (ViewName(kind.ToString()), s with { KindFilter = null });
        foreach (var review in s.ReviewStates) yield return (ReviewLabel(review), s with { ReviewStates = s.ReviewStates.Where(r => r != review).ToList() });
        if (f.UnratedOnly) yield return (T("Unrated"), s with { Filter = f with { UnratedOnly = false } });
        if (f.UntaggedOnly) yield return (T("Without tags"), s with { Filter = f with { UntaggedOnly = false } });
        if (f.MinMinutes is not null || f.MaxMinutes is not null) yield return (MinutesLabel(f.MinMinutes, f.MaxMinutes), s with { Filter = f with { MinMinutes = null, MaxMinutes = null } });
        if (f.Resolution != ResolutionClass.Any) yield return (new MediaFilter { Resolution = f.Resolution }.Describe().First(), s with { Filter = f with { Resolution = ResolutionClass.Any } });
        if (f.MinMegabytes is not null || f.MaxMegabytes is not null) yield return (RangeLabel(f.MinMegabytes, f.MaxMegabytes, "MB"), s with { Filter = f with { MinMegabytes = null, MaxMegabytes = null } });
        if (f.FileDate.IsActive) yield return (T("File ") + f.FileDate.Describe(), s with { Filter = f with { FileDate = new() } });
        if (f.Imported.IsActive) yield return (T("imported ") + f.Imported.Describe(), s with { Filter = f with { Imported = new() } });
        if (f.Released.IsActive) yield return (T("released {0}", f.Released.Describe()), s with { Filter = f with { Released = new() } });
        if (f.Watched is { } watched) yield return (watched ? T("watched") : T("never watched"), s with { Filter = f with { Watched = null } });
        if (f.HasThumbnail is { } thumb) yield return (thumb ? T("With thumbnail") : T("Without thumbnail"), s with { Filter = f with { HasThumbnail = null } });
        if (f.VideoCodecs.Count > 0) yield return (string.Join(", ", f.VideoCodecs.Select(TechnicalLabels.Codec)), s with { Filter = f with { VideoCodecs = [] } });
        if (f.Hdr is { } hdr) yield return (hdr ? "HDR" : T("without HDR"), s with { Filter = f with { Hdr = null } });
        if (f.MinFrameRate is { } rate) yield return (T("from {0} fps", rate.ToString("0.##", CultureInfo.CurrentCulture)), s with { Filter = f with { MinFrameRate = null } });
        if (f.MultipleFiles is { } multiple) yield return (multiple ? T("several versions") : T("one file"), s with { Filter = f with { MultipleFiles = null } });
        foreach (var mark in f.MarkTags) yield return (T("Jump mark: {0}", mark), s with { Filter = f with { MarkTags = f.MarkTags.Where(m => m != mark).ToList() } });
        foreach (var profile in f.Profiles) yield return (MediaFilter.DescribeProfile(profile), s with { Filter = f with { Profiles = f.Profiles.Where(p => p != profile).ToList() } });
    }

    private void ResetFiltersClicked(object sender, RoutedEventArgs e) =>
     ApplyViewState(CaptureViewState() with { SearchText = null, ReviewStates = [], Tag = null, TagCategory = null, Category = null, Folder = _view == FolderView ? _folder : null, KindFilter = null, RequiredTags = [], ExcludedTags = [], Expression = null, Filter = new(), MinimumStars = 0 });

    private void ApplyFilter(MediaFilter filter)
    {
        _filter = filter; _anchor = 0;
        UpdateFilterBar(); RenderTagFilterChips(); Refresh();
    }

    private string FilterDescription() => _filter.IsEmpty ? "" : " · " + string.Join(" · ", _filter.Describe());

    /// <summary>"Ansicht ▾": appearance, tile size and sorting, aligned to the right edge of its button.</summary>
    private void ViewMenuClicked(object sender, RoutedEventArgs e)
    {
        if (ViewPopup.IsOpen) { ViewPopup.IsOpen = false; return; }
        ViewPopupFrame.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        ViewPopup.HorizontalOffset = ViewButton.ActualWidth - ViewPopupFrame.DesiredSize.Width + 12;
        ViewPopup.IsOpen = true;
    }

    private string RatingLabel(int index) => (RatingFilter.Items[index] as ComboBoxItem)?.Content as string ?? "";

    /// <summary>Switches the media type and keeps search and filters, unlike a click in the sidebar.</summary>
    private void ShowKind(string view)
    {
        if (_view == FolderView) { _kindFilter = Enum.TryParse<MediaKind>(view, out var kind) ? kind : null; _anchor = 0; RenderTagFilterChips(); Refresh(); return; }
        if (_view == view) return;
        var wasTitle = ViewTitle.Text == ViewName(_view);
        _view = view; _anchor = 0;
        if (wasTitle) ViewTitle.Text = ViewName(view);
        UpdateNavigation(); SaveViewPreferences(); Refresh();
    }

    private static TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };

    /// <summary>0 off, 1 included, -1 excluded.</summary>
    private int State(DirectoryEntry entry)
    {
        if (entry.Role == TagRole.Tag)
            return _requiredTags.Contains(entry.Tag, StringComparer.OrdinalIgnoreCase) ? 1 : _excludedTags.Contains(entry.Tag, StringComparer.OrdinalIgnoreCase) ? -1 : 0;
        var facet = Facet(entry);
        return _filter.Include.Contains(facet) ? 1 : _filter.Exclude.Contains(facet) ? -1 : 0;
    }

    private static TagFacet Facet(DirectoryEntry entry) => new(entry.Category, entry.Tag, entry.Role);

    internal void CycleFacet(DirectoryEntry entry, TagRole role)
    {
        var next = State(entry) switch { 0 => 1, 1 => -1, _ => 0 };
        if (role == TagRole.Tag)
        {
            _browse.SetTagMode(entry.Tag, next);
            _anchor = 0; UpdateFilterBar(); ApplyTagFilterChanges(); return;
        }
        var facet = Facet(entry);
        var include = _filter.Include.Where(f => f != facet).ToList(); var exclude = _filter.Exclude.Where(f => f != facet).ToList();
        if (next == 1) include.Add(facet); else if (next == -1) exclude.Add(facet);
        ApplyFilter(_filter with { Include = include, Exclude = exclude });
    }

    /// <summary>Two number fields "von" and "bis"; Enter or leaving a field applies, an empty field means no limit.</summary>
    private static FrameworkElement RangeFields(string unit, double? min, double? max, Action<double?, double?> apply, string uid = "range")
    {
        var from = new TextBox { Text = min?.ToString("0.##", CultureInfo.CurrentCulture) ?? "", Width = 64, Padding = new Thickness(6, 4, 6, 4), Uid = uid + ":from" };
        var to = new TextBox { Text = max?.ToString("0.##", CultureInfo.CurrentCulture) ?? "", Width = 64, Padding = new Thickness(6, 4, 6, 4), Uid = uid + ":to" };
        System.Windows.Automation.AutomationProperties.SetName(from, T("{0} from", unit)); System.Windows.Automation.AutomationProperties.SetName(to, T("{0} to", unit));
        static double? Parse(string text) => double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var value) && value >= 0 ? value : null;
        var last = (min, max);
        void Commit() { var next = (Parse(from.Text), Parse(to.Text)); if (next == last) return; last = next; apply(next.Item1, next.Item2); }
        foreach (var box in new[] { from, to }) { box.LostKeyboardFocus += (_, _) => Commit(); box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); }; }
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        row.Children.Add(new TextBlock { Text = T("of"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) }); row.Children.Add(from);
        row.Children.Add(new TextBlock { Text = T("to"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0) }); row.Children.Add(to);
        row.Children.Add(new TextBlock { Text = unit, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Foreground = SettingsUi.Muted });
        return row;
    }

    private enum DateKind { File, Imported, Released }

    /// <summary>Chips for the active filter bar parts, each removing itself; used by the tag chip row.</summary>
    private IEnumerable<(string Label, bool Excluded, Action Remove)> FilterChips()
    {
        if (_filter.UnratedOnly) yield return (T("Unrated"), false, () => ApplyFilter(_filter with { UnratedOnly = false }));
        if (_filter.UntaggedOnly) yield return (T("Without tags"), false, () => ApplyFilter(_filter with { UntaggedOnly = false }));
        if (_filter.HasThumbnail is { } has) yield return (has ? T("With thumbnail") : T("Without thumbnail"), false, () => ApplyFilter(_filter with { HasThumbnail = null }));
        foreach (var facet in _filter.Include.ToList()) yield return (facet.Tag, false, () => ApplyFilter(_filter with { Include = _filter.Include.Where(f => f != facet).ToList() }));
        foreach (var facet in _filter.Exclude.ToList()) yield return (T("without {0}", facet.Tag), true, () => ApplyFilter(_filter with { Exclude = _filter.Exclude.Where(f => f != facet).ToList() }));
        foreach (var profile in _filter.Profiles.ToList()) yield return (MediaFilter.DescribeProfile(profile), false, () => ApplyFilter(_filter with { Profiles = _filter.Profiles.Where(p => p != profile).ToList() }));
        foreach (var tag in _filter.MarkTags.ToList()) yield return (T("Jump mark: {0}", tag), false, () => ApplyFilter(_filter with { MarkTags = _filter.MarkTags.Where(t => t != tag).ToList() }));
        var rest = _filter with { UnratedOnly = false, UntaggedOnly = false, HasThumbnail = null, Include = [], Exclude = [], Profiles = [], MarkTags = [] };
        if (rest.MinMinutes is not null || rest.MaxMinutes is not null) yield return (rest.Describe().First(), false, () => ApplyFilter(_filter with { MinMinutes = null, MaxMinutes = null }));
        var size = _filter with
        {
            UnratedOnly = false,
            UntaggedOnly = false,
            HasThumbnail = null,
            Include = [],
            Exclude = [],
            Profiles = [],
            MarkTags = [],
            MinMinutes = null,
            MaxMinutes = null,
            Resolution = ResolutionClass.Any,
            FileDate = new(),
            Imported = new(),
            Released = new(),
            Watched = null,
            VideoCodecs = [],
            Hdr = null,
            MinFrameRate = null,
            MultipleFiles = null
        };
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
