using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FlixyPix.Core;
using FlixyPix.MediaTools;
using Microsoft.Win32;

namespace FlixyPix.Desktop;

public partial class MainWindow : Window
{
    private readonly MediaCardContext _cardContext = new();
    private CatalogRepository _repository = null!;
    private DesktopSettings _settings;
    private MediaToolService _tools;
    private readonly LibraryTaskScope _libraryTasks = new(error => ErrorLog.Write("Library background work", error));
    private bool _closeInProgress, _closeCompleted;
    private CancellationTokenSource? _operation;
    private readonly PlaybackCoordinator _playback = new();
    private MpvPlayer? _player => _playback.Current?.Player;
    private VideoPlayerWindow? _videoWindow => _playback.Window;
    private bool _stoppingPlayer => _playback.IsStopping;
    private Guid? _playingId => _playback.Current?.MediaId;
    private readonly DispatcherTimer _positionTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private readonly LibraryBrowseController _browse = new();
    private string _view { get => _browse.State.View; set => _browse.Change(state => state with { View = value }); }
    private string? _tag { get => _browse.State.Tag; set => _browse.Change(state => state with { Tag = value }); }
    private Playlist? _playlist { get => _browse.Playlist; set => _browse.SelectPlaylist(value); }
    private bool _refreshing;
    private bool _closing;
    private bool _closeRequested;
    private Window? _viewer;
    private CancellationTokenSource? _collectionPlayback;
    private long _refreshVersion => _browse.Revision;
    private Task _lastRefresh = Task.CompletedTask;
    private string? _pendingDataDirectory;
    private MediaQuery _imageQuery = new() { Kind = MediaKind.Image };
    private Playlist? _imagePlaylist;
    private bool _imageCollectionOrder;
    private Task _previewReady = Task.CompletedTask;
    private long _previewVersion;
    private MediaItem? Selected => (MediaList.SelectedItem as MediaCard)?.Item ?? (MediaList.SelectedItems.Count == 0 ? _offWindowSelection.FirstOrDefault() : null);

    public MainWindow() : this(DesktopSettings.Load()) { }
    internal MainWindow(DesktopSettings settings, bool persistPreferences = true, CatalogKey? unlockedKey = null)
    {
        _persistPreferences = persistPreferences;
        InitializeComponent();
        _settings = settings;
        _tools = CreateTools();
        Privacy.Prepare(_settings.Privacy);
        AppShell.Install(this);
        AppShell.Configure(_settings);
        _instance = LibraryInstance.Claim(_settings.DataDirectory, () => Dispatcher.BeginInvoke(BringToFront))
            ?? throw new InvalidOperationException(T("This library is already open in another FlixyPix window."));
        Closing += OnClosing;
        // Tests and probes open where they expect to.
        if (_persistPreferences) SourceInitialized += (_, _) => WindowPlacement.Apply(this, _settings.MainWindowPixels, _settings.MainWindowPlace);
        InitializeStartup(unlockedKey);
    }

    private async Task InitializeLibraryAsync(CatalogKey? unlockedKey)
    {
        SourceProtection.Apply(_settings);
        DesktopImaging.Configure(_settings.DataDirectory);
        SetStartupPhase(T("Checking library"), .1);
        _repository = await SecretDialogs.OpenLibraryAsync(this, Path.Combine(_settings.DataDirectory, "catalog.db"), unlockedKey, _startup.Token, () => SetStartupPhase(T("Opening catalog"), .25));
        await _startup.OpenedAsync();
        SetStartupPhase(T("Preparing library"), .4);
        await Task.Run(() => _repository.PreparePortableLocation(AppStorage.Current.ApplicationDirectory, AppStorage.Current.Mode == StorageMode.Portable, _startup.Token), _startup.Token);
        _startup.Token.ThrowIfCancellationRequested();
        // The longest step without steps of its own: the line moves on slowly and arrives when the window is ready.
        SetStartupPhase(T("Loading view"), .9, 4);
        var startupData = await Task.Run(() => (Preferences: _repository.GetPreferences(), Drafts: _repository.GetMetadataDrafts(), Lists: _repository.LoadBrowsingLists()), _startup.Token);
        _listsKept = startupData.Lists;
        StartupMark(_listsKept ? "settings and drafts read, lists kept" : "settings and drafts read");
        try
        {
            InitializeBrowsing(startupData.Preferences);
            InitializeMainView();
            InitializeReviewEditor();
            InitializeDetailsDrafts(startupData.Drafts);
            InitializeKeyboardAccess();
            InitializeHoverPreview();
            InitializeScrolling();
            UpdateFilterBar();
            // The content also changes its size when only the interface size changes.
            ((FrameworkElement)Content).SizeChanged += (_, _) => AdaptToWidth();
            InitializeToasts();

            InitializeViewHistory(startupData.Preferences);
            AppLock.EncryptedCatalog = () => !_closing && _repository.IsEncrypted ? _repository.DatabasePath : null;
            AppShell.Hiding += PauseOnHide; AppShell.Muting += MuteOnHide;
            InitializeMarkPreviews();
            InitializeHome();
            InitializeBackupSchedule();
            _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); Refresh(); };
            _positionTimer.Tick += async (_, _) => await _libraryTasks.Run(_ => SavePlaybackPosition());
            _positionTimer.Start();

