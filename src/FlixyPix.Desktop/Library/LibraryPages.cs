using System.IO;
using System.Windows;
using System.Windows.Controls;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>Normal work stays in the main window: the galleries, the scene wall, the media jobs and the detail page of a
/// medium are pages that take the place of the tiles and take part in back and forward.</summary>
public partial class MainWindow
{
    private readonly HashSet<UIElement> _pages = [];
    private readonly Dictionary<UIElement, Visibility> _pageCovered = [];
    private SceneWallPage? _sceneWall;
    private MediaDetailPage? _detailPage;

    private bool IsPageView => _browse.Kind is LibraryViewKind.Gallery or LibraryViewKind.Wall or LibraryViewKind.Jobs or LibraryViewKind.Detail or LibraryViewKind.Order;
    private bool IsDetailView => _browse.Kind == LibraryViewKind.Detail;
    internal MediaDetailPage? DetailPage => _detailPage;
    private FrameworkElement? ActivePage => _pages.OfType<FrameworkElement>().FirstOrDefault(page => page.Visibility == Visibility.Visible);

    private void HidePages()
    {
        foreach (var page in _pages) page.Visibility = Visibility.Collapsed;
        foreach (var (element, visibility) in _pageCovered) element.Visibility = visibility;
        _pageCovered.Clear();
        if (_detailPage?.Shown != null)
        {
            // The medium of the detail page was selected only for the inspector.
            _detailPage.Release();
            _offWindowSelection = [];
        }
    }

    private void ShowPage(UIElement page)
    {
        var keep = _detailPage?.Shown;
        HideRoleView();
        foreach (var other in _pages) if (other != page) other.Visibility = Visibility.Collapsed;
        foreach (var (element, visibility) in _pageCovered) element.Visibility = visibility;
        _pageCovered.Clear();
        if (_pages.Add(page))
        {
            Grid.SetRow(page, 1); Grid.SetRowSpan(page, 4); Panel.SetZIndex(page, 20);
            MediaPane.Children.Add(page);
        }
        foreach (UIElement element in MediaPane.Children)
            if (!_pages.Contains(element) && Grid.GetRow(element) >= 1) { _pageCovered.TryAdd(element, element.Visibility); element.Visibility = Visibility.Collapsed; }
        page.Visibility = Visibility.Visible;
    }

    private async Task RefreshPageAsync(BrowseRequest request, CancellationToken token)
    {
        if (_detailPage?.Shown != null && !IsDetailView) { _detailPage.Release(); _offWindowSelection = []; }
        switch (_browse.Kind)
        {
            case LibraryViewKind.Gallery: await RefreshGalleryAsync(request, token); break;
            case LibraryViewKind.Detail: await RefreshDetailAsync(request, token); break;
            case LibraryViewKind.Order: await RefreshOrderAsync(request, token); break;
            case LibraryViewKind.Wall:
                ShowPage(WallPage());
                _sceneWall!.UseSearch(SearchBox.Text);
                FinishPage(ViewName(_view));
                break;
            case LibraryViewKind.Jobs:
                ShowPage(JobsPage());
                FinishPage(ViewName(_view));
                break;
        }
    }

    private void FinishPage(string title, string count = "")
    {
        ViewTitle.Text = title; CountLabel.Text = count;
        AdaptToWidth(); UpdateNavigation(); RecordView();
    }

    // ----- Scene wall -----

    private SceneWallPage WallPage()
    {
        if (_sceneWall != null) return _sceneWall;
        var page = new SceneWallPage(_repository, ToolExists(_settings.Ffmpeg, "ffmpeg") ? _tools : null, ClipFolder, (item, seconds) => Play(item, seconds));
        // The media menu of the tile's video, led by playing from the mark.
        page.MenuRequested = tile => OpenMediaMenu(tile.Mark.Media, MediaMenuPlace.Wall, [MenuSpec.Command(T("Play from here"), () => _ = page.Open(tile), ((char)0xE768).ToString())]);
        return _sceneWall = page;
    }

    /// <summary>Goes to the scene wall; a click on a tile plays its video from the mark.</summary>
    internal SceneWallPage ShowSceneWall()
    {
        var page = WallPage();
        if (_view != "wall") ApplyViewState(new ViewState { View = "wall", SortOrder = Math.Max(0, SortChoice.SelectedIndex), ViewMode = ViewChoice.SelectedIndex, TileWidth = (int)Math.Round(TileSize.Value) });
        return page;
    }

