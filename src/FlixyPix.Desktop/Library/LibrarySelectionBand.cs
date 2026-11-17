using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>Selecting with a rectangle, as in Explorer: pressing the button on the free space or on a tile that is not selected
/// and moving the mouse draws a frame, and every tile it touches is selected. Dragging a selected tile still drags the media
/// (onto tags, collections). Ctrl keeps the selection there was and adds to it. Near the top or bottom edge the list scrolls;
/// tiles outside the loaded window are selected too when the button is released.</summary>
public partial class MainWindow
{
    /// <summary>Where the button went down, in content coordinates of the tile panel; null when no rectangle can start.</summary>
    private Point? _bandOrigin;
    private SelectionBand? _band;
    /// <summary>Gap at the tile edges that the frame must pass before it takes the tile.</summary>
    private const double BandGap = 6;
    private const double BandScrollEdge = 32;

    private sealed class SelectionBand(VirtualizingTilePanel panel, Point origin, HashSet<Guid> kept, IReadOnlyList<MediaItem> keptOutside, BandAdorner adorner, DispatcherTimer scroll)
    {
        public VirtualizingTilePanel Panel { get; } = panel;
        public Point Origin { get; } = origin;
        public HashSet<Guid> Kept { get; } = kept;
        public IReadOnlyList<MediaItem> KeptOutside { get; } = keptOutside;
        public BandAdorner Adorner { get; } = adorner;
        public DispatcherTimer Scroll { get; } = scroll;
        public List<int> Indexes { get; set; } = [];
    }

    /// <summary>Remembers where a rectangle could start; called from the list's mouse down when no media drag can start there.</summary>
    private bool OfferBand(MouseButtonEventArgs e)
    {
        _bandOrigin = null;
        if (e.ClickCount != 1 || Keyboard.Modifiers is not (ModifierKeys.None or ModifierKeys.Control) || Tiles is not { } panel || _total == 0) return false;
        for (var node = e.OriginalSource as DependencyObject; node != null && node != MediaList; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (node is ScrollBar or ButtonBase or TileRating) return false;
        var point = e.GetPosition(panel);
        if (point.X < 0 || point.Y < 0 || point.X > panel.ActualWidth || point.Y > panel.ActualHeight) return false;
        _bandOrigin = new Point(point.X, point.Y + panel.VerticalOffset);
        return true;
    }

    /// <summary>Starts the rectangle once the mouse moved far enough; true while a rectangle is drawn (the move is then handled).</summary>
    private bool MoveBand(MouseEventArgs e)
    {
        if (_band != null) { UpdateBand(); return true; }
        if (_bandOrigin is not { } origin || e.LeftButton != MouseButtonState.Pressed || Tiles is not { } panel) return false;
        var point = e.GetPosition(panel);
        if (Math.Abs(point.X - origin.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y + panel.VerticalOffset - origin.Y) < SystemParameters.MinimumVerticalDragDistance) return true;
        return StartBand(origin, Keyboard.Modifiers == ModifierKeys.Control, follow: true) && UpdateBand(point);
    }

    /// <param name="origin">Where the frame starts, in content coordinates of the tile panel.</param>
    /// <param name="add">Keeps the selection there was (Ctrl).</param>
    /// <param name="follow">Captures the mouse and scrolls at the edges; tests leave it off and pass the points themselves.</param>
    private bool StartBand(Point origin, bool add, bool follow)
    {
        _bandOrigin = null; _dragStart = null; _pendingSingleSelection = null; _clickToPlay = null;
        if (Tiles is not { } panel || AdornerLayer.GetAdornerLayer(panel) is not { } layer) return false;
        var kept = add ? SelectedMedia.Select(item => item.Id).ToHashSet() : [];
        var keptOutside = add ? _offWindowSelection.ToList() : [];
        var adorner = new BandAdorner(panel, (TryFindResource("SelectionBorder") as SolidColorBrush)?.Color ?? SystemColors.HighlightColor);
        layer.Add(adorner);
        var scroll = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(30) };
        _band = new SelectionBand(panel, origin, kept, keptOutside, adorner, scroll);
        if (follow) { scroll.Tick += (_, _) => ScrollBand(); scroll.Start(); MediaList.CaptureMouse(); }
        if (!MediaList.IsKeyboardFocusWithin) MediaList.Focus();
        return true;
    }

    /// <summary>Scrolls while the mouse is near or beyond the upper or lower edge, faster the farther out it is.</summary>
    private void ScrollBand()
    {
        if (_band is not { } band) return;
        if (Mouse.LeftButton != MouseButtonState.Pressed) { _ = EndBandAsync(); return; }
        var y = Mouse.GetPosition(band.Panel).Y; var height = band.Panel.ActualHeight;
        var step = y < BandScrollEdge ? y - BandScrollEdge : y > height - BandScrollEdge ? y - (height - BandScrollEdge) : 0;
        if (step == 0) return;
        band.Panel.SetVerticalOffset(band.Panel.VerticalOffset + Math.Clamp(step, -BandScrollEdge * 2, BandScrollEdge * 2) / 2);
        UpdateBand();
    }

