using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>The filter bar above the tiles: one button per section (rating, performers, studios, one per tag category, time,
/// file and quality, status, …), each opening a small window with the section. Small sets of values are chips, large ones a
/// list with search and visible "+" and "−". Sections the library has nothing for stay away; the most used sections lead and
/// the rest wait under "More". Counts are counted within the current hits, so a value that would leave nothing shows as such.
/// Every change applies at once.</summary>
public partial class MainWindow
{
    private sealed record FilterSection(string Key, string Glyph, string Label, int Rank, Func<string> Summary, Func<int> Active, Func<FrameworkElement> Build);

    /// <summary>One value of a tag, performer or studio section.</summary>
    private sealed record FacetValue(string Name, int Count, int State, string? Detail, IReadOnlyList<string> Aliases, DirectoryEntry? Entry);

    private const int VisibleSections = 7, ShortList = 8, LongList = 300;
    private readonly Dictionary<string, (Button Button, TextBlock Text)> _pills = new(StringComparer.Ordinal);
    /// <summary>The bar's section buttons by their label, as they read now.</summary>
    internal IReadOnlyDictionary<string, (Button Button, TextBlock Text)> FilterPills => _pills;
    /// <summary>What a section button says: its label and, while it filters, ": " and the summary.</summary>
    internal string PillText(string label) => _pills.TryGetValue(label, out var pill) ? AutomationProperties.GetName(pill.Button) : "";
    private readonly Dictionary<string, (Button Button, TextBlock Text)> _sectionButtons = new(StringComparer.Ordinal);
    private List<FilterSection> _sections = [];
    private FilterSection? _moreSection;
    private List<string> _barOrder = [];
    private HashSet<string> _barShown = new(StringComparer.Ordinal);
    private Popup? _filterPopup;
    private string? _filterSection;
    private bool _sectionRefreshPending;
    private DateKind _dateKind = DateKind.File;
    private bool _technicalOpen;
    private readonly Dictionary<string, string> _sectionFind = new(StringComparer.Ordinal);
    private readonly HashSet<string> _sectionExpanded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _sectionPinned = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, int>? _hitCounts;
    private string? _hitCountsKey;

    // ----- Bar -----

    /// <summary>Builds the bar anew: which sections exist, their order and labels. Buttons are kept, so an open section stays
    /// under its button.</summary>
    private void UpdateFilterBar()
    {
        if (_repository == null) return;
        _sections = FilterSections();
        var usage = _settings.FilterUsage;
        // Sections used more lead; the steps are coarse, so the bar does not reshuffle after every click. While a section is
        // open the order stays as it is, so the window does not move away from its button; it is sorted again on closing.
        var ordered = _sections.OrderByDescending(s => Math.Floor(usage.GetValueOrDefault(s.Key) / 2)).ThenBy(s => s.Rank).ToList();
        if (FilterFlyout.IsOpen && _barOrder.Count > 0)
            ordered = ordered.OrderBy(s => _barOrder.IndexOf(s.Key) is >= 0 and var at ? at : int.MaxValue).ToList();
        var shown = ordered.Where((s, index) => index < VisibleSections || s.Active() > 0 || FilterFlyout.IsOpen && _barShown.Contains(s.Key)).ToList();
        _barOrder = ordered.Select(s => s.Key).ToList(); _barShown = shown.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        var hidden = ordered.Except(shown).ToList();
        // A button of the bar keeps the keyboard focus across the rebuild; the buttons themselves are kept.
        var focused = FilterStrip.IsKeyboardFocusWithin ? Keyboard.FocusedElement as UIElement : null;
        FilterStrip.Children.Clear(); _pills.Clear();
        foreach (var section in shown) { var pill = SectionButton(section); FilterStrip.Children.Add(pill.Button); _pills[section.Label] = pill; }
        if (hidden.Count > 0)
        {
            _moreSection = new("more", "\uE712", T("More"), 99, () => "", () => 0, () => MoreSections(hidden));
            var more = SectionButton(_moreSection);
            FilterStrip.Children.Add(more.Button);
        }
        foreach (var quick in QuickChips()) FilterStrip.Children.Add(quick);
        if (ActiveFilterCount() > 0)
        {
            var reset = new Button { Content = T("Remove all"), Style = (Style)FindResource("Quiet"), FontSize = 12, Margin = new Thickness(4, 0, 0, 6), VerticalAlignment = VerticalAlignment.Center, ToolTip = T("Remove all filters") };
            reset.SetResourceReference(ForegroundProperty, "Accent");
            reset.Click += ResetFiltersClicked;
            var save = new Button { Content = "\uE74E", FontFamily = SettingsUi.Icons, FontSize = 13, Style = (Style)FindResource("IconButton"), Padding = new Thickness(7), Margin = new Thickness(0, 0, 0, 6), VerticalAlignment = VerticalAlignment.Center,
                ToolTip = T("Save as smart collection …") + "\n" + T("Any search can become a smart collection. It keeps the search text and all filters and stays current: new matching items appear in it by themselves. You find it on the left under “Collections”.") };
            AutomationProperties.SetName(save, T("Save as smart collection …"));
            save.Click += SaveSearchClicked;
            FilterStrip.Children.Add(reset); FilterStrip.Children.Add(save);
        }
        if (focused != null && FilterStrip.Children.Contains(focused)) Dispatcher.BeginInvoke(() => focused.Focus(), DispatcherPriority.Input);
        MarkOpenSection();
        if (FilterFlyout.IsOpen) RequestHitCounts();
        RefreshFilterSection();
    }

    private (Button Button, TextBlock Text) SectionButton(FilterSection section)
    {
        var summary = section.Summary(); var active = section.Active() > 0;
        if (!_sectionButtons.TryGetValue(section.Key, out var pill))
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock { Text = section.Glyph, FontFamily = SettingsUi.Icons, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 7, 0) });
            var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 260, TextTrimming = TextTrimming.CharacterEllipsis };
            content.Children.Add(text);
            var caret = new TextBlock { Text = "\uE70D", FontFamily = SettingsUi.Icons, FontSize = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 1, 0, 0) };
            caret.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            content.Children.Add(caret);
            var button = new Button { Content = content, Style = (Style)FindResource("FilterBarButton"), Tag = section.Key };
            button.Click += (_, _) => ToggleFilterSection((string)button.Tag, button);
            pill = (button, text);
            _sectionButtons[section.Key] = pill;
        }
        pill.Text.Inlines.Clear();
        pill.Text.Inlines.Add(new System.Windows.Documents.Run(section.Label));
        if (summary.Length > 0) pill.Text.Inlines.Add(new System.Windows.Documents.Run(": " + summary) { FontWeight = FontWeights.SemiBold });
        pill.Button.SetResourceReference(BackgroundProperty, active ? "ChipFull" : "CardBackground");
        pill.Button.SetResourceReference(BorderBrushProperty, active ? "ChipFull" : "FieldBorder");
        pill.Button.ToolTip = summary.Length > 0 ? section.Label + ": " + summary : section.Label;
        AutomationProperties.SetName(pill.Button, summary.Length > 0 ? section.Label + ": " + summary : section.Label);
        return pill;
    }

    private void ToggleFilterSection(string key, FrameworkElement anchor)
    {
        if (FilterFlyout.IsOpen && _filterSection == key && ReferenceEquals(FilterFlyout.PlacementTarget, anchor)) { FilterFlyout.IsOpen = false; return; }
        OpenFilterSection(key, anchor);
    }

    /// <summary>Opens a section in the small window under its button (or under "More").</summary>
    private void OpenFilterSection(string key, FrameworkElement? anchor = null)
    {
        _filterPopup = FilterFlyout;
        _filterSection = key;
        _sectionPinned.Remove(key);
        if (anchor != null) FilterFlyout.PlacementTarget = anchor;
        else if (_sectionButtons.TryGetValue(key, out var own) && own.Button.IsVisible) FilterFlyout.PlacementTarget = own.Button;
        else if (_sectionButtons.TryGetValue("more", out var more)) FilterFlyout.PlacementTarget = more.Button;
        else FilterFlyout.PlacementTarget = FilterStrip;
        FilterSectionHost.Child = SectionContent(key);
        FilterFlyoutScroll.ScrollToTop();
        if (!FilterFlyout.IsOpen) OpenFilterFlyout();
        MarkOpenSection();
        RequestHitCounts();
    }

    private void OpenFilterFlyout()
    {
        if (!_filterFlyoutReady)
        {
            _filterFlyoutReady = true;
            FilterFlyoutFrame.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = Theme.IsDark ? .5 : .2 };
            FilterFlyoutFrame.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { FilterFlyout.IsOpen = false; e.Handled = true; } };
            FilterFlyout.Closed += (_, _) => { _filterSection = null; UpdateFilterBar(); };
        }
        FilterFlyout.IsOpen = true;
    }
    private bool _filterFlyoutReady;

    private void MarkOpenSection()
    {
        foreach (var (key, pill) in _sectionButtons)
        {
            var open = FilterFlyout.IsOpen && (key == _filterSection || key == "more" && ReferenceEquals(FilterFlyout.PlacementTarget, pill.Button));
            if (open) pill.Button.SetResourceReference(BorderBrushProperty, "Accent");
            else pill.Button.SetResourceReference(BorderBrushProperty, (_sections.FirstOrDefault(s => s.Key == key)?.Active.Invoke() ?? 0) > 0 ? "ChipFull" : "FieldBorder");
        }
    }

    /// <summary>Rebuilds the open section after a change, keeping the focus and the scroll position.</summary>
    private void RefreshFilterSection()
    {
        if (!FilterFlyout.IsOpen || _filterSection is not { } key || _sectionRefreshPending) return;
        _sectionRefreshPending = true;
        Dispatcher.BeginInvoke(() =>
        {
            _sectionRefreshPending = false;
            if (!FilterFlyout.IsOpen || _filterSection != key) return;
            var focused = Keyboard.FocusedElement as FrameworkElement;
            var uid = focused != null && FilterSectionHost.IsAncestorOf(focused) ? focused.Uid : null;
            var caret = (focused as TextBox)?.CaretIndex;
            var offset = FilterFlyoutScroll.VerticalOffset;
            FilterSectionHost.Child = SectionContent(key);
            FilterSectionHost.UpdateLayout(); FilterFlyoutScroll.ScrollToVerticalOffset(offset);
            if (uid is { Length: > 0 } && Logicals<FrameworkElement>(FilterSectionHost).FirstOrDefault(e => e.Uid == uid) is { } again)
            {
                again.Focus();
                if (again is TextBox box && caret is int at) box.CaretIndex = Math.Min(at, box.Text.Length);
            }
        }, DispatcherPriority.Input);
    }

    private FrameworkElement SectionContent(string key)
    {
        var section = key == "more" ? _moreSection : _sections.FirstOrDefault(s => s.Key == key) ?? FilterSections().FirstOrDefault(s => s.Key == key);
        if (section == null) return SettingsUi.Hint(T("This filter is not available here."));
        var panel = new StackPanel { Tag = key };
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        if (section.Active() > 0)
        {
            var clear = new Button { Content = T("Reset"), Style = (Style)FindResource("Quiet"), FontSize = 12, Padding = new Thickness(6, 2, 6, 2), Uid = "reset:" + key };
            clear.SetResourceReference(ForegroundProperty, "Accent");
            clear.Click += (_, _) => ResetSection(key);
            DockPanel.SetDock(clear, Dock.Right); head.Children.Add(clear);
        }
        head.Children.Add(new TextBlock { Text = key == "more" ? T("More filters") : section.Label, FontWeight = FontWeights.SemiBold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(head);
        panel.Children.Add(section.Build());
        return panel;
    }

    private FrameworkElement MoreSections(IReadOnlyList<FilterSection> hidden)
    {
        var list = new StackPanel();
        foreach (var section in hidden)
        {
            var row = new DockPanel();
            row.Children.Add(new TextBlock { Text = section.Glyph, FontFamily = SettingsUi.Icons, FontSize = 13, Width = 24, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = section.Label, VerticalAlignment = VerticalAlignment.Center });
            var button = new Button { Content = row, Style = (Style)FindResource("FilterRow"), Uid = "open:" + section.Key };
            var key = section.Key;
            button.Click += (_, _) => OpenFilterSection(key, FilterFlyout.PlacementTarget as FrameworkElement);
            list.Children.Add(button);
        }
        return list;
    }

    // ----- Which sections exist -----

    private List<FilterSection> FilterSections()
    {
        var list = new List<FilterSection>
        {
            new("rating", "\uE734", T("Rating"), 0, () => _filter.UnratedOnly ? T("Unrated") : RatingFilter.SelectedIndex > 0 ? RatingLabel(RatingFilter.SelectedIndex) : "",
                () => _filter.UnratedOnly || RatingFilter.SelectedIndex > 0 ? 1 : 0, RatingSection),
        };
        foreach (var (role, key, glyph, label, rank) in new[] { (TagRole.Performer, "performers", "\uE716", T("Performers"), 1), (TagRole.Studio, "studios", "\uE731", T("Studios"), 2) })
            if (_roleNames.Any(n => n.Role == role) || _filter.Include.Concat(_filter.Exclude).Any(f => f.Role == role))
                list.Add(new(key, glyph, label, rank, () => FacetSummary(_filter.Include.Where(f => f.Role == role).Select(f => f.Tag), _filter.Exclude.Where(f => f.Role == role).Select(f => f.Tag)),
                    () => _filter.Include.Concat(_filter.Exclude).Count(f => f.Role == role), () => FacetSection(key, RoleValues(role), role)));
        var groups = TagGroups();
        var tagRank = 3;
        foreach (var (category, tags) in groups.Categories)
        {
            var name = category; var members = tags; var set = tags.ToHashSet(StringComparer.OrdinalIgnoreCase);
            list.Add(new("cat:" + name, "\uE8EC", name, tagRank, () => TagSummary(set), () => TagActive(set), () => FacetSection("cat:" + name, TagValues(members), TagRole.Tag)));
            tagRank = tagRank == 3 ? 10 : tagRank + 1;
        }
        if (groups.Loose.Length > 0 || _filter.UntaggedOnly || groups.Categories.Count == 0)
        {
            var loose = groups.Loose; var set = loose.ToHashSet(StringComparer.OrdinalIgnoreCase);
            list.Add(new("tags", "\uE8EC", groups.Categories.Count > 0 ? T("Other tags") : T("Tags"), groups.Categories.Count > 0 ? tagRank : 3,
                () => _filter.UntaggedOnly ? T("Without tags") : TagSummary(set), () => TagActive(set) + (_filter.UntaggedOnly ? 1 : 0), () => FacetSection("tags", TagValues(loose), TagRole.Tag, untagged: true)));
        }
        list.Add(new("time", "\uE787", T("Time"), 4, TimeSummary, () => new[] { _filter.FileDate, _filter.Imported, _filter.Released }.Count(d => d.IsActive), TimeSection));
        list.Add(new("file", "\uE7C3", T("File and quality"), 5, () => string.Join(", ", FileParts()), () => FileParts().Count, FileSection));
        list.Add(new("status", "\uE73E", T("Status"), 6, () => string.Join(", ", StatusParts()), () => StatusParts().Count, StatusSection));
        list.Add(new("kind", "\uE8B9", T("Type"), 30, () => _view == FolderView && _kindFilter is { } kind ? ViewName(kind.ToString()) : "", () => _view == FolderView && _kindFilter != null ? 1 : 0, KindSection));
        if (CategoryFilter.Items.Count > 2 || _category != null)
            list.Add(new("category", "\uE8B7", T("Category"), 31, () => _category ?? "", () => _category != null ? 1 : 0, CategorySection));
        if (_roleNames.Any(n => n.Role == TagRole.Performer) || _filter.Profiles.Count > 0)
            list.Add(new("properties", "\uE77B", T("Properties"), 32, () => _filter.Profiles.Count > 0 ? _filter.Profiles.Count.ToString(CultureInfo.CurrentCulture) : "", () => _filter.Profiles.Count, () => ProfilePanel(FilterButton)));
        return list;
    }

    /// <summary>Tag categories of the role "tag" with the tags used in them, largest first, and the tags in no category. Kept
    /// until the tags, their categories or the chosen tags change: the bar is rebuilt on every filter change.</summary>
    private (List<(string Category, string[] Tags)> Categories, string[] Loose) TagGroups()
    {
        var chosen = string.Join("\n", _requiredTags.Concat(_excludedTags));
        if (_tagGroupsCache is { } cached && ReferenceEquals(cached.Entries, _tagEntries) && ReferenceEquals(cached.Tree, CategorizedTags.ItemsSource) && ReferenceEquals(cached.Roles, _categoryRoles) && cached.Chosen == chosen)
            return cached.Groups;
        var groups = BuildTagGroups();
        _tagGroupsCache = (_tagEntries, CategorizedTags.ItemsSource, _categoryRoles, chosen, groups);
        return groups;
    }
    private (object Entries, object? Tree, object Roles, string Chosen, (List<(string Category, string[] Tags)> Categories, string[] Loose) Groups)? _tagGroupsCache;

    private (List<(string Category, string[] Tags)> Categories, string[] Loose) BuildTagGroups()
    {
        var used = _tagEntries.Where(e => e.Count > 0).Select(e => e.Tag).ToHashSet(StringComparer.OrdinalIgnoreCase);
        used.UnionWith(_requiredTags); used.UnionWith(_excludedTags);
        var counts = TagCounts();
        var categorized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var categories = new List<(string Category, string[] Tags)>();
        foreach (var node in CategorizedTags.ItemsSource as IEnumerable<BrowseNode> ?? [])
        {
            if (node.Category is not { } category) continue;
            var tags = node.Children.Select(c => c.Tag ?? c.Label).Where(t => t.Length > 0).ToArray();
            categorized.UnionWith(tags);
            if (RoleOf(category) != TagRole.Tag) continue;
            var inUse = tags.Where(used.Contains).ToArray();
            if (inUse.Length > 0) categories.Add((category, inUse));
        }
        categories = categories.OrderByDescending(c => c.Tags.Sum(t => counts.GetValueOrDefault(t))).ThenBy(c => c.Category, StringComparer.CurrentCultureIgnoreCase).ToList();
        var loose = used.Where(t => !categorized.Contains(t)).ToArray();
        return (categories, loose);
    }

    private Dictionary<string, int> TagCounts()
    {
        if (_tagCountsCache is { } cached && ReferenceEquals(cached.Entries, _tagEntries)) return cached.Counts;
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _tagEntries) counts[entry.Tag] = Math.Max(counts.GetValueOrDefault(entry.Tag), entry.Count);
        _tagCountsCache = (_tagEntries, counts);
        return counts;
    }
    private (object Entries, Dictionary<string, int> Counts)? _tagCountsCache;

    private int TagActive(HashSet<string> tags) => _requiredTags.Count(tags.Contains) + _excludedTags.Count(tags.Contains);
    private string TagSummary(HashSet<string> tags) => FacetSummary(_requiredTags.Where(tags.Contains), _excludedTags.Where(tags.Contains));

    /// <summary>Counts within the hits when known, otherwise in the whole library.</summary>
    private int ValueCount(string name, int total) => _hitCounts is { } hits ? hits.GetValueOrDefault(name) : total;

    private IReadOnlyList<FacetValue> TagValues(IEnumerable<string> tags)
    {
        var counts = TagCounts();
        return tags.Select(tag => new FacetValue(tag, ValueCount(tag, counts.GetValueOrDefault(tag)),
            _requiredTags.Contains(tag, StringComparer.OrdinalIgnoreCase) ? 1 : _excludedTags.Contains(tag, StringComparer.OrdinalIgnoreCase) ? -1 : 0, null, [], null)).ToList();
    }

    private IReadOnlyList<FacetValue> RoleValues(TagRole role)
    {
        var values = new List<FacetValue>();
        foreach (var group in _roleNames.Where(n => n.Role == role).GroupBy(n => n.Tag, StringComparer.OrdinalIgnoreCase))
        {
            var first = group.First();
            var entry = new DirectoryEntry(first.Tag, first.Category, role, group.Max(n => n.Count), Aliases: group.SelectMany(n => n.Aliases).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
            var state = group.Select(n => State(new DirectoryEntry(n.Tag, n.Category, role, 0))).FirstOrDefault(s => s != 0);
            values.Add(new(first.Tag, ValueCount(first.Tag, entry.MediaCount), state, entry.AliasList.Count > 0 ? string.Join(", ", entry.AliasList) : null, entry.AliasList, entry));
        }
        // Chosen entries whose names left the directory stay visible, so they can be removed.
        foreach (var facet in _filter.Include.Concat(_filter.Exclude).Where(f => f.Role == role && !values.Any(v => string.Equals(v.Name, f.Tag, StringComparison.OrdinalIgnoreCase))))
            values.Add(new(facet.Tag, 0, _filter.Include.Contains(facet) ? 1 : -1, null, [], new DirectoryEntry(facet.Tag, facet.Category, role, 0)));
        return values;
    }

    private static string FacetSummary(IEnumerable<string> included, IEnumerable<string> excluded)
    {
        var chosen = included.Concat(excluded.Select(name => T("without {0}", name))).ToList();
        return chosen.Count switch { 0 => "", 1 or 2 => string.Join(", ", chosen), _ => string.Join(", ", chosen.Take(2)) + " +" + (chosen.Count - 2).ToString(CultureInfo.CurrentCulture) };
    }

    private int ActiveFilterCount() =>
        (RatingFilter.SelectedIndex > 0 ? 1 : 0) + (_category != null ? 1 : 0) + (_folder != null && _view != FolderView ? 1 : 0) + (_view == FolderView && _kindFilter != null ? 1 : 0)
        + _requiredTags.Count + _excludedTags.Count + _reviewStates.Count + FilterChips().Count();

    // ----- Building blocks -----

    private static string StateWord(int state) => state switch { 1 => T(", included"), -1 => T(", excluded"), _ => "" };

    /// <summary>A pill for one value: chosen ones are filled, excluded ones struck through; Delete or a right click excludes.</summary>
    private Button FilterChip(string uid, string label, int? count, int state, Action click, Action? exclude = null, string? tooltip = null, bool zero = false)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        if (state == 1) panel.Children.Add(new TextBlock { Text = "\uE73E", FontFamily = SettingsUi.Icons, FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 6, 0) });
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 240, TextTrimming = TextTrimming.CharacterEllipsis };
        if (state == 1) text.FontWeight = FontWeights.SemiBold;
        if (state == -1) text.TextDecorations = TextDecorations.Strikethrough;
        if (zero) text.SetResourceReference(TextBlock.ForegroundProperty, "Subtle");
        panel.Children.Add(text);
        if (count is { } number)
        {
            var hint = new TextBlock { Text = number.ToString("N0", CultureInfo.CurrentCulture), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 0, 0), Typography = { NumeralAlignment = FontNumeralAlignment.Tabular } };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            panel.Children.Add(hint);
        }
        var button = new Button { Content = panel, Style = (Style)FindResource("FilterChip"), Uid = uid, ToolTip = tooltip };
        button.SetResourceReference(BackgroundProperty, state switch { 1 => "ChipFull", -1 => "ChipExcluded", _ => "ButtonBackground" });
        AutomationProperties.SetName(button, label + StateWord(state) + (count is { } c ? ", " + T("{0:N0} hits", c) : ""));
        button.Click += (_, _) => click();
        if (exclude != null)
        {
            AutomationProperties.SetHelpText(button, T("Enter: include · Delete: exclude"));
            button.PreviewKeyDown += (_, e) => { if (e.Key == Key.Delete) { e.Handled = true; exclude(); } };
        }
        return button;
    }

    private static WrapPanel Cloud(IEnumerable<UIElement> items) { var cloud = new WrapPanel(); foreach (var item in items) cloud.Children.Add(item); return cloud; }

    private static TextBlock SubHeading(string text, double top = 12)
    {
        var heading = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, top, 0, 6) };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        return heading;
    }

    /// <summary>A single choice as pills; clicking the chosen one again removes the filter.</summary>
    private WrapPanel Choices<TValue>(string uid, IEnumerable<(string Label, TValue Value)> options, TValue current, TValue none, Action<TValue> apply) =>
        Cloud(options.Select(option => (UIElement)FilterChip(uid + ":" + option.Label, option.Label, null, Equals(option.Value, current) ? 1 : 0,
            () => apply(Equals(option.Value, current) ? none : option.Value))));

    // ----- Usage -----

    /// <summary>Remembers that a section and a value were used; older uses fade, so the bar follows what is used now.</summary>
    private void NoteFilterUse(string section, string? value = null)
    {
        var usage = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (key, weight) in _settings.FilterUsage) if (weight * .97 >= .05) usage[key] = weight * .97;
        usage[section] = usage.GetValueOrDefault(section) + 1;
        if (value != null) usage[value] = usage.GetValueOrDefault(value) + 1;
        if (usage.Count > 200) usage = usage.OrderByDescending(p => p.Value).Take(200).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        _settings = _settings with { FilterUsage = usage };
        SaveViewPreferences();
    }

    /// <summary>Up to four values used often lately that are not chosen now, one click each.</summary>
    private IEnumerable<UIElement> QuickChips()
    {
        var counts = TagCounts();
        var roles = _roleNames.GroupBy(n => n.Tag, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var chips = new List<UIElement>();
        foreach (var (key, weight) in _settings.FilterUsage.Where(p => p.Key.StartsWith("v:", StringComparison.Ordinal) && p.Value >= 1.5).OrderByDescending(p => p.Value))
        {
            if (chips.Count >= 4) break;
            var parts = key.Split(':', 3); if (parts.Length < 3) continue;
            var (kind, name) = (parts[1], parts[2]);
            Action? apply = null; string label = name;
            if (kind == "tag" && counts.ContainsKey(name) && !_requiredTags.Contains(name, StringComparer.OrdinalIgnoreCase) && !_excludedTags.Contains(name, StringComparer.OrdinalIgnoreCase))
                apply = () => SetTagValue(name, 1);
            else if (kind is "performer" or "studio" && roles.TryGetValue(name, out var role) && State(new DirectoryEntry(role.Tag, role.Category, role.Role, 0)) == 0)
                apply = () => SetFacetValue(new DirectoryEntry(role.Tag, role.Category, role.Role, 0), 1);
            else if (kind == "stars" && int.TryParse(name, out var stars) && stars is > 0 and <= 5 && RatingFilter.SelectedIndex != stars)
            { label = RatingLabel(stars); apply = () => { _filter = _filter with { UnratedOnly = false }; RatingFilter.SelectedIndex = stars; }; }
            if (apply == null) continue;
            var chip = FilterChip("quick:" + key, label, null, 0, apply, tooltip: T("Used often lately; click to filter"));
            chip.Margin = new Thickness(0, 0, 5, 6); chip.Opacity = .9;
            if (chips.Count == 0)
            {
                var divider = new Border { Width = 1, Height = 18, Margin = new Thickness(4, 0, 10, 6), VerticalAlignment = VerticalAlignment.Center, ToolTip = T("Used often lately") };
                divider.SetResourceReference(Border.BackgroundProperty, "Divider");
                chips.Add(divider);
            }
            chips.Add(chip);
        }
        return chips;
    }

    // ----- Counts within the hits -----

    /// <summary>Counts the tags of the current hits off the UI thread; a collection keeps the library's counts.</summary>
    private void RequestHitCounts()
    {
        if (_repository == null) return;
        if (_playlist != null || !ShowsMediaGrid) { _hitCounts = null; _hitCountsKey = null; return; }
        var state = CaptureViewState();
        var query = state.ToQuery(DateTimeOffset.Now) with { Offset = 0, Limit = 1 };
        var key = FilterKey(state);
        if (key == _hitCountsKey) return;
        // Until the new counts are there, the library's counts stand in rather than those of another result.
        _hitCountsKey = key; _hitCounts = null;
        _ = _libraryTasks.Run(async token =>
        {
            var counts = await Task.Run(() => _repository.TagCountsWithin(query, token), token);
            if (_closing || _hitCountsKey != key) return;
            _hitCounts = counts;
            UpdateFilterBar();
        });
    }

    /// <summary>What a state filters, without position, selection and display; equal keys mean equal hits.</summary>
    private static string FilterKey(ViewState state)
    {
        try { return System.Text.Json.JsonSerializer.Serialize(state with { Position = 0, AnchorId = null, Selection = [], SortOrder = 0, ViewMode = 0, TileWidth = 0, Directory = null }); }
        catch (Exception) { return Guid.NewGuid().ToString(); }
    }

    // ----- Rating, type, category -----

    private FrameworkElement RatingSection()
    {
        var panel = new StackPanel();
        var level = _filter.UnratedOnly ? 0 : RatingFilter.SelectedIndex;
        var stars = new StackPanel { Orientation = Orientation.Horizontal };
        for (var n = 1; n <= 5; n++)
        {
            var value = n;
            var glyph = new TextBlock { Text = n <= level ? "\uE735" : "\uE734", FontFamily = SettingsUi.Icons, FontSize = 20 };
            glyph.SetResourceReference(TextBlock.ForegroundProperty, n <= level ? "Gold" : "Muted");
            var star = new Button { Content = glyph, Style = (Style)FindResource("Quiet"), Padding = new Thickness(3), Uid = "star:" + n, ToolTip = value == 5 ? RatingLabel(5) : RatingLabel(value) };
            AutomationProperties.SetName(star, RatingLabel(value) + (value == level ? StateWord(1) : ""));
            star.Click += (_, _) => { var next = level == value ? 0 : value; _filter = _filter with { UnratedOnly = false }; RatingFilter.SelectedIndex = next; if (next > 0) NoteFilterUse("rating", "v:stars:" + next); };
            stars.Children.Add(star);
        }
        var what = new TextBlock { Text = _filter.UnratedOnly ? T("only unrated") : level > 0 ? RatingLabel(level) : T("any rating"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), FontSize = 12 };
        what.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        stars.Children.Add(what);
        panel.Children.Add(stars);
        panel.Children.Add(Cloud([FilterChip("unrated", T("Unrated"), null, _filter.UnratedOnly ? 1 : 0, () =>
        {
            var on = !_filter.UnratedOnly;
            if (on) { RatingFilter.SelectedIndex = 0; NoteFilterUse("rating"); }
            ApplyFilter(_filter with { UnratedOnly = on });
        })]));
        ((FrameworkElement)panel.Children[^1]).Margin = new Thickness(0, 10, 0, 0);
        return panel;
    }

    private FrameworkElement KindSection()
    {
        var current = _view == FolderView ? _kindFilter?.ToString() ?? "all" : _view;
        var kinds = new List<string> { "all", "Video", "Image" };
        if (AudioNav.Visibility == Visibility.Visible || current == "Audio") kinds.Add("Audio");
        var panel = new StackPanel();
        panel.Children.Add(Cloud(kinds.Select(kind => (UIElement)FilterChip("kind:" + kind, ViewName(kind), null, current == kind ? 1 : 0, () => { ShowKind(kind); NoteFilterUse("kind"); UpdateFilterBar(); RenderTagFilterChips(); }))));
        if (_view != FolderView) panel.Children.Add(SettingsUi.Hint(T("The type is the same choice as videos, images and audio in the sidebar; the other filters stay.")));
        return panel;
    }

    private FrameworkElement CategorySection() =>
        Cloud(CategoryFilter.Items.OfType<CategoryOption>().Select(option => (UIElement)FilterChip("category:" + (option.Value ?? ""), option.Value ?? T("All categories"), option.Count is { } count ? (int)Math.Min(int.MaxValue, count) : null,
            ReferenceEquals(CategoryFilter.SelectedItem, option) && option.Value != null ? 1 : option.Value == null && _category == null ? 1 : 0,
            () => { CategoryFilter.SelectedItem = option; if (option.Value != null) NoteFilterUse("category"); UpdateFilterBar(); })));

    // ----- Tags, performers, studios -----

    private FrameworkElement FacetSection(string key, IReadOnlyList<FacetValue> values, TagRole role, bool untagged = false)
    {
        var panel = new StackPanel();
        var included = role == TagRole.Tag ? _requiredTags.Count : _filter.Include.Count(f => f.Role == role);
        if (included > 1)
        {
            var any = role == TagRole.Tag ? _tagsMatchAny : _filter.AnyOf.Contains(role);
            var mode = Cloud([
                FilterChip(key + ":all", T("all {0}", included), null, any ? 0 : 1, () => SetMatchAny(role, false)),
                FilterChip(key + ":any", T("at least one"), null, any ? 1 : 0, () => SetMatchAny(role, true)) ]);
            mode.Margin = new Thickness(0, 0, 0, 6);
            panel.Children.Add(mode);
        }
        if (role == TagRole.Studio)
            panel.Children.Add(Cloud([FilterChip("substudios", T("Include sub-studios"), null, _filter.IncludeSubStudios ? 1 : 0, () => ApplyFilter(_filter with { IncludeSubStudios = !_filter.IncludeSubStudios }),
                tooltip: T("A studio also finds the items of the studios that belong to it (directory: “Belongs to”)."))]));
        if (untagged)
            panel.Children.Add(Cloud([FilterChip("untagged", T("Without tags"), null, _filter.UntaggedOnly ? 1 : 0, () => { var on = !_filter.UntaggedOnly; if (on) NoteFilterUse(key); ApplyFilter(_filter with { UntaggedOnly = on }); })]));
        // Chosen values lead when the window opens or the search changes, not after a click: a row must not move under the pointer.
        if (!_sectionPinned.TryGetValue(key, out var pinned)) _sectionPinned[key] = pinned = values.Where(v => v.State != 0).Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var usage = _settings.FilterUsage; var prefix = "v:" + UsageKind(role) + ":";
        IEnumerable<FacetValue> Ordered(IEnumerable<FacetValue> list) => list.OrderByDescending(v => pinned.Contains(v.Name)).ThenByDescending(v => v.Count > 0 || v.State != 0)
            .ThenByDescending(v => Math.Round(usage.GetValueOrDefault(prefix + v.Name))).ThenByDescending(v => v.Count).ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase);
        if (values.Count == 0) { panel.Children.Add(SettingsUi.Hint(T("No entries yet."))); return panel; }
        if (values.Count <= 12)
        {
            panel.Children.Add(Cloud(Ordered(values).Select(v => (UIElement)ValueChip(key, v, role))));
            panel.Children.Add(SettingsUi.Hint(role == TagRole.Tag ? T("Click: include · Delete: exclude · right-click: more") : T("Click: include · Delete or right-click: exclude")));
        }
        else
        {
            var search = new TextBox { Padding = new Thickness(26, 5, 7, 5), FontSize = 12, Uid = "find:" + key, Text = _sectionFind.GetValueOrDefault(key) ?? "", Margin = new Thickness(0, 0, 0, 6) };
            AutomationProperties.SetName(search, T("Search {0}", SectionLabel(key)));
            var placeholder = new TextBlock { Text = T("Search {0} …", SectionLabel(key)) + " (" + values.Count.ToString("N0", CultureInfo.CurrentCulture) + ")", IsHitTestVisible = false, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(27, 0, 0, 6),
                Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed };
            placeholder.SetResourceReference(TextBlock.ForegroundProperty, "Subtle");
            var magnifier = new TextBlock { Text = "\uE721", FontFamily = SettingsUi.Icons, FontSize = 11, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 0, 0, 6) };
            magnifier.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            var box = new Grid(); box.Children.Add(search); box.Children.Add(magnifier); box.Children.Add(placeholder);
            panel.Children.Add(box);
            var rows = new StackPanel();
            panel.Children.Add(rows);
            void Fill()
            {
                rows.Children.Clear();
                var text = search.Text.Trim();
                var found = Ordered(values.Where(v => text.Length == 0 || v.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase) || v.Aliases.Any(a => a.Contains(text, StringComparison.CurrentCultureIgnoreCase)))).ToList();
                var limit = text.Length > 0 || _sectionExpanded.Contains(key) ? LongList : ShortList;
                foreach (var value in found.Take(limit)) rows.Children.Add(ValueRow(key, value, role));
                if (found.Count == 0) rows.Children.Add(SettingsUi.Hint(T("Nothing found.")));
                if (found.Count > limit || _sectionExpanded.Contains(key) && text.Length == 0)
                {
                    var more = new Button { Style = (Style)FindResource("Quiet"), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Left, Uid = "more:" + key, Margin = new Thickness(0, 4, 0, 0),
                        Content = found.Count > limit ? (text.Length == 0 && limit == ShortList ? T("Show {0:N0} more", found.Count - limit) : T("{0:N0} more; refine the search", found.Count - limit)) : T("Show fewer") };
                    more.SetResourceReference(ForegroundProperty, "Accent");
                    more.Click += (_, _) => { if (!_sectionExpanded.Remove(key)) _sectionExpanded.Add(key); Fill(); };
                    rows.Children.Add(more);
                }
            }
            search.TextChanged += (_, _) =>
            {
                _sectionFind[key] = search.Text; placeholder.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                _sectionPinned[key] = values.Where(v => v.State != 0).Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase); pinned = _sectionPinned[key];
                Fill();
            };
            search.PreviewKeyDown += (_, e) =>
            {
                // Down leaves the search for the first row; Enter takes the first row.
                if (e.Key == Key.Down && rows.Children.OfType<Grid>().FirstOrDefault() is { } first) { e.Handled = true; first.Children.OfType<Button>().First().Focus(); }
                else if (e.Key == Key.Enter && rows.Children.OfType<Grid>().FirstOrDefault()?.Children.OfType<Button>().First() is { } top) { e.Handled = true; top.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); }
            };
            Fill();
            panel.Children.Add(SettingsUi.Hint(T("Enter or click: include · Delete or “−”: exclude")));
        }
        if (role == TagRole.Tag)
        {
            var manage = new Button { Content = T("Manage tags …"), Style = (Style)FindResource("Quiet"), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-6, 6, 0, 0), Uid = "manage:" + key,
                ToolTip = T("Opens the tag panel: drag items onto tags, rename, merge, pin, unused tags") };
            manage.Click += (_, _) => { FilterFlyout.IsOpen = false; _ = OpenTagPanelAsync(); };
            panel.Children.Add(manage);
        }
        return panel;
    }

    private string SectionLabel(string key) => _sections.FirstOrDefault(s => s.Key == key)?.Label ?? T("Tags");
    private static string UsageKind(TagRole role) => role switch { TagRole.Performer => "performer", TagRole.Studio => "studio", _ => "tag" };
    private bool HitsZero(FacetValue value) => _hitCounts != null && value.Count == 0 && value.State == 0;

    private Button ValueChip(string key, FacetValue value, TagRole role)
    {
        var chip = FilterChip(key + ":" + value.Name, value.Name, value.State == -1 ? null : value.Count, value.State,
            () => SetValue(key, value, role, value.State == 1 ? 0 : 1), () => SetValue(key, value, role, value.State == -1 ? 0 : -1), value.Detail, HitsZero(value));
        chip.MouseRightButtonUp += (_, e) => { e.Handled = true; ValueMenu(key, value, role, chip); };
        return chip;
    }

    /// <summary>A row of a long list: the name with its count, and "+" and "−" that appear on hover and stay for chosen values.</summary>
    private Grid ValueRow(string key, FacetValue value, TagRole role)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 1) };
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var line = new DockPanel();
        if (role == TagRole.Performer) line.Children.Add(Initials(value.Name));
        else
        {
            var mark = new TextBlock { Text = value.State switch { 1 => "\uE73E", -1 => "\uE711", _ => "" }, FontFamily = SettingsUi.Icons, FontSize = 11, Width = 22, VerticalAlignment = VerticalAlignment.Center };
            mark.SetResourceReference(TextBlock.ForegroundProperty, value.State == -1 ? "TagExcluded" : "TagIncluded");
            line.Children.Add(mark);
        }
        var count = new TextBlock { Text = value.State == -1 ? T("without") : value.Count.ToString("N0", CultureInfo.CurrentCulture), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 2, 0), Typography = { NumeralAlignment = FontNumeralAlignment.Tabular } };
        count.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        DockPanel.SetDock(count, Dock.Right); line.Children.Add(count);
        var name = new TextBlock { Text = value.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = value.Detail };
        if (value.State == 1) name.FontWeight = FontWeights.SemiBold;
        if (value.State == -1) name.TextDecorations = TextDecorations.Strikethrough;
        if (HitsZero(value)) { name.SetResourceReference(TextBlock.ForegroundProperty, "Subtle"); count.SetResourceReference(TextBlock.ForegroundProperty, "Subtle"); }
        line.Children.Add(name);
        var row = new Button { Content = line, Style = (Style)FindResource("FilterRow"), Uid = "row:" + key + ":" + value.Name };
        if (value.State != 0) row.SetResourceReference(BackgroundProperty, value.State == 1 ? "ChipFull" : "ChipExcluded");
        AutomationProperties.SetName(row, value.Name + StateWord(value.State) + ", " + T("{0:N0} hits", value.Count));
        AutomationProperties.SetHelpText(row, T("Enter: include · Delete: exclude"));
        row.Click += (_, _) => SetValue(key, value, role, value.State == 1 ? 0 : 1);
        row.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete) { e.Handled = true; SetValue(key, value, role, value.State == -1 ? 0 : -1); }
            else if (e.Key is Key.Down or Key.Up) { e.Handled = true; row.MoveFocus(new(e.Key == Key.Down ? FocusNavigationDirection.Down : FocusNavigationDirection.Up)); }
        };
        row.MouseRightButtonUp += (_, e) => { e.Handled = true; ValueMenu(key, value, role, row); };
        grid.Children.Add(row);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 0, 0), Opacity = value.State != 0 ? 1 : 0 };
        foreach (var (glyph, target, tip) in new[] { ("\uE710", 1, T("Include")), ("\uE738", -1, T("Exclude")) })
        {
            var on = value.State == target;
            var button = new Button { Content = glyph, FontFamily = SettingsUi.Icons, FontSize = 10, Width = 24, Height = 24, Padding = new Thickness(0), Margin = new Thickness(2, 0, 0, 0), Focusable = false, ToolTip = tip, Style = (Style)FindResource("FilterChip") };
            button.SetResourceReference(BackgroundProperty, on ? (target == 1 ? "TagIncluded" : "TagExcluded") : "ButtonBackground");
            button.SetResourceReference(ForegroundProperty, on ? "OnAccent" : "Text");
            AutomationProperties.SetName(button, tip + ": " + value.Name);
            button.Click += (_, _) => SetValue(key, value, role, on ? 0 : target);
            actions.Children.Add(button);
        }
        Grid.SetColumn(actions, 1); grid.Children.Add(actions);
        if (value.State == 0)
        {
            grid.MouseEnter += (_, _) => actions.Opacity = 1;
            grid.MouseLeave += (_, _) => { if (!grid.IsKeyboardFocusWithin) actions.Opacity = 0; };
            grid.IsKeyboardFocusWithinChanged += (_, _) => actions.Opacity = grid.IsKeyboardFocusWithin || grid.IsMouseOver ? 1 : 0;
        }
        return grid;
    }

    private static FrameworkElement Initials(string name)
    {
        var letters = string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpper(w[0], CultureInfo.CurrentCulture)).Take(2));
        var hue = name.Aggregate(7, (h, c) => (h * 31 + c) % 360);
        var color = FromHsl(hue, .42, .44);
        var badge = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = new SolidColorBrush(color), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        badge.Child = new TextBlock { Text = letters, Foreground = Brushes.White, FontSize = 9.5, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        return badge;
    }

    private static Color FromHsl(double hue, double saturation, double lightness)
    {
        var c = (1 - Math.Abs(2 * lightness - 1)) * saturation; var x = c * (1 - Math.Abs(hue / 60 % 2 - 1)); var m = lightness - c / 2;
        var (r, g, b) = hue switch { < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x), < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x) };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    /// <summary>Right click on a value: tags offer their full menu (rename, merge, pin, …), performers and studios include or exclude.</summary>
    private void ValueMenu(string key, FacetValue value, TagRole role, UIElement target)
    {
        if (role == TagRole.Tag) { OpenTagMenu(new TagRow(value.Name, value.Name, "", TagBrush), target); return; }
        Menus.Open(target,
        [
            MenuSpec.Command(T("Include"), () => SetValue(key, value, role, 1), "\uE710", enabled: value.State != 1),
            MenuSpec.Command(T("Exclude"), () => SetValue(key, value, role, -1), "\uE738", enabled: value.State != -1),
            MenuSpec.Command(T("Remove filter"), () => SetValue(key, value, role, 0), "\uE711", enabled: value.State != 0),
        ], MenuFailed);
    }

    private void SetValue(string key, FacetValue value, TagRole role, int state)
    {
        if (state != 0) NoteFilterUse(key, "v:" + UsageKind(role) + ":" + value.Name);
        if (role == TagRole.Tag) SetTagValue(value.Name, state);
        else if (value.Entry is { } entry) SetFacetValue(entry, state);
    }

    private void SetTagValue(string tag, int state)
    {
        _browse.SetTagMode(tag, state);
        _anchor = 0; ApplyTagFilterChanges();
    }

    /// <summary>Includes (1), excludes (−1) or frees (0) a performer or studio.</summary>
    private void SetFacetValue(DirectoryEntry entry, int state)
    {
        var facet = Facet(entry);
        var include = _filter.Include.Where(f => !SameFacet(f, facet)).ToList(); var exclude = _filter.Exclude.Where(f => !SameFacet(f, facet)).ToList();
        if (state == 1) include.Add(facet); else if (state == -1) exclude.Add(facet);
        ApplyFilter(_filter with { Include = include, Exclude = exclude });
    }
    private static bool SameFacet(TagFacet a, TagFacet b) => a.Role == b.Role && string.Equals(a.Tag, b.Tag, StringComparison.OrdinalIgnoreCase);

    private void SetMatchAny(TagRole role, bool any)
    {
        if (role == TagRole.Tag) { _tagsMatchAny = any; _anchor = 0; RenderTagFilterChips(); UpdateFilterBar(); Refresh(); return; }
        ApplyFilter(_filter with { AnyOf = any ? [.. _filter.AnyOf.Where(r => r != role), role] : _filter.AnyOf.Where(r => r != role).ToList() });
    }

    // ----- Time -----

    private DateFilter DateOf(DateKind kind) => kind switch { DateKind.File => _filter.FileDate, DateKind.Imported => _filter.Imported, _ => _filter.Released };
    private MediaFilter WithDate(DateKind kind, DateFilter value) => kind switch
    {
        DateKind.File => _filter with { FileDate = value },
        DateKind.Imported => _filter with { Imported = value },
        _ => _filter with { Released = value },
    };
    private static string DateKindLabel(DateKind kind) => kind switch { DateKind.File => T("File date"), DateKind.Imported => T("Import date"), _ => T("Release date") };

    private string TimeSummary() => string.Join(", ", new[] { DateKind.File, DateKind.Imported, DateKind.Released }.Where(k => DateOf(k).IsActive)
        .Select(k => DateKindLabel(k) + " " + DateOf(k).Describe()));

    private FrameworkElement TimeSection()
    {
        var panel = new StackPanel();
        // Which date the choices below apply to; a dot marks a date that filters already.
        var which = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var kind in new[] { DateKind.File, DateKind.Imported, DateKind.Released })
        {
            var choice = new RadioButton { Content = DateKindLabel(kind) + (DateOf(kind).IsActive ? " •" : ""), GroupName = "datekind", IsChecked = kind == _dateKind, Style = (Style)FindResource("Segment"), FontSize = 12, Padding = new Thickness(10, 4, 10, 4), Uid = "datekind:" + kind,
                ToolTip = kind == DateKind.File ? T("File date (age of the item)") : null };
            choice.Checked += (_, _) => { _dateKind = kind; RefreshFilterSection(); };
            which.Children.Add(choice);
        }
        panel.Children.Add(new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left, Child = which });
        ((Border)panel.Children[^1]).SetResourceReference(Border.BorderBrushProperty, "FieldBorder");
        var current = DateOf(_dateKind);
        panel.Children.Add(SubHeading(T("Period")));
        panel.Children.Add(Cloud(new[] { (T("last 7 days"), DateWindow.Last7Days), (T("last 30 days"), DateWindow.Last30Days), (T("last year"), DateWindow.LastYear),
            (T("older than 1 year"), DateWindow.OlderThanYear), (T("older than 2 years"), DateWindow.OlderThan2Years), (T("older than 5 years"), DateWindow.OlderThan5Years) }
            .Select(option => (UIElement)FilterChip("window:" + option.Item2, option.Item1, null, current.Window == option.Item2 ? 1 : 0, () =>
            {
                var next = current.Window == option.Item2 ? new DateFilter() : new DateFilter(option.Item2);
                if (next.IsActive) NoteFilterUse("time");
                ApplyFilter(WithDate(_dateKind, next));
            }))));
        panel.Children.Add(SubHeading(T("From – to")));
        var from = new TextBox { Text = current.Window == DateWindow.Custom ? current.From?.ToString("d", CultureInfo.CurrentCulture) ?? "" : "", Width = 110, Padding = new Thickness(6, 4, 6, 4), Uid = "from" };
        var to = new TextBox { Text = current.Window == DateWindow.Custom ? current.To?.ToString("d", CultureInfo.CurrentCulture) ?? "" : "", Width = 110, Padding = new Thickness(6, 4, 6, 4), Uid = "to" };
        AutomationProperties.SetName(from, T("From date")); AutomationProperties.SetName(to, T("To date"));
        static DateTimeOffset? Parse(string text) => DateTime.TryParse(text.Trim(), CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var date) ? new DateTimeOffset(date.Date) : null;
        var kindNow = _dateKind;
        void Commit()
        {
            var (a, b) = (Parse(from.Text), Parse(to.Text));
            var next = a is null && b is null ? new DateFilter() : new DateFilter(DateWindow.Custom, a, b);
            if (next == DateOf(kindNow)) return;
            if (next.IsActive) NoteFilterUse("time");
            ApplyFilter(WithDate(kindNow, next));
        }
        foreach (var box in new[] { from, to }) { box.LostKeyboardFocus += (_, _) => Commit(); box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); }; }
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(from); row.Children.Add(new TextBlock { Text = "–", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) }); row.Children.Add(to);
        panel.Children.Add(row);
        panel.Children.Add(SettingsUi.Hint(T("Date such as {0:d}; leave the field empty for no limit.", DateTime.Today)));
        return panel;
    }

    // ----- File and quality -----

    private List<string> FileParts()
    {
        var parts = new List<string>();
        if (_filter.MinMinutes is not null || _filter.MaxMinutes is not null) parts.Add(MinutesLabel(_filter.MinMinutes, _filter.MaxMinutes));
        if (_filter.Resolution != ResolutionClass.Any) parts.Add(new MediaFilter { Resolution = _filter.Resolution }.Describe().First());
        if (_filter.MinMegabytes is not null || _filter.MaxMegabytes is not null) parts.Add(RangeLabel(_filter.MinMegabytes, _filter.MaxMegabytes, "MB"));
        if (_filter.HasThumbnail is { } thumb) parts.Add(thumb ? T("With thumbnail") : T("Without thumbnail"));
        parts.AddRange(_filter.VideoCodecs.Select(TechnicalLabels.Codec));
        if (_filter.Hdr is { } hdr) parts.Add(hdr ? "HDR" : T("without HDR"));
        if (_filter.MinFrameRate is { } rate) parts.Add(T("from {0} fps", rate.ToString("0.##", CultureInfo.CurrentCulture)));
        if (_filter.MultipleFiles is { } multiple) parts.Add(multiple ? T("several versions") : T("one file"));
        return parts;
    }

    private static string RangeLabel(double? min, double? max, string unit) => (min, max) switch
    {
        ({ } a, { } b) => $"{a.ToString("0.##", CultureInfo.CurrentCulture)}–{b.ToString("0.##", CultureInfo.CurrentCulture)} {unit}",
        ({ } a, null) => T("from {0:0.##} {1}", a, unit),
        (null, { } b) => T("up to {0:0.##} {1}", b, unit),
        _ => "",
    };
    private static string MinutesLabel(double? min, double? max) => RangeLabel(min, max, "min");

    private FrameworkElement FileSection()
    {
        var panel = new StackPanel();
        panel.Children.Add(SubHeading(T("Duration"), 0));
        var presets = new (string Label, double? Min, double? Max)[] { (T("up to 5 min"), null, 5), (T("5–20 min"), 5, 20), (T("20–60 min"), 20, 60), (T("over 1 h"), 60, null) };
        panel.Children.Add(Cloud(presets.Select(p => (UIElement)FilterChip("duration:" + p.Label, p.Label, null, _filter.MinMinutes == p.Min && _filter.MaxMinutes == p.Max ? 1 : 0, () =>
        {
            var on = !(_filter.MinMinutes == p.Min && _filter.MaxMinutes == p.Max);
            if (on) NoteFilterUse("file");
            ApplyFilter(on ? _filter with { MinMinutes = p.Min, MaxMinutes = p.Max } : _filter with { MinMinutes = null, MaxMinutes = null });
        }))));
        panel.Children.Add(RangeFields(T("Minutes"), _filter.MinMinutes, _filter.MaxMinutes, (min, max) => { NoteFilterUse("file"); ApplyFilter(_filter with { MinMinutes = min, MaxMinutes = max }); }, "minutes"));
        panel.Children.Add(SubHeading(T("Minimum resolution")));
        panel.Children.Add(Choices("resolution", [("HD", ResolutionClass.HD), (T("Full HD"), ResolutionClass.FullHD), ("4K", ResolutionClass.UHD), ("8K", ResolutionClass.EightK)], _filter.Resolution, ResolutionClass.Any,
            value => { if (value != ResolutionClass.Any) NoteFilterUse("file"); ApplyFilter(_filter with { Resolution = value }); }));
        panel.Children.Add(SubHeading(T("File size")));
        panel.Children.Add(RangeFields("MB", _filter.MinMegabytes, _filter.MaxMegabytes, (min, max) => { NoteFilterUse("file"); ApplyFilter(_filter with { MinMegabytes = min, MaxMegabytes = max }); }, "megabytes"));
        panel.Children.Add(SubHeading(T("Thumbnails")));
        panel.Children.Add(Choices("thumbnail", [(T("With thumbnail"), (bool?)true), (T("Without thumbnail"), (bool?)false)], _filter.HasThumbnail, null, value => ApplyFilter(_filter with { HasThumbnail = value })));
        var technical = _filter.VideoCodecs.Count > 0 || _filter.Hdr != null || _filter.MinFrameRate != null || _filter.MultipleFiles != null;
        var open = _technicalOpen || technical;
        var toggle = new Button { Style = (Style)FindResource("Quiet"), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-6, 10, 0, 0), Uid = "technical" };
        var label = new StackPanel { Orientation = Orientation.Horizontal };
        label.Children.Add(new TextBlock { Text = open ? "\uE70D" : "\uE76C", FontFamily = SettingsUi.Icons, FontSize = 9, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 7, 0) });
        label.Children.Add(new TextBlock { Text = T("Technology: codec, HDR, frame rate, versions") });
        toggle.Content = label;
        AutomationProperties.SetName(toggle, T("Technology: codec, HDR, frame rate, versions"));
        toggle.Click += (_, _) => { _technicalOpen = !open; RefreshFilterSection(); };
        panel.Children.Add(toggle);
        if (!open) return panel;
        var codecs = _repository.GetVideoCodecs();
        var codecList = codecs.Concat(_filter.VideoCodecs.Where(c => !codecs.Any(k => k.Codec == c)).Select(c => (Codec: c, Count: 0L))).ToList();
        if (codecList.Count == 0) panel.Children.Add(SettingsUi.Hint(T("No technical data read yet. New imports read it; for the library use Organize ▾ → Read technical data.")));
        else
        {
            panel.Children.Add(SubHeading(T("Video codec"), 6));
            panel.Children.Add(Cloud(codecList.Select(item => (UIElement)FilterChip("codec:" + item.Codec, TechnicalLabels.Codec(item.Codec), item.Count > 0 ? (int)Math.Min(int.MaxValue, item.Count) : null, _filter.VideoCodecs.Contains(item.Codec) ? 1 : 0,
                () => { var on = !_filter.VideoCodecs.Contains(item.Codec); if (on) NoteFilterUse("file"); ApplyFilter(_filter with { VideoCodecs = on ? [.. _filter.VideoCodecs, item.Codec] : _filter.VideoCodecs.Where(c => c != item.Codec).ToList() }); }))));
        }
        panel.Children.Add(SubHeading("HDR"));
        panel.Children.Add(Choices("hdr", [("HDR", (bool?)true), (T("without HDR"), (bool?)false)], _filter.Hdr, null, value => ApplyFilter(_filter with { Hdr = value })));
        panel.Children.Add(SubHeading(T("Frame rate")));
        panel.Children.Add(Choices("fps", [(T("from 30 fps"), (double?)30d), (T("from 50 fps"), 50d), (T("from 60 fps"), 60d)], _filter.MinFrameRate, null, value => ApplyFilter(_filter with { MinFrameRate = value })));
        panel.Children.Add(SubHeading(T("Files")));
        panel.Children.Add(Choices("files", [(T("several versions"), (bool?)true), (T("one file"), (bool?)false)], _filter.MultipleFiles, null, value => ApplyFilter(_filter with { MultipleFiles = value })));
        return panel;
    }

    // ----- Status -----

    private List<string> StatusParts()
    {
        var parts = new List<string>();
        if (_filter.Watched is { } watched) parts.Add(watched ? T("watched") : T("never watched"));
        parts.AddRange(_reviewStates.Select(ReviewLabel));
        parts.AddRange(_filter.MarkTags.Select(tag => T("Jump mark: {0}", tag)));
        return parts;
    }

    private FrameworkElement StatusSection()
    {
        var panel = new StackPanel();
        panel.Children.Add(SubHeading(T("Watched"), 0));
        panel.Children.Add(Choices("watched", [(T("already watched"), (bool?)true), (T("never watched"), (bool?)false)], _filter.Watched, null, value => { if (value != null) NoteFilterUse("status"); ApplyFilter(_filter with { Watched = value }); }));
        panel.Children.Add(SubHeading(T("Review status")));
        panel.Children.Add(Cloud(Enum.GetValues<ReviewState>().Select(state => (UIElement)FilterChip("review:" + state, ReviewLabel(state), null, _reviewStates.Contains(state) ? 1 : 0, () =>
        {
            var on = !_reviewStates.Contains(state);
            if (on) NoteFilterUse("status");
            _reviewStates = on ? [.. _reviewStates, state] : _reviewStates.Where(s => s != state).ToList();
            _anchor = 0; UpdateFilterBar(); RenderTagFilterChips(); Refresh();
        }))));
        if (_reviewStates.Count == 0) panel.Children.Add(SettingsUi.Hint(_view == "inbox" ? T("The review inbox shows unreviewed items and those that need attention.") : T("Nothing chosen: every review status counts.")));
        var marks = _repository.GetMarkTags();
        var markList = _filter.MarkTags.Where(t => !marks.Any(m => string.Equals(m.Tag, t, StringComparison.OrdinalIgnoreCase))).Select(t => new MarkTagCount(t, 0)).Concat(marks).ToList();
        if (markList.Count > 0)
        {
            panel.Children.Add(SubHeading(T("Jump mark with tag")));
            panel.Children.Add(Cloud(markList.Select(mark => (UIElement)FilterChip("mark:" + mark.Tag, mark.Tag, mark.Count > 0 ? mark.Count : null, _filter.MarkTags.Contains(mark.Tag, StringComparer.OrdinalIgnoreCase) ? 1 : 0, () =>
            {
                var on = !_filter.MarkTags.Contains(mark.Tag, StringComparer.OrdinalIgnoreCase);
                if (on) NoteFilterUse("status");
                ApplyFilter(_filter with { MarkTags = on ? [.. _filter.MarkTags, mark.Tag] : _filter.MarkTags.Where(t => !string.Equals(t, mark.Tag, StringComparison.OrdinalIgnoreCase)).ToList() });
            }))));
            panel.Children.Add(SettingsUi.Hint(T("Finds items with a bookmark or scene carrying the tag. Short form in the search box: @tag")));
        }
        return panel;
    }

    // ----- Reset of one section -----

    private void ResetSection(string key)
    {
        switch (key)
        {
            case "rating": _filter = _filter with { UnratedOnly = false }; RatingFilter.SelectedIndex = 0; ApplyFilter(_filter); break;
            case "performers": case "studios":
                var role = key == "performers" ? TagRole.Performer : TagRole.Studio;
                ApplyFilter(_filter with { Include = _filter.Include.Where(f => f.Role != role).ToList(), Exclude = _filter.Exclude.Where(f => f.Role != role).ToList() }); break;
            case "time": ApplyFilter(_filter with { FileDate = new(), Imported = new(), Released = new() }); break;
            case "file": ApplyFilter(_filter with { MinMinutes = null, MaxMinutes = null, Resolution = ResolutionClass.Any, MinMegabytes = null, MaxMegabytes = null, HasThumbnail = null, VideoCodecs = [], Hdr = null, MinFrameRate = null, MultipleFiles = null }); break;
            case "status": _reviewStates = []; ApplyFilter(_filter with { Watched = null, MarkTags = [] }); break;
            case "kind": ShowKind(_view == FolderView ? "all" : _view); _kindFilter = null; _anchor = 0; RenderTagFilterChips(); UpdateFilterBar(); Refresh(); break;
            case "category": CategoryFilter.SelectedIndex = 0; UpdateFilterBar(); break;
            case "properties": ApplyFilter(_filter with { Profiles = [] }); break;
            default:
                var section = _sections.FirstOrDefault(s => s.Key == key);
                if (key == "tags") _filter = _filter with { UntaggedOnly = false };
                foreach (var tag in TagGroupOf(key)) _browse.SetTagMode(tag, 0);
                _anchor = 0; ApplyTagFilterChanges(); Refresh(); break;
        }
    }

    /// <summary>The bar section a tag belongs to: its category's, or "tags" for tags without one.</summary>
    private string TagSectionKey(string tag) => TagGroups().Categories.FirstOrDefault(c => c.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)).Category is { } category ? "cat:" + category : "tags";

    private IEnumerable<string> TagGroupOf(string key)
    {
        var groups = TagGroups();
        if (key == "tags") return groups.Loose;
        return key.StartsWith("cat:", StringComparison.Ordinal) ? groups.Categories.FirstOrDefault(c => "cat:" + c.Category == key).Tags ?? [] : [];
    }
}
