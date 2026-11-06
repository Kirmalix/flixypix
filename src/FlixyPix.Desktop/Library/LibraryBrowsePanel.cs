using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace FlixyPix.Desktop;

/// <summary>The panel beside the media: the folder tree while browsing folders, or the filters on demand. The left bar
/// stays for navigation only. Folders are a place to walk through (tree, path and subfolders), so inside this view the
/// folder is not a filter chip.</summary>
public partial class MainWindow
{
    private const double PanelMinimum = 200, PanelMaximum = 480;
    private const string FolderView = "folders";
    private static readonly string FolderGlyph = ((char)0xE8B7).ToString();
    private string? _folderNavigationKey;

    private bool ShowsMediaGrid => !(_view == "home" || IsRoleView || IsImageSetView || IsPublicationGroupView);
    private bool InFolderView => _view == FolderView && _playlist == null;
    internal bool FilterPanelShown => FilterPane.Visibility == Visibility.Visible;
    internal bool FolderTreeShown => FolderPane.Visibility == Visibility.Visible;

    private void InitializeBrowsePanel()
    {
        SearchFrame.SizeChanged += (_, e) => ActiveTagFilters.MaxWidth = Math.Max(120, e.NewSize.Width * .7);
        ShowFilterTab(_settings.FilterPanelTab);
        UpdateBrowsePanel();
    }

    /// <summary>The filters take the place of the tree while they are open; closing them brings the tree back.</summary>
    private void UpdateBrowsePanel()
    {
        var filters = ShowsMediaGrid && _settings.FilterPanelOpen; var wasOpen = FilterPanelShown;
        var tree = !filters && InFolderView && !_settings.FolderTreeHidden;
        FilterPane.Visibility = filters ? Visibility.Visible : Visibility.Collapsed;
        FolderPane.Visibility = tree ? Visibility.Visible : Visibility.Collapsed;
        PanelResizer.Visibility = filters || tree ? Visibility.Visible : Visibility.Collapsed;
        BrowsePanelColumn.Width = new GridLength(filters || tree ? Math.Clamp(_settings.BrowsePanelWidth, PanelMinimum, PanelMaximum) : 0);
        FolderTreeButton.Visibility = InFolderView && !tree ? Visibility.Visible : Visibility.Collapsed;
        IncludeFolderChildren.Visibility = InFolderView ? Visibility.Visible : Visibility.Collapsed;
        if (filters && !wasOpen && FilterMoreTab.Visibility == Visibility.Visible) RenderFilterMore();
    }

    private void PanelResizing(object sender, DragDeltaEventArgs e) =>
        BrowsePanelColumn.Width = new GridLength(Math.Clamp(BrowsePanelColumn.ActualWidth + e.HorizontalChange, PanelMinimum, PanelMaximum));

    // ----- Filters -----

    private void FilterButtonClicked(object sender, RoutedEventArgs e) => _ = ToggleFilterPanelAsync();
    /// <summary>Pages without media have nothing to filter; the button then opens the filters over all items.</summary>
    internal async Task ToggleFilterPanelAsync()
    {
        var open = !ShowsMediaGrid || !_settings.FilterPanelOpen;
        _settings = _settings with { FilterPanelOpen = open };
        SaveViewPreferences();
        if (!ShowsMediaGrid) await NavigateAreaAsync("all");
        UpdateBrowsePanel();
    }