    // ----- Media jobs -----

    private MediaQueuePage JobsPage() => _queueWindow ??= new MediaQueuePage(this, _settings.DataDirectory, _tools, Selected?.Path, [], ApplyScenePreviewResults);

    /// <summary>Goes to the media jobs; they run on whatever is shown meanwhile.</summary>
    private void OpenQueue()
    {
        try
        {
            var inputs = _playlist?.MediaIds.Select(_repository.GetMedia).OfType<MediaItem>().Select(i => i.Path).ToList() ?? [];
            var source = Selected?.Path;
            JobsPage().UseContext(source, inputs);
            if (_view != "jobs") ApplyViewState(new ViewState { View = "jobs", SortOrder = Math.Max(0, SortChoice.SelectedIndex), ViewMode = ViewChoice.SelectedIndex, TileWidth = (int)Math.Round(TileSize.Value) });
        }
        catch (Exception ex) { Message.Show(this, ex.Message, T("Media jobs"), MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    // ----- Detail page -----

    /// <summary>Opens the page of a medium; the current view first keeps its selection, so back finds it again.</summary>
    internal void OpenDetail(MediaItem item)
    {
        RecordView();
        ApplyViewState(new ViewState { View = "detail", DetailId = item.Id, SortOrder = Math.Max(0, SortChoice.SelectedIndex), ViewMode = ViewChoice.SelectedIndex, TileWidth = (int)Math.Round(TileSize.Value) }, item.Title);
    }

    private MediaDetailPage DetailPageFor()
    {
        if (_detailPage != null) return _detailPage;
        return _detailPage = new MediaDetailPage(_cardContext)
        {
            Play = (item, seconds) => { StopSlideshow(); _ = Play(item, seconds); },
            OpenOther = OpenDetail,
            ShowFolder = item => { OpenFolder(Path.GetDirectoryName(item.Path)); Refresh(item.Id); },
            FindSimilar = item => ShowSimilarMedia(item),
            OpenCollection = OpenCollection,
            MenuRequested = (item, target) => Menus.Open(target, MediaMenu([item]), MenuFailed),
        };
    }

    private async Task RefreshDetailAsync(BrowseRequest request, CancellationToken token)
    {
        if (_browse.State.DetailId is not { } id) return;
        var details = await Task.Run(() =>
        {
            if (_repository.GetMedia(id) is not { } item) return null;
            var meta = new MediaCard(item, false, context: _cardContext).Subtitle;
            return new MediaDetails(item, meta, _repository.GetOrderedScenes(id), _repository.GetOrderedBookmarks(id), HistoryLines(item, _repository.GetPreferences().History),
             _repository.GetPlaylists().Where(p => p.MediaIds.Contains(id)).OrderBy(p => p.Kind != PlaylistKind.Quick).ThenBy(CollectionName, StringComparer.CurrentCultureIgnoreCase).ToList(), SimilarTo(item, token));
        }, token);
        if (_closing || !_browse.IsCurrent(request)) return;
        if (details == null) { ApplyViewState(new ViewState()); return; }
        var page = DetailPageFor();
        ShowPage(page);
        page.Show(details);
        _refreshing = true;
        try { MediaList.SelectedItems.Clear(); _offWindowSelection = [details.Item]; }
        finally { _refreshing = false; }
        FinishPage(new MediaCard(details.Item, false, context: _cardContext).Title, details.Meta);
        ShowDetails();
    }

    /// <summary>Media sharing tags with this one, best rated first; without tags the rest of its folder.</summary>
    private IReadOnlyList<MediaItem> SimilarTo(MediaItem item, CancellationToken token)
    {
        static SearchExpression Has(string tag) => SearchExpression.Where(new() { Field = SearchField.Tag, Comparison = SearchComparison.Equals, Text = tag });
        var tags = item.Tags.Take(6).ToArray();
        var query = tags.Length > 0 ? new MediaQuery { Expression = tags.Length == 1 ? Has(tags[0]) : SearchExpression.Any(tags.Select(Has).ToArray()) } : new MediaQuery { Folder = Path.GetDirectoryName(item.Path), IncludeSubfolders = false };
        return _repository.QueryPage(query with { Kind = item.Kind, Sort = MediaSort.Rating, Descending = true, Limit = 13 }, token).Items.Where(other => other.Id != item.Id).Take(12).ToList();
    }
}
