using System.Windows;
using System.Windows.Media;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>Endless scrolling: the scroll range covers the whole result, and a window of up to <see cref="WindowSize"/>
/// media around the visible place is loaded. Nearing the window's edge loads the window around the new place.</summary>
public partial class MainWindow
{
    private const int WindowSize = 600;
    /// <summary>Distance to the window's edge at which the next window is loaded.</summary>
    private const int WindowMargin = 120;
    /// <summary>Index in the whole result that should be at the top; 0 after a new search, filter or view.</summary>
    private int _anchor { get => _browse.Anchor; set => _browse.Anchor = value; }
    private int _visibleFirst = -1, _visibleLast = -1, _windowStart, _total;
    private (MediaQuery Query, Playlist? Playlist, bool CollectionOrder)? _window;
    private Task _windowShift = Task.CompletedTask;
    private int _windowTarget = -1;
    internal Task WindowShiftTask => _windowShift;
    internal int WindowStart => _windowStart;

    private static int WindowStartFor(int anchor) => Math.Max(0, anchor - WindowSize / 4);
    private VirtualizingTilePanel? Tiles => VirtualizingTilePanel.FindPanel(MediaList);

    private void InitializeScrolling()
    {
        MediaList.AddHandler(VirtualizingTilePanel.VisibleRangeChangedEvent, new EventHandler<VisibleRangeEventArgs>(VisibleRangeChanged));
        MediaList.PreviewMouseWheel += ZoomTiles;
    }

    /// <summary>Ctrl+wheel over the tiles changes their size by one column, as in Explorer: up makes them larger.</summary>
    private void ZoomTiles(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.Control || ViewChoice.SelectedIndex == 1) return;
        e.Handled = true;
        SetTileColumns(CurrentTileColumns - Math.Sign(e.Delta));
        SaveViewPreferences();
    }

    private void VisibleRangeChanged(object? sender, VisibleRangeEventArgs e)
    {
        _visibleFirst = e.First; _visibleLast = e.Last;
        // Thumbnails are decoded for the tile as laid out, in steps, so a slightly wider window does not reload them.
        if (Tiles is { TileWidth: > 0 } panel)
            _cardContext.DecodeWidth = Math.Clamp((int)Math.Ceiling(panel.TileWidth * VisualTreeHelper.GetDpi(this).DpiScaleX / 160) * 160, 320, 1280);
        if (e.First >= 0) _anchor = e.First;
        UpdatePager(); UpdateVideoWall();
        var loadedEnd = _windowStart + MediaList.Items.Count;
        var before = _windowStart > 0 && e.First < _windowStart + WindowMargin;
        var after = loadedEnd < _total && e.Last > loadedEnd - WindowMargin;
        if (!before && !after || _window == null) return;
        _windowTarget = e.First;
        if (_windowShift.IsCompleted) _windowShift = _libraryTasks.Run(ShiftWindowAsync);
    }

    /// <summary>Loads the window around the latest requested place; requests arriving meanwhile are merged into one more load.</summary>
    private async Task ShiftWindowAsync(CancellationToken token)
    {
        while (_windowTarget >= 0 && _window is { } window && !_closing)
        {
            var target = _windowTarget; _windowTarget = -1; var request = _browse.CaptureRequest(SelectedMedia.Select(i => i.Id)); var total = _total;
            var start = Math.Max(0, Math.Min(target - WindowSize / 3, total - WindowSize));
            var general = _repository.GetPreferences().General;
            var loaded = await Task.Run(() =>
            {
                if (window.Playlist == null) return (Items: _repository.QueryItems(window.Query with { Offset = start }, token), Offset: start);
                var result = _repository.QueryPlaylistPage(window.Playlist, window.Query with { Offset = start }, window.CollectionOrder, null, token);
                return (result.Page.Items, Offset: result.Offset);
            });
            var preparedCards = await Task.Run(() => loaded.Items.Select(i => new MediaCard(i, true, general.ShowExtension, general.MarkMissingFiles, context: _cardContext)).ToList());
            if (_closing) return;
            if (!_browse.TryPublish(request.Complete((Page: loaded, Cards: preparedCards)), result =>
            {
                var selected = SelectedMedia; var cards = result.Cards;
                _refreshing = true;
                try
                {
                    _windowStart = result.Page.Offset;
                    VirtualizingTilePanel.SetWindowStart(MediaList, result.Page.Offset);
                    MediaList.ItemsSource = cards;
                    _currentImages = cards.Select(c => c.Item).Where(i => i.Kind == MediaKind.Image).ToArray();
                    // The selection survives the new window: media outside it are kept aside and come back when scrolled to.
                    var loadedIds = cards.Select(c => c.Item.Id).ToHashSet(); var selectedIds = selected.Select(i => i.Id).ToHashSet();
                    foreach (var card in cards.Where(c => selectedIds.Contains(c.Item.Id))) MediaList.SelectedItems.Add(card);
                    _offWindowSelection = selected.Where(i => !loadedIds.Contains(i.Id)).ToList(); UpdateSelectionBar();
                }
                finally { _refreshing = false; }
            })) return;
        }
    }

    /// <summary>Scrolls to an index of the whole result and waits until the window there is loaded.</summary>
    internal async Task ScrollToAsync(int index)
    {
        Tiles?.ScrollToIndex(index);
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        await _windowShift;
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }
}