            StartupMark("window parts built");
            _showStartupRecovery?.Invoke();
            await StartAsync(mayOpenHome: _persistPreferences);
            StartupMark("start view done");
            if (_view == "home") await HomeTask;
            if (_roleBrowser != null && IsRoleView) await _roleBrowser.ProfileReady;
            _startup.Token.ThrowIfCancellationRequested();
            RememberOpenLibrary();
            StartupMark("open library noted");
            InitializeFolderSync(checkAtStart: _persistPreferences);
            StartupMark("folder sync set");
            if (_persistPreferences && DesktopSettings.LastRepair is { } repair) ShowSettingsRepair(repair);
        }
        catch
        {
            _closing = true;
            _positionTimer.Stop(); _searchTimer.Stop(); _viewSaveTimer.Stop(); _draftTimer.Stop();
            _backupTimer.Stop(); StopFolderSync();
            _searchIndexStop.Cancel(); _wallRun?.Cancel(); StopHoverPreview();
            AppShell.Hiding -= PauseOnHide; AppShell.Muting -= MuteOnHide;
            await ReleaseFailedStartAsync();
            throw;
        }
    }
    private async Task ReleaseFailedStartAsync()
    {
        try { await _libraryTasks.StopAsync(); }
        finally { _repository?.Dispose(); _instance.Dispose(); }
    }
    /// <summary>Says once that appsettings.json was repaired; the damaged file stays beside it to look at.</summary>
    private void ShowSettingsRepair(SettingsRepair repair)
    {
        if (repair.DamagedCopy is { } copy) ShowToast(repair.Summary, MessageBoxImage.Warning, T("Show damaged file"), () => { var info = new System.Diagnostics.ProcessStartInfo("explorer.exe"); info.ArgumentList.Add("/select,"); info.ArgumentList.Add(copy); System.Diagnostics.Process.Start(info); }, seconds: 12);
        else ShowToast(repair.Summary, MessageBoxImage.Warning, seconds: 12);
    }
    private void PauseOnHide() { if (_videoWindow is { HasPlayer: true } window) _ = window.PauseForPrivacyAsync(); else if (_player != null) _ = _player.PauseAsync(true); }
    private void MuteOnHide(bool muted) { if (_player != null) _ = _player.SetMuteAsync(muted); }
    private readonly LibraryInstance _instance;

    /// <summary>Another start asked for this library: show this instance in front, also out of the tray or minimized.</summary>
    internal void BringToFront()
    {
        if (_closing) return;
        AppShell.ShowAll();
        if (WindowState == WindowState.Minimized) WindowState = AppShell.RestoreState;
        Show(); Activate();
        // Activate alone may only flash the taskbar button when another program has the focus.
        Topmost = true; Topmost = false; Focus();
    }

    private MediaToolService CreateTools() => new(new MediaToolPaths(_settings.Ffmpeg, _settings.Ffprobe, _settings.Mpv));
    private void Refresh(Guid? selectId = null)
    {
        var work = RefreshAsync(selectId);
        _lastRefresh = work;
        _ = _libraryTasks.Run(_ => work);
    }
    private Task RefreshAsync(Guid? selectId = null, bool includePreviews = true, Func<bool>? publicationGuard = null)
    {
        if (_closing) return Task.CompletedTask;
        var requested = CaptureViewState();
        var resolved = LibraryBrowseController.ResolveTransition(requested);
        if (_roleBrowser?.IsVisible == true && resolved.View != _roleBrowserView && !LeaveRoleView())
        {
            var anchor = _anchor; var pendingDirectory = _pendingDirectoryState;
            var accepted = _browse.Accepted.ToViewState();
            _browse.ApplyTransition(accepted);
            PresentViewState(accepted);
            _anchor = anchor; _pendingDirectoryState = pendingDirectory;
            RefreshRoleView();
            ReturnPageIfLeft();
            return Task.CompletedTask;
        }
        _browse.ApplyTransition(resolved);
        if (resolved.View != requested.View) { ViewTitle.Text = ViewName(resolved.View); UpdateNavigation(); }
        var changesView = _pageChanging || _shownView is { } shown && !shown.SameView(requested);
        var request = _browse.BeginLoad(SelectedMedia.Select(i => i.Id));
        if (changesView || _loadingFor != null) ExpectLoading(request);
        // A later request makes this one pointless: its reading stops, so the catalog is free for the newer one at once.
        // Otherwise a search for one letter on a cold disk keeps the next letters waiting for seconds.
        _refreshStop?.Cancel();
        var stop = _refreshStop = CancellationTokenSource.CreateLinkedTokenSource(_libraryTasks.Token);
        return _libraryTasks.Track(async token =>
        {
            try { await RefreshCoreAsync(selectId, includePreviews, token, request, publicationGuard, stop.Token); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested && !token.IsCancellationRequested) { }
            // A later request brings its own page; this one no longer shows.
            finally
            {
                if (ReferenceEquals(_refreshStop, stop)) _refreshStop = null;
                stop.Dispose();
                if (_browse.IsCurrent(request))
                {
                    EndLoading();
                    if (_pageChanging) Motion.EnterPage(MediaPane); else if (_resultsChanged) Motion.EnterResults(MediaArea);
                    _pageChanging = _resultsChanged = false;
                }
            }
        });
    }
    private bool _pageChanging, _resultsChanged, _listsKept;
    private CancellationTokenSource? _refreshStop;
    /// <summary>From this many media the lists are read beside the shared connection after a change; below it they are there
    /// before the tiles, in the time of a click.</summary>
    private const int ListsBesideFrom = 20_000;
    /// <param name="superseded">Cancelled when a newer request comes; only the reading of the media list stops with it.</param>
    private async Task RefreshCoreAsync(Guid? selectId, bool includePreviews, CancellationToken token, BrowseRequest request, Func<bool>? publicationGuard, CancellationToken superseded)
    {
        if (_repository == null || _closing || !_browse.IsCurrent(request)) return;
        var state = request.State.ToViewState();
        if (_filter.HasThumbnail is null && !_filter.UnratedOnly && !_filter.UntaggedOnly || IsPublicationGroupView || IsImageSetView || IsRoleView || IsPageView) HideWorklistActions();
        if (IsPageView) { HidePublicationGroups(); HideImageSets(); await RefreshPageAsync(request, token); return; }
        HidePages();
        if (IsPublicationGroupView) { HideImageSets(); RefreshPublicationGroups(); return; }
        HidePublicationGroups();
        if (IsImageSetView) { RefreshImageSets(); return; }
        HideImageSets();
        if (IsRoleView)
        {
            var initial = await Task.Run(() => DirectoryBrowser.ReadSnapshot(_repository, token), token);
            if (!_closing && _browse.IsCurrent(request)) RefreshRoleView(initial);
            return;
        }
        HideRoleView();
        _keyboardIndex = _keyboardAnchor = null;
        var sort = state.SortOrder;
        var playlist = _playlist;
        try
        {
            // One state describes the view; saved searches and restoring the last view use the same query.
            var matches = state.ToQuery(DateTimeOffset.Now);
            StartupMark("refresh begins");
            await UpdateKeptMediaAsync(state, matches, request.Revision);
            StartupMark("kept media");
            if (!_browse.IsCurrent(request) || _closing) return;
            UpdateThumbnailCheck(state);
            var kept = _keptMedia.ToArray();
            var query = matches with { Limit = WindowSize, Offset = WindowStartFor(_anchor), KeepIds = kept };
            // A requested medium outside the view becomes the new top; one in view keeps the scroll position.
            var anchor = _anchor; var visibleFirst = _visibleFirst; var visibleLast = _visibleLast;
            // The first view of a start shows tiles; the lists and the folder tree follow right after. A view that names a tag needs them now.
            var namesTag = state.Tag != null || state.TagCategory != null;
            if (!_startupReady && !namesTag && !_listsKept) _browsingFilled ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            // While they are still being read beside the connection, a change of view does not read them a second time.
            var listsFollow = !namesTag && _browsingFilled is { Task.IsCompleted: false };
            var fillLists = false;
            var read = await Task.Run(() =>
            {
                // New media in a large library: reading the lists takes seconds and would hold the catalog. The tiles need none of it.
                if (!namesTag && _startupReady && !listsFollow && !_repository.BrowsingKnown && _repository.QuickFacets(token).MediaCount >= ListsBesideFrom) listsFollow = fillLists = true;
                MediaPage page;
                if (playlist == null)
                {
                    if (selectId is { } requested && _repository.GetPosition(query, requested, superseded) is { } position && (position < visibleFirst || position > visibleLast))
                    { anchor = position; query = query with { Offset = WindowStartFor(position) }; }
                    page = _repository.QueryPage(query, superseded);
                    StartupMark("media page");
                    if (page.TotalCount > 0 && page.Items.Count == 0)
                    { query = query with { Offset = (int)Math.Max(0, page.TotalCount - WindowSize) }; anchor = (int)page.TotalCount - 1; page = _repository.QueryPage(query, superseded); }
                }
                else
                {
                    var result = _repository.QueryPlaylistPage(playlist, query, sort == 5, selectId, superseded);
                    query = query with { Offset = result.Offset }; page = result.Page;
                }
                var facets = listsFollow ? _repository.QuickFacets(token) : _repository.GetFacets(token);
                StartupMark("facets");
                var preferences = _repository.GetPreferences();
                IReadOnlyList<TagUsage> tagUsage = listsFollow ? [] : _repository.GetTagUsage(); StartupMark("tag usage");
                var navigationCounts = listsFollow ? new NavigationCounts(0, 0, 0, 0) : _repository.GetNavigationCounts(); StartupMark("navigation counts");
                var playlists = _repository.GetPlaylists(); var searches = _repository.GetSavedSearches(); var sets = _repository.GetCollectionSets(); var target = _repository.FindTargetCollection(); StartupMark("collections");
                var finished = FinishedMedia(matches, kept); StartupMark("finished media");
                return (Preferences: preferences, Page: page, Browse: listsFollow && _startupReady ? null : BrowsingFor(facets, preferences, tagUsage, navigationCounts), Playlists: playlists, Searches: searches,
        Sets: sets, Target: target, CatalogEmpty: facets.MediaCount == 0, Finished: finished);
            });
            if (!_browse.IsCurrent(request) || _closing) return;
            var pageItems = read.Page.Items;
            var imageQuery = query with { Kind = MediaKind.Image, Offset = 0, Limit = 1 };
            var general = read.Preferences.General;
            var preparedCards = await Task.Run(() => pageItems.Select(i => new MediaCard(i, includePreviews, general.ShowExtension, general.MarkMissingFiles && includePreviews, context: _cardContext)).ToList());
            StartupMark("cards prepared");
            if (!_browse.IsCurrent(request) || _closing) return;
            if (publicationGuard is not null && (!publicationGuard() || !_browse.IsCurrentSelection(request, SelectedMedia.Select(i => i.Id)))) return;
            if (!_browse.TryPublish(request.Complete((Snapshot: read, Cards: preparedCards)), result =>
            {
                var snapshot = result.Snapshot; var cards = result.Cards;
                var selectionNow = selectId is null ? SelectedMedia.ToArray() : [];
                var keepSelection = selectionNow.Select(item => item.Id).ToHashSet();
                _windowStart = query.Offset; _total = checked((int)snapshot.Page.TotalCount); _window = (query, playlist, sort == 5);
                _currentImages = pageItems.Where(i => i.Kind == MediaKind.Image).ToArray();
                _imageQuery = imageQuery;
                _imagePlaylist = playlist; _imageCollectionOrder = sort == 5;
                LoupeListChanged();
                _cardContext.QuickMembers = snapshot.Playlists.FirstOrDefault(p => p.Kind == PlaylistKind.Quick)?.MediaIds.ToHashSet() ?? [];
                _cardContext.FinishedMembers = snapshot.Finished; _cardContext.Display = general; _cardContext.TargetMembers = snapshot.Target?.MediaIds.ToHashSet() ?? [];
                Motion.TilesLift = general.HoverLift; _history.Capacity = general.HistoryLength;
                _refreshing = true;
                _offWindowSelection = [];
                // Empty lists only for the first view: read beside the start, the real ones may already be in place.
                if (snapshot.Browse is { } browse) ApplyBrowsing(browse);
                VirtualizingTilePanel.SetTotalCount(MediaList, _total); VirtualizingTilePanel.SetWindowStart(MediaList, _windowStart);
                // Other filters, a search or another order show new results; an edit or a scroll keeps them in place.
                if (_startupReady && _shownView is { } shown && !shown.SameView(state)) _resultsChanged = true;
                MediaList.ItemsSource = cards; _shownView = state;
                if (anchor < visibleFirst || anchor > visibleLast) { MediaList.UpdateLayout(); Tiles?.ScrollToIndex(anchor); }
                if (selectId is null && keepSelection.Count > 0) foreach (var card in cards.Where(c => keepSelection.Contains(c.Item.Id))) MediaList.SelectedItems.Add(card);
                else if (selectId.HasValue && cards.FirstOrDefault(c => c.Item.Id == selectId) is { } selectedCard) { MediaList.SelectedItem = selectedCard; MediaList.ScrollIntoView(selectedCard); }
                var loadedIds = cards.Select(c => c.Item.Id).ToHashSet();
                _offWindowSelection = selectionNow.Where(item => !loadedIds.Contains(item.Id)).ToArray();
                UpdateSelectionBar();
                var playlists = snapshot.Playlists;
                if (_playlist != null) _playlist = playlists.FirstOrDefault(p => p.Id == _playlist.Id);
                ShowCollections(playlists, snapshot.Sets, snapshot.Searches, snapshot.Target);
                UpdateNavigation(); RenderTagFilterChips();
                CountLabel.Text = Tn(snapshot.Page.TotalCount, "1 item", "{0:N0} items") + TagFilterDescription() + (_category != null ? $" · {_category}" : "")
        + (_folder != null && !InFolderView ? T(" · folder filter") : "") + (_playlist != null
        ? $" · {(_playlist.Kind switch { PlaylistKind.Slideshow => T("Slideshow"), PlaylistKind.Quick => T("Quick collection"), _ => T("Playlist") })}" : "");
                if (_advancedExpression != null) CountLabel.Text += T(" · Advanced search");
                CountLabel.Text += FilterDescription();
                if (snapshot.Finished.Count > 0) CountLabel.Text += T(" · {0:N0} done", snapshot.Finished.Count);
                ShowWorklistActions(state, _total, snapshot.Finished.Count);
                _pager = (-1, -1, -1); _ = Dispatcher.BeginInvoke(UpdatePager, System.Windows.Threading.DispatcherPriority.Loaded);
                RecordView();
                EmptyState.Visibility = snapshot.Page.TotalCount == 0 ? Visibility.Visible : Visibility.Collapsed;
                ShowEmptyState(snapshot.CatalogEmpty);
                FirstRunActions.Visibility = snapshot.CatalogEmpty ? Visibility.Visible : Visibility.Collapsed;
                CollectionActions.Visibility = _playlist != null ? Visibility.Visible : Visibility.Collapsed;
                UpdateHome(snapshot.CatalogEmpty, snapshot.Page.TotalCount);
            })) return;
            if (fillLists && _browsingFilled is not { Task.IsCompleted: false }) { _browsingFilled = new(TaskCreationOptions.RunContinuationsAsynchronously); FillBrowsingAfterStart(); }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !superseded.IsCancellationRequested) { if (!_closing) Status.Text = T("The library could not be loaded: ") + ex.Message; throw; }
        finally { _refreshing = false; }
        StartupMark("view published");
        ShowDetails(includePreviews);
        StartupMark("details shown");
        WarmSearchIndex();
    }

    private MediaItem? _detailsShown;
    /// <summary>Several media, or some that paging or filtering took out of sight: tag changes reach all of them, so the bar says so and offers what works on all.</summary>
    private void UpdateSelectionBar()
    {
        var count = SelectedMedia.Count; var hidden = _offWindowSelection.Count;
        var wasShown = SelectionBar.Visibility == Visibility.Visible;
        SelectionBar.Visibility = !IsPageView && (count > 1 || hidden > 0) ? Visibility.Visible : Visibility.Collapsed;
        if (!wasShown && SelectionBar.Visibility == Visibility.Visible) Motion.Arrive(SelectionBar, 0);
        // Notices keep clear of the bar and its actions.
        _toasts.Margin = new Thickness(0, 0, 360, SelectionBar.Visibility == Visibility.Visible ? 92 : 40);
        SelectionBarText.Text = Tn(count, "1 item selected", "{0:N0} items selected") + (hidden > 0 ? T(" · {0:N0} not in this view", hidden) : "");
    }
    private void ClearSelectionClicked(object sender, RoutedEventArgs e)
    {
        _offWindowSelection = []; MediaList.SelectedItems.Clear(); ShowDetails(); MediaList.Focus();
    }
    private void SelectionTagModeClicked(object sender, RoutedEventArgs e) => SetTagWork(true);
    private void SelectionCollectionClicked(object sender, RoutedEventArgs e) => MenuBelow(sender, CollectionChoices(SelectedMedia));
    private void SelectionMoreClicked(object sender, RoutedEventArgs e) => MenuBelow(sender, MediaMenu(SelectedMedia));
    private void ShowDetails(bool includePreviews = true)
    {
        UpdateSelectionBar();
        UpdateDetailsNeed();
        RememberDetailsDraft();
        var items = SelectedMedia;
        var item = Selected;
        UpdateReviewEditor();
        var multiple = items.Count > 1;
        var single = item != null && !multiple;
        foreach (var element in new UIElement[] { SingleDetailsTop, SingleActions, SingleRating, SingleDetailsBottom }) element.Visibility = single ? Visibility.Visible : Visibility.Collapsed;
        SelectedName.Visibility = single ? Visibility.Collapsed : Visibility.Visible;
        SelectedName.Text = multiple ? Tn(items.Count, "1 item selected", "{0:N0} items selected") : item?.Title ?? T("Choose an item");
        // Showing the same medium again (for example after a tag change) keeps unsaved edits.
        var previous = _detailsShown?.Id == item?.Id ? _detailsShown : null;
        void Text(TextBox box, string? before, string? value) { if (previous == null || box.Text == (before ?? "")) box.Text = value ?? ""; }
        Text(EditTitle, previous?.Title, item?.Title);
        Text(EditDescription, previous?.Description, item?.Description);
        Text(EditCategory, previous?.Category, item?.Category);
        Text(EditReleaseDate, ReleaseText(previous?.ReleaseDate), ReleaseText(item?.ReleaseDate));
        // Rating, favourite and watch later save immediately, so the stored values are always current.
        _showingDetails = true; try { EditRating.Value = item?.Rating ?? 0; } finally { _showingDetails = false; }
        if (previous == null || EditFavorite.IsChecked == previous.IsFavorite) EditFavorite.IsChecked = item?.IsFavorite ?? false;
        if (previous == null || EditWatchLater.IsChecked == previous.WatchLater) EditWatchLater.IsChecked = item?.WatchLater ?? false;
        _detailsShown = single ? item : null;
        if (single && item != null && _detailsDrafts.TryGetValue(item.Id, out var draft)) ApplyDetailsDraft(draft);
        if (includePreviews) _previewReady = _libraryTasks.Run(_ => LoadSelectedPreviewAsync(item));
        else { ++_previewVersion; Preview.Source = null; _previewReady = Task.CompletedTask; }
        MetadataLabel.Text = multiple ? T("Tag changes apply to all selected items.") : item == null ? "" : new MediaCard(item, false, context: _cardContext).Subtitle + (TechnicalSummary(item.Technical) is { Length: > 0 } technical ? Environment.NewLine + technical : "") + (includePreviews ? (File.Exists(item.Path) ? "" : T("\nFile is currently unavailable")) : T("\nFile path kept · file not checked"));
        PathLabel.Text = multiple ? "" : item?.Path ?? "";
        Scenes.ItemsSource = item == null || multiple ? null : _repository.GetOrderedScenes(item.Id);
        Bookmarks.ItemsSource = item == null || multiple ? null : _repository.GetOrderedBookmarks(item.Id);
        // A tag chosen above the lists stays while the same medium is shown.
        if (previous == null) _markTagFilters.Clear();
        ShowMarkTagFilters();
        ShowHistory(single ? item : null);
        ShowVersions(single ? item : null);
        ShowMemberships(single ? item : null);
        if (item != null && !multiple && includePreviews) _ = EnsureMarkPreviewsAsync(item);
        UpdateTagEditor(items);
        UpdateDetailsDraftState();
    }

    private Task RunOperation(string title, Func<CancellationToken, Task> action, bool refresh = true)
     => _libraryTasks.Run(async libraryToken =>
    {
        if (_operation != null) { Status.Text = T("An operation is already running. You can cancel it below."); return; }
        using var cancellation = new CancellationTokenSource();
        _operation = cancellation;
        Progress.Visibility = CancelButton.Visibility = Visibility.Visible;
        Status.Text = title;
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await action(cancellation.Token);
            // A longer run may have been left alone; a notification tells when it is done.
            if (started.Elapsed > TimeSpan.FromSeconds(2)) { Status.Text = T("Ready · drop files here or import a folder"); ShowToast(title + T(" · completed")); }
            else Status.Text = title + T(" · completed");
        }
        catch (OperationCanceledException) { Status.Text = T("Ready · drop files here or import a folder"); ShowToast(T("Operation cancelled. Items already imported stay in the catalog."), MessageBoxImage.Warning); }
        catch (Exception ex) { Status.Text = T("Operation failed"); ErrorLog.Write("Vorgang „" + title + "“", ex); Message.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { CompleteLibraryOperation(); Progress.Visibility = CancelButton.Visibility = Visibility.Collapsed; if (refresh) await RefreshAsync(); }
    });

    private void CompleteLibraryOperation()
    {
        _operation = null;
        if (_closeRequested) _ = Dispatcher.BeginInvoke(Close);
        // New media that arrived while another operation ran are prepared now.
        else if (!_preparingMedia && _preparationQueue.Count > 0) _ = Dispatcher.BeginInvoke(() => PrepareQueuedMediaAsync());
    }

    private async Task ImportPaths(IEnumerable<string> paths)
    {
        await RunOperation(T("Import media"), async token =>
        {
            var progress = new Progress<ScanProgress>(p => Status.Text = T("Import · {0:N0} checked, {1:N0} added · {2}", p.Processed, p.Imported, Path.GetFileName(p.Path)));
            // The first import of a library: the welcome page gives way and the new items arrive while they are found.
            DispatcherTimer? growing = null;
            if (_view == "home" && _total == 0)
            {
                ApplyViewState(new ViewState { View = "all", SortOrder = 2 });
                growing = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
                growing.Tick += (_, _) => { if (_lastRefresh.IsCompleted && !_closing) Refresh(); };
                growing.Start();
            }
            try
            {
                var result = await Task.Run(() => new MediaScanner(_repository).ScanAsync(paths, progress, token), token);
                // What the files say about themselves comes first: tags, ratings and dates are there while the previews follow.
                var adopted = await AdoptFileMetadataAsync(result.ImportedIds, token, (done, total) => Status.Text = T("Read metadata") + $" · {done:N0} / {total:N0}");
                if (adopted > 0) { ShowToast(Tn(adopted, "Metadata imported for 1 item", "Metadata imported for {0:N0} items")); Refresh(); }
                await AfterImportAsync(token);
                ShowImportResult(result);
            }
            finally { growing?.Stop(); }
        });
    }
    private void ShowImportResult(ImportResult result)
    {
        if (ImportReportWanted(result)) Dialogs.Text(this, T("Import report"), T("{0} imported · {1} skipped\n\n", result.Imported, result.Skipped) + string.Join("\n", result.Errors.Concat(result.Warnings)));
    }
    internal bool ImportReportWanted(ImportResult result) => result.Errors.Count > 0 || result.Warnings.Count > 0 || _repository.GetPreferences().General.AlwaysShowImportReport;
    private async void ImportFiles(object sender, RoutedEventArgs e) { var picker = new OpenFileDialog { AddToRecent = Privacy.AddToRecent, Multiselect = true, Title = T("Import media"), Filter = MediaFormats.MediaDialogFilter }; if (picker.ShowDialog(this) == true) await ImportPaths(picker.FileNames); }
    private async void ImportFolder(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { AddToRecent = Privacy.AddToRecent, Title = T("Import media folder (including subfolders)"), Multiselect = true }; if (picker.ShowDialog(this) == true)
        {
            var watch = new CheckBox { Content = T("Monitor these folders after import"), IsChecked = true, Margin = new Thickness(0, 16, 0, 0) };
            var panel = new StackPanel(); panel.Children.Add(new TextBlock { Text = string.Join("\n", picker.FolderNames), TextWrapping = TextWrapping.Wrap }); panel.Children.Add(watch);
            var dialog = new Window { Owner = this, Title = T("Import media"), Width = 620, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var frame = new WindowFrame(T("Import media"), T("Your files stay where they are. Your library grows here."), panel, "\uE8F7");
            frame.Add(T("Cancel"), () => dialog.DialogResult = false, cancel: true); frame.Add(T("Import"), () => dialog.DialogResult = true, primary: true); dialog.Content = frame;
            if (dialog.ShowDialog() != true) return;
            await ImportPaths(picker.FolderNames);
            if (_closing) return;
            if (watch.IsChecked == true) { var folders = _repository.GetLibraryFolders().ToList(); foreach (var path in picker.FolderNames) if (!folders.Any(f => string.Equals(Path.GetFullPath(f.Path).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))) folders.Add(new LibraryFolder { Path = path }); _repository.SaveLibraryFolders(folders); RestartFolderWatcher(); }
        }
    }
    private async void OnDrop(object sender, DragEventArgs e) { if (!_startupReady) return; if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) await ImportPaths(paths); }
    private void CancelOperation(object sender, RoutedEventArgs e) => _operation?.Cancel();
    private void SearchChanged(object sender, TextChangedEventArgs e) { if (_repository == null) return; _anchor = 0; _searchTimer.Stop(); _searchTimer.Start(); }
    private void FilterChanged(object sender, SelectionChangedEventArgs e) { if (RatingFilter.SelectedIndex > 0) _filter = _filter with { UnratedOnly = false }; _anchor = 0; if (_repository == null) return; UpdateFilterBar(); RenderTagFilterChips(); Refresh(); }
    /// <summary>Enter turns short forms such as #tag, -#tag, ★4, type:video and @tag into filters; the rest stays search text.</summary>
    private void SearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _repository == null) return;
        if (IsRoleView || IsPageView || IsImageSetView || IsPublicationGroupView) return;
        var parsed = SearchShorthand.Parse(SearchBox.Text);
        if (!parsed.Recognized) return;
        e.Handled = true;
        foreach (var tag in parsed.Include) _browse.SetTagMode(tag, 1);
        foreach (var tag in parsed.Exclude) _browse.SetTagMode(tag, -1);
        if (parsed.MinimumStars is { } stars) RatingFilter.SelectedIndex = stars;
        if (parsed.Kind is { } kind) { if (_view == FolderView) _kindFilter = kind; else { _view = kind.ToString(); UpdateNavigation(); } }
        if (parsed.MarkTags.Count > 0) { _filter = _filter with { MarkTags = _filter.MarkTags.Concat(parsed.MarkTags).Distinct(StringComparer.OrdinalIgnoreCase).ToList() }; UpdateFilterBar(); }
        SearchBox.Text = parsed.Text; SearchBox.CaretIndex = SearchBox.Text.Length; _searchTimer.Stop();
        _anchor = 0; Status.Text = T("Short forms became filters");
        ApplyTagFilterChanges();
    }
    private void MediaSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing) return;
        // A plain click or arrow key starts a new selection; Ctrl or Shift extend the current one, including media scrolled away.
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 0) _offWindowSelection = [];
        ShowDetails();
    }
    private async void Navigate(object sender, RoutedEventArgs e)
    {
        await NavigateAreaAsync((string)((FrameworkElement)sender).Tag);
    }
    private void SaveDetails(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } selected || SelectedMedia.Count > 1) return;
        // Tags are saved immediately by the tag editor; keep the stored ones.
        var item = _repository.GetMedia(selected.Id) ?? selected;
        if (!TryDate(EditReleaseDate.Text, out var released))
        { Status.Text = T("The release date cannot be read; please enter it like {0}.", DateTime.Today.ToString("d", System.Globalization.CultureInfo.CurrentCulture)); EditReleaseDate.Focus(); return; }
        RememberDetailsDraft();
        var expected = _detailsDrafts.GetValueOrDefault(item.Id)?.Original ?? MetadataValues.From(item);
        try
        {
            _repository.ApplyMetadataPatches([new(item.Id, expected, new(EditTitle.Text.Trim(),
   string.IsNullOrWhiteSpace(EditCategory.Text) ? null : EditCategory.Text.Trim(), EditDescription.Text, released))], clearDraftIds: [item.Id]);
        }
        catch (MetadataConflictException error)
        { ShowMetadataConflict(error); return; }
        catch (Exception error) when (error is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        { Status.Text = error.Message; return; }
        _detailsDrafts.Remove(item.Id);
        _metadataConflicts.Remove(item.Id);
        _detailsShown = null;
        Refresh(item.Id); Status.Text = T("Media details saved");
    }

    internal static string? ReleaseText(DateOnly? date) => date?.ToString("d", System.Globalization.CultureInfo.CurrentCulture);
    /// <summary>A date in the user's format or as yyyy-MM-dd; an empty text clears it.</summary>
    internal static bool TryDate(string text, out DateOnly? date)
    {
        date = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        var styles = System.Globalization.DateTimeStyles.None;
        if (DateOnly.TryParse(text.Trim(), System.Globalization.CultureInfo.CurrentCulture, styles, out var local)) { date = local; return true; }
        if (DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, styles, out var iso)) { date = iso; return true; }
        return false;
    }

    private async void PlaySelected(object sender, RoutedEventArgs e) { if (Selected is { } item) { StopSlideshow(); await Play(item); } }
    /// <summary>A double click on an image opens it in the loupe. On another tile it unfolds the details when they are folded
    /// to their rail; with the details in place it opens the page of the medium. The buttons and stars of a tile keep their
    /// own clicks.</summary>
    private void MediaDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (OnTileControl(e.OriginalSource as DependencyObject) || Selected is not { } item) return;
        OpenFromDoubleClick(item);
    }
    internal void OpenFromDoubleClick(MediaItem item)
    {
        if (item.Kind == MediaKind.Image) { StopSlideshow(); _ = Play(item); return; }
        if (!DetailsExpanded && !DetailsNotNeeded && (_settings.DetailsCollapsed || _detailsAutoFolded)) { OpenDetailsAt(_detailsTab); return; }
        OpenDetail(item);
    }
    private void Settings(object sender, RoutedEventArgs e) => OpenSettings();
    /// <summary>Opens the settings, on <paramref name="page"/> (German rubric name) if given.</summary>
    private void OpenSettings(string? page = null)
    {
        var window = new SettingsWindow(this, _repository, _settings, _pendingDataDirectory ?? _settings.DataDirectory, EncryptionMenu, ImportOriginalDatabase, RenderSampleSheetAsync, () => ShowDatabase(), ChangeStorageMode, () => OfferProfileImport());
        if (page != null) window.SelectPage(page);
        window.ShowDialog();
        if (window.Result is not { } choice) return;
        FileAccessPolicy.Current.EnsureWritable(choice.DataDirectory);
        var settings = _settings with
        {
            DataDirectory = choice.DataDirectory,
            Ffmpeg = choice.Ffmpeg,
            Ffprobe = choice.Ffprobe,
            Mpv = choice.Mpv,
            AutoGeneratePreviews = choice.AutoGeneratePreviews,
            Theme = choice.Theme,
            InterfaceScale = window.ChosenInterfaceScale,
            ViewMode = ViewChoice.SelectedIndex,
            SortOrder = SortChoice.SelectedIndex,
            BossKey = choice.BossKey,
            MinimizeToTray = choice.MinimizeToTray,
            DetailsTabs = choice.DetailsTabs,
            Language = choice.Language,
            Privacy = choice.Privacy ?? _settings.Privacy,
            ExternalPrograms = choice.ExternalPrograms is { } programs ? [.. programs] : _settings.ExternalPrograms,
            QuickAccessFolders = choice.QuickAccessFolders is { } folders ? [.. folders] : _settings.QuickAccessFolders,
            ProtectedSourcePaths = choice.ProtectedFolders is { } guarded ? [.. guarded] : _settings.ProtectedSourcePaths,
            WebSearchProviders = choice.WebSearchProviders is { } providers ? [.. providers] : null
        };
        SourceProtection.Apply(settings, _settings.DataDirectory);
        AppShell.Configure(settings);
        if (_persistPreferences) settings.Save();
        _pendingDataDirectory = settings.DataDirectory;
        var changed = !string.Equals(_settings.DataDirectory, settings.DataDirectory, StringComparison.OrdinalIgnoreCase);
        // Keep the open catalog directory until restart; thumbnail paths must stay with this catalog.
        _settings = settings with { DataDirectory = _settings.DataDirectory }; _tools = CreateTools();
        ApplyDetailsLayout();
        Status.Text = changed ? T("Settings saved · the library folder changes after a restart. Exporting or backing up first is recommended.") : T("Settings saved");
        Refresh();
        if (choice.RegenerateThumbnails) RegenerateAllThumbnails();
        else if (settings.Language != LanguageSetup.StartedWith) OfferLanguageRestart();
    }
    /// <summary>Saves a language for this computer (from the takeover of the original); it applies after a restart.</summary>
    internal void SaveLanguage(string language)
    {
        _settings = _settings with { Language = language };
        if (_persistPreferences) (DesktopSettings.Load() with { Language = language }).Save();
    }
    internal void OfferLanguageRestart()
    {
        if (_persistPreferences && Message.Show(this, T("The new language applies after FlixyPix restarts. Restart now?"), T("Language"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes, yes: T("Restart now"), no: T("Later")) == MessageBoxResult.Yes)
        { _restartAfterClose = true; Close(); }
    }
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_startupReady && !_closeCompleted) { e.Cancel = true; RequestStartupCancellation(); return; }
        if (_storageTransferring) { e.Cancel = true; _operation?.Cancel(); return; }
        if (_closeCompleted) return;
        e.Cancel = true;
        if (_closeInProgress) return;
        if (_operation != null) { _closeRequested = true; _operation.Cancel(); Status.Text = T("Finishing the operation …"); return; }
        if (!TryBeginClose()) return;
        CloseTask = CloseLibraryAsync();
    }
    internal Task CloseTask { get; private set; } = Task.CompletedTask;

    private bool _storageTransferring;
    private bool TryBeginClose()
    {
        _closeInProgress = true;
        var accepted = false;
        try
        {
            if (!LeaveRoleView() || !ResolveDetailsDrafts()) { CancelLibrarySwitch(); return false; }
            accepted = true;
            return true;
        }
        finally { if (!accepted) { _closeInProgress = false; _closeRequested = false; } }
    }
    private bool _restartAfterClose;
    private void OnKeyDown(object sender, KeyEventArgs e)
     => _ = _libraryTasks.Run(async libraryToken =>
    {
        if (!_startupReady) return;
        if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control && !TextEntry(e.OriginalSource))
        { e.Handled = true; UndoImmediate(); return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.OemPlus or Key.Add or Key.OemMinus or Key.Subtract or Key.D0 or Key.NumPad0 && !TextEntry(e.OriginalSource))
        {
            e.Handled = true;
            InterfaceScaling.Apply(InterfaceScaling.Step(e.Key is Key.OemPlus or Key.Add ? 1 : e.Key is Key.OemMinus or Key.Subtract ? -1 : 0));
            _settings = _settings with { InterfaceScale = InterfaceScaling.Current }; if (_persistPreferences) (_settings with { DataDirectory = _pendingDataDirectory ?? _settings.DataDirectory }).Save();
            return;
        }
        if (TagWorkKey(e.Key, Keyboard.Modifiers, TextEntry(e.OriginalSource)) is { } tagWork) { e.Handled = true; await tagWork; return; }
        if (LoupeTakesKeys(e.OriginalSource) && LoupeKey(e)) { e.Handled = true; return; }
        if (await HandleLibraryKeyboardAsync(e)) { e.Handled = true; return; }
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.ButtonBase or Slider && e.Key is Key.Enter or Key.Space or Key.Left or Key.Right or Key.Up or Key.Down) return;
        // Back and forward like a browser; Alt arrives as a system key.
        if (e.Key == Key.System && Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey is Key.Left or Key.Right || e.Key is Key.BrowserBack or Key.BrowserForward)
        {
            e.Handled = true; await WalkHistoryAsync(e.SystemKey == Key.Left || e.Key == Key.BrowserBack ? -1 : 1); return;
        }
        if (OpenWithHotkey(e) || MediaMenuKey(e)) return;
        // Bookmarks, scenes and the collection tree handle these keys themselves (rename, open or play, remove).
        if (e.Key is Key.F2 or Key.Enter or Key.Delete && (Scenes.IsKeyboardFocusWithin || Bookmarks.IsKeyboardFocusWithin || CollectionTree.IsKeyboardFocusWithin)) return;
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt) && e.Key is >= Key.D1 and <= Key.D9 && !TextEntry(e.OriginalSource)) { e.Handled = OpenSavedSearchByNumber(e.Key - Key.D0); return; }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        if (e.Key == Key.Escape && SearchBox.IsKeyboardFocused && SearchBox.Text.Length > 0) { SearchBox.Clear(); e.Handled = true; }
        else if (e.Key == Key.F5 && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true; Status.Text = T("Checking media folders …");
            await CheckLibraryFoldersAsync(); if (!FolderBannerShown) Status.Text = _repository.GetLibraryFolders().Count == 0
         ? T("No media folders set up · Add ▾ → Media folders …") : T("Media folders are up to date");
        }
        else if (Keyboard.FocusedElement is not TextBox && Keyboard.FocusedElement is not ComboBox)
        {
            var digit = e.Key is >= Key.D0 and <= Key.D5 ? e.Key - Key.D0 : e.Key is >= Key.NumPad0 and <= Key.NumPad5 ? e.Key - Key.NumPad0 : -1;
            if (digit >= 0 && Keyboard.Modifiers == ModifierKeys.None && MediaList.IsKeyboardFocusWithin) { e.Handled = true; RateSelection(digit); if (digit > 0 && _filter.UnratedOnly && MediaList.SelectedIndex >= 0) await SelectIndexAsync(_windowStart + MediaList.SelectedIndex + 1); return; }
            if (e.Key == Key.F2 && MediaList.IsKeyboardFocusWithin && Selected != null && SelectedMedia.Count == 1) { e.Handled = true; EditTitle.Focus(); EditTitle.SelectAll(); return; }
            if (Keyboard.Modifiers == ModifierKeys.None && MediaList.IsKeyboardFocusWithin)
            {
                // The list itself only knows the loaded window; these keys work on the whole result.
                if (e.Key is Key.Home or Key.End) { e.Handled = true; await SelectIndexAsync(e.Key == Key.Home ? 0 : _total - 1); return; }
                if (e.Key == Key.Space && Selected is { } shown) { e.Handled = true; ShowQuickLook(shown); return; }
                if (e.Key == Key.F && SelectedMedia is { Count: > 0 } chosen) { e.Handled = true; PinFavorite(chosen); return; }
                if (e.Key == Key.B && SelectedMedia.Count > 0) { e.Handled = true; ToggleTargetCollection(); return; }
            }
            if (e.Key == Key.Enter && MediaList.IsKeyboardFocusWithin && Selected is { } item) { e.Handled = true; StopSlideshow(); await Play(item); }
            if (e.Key == Key.Delete && MediaList.IsKeyboardFocusWithin) { e.Handled = true; RemoveSelected(); }
        }
    });
}