    private void FilterTabClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tab }) return;
        _settings = _settings with { FilterPanelTab = tab };
        SaveViewPreferences();
        ShowFilterTab(tab);
    }

    private void ShowFilterTab(string tab)
    {
        var more = tab == "more";
        FilterTabTags.IsChecked = !more; FilterTabMore.IsChecked = more;
        FilterTagsTab.Visibility = more ? Visibility.Collapsed : Visibility.Visible;
        FilterMoreTab.Visibility = more ? Visibility.Visible : Visibility.Collapsed;
        if (more) RenderFilterMore();
    }

    /// <summary>The common filters at hand. A control being used already shows its value; rebuilding it would take the focus away.</summary>
    private void RenderFilterMore()
    {
        if (FilterMoreHost.IsKeyboardFocusWithin) return;
        var panel = new StackPanel();
        panel.Children.Add(RatingPanel(AllFiltersButton));
        var duration = DurationPanel(AllFiltersButton);
        duration.Margin = new Thickness(0, 14, 0, 0);
        panel.Children.Add(duration);
        FilterMoreHost.Child = panel;
    }

    /// <summary>Every filter section, for what the panel does not hold; opens at the section shown last.</summary>
    private void AllFiltersClicked(object sender, RoutedEventArgs e)
    {
        if (FilterFlyout.IsOpen) { FilterFlyout.IsOpen = false; return; }
        var label = _filterSection != null && _pills.ContainsKey(_filterSection) ? _filterSection : _pills.Keys.First();
        _pills[label].Button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }

    // ----- Folders -----

    private void FolderTreeToggleClicked(object sender, RoutedEventArgs e)
    {
        // While the filters cover the tree, the button brings the tree back.
        var hide = !_settings.FolderTreeHidden && !_settings.FilterPanelOpen;
        _settings = _settings with { FolderTreeHidden = hide, FilterPanelOpen = false };
        SaveViewPreferences();
        UpdateBrowsePanel();
    }

    internal void OpenFolder(string? path)
    {
        if (!InFolderView)
        {
            // Filters of the area left behind stay there; here they could hide what was asked for.
            var state = _browse.RecallArea(FolderView) ?? new() { View = FolderView, SortOrder = Math.Max(0, SortChoice.SelectedIndex), ViewMode = ViewChoice.SelectedIndex, TileWidth = (int)Math.Round(TileSize.Value) };
            ApplyViewState(state with { Folder = path, SearchText = null, Position = 0, AnchorId = null, Selection = [] });
            return;
        }
        RememberCurrentArea();
        _folder = path; _anchor = 0;
        Refresh();
        ViewTitle.Text = FolderTitle();
        UpdateNavigation();
    }

    private void ShowInFolderClicked(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } item) return;
        OpenFolder(Path.GetDirectoryName(item.Path));
        Refresh(item.Id);
    }

    private string FolderTitle() => _folder is not { } folder ? T("All folders")
        : Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)) is { Length: > 0 } name ? name : folder;

    /// <summary>The nodes from the top of the tree down to a folder; only the top when the tree does not hold it.</summary>
    private List<BrowseNode> FolderChain(string? path)
    {
        var tops = Folders.ItemsSource as IEnumerable<BrowseNode> ?? [];
        var top = tops.FirstOrDefault();
        if (top == null) return [];
        if (path == null) return [top];
        bool Walk(BrowseNode node, List<BrowseNode> chain)
        {
            chain.Add(node);
            if (string.Equals(node.Path, path, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var child in node.Children) if (Walk(child, chain)) return true;
            chain.RemoveAt(chain.Count - 1);
            return false;
        }
        var found = new List<BrowseNode>();
        return Walk(top, found) ? found : [top];
    }

    /// <summary>Path and subfolders above the media; rebuilt only when the folder or its content changed.</summary>
    private void RenderFolderNavigation()
    {
        var shown = InFolderView;
        FolderCrumbs.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (!shown) { SubfolderScroll.Visibility = Visibility.Collapsed; _folderNavigationKey = null; return; }
        var chain = FolderChain(_folder);
        var current = chain.LastOrDefault();
        var children = current?.Children ?? [];
        var searching = SearchBox.Text.Length > 0;
        SubfolderScroll.Visibility = children.Count > 0 && !searching ? Visibility.Visible : Visibility.Collapsed;
        var key = string.Join('\n', chain.Select(n => n.Path)) + "\f" + string.Join('\n', children.Select(n => n.Path + "\t" + n.Count));
        if (key == _folderNavigationKey) return;
        var moved = _folderNavigationKey?.Split('\f')[0] != key.Split('\f')[0];
        _folderNavigationKey = key;
        RenderFolderCrumbs(chain);
        SubfolderCards.Children.Clear();
        foreach (var child in children) SubfolderCards.Children.Add(SubfolderCard(child));
        if (moved) RevealInFolderTree(chain);
    }

    private void RenderFolderCrumbs(IReadOnlyList<BrowseNode> chain)
    {
        FolderCrumbs.Children.Clear();
        for (var index = 0; index < chain.Count; index++)
        {
            var node = chain[index];
            var crumb = new Button { Content = node.FolderName, Style = (Style)FindResource("Quiet"), Padding = new Thickness(5, 2, 5, 2), FontSize = 12, ToolTip = node.FolderTip };
            crumb.Click += (_, _) => OpenFolder(node.Path);
            FolderCrumbs.Children.Add(crumb);
            if (index < chain.Count - 1) FolderCrumbs.Children.Add(new TextBlock { Text = "›", Foreground = SettingsUi.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(1, 0, 1, 0) });
        }
    }

    private Button SubfolderCard(BrowseNode node)
    {
        var row = new DockPanel { Width = 176 };
        var glyph = new TextBlock { Text = FolderGlyph, FontFamily = SettingsUi.Icons, FontSize = 14, Foreground = SettingsUi.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0) };
        var count = new TextBlock { Text = node.Count.Trim(), FontSize = 11, Foreground = SettingsUi.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(glyph, Dock.Left); DockPanel.SetDock(count, Dock.Right);
        row.Children.Add(glyph); row.Children.Add(count);
        row.Children.Add(new TextBlock { Text = node.FolderName, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        var card = new Button { Content = row, Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 8, 8), ToolTip = node.FolderTip };
        card.SetResourceReference(BackgroundProperty, "CardBackground");
        System.Windows.Automation.AutomationProperties.SetName(card, node.FolderName);
        card.Click += (_, _) => OpenFolder(node.Path);
        return card;
    }

    /// <summary>Unfolds the way to the folder and brings its row into view, however the folder was reached.</summary>
    private void RevealInFolderTree(IReadOnlyList<BrowseNode> chain)
    {
        foreach (var node in chain) node.IsExpanded = true;
        Dispatcher.BeginInvoke(() =>
        {
            if (_closing || !FolderTreeShown) return;
            ItemsControl parent = Folders; TreeViewItem? row = null;
            foreach (var node in chain)
            {
                parent.UpdateLayout();
                row = parent.ItemContainerGenerator.ContainerFromItem(node) as TreeViewItem;
                if (row == null) return;
                parent = row;
            }
            row?.BringIntoView();
        }, DispatcherPriority.Loaded);
    }
}
