using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using FlixyPix.Core;
using Microsoft.Win32;

namespace FlixyPix.Desktop;

/// <summary>Library folders and the changes the last check found. Folder options are saved at once; changes are taken
/// over only for the rows left ticked.</summary>
internal sealed class LibraryFoldersWindow : Window
{
    private readonly CatalogRepository _repository;
    private readonly LibraryTaskScope _tasks;
    private readonly Action _foldersChanged;
    private readonly Func<FolderSyncPlan, IReadOnlyList<FolderChange>, bool, Task<FolderSyncResult>> _apply;
    private readonly StackPanel _folders = new();
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) };
    private readonly ListView _changes = new() { Background = Brushes.Transparent, BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox _ignoreDeclined = new() { Content = T("Don't offer deselected new files again"), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _applyButton = new() { Content = T("Apply selected"), IsEnabled = false };
    private readonly Button _check = new() { Content = T("Check now") };
    private readonly Button _adopt = new() { Visibility = Visibility.Collapsed };
    private IReadOnlyList<ChangeRow> _rows = [];
    private readonly StackPanel _kindBar = new() { Orientation = Orientation.Horizontal }, _mediaBar = new() { Orientation = Orientation.Horizontal };
    private readonly List<Action> _filterLabels = [];
    private FolderChangeKind? _kindFilter;
    private MediaKind? _mediaFilter;
    private bool _choosing;
    private readonly ProgressBar _progress = new() { Height = 4, Margin = new Thickness(0, 8, 0, 12), Visibility = Visibility.Collapsed };
    private Button? _cancel, _removeMissing;
    internal TabControl? ReviewTabs { get; private set; }
    private FolderSyncPlan? _plan;
    private CancellationTokenSource? _checking;
    internal FolderSyncPlan? Plan => _plan;
    internal IReadOnlyList<ChangeRow> Rows => _rows;

    internal sealed class ChangeRow(FolderChange change) : INotifyPropertyChanged
    {
        private bool _chosen = change.Kind != FolderChangeKind.Missing;
        public event PropertyChangedEventHandler? PropertyChanged;
        internal event Action? ChosenChanged;
        public FolderChange Change { get; } = change;
        public bool Chosen
        {
            get => _chosen;
            set { if (_chosen == value) return; _chosen = value; PropertyChanged?.Invoke(this, new(nameof(Chosen))); ChosenChanged?.Invoke(); }
        }
        public bool CanChoose => Change.Kind != FolderChangeKind.Missing;
        public string Type => KindName(Change.Kind);
        /// <summary>Kind for the colour mark of the row template.</summary>
        public string Kind => Change.Kind.ToString();
        public string MediaType => Change.MediaKind switch { MediaKind.Image => T("Image"), MediaKind.Video => T("Video"), _ => T("Audio") };
        public string Name => Path.GetFileName(Change.Path);
        public string Folder => Path.GetDirectoryName(Change.Path) ?? Change.Path;
        /// <summary>What the check saw: size and date before and now, or the former place.</summary>
        public string Detail => Change.Kind switch
        {
            FolderChangeKind.Modified => string.Join(" · ", ModifiedParts()),
            FolderChangeKind.New => State(Change.Size, Change.ModifiedAtUtc),
            FolderChangeKind.Moved => T("Previously: {0}", Path.GetDirectoryName(Change.PreviousPath) ?? ""),
            FolderChangeKind.Renamed => T("Previously: {0}", string.Equals(Path.GetDirectoryName(Change.PreviousPath), Folder, StringComparison.OrdinalIgnoreCase) ? Path.GetFileName(Change.PreviousPath) ?? "" : Change.PreviousPath ?? ""),
            _ => State(Change.PreviousSize ?? Change.Size, Change.PreviousModifiedAtUtc) is { Length: > 0 } known ? T("Last known: {0}", known) : "",
        };
        /// <summary>Why a changed file may well have the same content, so the user can decide before taking it over.</summary>
        public string Hint
        {
            get
            {
                if (Change.Kind != FolderChangeKind.Modified || Change.PreviousSize != Change.Size || Change.PreviousModifiedAtUtc is not { } before || Change.ModifiedAtUtc is not { } now) return "";
                return now < before ? T("Same size, older date: perhaps restored from a backup.") : T("Same size, newer date: often another program wrote details such as tags into the file.");
            }
        }
        public Visibility HintVisibility => Hint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        private IEnumerable<string> ModifiedParts()
        {
            if (Change.PreviousSize is { } before && Change.Size is { } now)
                yield return before == now ? T("Size unchanged") : T("Size {0} → {1} ({2})", MediaCard.SizeText(before), MediaCard.SizeText(now), (now > before ? "+" : "−") + MediaCard.SizeText(Math.Abs(now - before)));
            else if (Change.Size is { } size) yield return MediaCard.SizeText(size);
            if (Change.PreviousModifiedAtUtc is { } earlier && Change.ModifiedAtUtc is { } later)
                yield return earlier == later ? T("Date unchanged") : T("Date {0} → {1}", Stamp(earlier), Stamp(later));
            else if (Change.ModifiedAtUtc is { } date) yield return Stamp(date);
        }
        private static string Stamp(DateTimeOffset date) => date.ToLocalTime().ToString("G");
        private static string State(long? size, DateTimeOffset? date) => string.Join(" · ", new[] { size is { } bytes ? MediaCard.SizeText(bytes) : null, date is { } at ? Stamp(at) : null }.OfType<string>());
    }

    internal static string KindName(FolderChangeKind kind) => kind switch { FolderChangeKind.Moved => T("Moved"), FolderChangeKind.Renamed => T("Renamed"), FolderChangeKind.New => T("New"), FolderChangeKind.Modified => T("Changed"), _ => T("Missing") };

    internal LibraryFoldersWindow(Window owner, CatalogRepository repository, FolderSyncPlan? plan, Action foldersChanged,
     Func<FolderSyncPlan, IReadOnlyList<FolderChange>, bool, Task<FolderSyncResult>> apply, LibraryTaskScope tasks)
    {
        _tasks = tasks;
        Owner = owner; Title = T("Media folders · FlixyPix"); Width = 980; Height = 720; MinWidth = 760; MinHeight = 520; WindowStartupLocation = WindowStartupLocation.CenterOwner; FontSize = 14;
        _repository = repository; _foldersChanged = foldersChanged; _apply = apply;

        var header = new StackPanel();


        var add = new Button { Content = T("＋ Add folder …"), Style = (Style)Application.Current.Resources["Primary"] };
        add.Click += (_, _) => AddFolders();
        _check.Click += async (_, _) => await CheckAsync();
        _adopt.Click += (_, _) => AdoptFromOriginal();
        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 12) };
        foreach (var button in new[] { add, _adopt }) { button.Margin = new Thickness(0, 0, 8, 0); actions.Children.Add(button); }
        header.Children.Add(actions);


        var folderScroll = new ScrollViewer { Content = _folders, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 0, 10) };


        var changesPanel = new DockPanel();
        var changesHeader = new StackPanel();

        changesHeader.Children.Add(_summary); changesHeader.Children.Add(_progress);
        DockPanel.SetDock(changesHeader, Dock.Top); changesPanel.Children.Add(changesHeader);
        VirtualizingPanel.SetIsVirtualizing(_changes, true); VirtualizingPanel.SetVirtualizationMode(_changes, VirtualizationMode.Recycling);
        ScrollViewer.SetHorizontalScrollBarVisibility(_changes, ScrollBarVisibility.Disabled);
        // Two filters side by side: what happened, and to which kind of media. Each counts what the other one leaves.
        var filters = new WrapPanel { Margin = new Thickness(0, 4, 0, 10) };
        filters.Children.Add(FilterBar(_kindBar)); filters.Children.Add(FilterBar(_mediaBar));
        var selectShown = new Button { Content = T("Select shown"), Style = (Style)Application.Current.Resources["Quiet"], Margin = new Thickness(0, 0, 6, 8) };
        var selectNone = new Button { Content = T("Select none"), Style = (Style)Application.Current.Resources["Quiet"], Margin = new Thickness(0, 0, 0, 8) };
        selectShown.Click += (_, _) => Choose(true); selectNone.Click += (_, _) => Choose(false);
        var choosing = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        choosing.Children.Add(selectShown); choosing.Children.Add(selectNone); filters.Children.Add(choosing);
        changesHeader.Children.Add(filters);
        _ignoreDeclined.Margin = new Thickness(0, 10, 0, 0); DockPanel.SetDock(_ignoreDeclined, Dock.Bottom); changesPanel.Children.Add(_ignoreDeclined);
        changesPanel.Children.Add(_changes);


        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var close = new Button { Content = T("Close"), IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        close.Click += (_, _) => Close();
        _applyButton.Style = (Style)Application.Current.Resources["Primary"];
        _applyButton.Click += async (_, _) => await ApplyAsync();

        var cancel = new Button { Content = T("Cancel"), Style = (Style)Application.Current.Resources["Quiet"] }; _cancel = cancel; cancel.Visibility = Visibility.Collapsed; cancel.Click += (_, _) => { _checking?.Cancel(); if (Owner is MainWindow main) main.CancelFolderSync(); };
        var removeMissing = new Button { Content = T("Remove from catalog"), Style = (Style)Application.Current.Resources["Quiet"] };
        _removeMissing = removeMissing; _changes.SelectionMode = SelectionMode.Extended;
        removeMissing.IsEnabled = false; _changes.SelectionChanged += (_, _) => removeMissing.IsEnabled = _changes.SelectedItems.Cast<ChangeRow>().Any(r => r.Change.Kind == FolderChangeKind.Missing);
        removeMissing.Click += async (_, _) =>
        {
            if (_plan == null || _plan.Errors.Count > 0) return;
            try
            {
                var missing = _changes.SelectedItems.Cast<ChangeRow>().Where(r => r.Change.Kind == FolderChangeKind.Missing).Select(r => r.Change).ToList();
                if (missing.Count == 0) return;
                if (Message.Show(this, T("Remove selected missing files from the catalog? Personal details of removed items will be lost."), T("Remove from catalog"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No, yes: T("Remove")) != MessageBoxResult.Yes) return;
                foreach (var change in missing)
                {
                    var item = _repository.GetMedia(change.MediaId!.Value);
                    if (item?.Path == change.Path && item.Files.Count > 0)
                    {
                        var choice = new ChoiceDialog<MediaFile>(this, T("Main file"), T("Choose the main file."), text => item.Files.Where(f => File.Exists(f.Path)).Select(f => (f, Path.GetFileName(f.Path))).ToList());
                        choice.ShowDialog(); if (choice.Chosen is not { } replacement) continue;
                        _repository.SetMainVersion(item.Id, replacement.Path);
                    }
                }
                SetBusy(true);
                var removed = Owner is MainWindow main ? await main.RemoveMissingFolderFilesAsync(_plan, missing) : _repository.RemoveMissingFolderFiles(_plan, missing); _repository.SaveFolderSyncReport(_plan with { Changes = missing }, new FolderSyncResult(0, 0, 0, []) { Removed = removed }); await CheckAsync();
            }
            catch (OperationCanceledException) { _summary.Text = T("Operation cancelled"); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) { _summary.Text = error.Message; }
            finally { SetBusy(false); }
        };
        buttons.Children.Add(cancel); buttons.Children.Add(removeMissing); buttons.Children.Add(_check); buttons.Children.Add(_applyButton); buttons.Children.Add(close);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        DockPanel.SetDock(buttons, Dock.Right); footer.Children.Add(buttons);
        var tabs = new TabControl { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        var folderBody = new DockPanel(); DockPanel.SetDock(header, Dock.Top); folderBody.Children.Add(header); folderBody.Children.Add(folderScroll);
        ReviewTabs = tabs;
        tabs.SetResourceReference(Control.ForegroundProperty, "Text");
        tabs.Resources = new ResourceDictionary { Source = new Uri("/FlixyPix;component/Windows/LibraryFolderTabs.xaml", UriKind.Relative) };
        tabs.Style = (Style)tabs.Resources["LibraryFolderTabs"];
        _changes.ItemTemplate = (DataTemplate)tabs.Resources["FolderChangeRow"];
        tabs.SelectionChanged += (_, e) => { if (ReferenceEquals(e.OriginalSource, tabs) && tabs.IsLoaded && tabs.Template.FindName("PART_SelectedContentHost", tabs) is FrameworkElement shown) Motion.EnterResults(shown); };
        tabs.Items.Add(new TabItem { Header = T("Changes found"), Content = changesPanel });
        tabs.Items.Add(new TabItem { Header = T("Media folders"), Content = folderBody });
        var report = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 16, Margin = new Thickness(16) };
        var reportDetails = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = SettingsUi.Muted, Margin = new Thickness(16) };
        var reportPanel = new StackPanel();
        reportPanel.Children.Add(new Border { Background = SettingsUi.CardBackground, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 12, 0, 12), Child = report });
        reportPanel.Children.Add(new Expander { Header = T("Details"), Foreground = Theme.Brush("Text"), Content = reportDetails });
        void LoadReport() { var last = _repository.GetFolderSyncReport(); report.Text = last == null ? T("Not checked yet.") : last.At.ToLocalTime().ToString("g") + "\n\n" + (last.Applied ? Result(last.Result) : Describe(new FolderSyncPlan([], last.Changes, last.Unreachable, last.Errors))); reportDetails.Text = last == null ? "" : ReportDetails(last); }
        tabs.Items.Add(new TabItem { Header = T("Report"), Content = new ScrollViewer { Content = reportPanel } });
        tabs.SelectionChanged += (_, _) => { LoadReport(); _applyButton.Visibility = tabs.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed; if (_removeMissing != null) _removeMissing.Visibility = _applyButton.Visibility; _check.Visibility = tabs.SelectedIndex == 2 ? Visibility.Collapsed : Visibility.Visible; }; LoadReport();
        var body = new DockPanel(); DockPanel.SetDock(footer, Dock.Bottom); body.Children.Add(footer); body.Children.Add(tabs);
        Content = new WindowFrame(T("Media folders"), T("New files, familiar places. Keep your library up to date."), body, "", scroll: false);
        if (plan == null) tabs.SelectedIndex = 1;

        Closed += (_, _) => _checking?.Cancel();
        ShowFolders();
        if (plan != null) ShowPlan(plan); else ShowPlan(null);
    }

    private void ShowFolders()
    {
        _folders.Children.Clear();
        var folders = _repository.GetLibraryFolders();
        if (folders.Count == 0) _folders.Children.Add(SettingsUi.Hint(T("No media folders yet. Add the folders that hold your videos, images and music.")));
        foreach (var folder in folders) _folders.Children.Add(FolderCard(folder));
        var suggestions = _repository.SuggestLibraryFoldersFromOriginal();
        _adopt.Content = T("Import content folders from EazyFlixPix 4 ({0})", suggestions.Count);
        _adopt.Visibility = suggestions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _check.IsEnabled = folders.Count > 0;
    }

    private Border FolderCard(LibraryFolder folder)
    {
        var automatic = new CheckBox { Content = T("Apply automatically"), IsChecked = folder.AutoApply, Margin = new Thickness(12, 8, 0, 8) };
        var watch = SettingsUi.Switch(); watch.IsChecked = folder.Watch; AutomationProperties.SetName(watch, T("Monitor"));
        var subfolders = SettingsUi.Switch(); subfolders.IsChecked = folder.IncludeSubfolders; AutomationProperties.SetName(subfolders, T("Subfolders"));
        var kind = new ComboBox { Width = 120, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = T("Media type") };
        foreach (var label in new[] { T("All items"), T("Images"), "Videos", "Audio" }) kind.Items.Add(label);
        kind.SelectedIndex = folder.Kind is { } value ? (int)value + 1 : 0;
        var category = new TextBox { Text = folder.Category ?? "", Width = 150, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = T("Category for newly imported files") };
        AutomationProperties.SetName(category, T("Category"));
        var remove = new Button { Content = "", Style = (Style)Application.Current.Resources["IconButton"], ToolTip = T("Remove folder (items stay in the catalog)"), Margin = new Thickness(8, 0, 0, 0) };
        AutomationProperties.SetName(remove, T("Remove folder"));
        void Save(LibraryFolder changed) => Update(folders => folders.Select(f => f.Id == folder.Id ? changed : f).ToList());
        LibraryFolder Current() => _repository.GetLibraryFolders().FirstOrDefault(f => f.Id == folder.Id) ?? folder;
        automatic.Click += (_, _) => Save(Current() with { AutoApply = automatic.IsChecked == true });
        watch.Click += (_, _) => Save(Current() with { Watch = watch.IsChecked == true });
        subfolders.Click += (_, _) => Save(Current() with { IncludeSubfolders = subfolders.IsChecked == true });
        kind.SelectionChanged += (_, _) => Save(Current() with { Kind = kind.SelectedIndex == 0 ? null : (MediaKind)(kind.SelectedIndex - 1) });
        category.LostKeyboardFocus += (_, _) => { if ((Current().Category ?? "") != category.Text.Trim()) Save(Current() with { Category = category.Text }); };
        remove.Click += (_, _) => Update(folders => folders.Where(f => f.Id != folder.Id).ToList());

        var reachable = Directory.Exists(folder.Path);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = Path.GetFileName(folder.Path.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : folder.Path, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = folder.Path });
        text.Children.Add(new TextBlock
        {
            Text = reachable ? (folder.IgnoredPaths.Count > 0 ? Tn(folder.IgnoredPaths.Count, "Available · 1 file hidden", "Available · {0} files hidden") : T("Available")) : T("Unavailable · missing files are not reported here"),
            FontSize = 12,
            Foreground = reachable ? SettingsUi.Muted : SettingsUi.Error
        });
        text.Children.Add(new TextBlock { Text = folder.Path, Foreground = SettingsUi.Muted, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = folder.Path });
        var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        controls.Children.Add(Labeled(T("Monitor"), watch)); controls.Children.Add(Labeled(T("Subfolders"), subfolders));
        controls.Children.Add(automatic); controls.Children.Add(kind); controls.Children.Add(category); controls.Children.Add(remove);
        var row = new DockPanel { Margin = new Thickness(14, 10, 10, 10) };
        DockPanel.SetDock(text, Dock.Top); row.Children.Add(text); row.Children.Add(new Expander { Foreground = Theme.Brush("Text"), Header = T("Settings"), Content = new ScrollViewer { Content = controls, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto } });
        return new Border { Background = SettingsUi.CardBackground, BorderBrush = SettingsUi.CardBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 0, 8), Child = row };
    }

    private static StackPanel Labeled(string label, Control control)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = SettingsUi.Muted, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(control);
        return panel;
    }

    private void Update(Func<List<LibraryFolder>, List<LibraryFolder>> change)
    {
        _checking?.Cancel(); ShowPlan(null);
        try { _repository.SaveLibraryFolders(change(_repository.GetLibraryFolders().ToList())); }
        catch (ArgumentException error) { Message.Show(this, error.Message, T("Media folders"), MessageBoxButton.OK, MessageBoxImage.Warning); }
        ShowFolders();
        _foldersChanged();
    }

    private async void AddFolders()
    {
        var picker = new OpenFolderDialog { AddToRecent = Privacy.AddToRecent, Title = T("Add media folder"), Multiselect = true };
        if (picker.ShowDialog(this) != true) return;
        await AddFoldersAsync(picker.FolderNames);
    }

    internal Task AddFoldersAsync(IEnumerable<string> paths)
     => _tasks.Run(async libraryToken =>
    {
        var known = _repository.GetLibraryFolders().Select(f => Path.GetFullPath(f.Path).TrimEnd(Path.DirectorySeparatorChar)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = paths.Select(Path.GetFullPath).Where(p => !known.Contains(p.TrimEnd(Path.DirectorySeparatorChar))).Select(p => new LibraryFolder { Path = p }).ToList();
        if (added.Count == 0) return;
        Update(folders => [.. folders, .. added]);
        await CheckAsync();
    });

    private async void AdoptFromOriginal()
    {
        var suggestions = _repository.SuggestLibraryFoldersFromOriginal();
        if (suggestions.Count == 0) return;
        var watched = suggestions.Count(f => f.Watch);
        if (Message.Show(this, Tn(suggestions.Count, "Import 1 content folder from EazyFlixPix 4?\n\n{1}{2}\n\n", "Import {0} content folders from EazyFlixPix 4?\n\n{1}{2}\n\n", string.Join("\n", suggestions.Select(f => f.Path).Take(12)), (suggestions.Count > 12 ? "\n…" : "")) +
         Tn(watched, "EazyFlixPix 4 updated 1 of them automatically; it is monitored. FlixyPix checks the others only on request.",
          "EazyFlixPix 4 updated {0} of them automatically; they are monitored. FlixyPix checks the others only on request."),
          T("Import content folders"), MessageBoxButton.YesNo, MessageBoxImage.Question, yes: T("Start import")) != MessageBoxResult.Yes) return;
        Update(folders => [.. folders, .. suggestions]);
        await CheckAsync();
    }

    internal Task CheckAsync()
     => _tasks.Run(async libraryToken =>
    {
        _checking?.Cancel();
        if (ReviewTabs != null) ReviewTabs.SelectedIndex = 0;
        var cancellation = _checking = CancellationTokenSource.CreateLinkedTokenSource(libraryToken);
        SetBusy(true);
        _check.IsEnabled = _applyButton.IsEnabled = false;
        _summary.Text = T("Checking folders …");
        try
        {
            var progress = new Progress<FolderSyncProgress>(ReportProgress);
            var plan = Owner is MainWindow main ? await main.BuildFolderPlanAsync(cancellation.Token, progress) : await Task.Run(() => _repository.PlanFolderSync(cancellationToken: cancellation.Token, progress: progress), cancellation.Token);
            if (!cancellation.IsCancellationRequested && _checking == cancellation) { ShowPlan(plan); if (plan.Changes.Count > 0 || plan.Errors.Count > 0 || plan.Unreachable.Count > 0) _repository.SaveFolderSyncReport(plan, new(0, 0, 0, []), applied: false); }
        }
        catch (OperationCanceledException) { _summary.Text = T("Operation cancelled"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { _summary.Text = error.Message; }
        finally { if (_checking == cancellation) { SetBusy(false); _check.IsEnabled = _repository.GetLibraryFolders().Count > 0; } }
    });

    internal void ShowPlan(FolderSyncPlan? plan)
    {
        _plan = plan;
        _rows = plan?.Changes.Select(c => new ChangeRow(c)).ToList() ?? [];
        foreach (var row in _rows) row.ChosenChanged += ShowChosenCount;
        // A filter stays after a new check as long as something still matches it.
        if (_kindFilter is { } kind && !_rows.Any(r => r.Change.Kind == kind)) _kindFilter = null;
        if (_mediaFilter is { } media && !_rows.Any(r => r.Change.MediaKind == media)) _mediaFilter = null;
        BuildFilters();
        ShowRows();
        _ignoreDeclined.Visibility = plan?.Count(FolderChangeKind.New) > 0 ? Visibility.Visible : Visibility.Collapsed;
        _summary.Text = plan == null ? T("Not checked yet.") : Describe(plan);
    }

    /// <summary>Rows the two filters leave, still sorted by kind and path.</summary>
    internal IReadOnlyList<ChangeRow> ShownRows => _rows.Where(r => (_kindFilter is null || r.Change.Kind == _kindFilter) && (_mediaFilter is null || r.Change.MediaKind == _mediaFilter)).ToList();

    internal void ShowOnly(FolderChangeKind? kind, MediaKind? media)
    {
        (_kindFilter, _mediaFilter) = (kind, media);
        BuildFilters(); ShowRows();
    }

    private void ShowRows()
    {
        _changes.ItemsSource = ShownRows;
        foreach (var label in _filterLabels) label();
        ShowChosenCount();
    }

    private static Border FilterBar(Panel segments) => new()
    {
        Child = segments, BorderBrush = SettingsUi.FieldBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), ClipToBounds = true,
        Margin = new Thickness(0, 0, 12, 8), VerticalAlignment = VerticalAlignment.Top
    };

    private void BuildFilters()
    {
        _kindBar.Children.Clear(); _mediaBar.Children.Clear(); _filterLabels.Clear();
        ((FrameworkElement)_kindBar.Parent!).Visibility = ((FrameworkElement)_mediaBar.Parent!).Visibility = _rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var kinds = new FolderChangeKind?[] { null, FolderChangeKind.New, FolderChangeKind.Modified, FolderChangeKind.Moved, FolderChangeKind.Renamed, FolderChangeKind.Missing };
        foreach (var kind in kinds)
            Segment(_kindBar, "kinds", kind == null ? T("All") : KindName(kind.Value), () => _kindFilter == kind, () => _kindFilter = kind,
                () => _rows.Count(r => (kind is null || r.Change.Kind == kind) && (_mediaFilter is null || r.Change.MediaKind == _mediaFilter)));
        foreach (var media in new MediaKind?[] { null, MediaKind.Video, MediaKind.Image, MediaKind.Audio })
            Segment(_mediaBar, "media", media switch { null => T("All items"), MediaKind.Video => T("Videos"), MediaKind.Image => T("Images"), _ => T("Audio") }, () => _mediaFilter == media, () => _mediaFilter = media,
                () => _rows.Count(r => (media is null || r.Change.MediaKind == media) && (_kindFilter is null || r.Change.Kind == _kindFilter)));
    }

    /// <summary>One filter choice with the number of rows it shows; choices without rows are hidden unless chosen.</summary>
    private void Segment(Panel bar, string group, string label, Func<bool> chosen, Action choose, Func<int> count)
    {
        var option = new RadioButton { Style = (Style)Application.Current.Resources["Segment"], GroupName = group + GetHashCode(), Padding = new Thickness(12, 6, 12, 6), IsChecked = chosen() };
        option.Checked += (_, _) => { if (chosen()) return; choose(); ShowRows(); };
        void Label()
        {
            var shown = count();
            option.Content = label + " · " + shown.ToString("N0");
            option.Visibility = shown > 0 || chosen() || bar.Children.IndexOf(option) == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        _filterLabels.Add(Label); Label();
        bar.Children.Add(option);
    }

    /// <summary>Ticks or clears the rows in view; rows hidden by a filter keep their choice.</summary>
    private void Choose(bool chosen)
    {
        // Counted once at the end: a large check has a hundred thousand rows.
        _choosing = true;
        try { foreach (var row in ShownRows.Where(r => r.CanChoose)) row.Chosen = chosen; }
        finally { _choosing = false; }
        ShowChosenCount();
    }

    private void ShowChosenCount()
    {
        if (_choosing) return;
        var count = _rows.Count(r => r.CanChoose && r.Chosen);
        _applyButton.Content = count > 0 ? T("Apply selected ({0:N0})", count) : T("Apply selected");
        _applyButton.IsEnabled = count > 0 && _progress.Visibility != Visibility.Visible;
    }

    /// <summary>Notes first, then the changes by kind. A large check lists a few hundred files per kind, not every one of them.</summary>
    internal static string ReportDetails(FolderSyncReport report, int perKind = 200)
    {
        var lines = report.Result.Skipped.Concat(report.Errors).Concat(report.Unreachable.Select(f => f.Path)).ToList();
        foreach (var group in report.Changes.GroupBy(c => c.Kind).OrderBy(g => g.Key))
        {
            if (lines.Count > 0) lines.Add("");
            lines.Add(KindName(group.Key) + " · " + group.Count().ToString("N0"));
            foreach (var row in group.Take(perKind).Select(c => new ChangeRow(c))) lines.Add("   " + row.Change.Path + (row.Detail.Length > 0 ? "  —  " + row.Detail : ""));
            if (group.Count() > perKind) lines.Add("   " + T("… and {0:N0} more", group.Count() - perKind));
        }
        return string.Join("\n", lines);
    }

    internal static string Describe(FolderSyncPlan plan)
    {
        var parts = new List<string>();
        void Part(FolderChangeKind kind, Func<int, string> text) { var count = plan.Count(kind); if (count > 0) parts.Add(text(count)); }
        Part(FolderChangeKind.Modified, n => T("Changed") + ": " + n.ToString("N0"));
        Part(FolderChangeKind.New, n => Tn(n, "1 new file", "{0:N0} new files")); Part(FolderChangeKind.Moved, n => Tn(n, "1 moved file", "{0:N0} moved files"));
        Part(FolderChangeKind.Renamed, n => Tn(n, "1 renamed file", "{0:N0} renamed files")); Part(FolderChangeKind.Missing, n => Tn(n, "1 missing file", "{0:N0} missing files"));
        var text = parts.Count == 0 ? (plan.Unreachable.Count > 0 || plan.Errors.Count > 0 ? T("Not checked yet.") : T("Everything is up to date.")) : string.Join(" · ", parts) + ".";
        if (plan.Count(FolderChangeKind.Missing) > 0) text += T(" Missing files stay in the catalog with all their details and are marked as “Missing”.");
        if (plan.Unreachable.Count > 0) text += T(" Unavailable: {0}.", string.Join(", ", plan.Unreachable.Select(f => f.Path)));
        if (plan.Errors.Count > 0) text += Tn(plan.Errors.Count, " 1 folder could not be read.", " {0} folders could not be read.");
        return text;
    }

    internal Task ApplyAsync()
     => _tasks.Run(async libraryToken =>
    {
        if (_plan is not { } plan) return;
        var chosen = Rows.Where(r => r.CanChoose && r.Chosen).Select(r => r.Change).ToList();
        SetBusy(true);
        _applyButton.IsEnabled = _check.IsEnabled = false;
        try
        {
            var result = await _apply(plan, chosen, _ignoreDeclined.IsChecked == true);
            ShowFolders();
            await CheckAsync();
            _repository.SaveFolderSyncReport(plan with { Changes = chosen }, result);
            _summary.Text = Result(result) + " " + _summary.Text;
        }
        catch (OperationCanceledException) { _summary.Text = T("Operation cancelled"); _check.IsEnabled = true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            Message.Show(this, error.Message, T("Media folders"), MessageBoxButton.OK, MessageBoxImage.Warning);
            _check.IsEnabled = true;
        }
        finally { SetBusy(false); }
    });

    internal void SetBusy(bool busy)
    {
        _progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (_cancel != null) _cancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _changes.IsEnabled = _folders.IsEnabled = !busy;
        if (_removeMissing != null) _removeMissing.IsEnabled = !busy && _changes.SelectedItems.Cast<ChangeRow>().Any(r => r.Change.Kind == FolderChangeKind.Missing);
        _check.IsEnabled = !busy && _repository.GetLibraryFolders().Count > 0;
        ShowChosenCount();
    }

    internal void ReportProgress(FolderSyncProgress step)
    {
        _progress.IsIndeterminate = step.Total == null; _progress.Maximum = Math.Max(1, step.Total ?? 1); _progress.Value = step.Done;
        _summary.Text = T(step.Phase) + " · " + step.Done.ToString("N0") + (step.Total is { } total ? " / " + total.ToString("N0") : "") + " · " + Path.GetFileName(step.Path);
    }
    internal static string Result(FolderSyncResult result)
    {
        var text = T("{0:N0} imported, {1:N0} reassigned", result.Imported, result.Relinked) + " · " + T("Changed") + ": " + result.Updated.ToString("N0");
        if (result.Removed > 0) text += " · " + T("Remove from catalog") + ": " + result.Removed.ToString("N0");
        if (result.Ignored > 0) text += T(", {0:N0} hidden", result.Ignored);
        if (result.Skipped.Count > 0) text += " · " + T("Skipped") + ": " + result.Skipped.Count.ToString("N0");
        return text + ".";
    }
}