    /// <summary>Draws the frame to <paramref name="to"/> (panel coordinates; the mouse when left out) and selects the loaded tiles
    /// it touches, together with the kept selection.</summary>
    private bool UpdateBand(Point? to = null)
    {
        if (_band is not { } band) return false;
        var panel = band.Panel; var point = to ?? Mouse.GetPosition(panel);
        point = new Point(Math.Clamp(point.X, 0, panel.ActualWidth), Math.Clamp(point.Y, 0, panel.ActualHeight) + panel.VerticalOffset);
        var content = new Rect(band.Origin, point);
        band.Indexes = panel.IndexesIn(content, BandGap);
        band.Adorner.Show(new Rect(content.X, content.Y - panel.VerticalOffset, content.Width, content.Height));
        var wanted = band.Indexes.Where(index => index >= _windowStart && index < _windowStart + MediaList.Items.Count)
            .Select(index => ((MediaCard)MediaList.Items[index - _windowStart]).Item.Id).ToHashSet();
        wanted.UnionWith(band.Kept);
        _refreshing = true;
        try
        {
            foreach (var card in MediaList.SelectedItems.Cast<MediaCard>().Where(card => !wanted.Contains(card.Item.Id)).ToList()) MediaList.SelectedItems.Remove(card);
            foreach (var card in MediaList.Items.Cast<MediaCard>().Where(card => wanted.Contains(card.Item.Id))) if (!MediaList.SelectedItems.Contains(card)) MediaList.SelectedItems.Add(card);
            _offWindowSelection = band.KeptOutside;
        }
        finally { _refreshing = false; }
        UpdateSelectionBar();
        return true;
    }

    /// <summary>Removes the frame; tiles of the frame outside the loaded window are fetched and kept aside like a Shift selection.</summary>
    private async Task EndBandAsync()
    {
        _bandOrigin = null;
        if (_band is not { } band) return;
        _band = null;
        band.Scroll.Stop();
        AdornerLayer.GetAdornerLayer(band.Panel)?.Remove(band.Adorner);
        if (MediaList.IsMouseCaptured) MediaList.ReleaseMouseCapture();
        var outside = band.Indexes.Where(index => index < _windowStart || index >= _windowStart + MediaList.Items.Count).ToHashSet();
        if (outside.Count > 0 && _window is { } window)
        {
            var version = _refreshVersion; var found = new List<MediaItem>();
            try
            {
                for (int offset = outside.Min(), last = outside.Max(); offset <= last; offset += WindowSize)
                {
                    var query = window.Query with { Offset = offset, Limit = Math.Min(WindowSize, last - offset + 1) };
                    var page = await Task.Run(() => window.Playlist == null ? _repository.QueryPage(query, _libraryTasks.Token)
                        : _repository.QueryPlaylistPage(window.Playlist, query, window.CollectionOrder, null, _libraryTasks.Token).Page);
                    found.AddRange(page.Items.Where((_, i) => outside.Contains(offset + i)));
                }
            }
            catch (OperationCanceledException) { return; }
            if (version != _refreshVersion) return;
            var loaded = MediaList.Items.Cast<MediaCard>().Select(card => card.Item.Id).ToHashSet();
            _offWindowSelection = band.KeptOutside.Concat(found).Where(item => !loaded.Contains(item.Id)).DistinctBy(item => item.Id).ToList();
        }
        ShowDetails();
    }

    /// <summary>The frame of the rectangle, drawn above the tiles in panel coordinates.</summary>
    private sealed class BandAdorner : Adorner
    {
        private readonly Brush _fill; private readonly Pen _edge; private Rect _rect = Rect.Empty;
        public BandAdorner(UIElement panel, Color color) : base(panel)
        {
            IsHitTestVisible = false;
            _fill = new SolidColorBrush(Color.FromArgb(0x30, color.R, color.G, color.B)); _fill.Freeze();
            _edge = new Pen(new SolidColorBrush(color), 1); _edge.Freeze();
            ClipToBounds = true;
        }
        public void Show(Rect rect) { _rect = rect; InvalidateVisual(); }
        protected override Geometry GetLayoutClip(Size layoutSlotSize) => new RectangleGeometry(new Rect(AdornedElement.RenderSize));
        protected override void OnRender(DrawingContext context)
        {
            if (_rect.IsEmpty) return;
            var rect = Rect.Intersect(_rect, new Rect(AdornedElement.RenderSize));
            if (rect.IsEmpty) return;
            context.DrawRectangle(_fill, _edge, new Rect(Math.Round(rect.X) + .5, Math.Round(rect.Y) + .5, Math.Max(0, Math.Round(rect.Width) - 1), Math.Max(0, Math.Round(rect.Height) - 1)));
        }
    }
}
