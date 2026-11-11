using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>The loupe: an image of the list on show, large in place of the tiles. The arrow keys leaf through the images
/// of that list and the film strip shows its neighbours. The image on show is the selection: the details beside it
/// follow, and rating, favorite, tag mode and removing work as on the tiles. Esc or G leads back to the tiles, at the
/// image last looked at.</summary>
public partial class MainWindow
{
    private ImageViewer? _loupe;
    private Border? _loupeHost;
    /// <summary>The details follow a moment after the image, so leafing through quickly does not rebuild them at every step.</summary>
    private readonly DispatcherTimer _loupeDetails = new() { Interval = TimeSpan.FromMilliseconds(140) };
    internal ImageViewer? Loupe => _loupe;
    /// <summary>The image viewer on show: the loupe or the one in a window.</summary>
    internal ImageViewer? ImageView => _loupe ?? (_viewer as ImageViewerWindow)?.View;
    /// <summary>The tiles are what is on show, so an image of them can take their place.</summary>
    private bool LoupeFits => _startupReady && (_loupe != null || MediaList.IsVisible && MediaPane.IsVisible);

    private void CloseImageViewers() { var window = _viewer; _viewer = null; window?.Close(); CloseLoupe(reselect: false); }

    private void OpenLoupe(ImageViewer viewer)
    {
        CloseLoupe(reselect: false);
        if (_loupeHost == null)
        {
            _loupeHost = new Border { Visibility = Visibility.Collapsed };
            _loupeHost.SetResourceReference(Border.BackgroundProperty, "WindowBackground");
            Grid.SetColumn(_loupeHost, Grid.GetColumn(MediaPane)); Panel.SetZIndex(_loupeHost, 25);
            ((Grid)MediaPane.Parent).Children.Add(_loupeHost);
            _loupeDetails.Tick += (_, _) => { _loupeDetails.Stop(); if (_loupe != null) ShowDetails(); };
            DetailsPane.PreviewMouseDown += (_, _) => FlushLoupeDetails();
        }
        _loupe = viewer;
        viewer.CurrentChanged += item => { if (ReferenceEquals(_loupe, viewer)) LoupeMoved(item); };
        viewer.Closed += (_, _) => LoupeClosed(viewer);
        if (QuickLook is { } quick) quick.Close();
        _loupeHost.Child = viewer; _loupeHost.Visibility = Visibility.Visible;
        // Hidden, not gone: the tiles keep their place, so the way back needs no new layout.
        MediaPane.Visibility = Visibility.Hidden;
        Dispatcher.BeginInvoke(() => { if (ReferenceEquals(_loupe, viewer)) viewer.Focus(); }, DispatcherPriority.Loaded);
    }

    /// <summary>Back to the tiles. <paramref name="reselect"/> brings the tile of the image last shown into view; a
    /// change of the place does without.</summary>
    internal void CloseLoupe(bool reselect = true)
    {
        if (_loupe is not { } viewer) return;
        _loupeReselect = reselect; viewer.Close();
    }
    private bool _loupeReselect = true;

    private void LoupeClosed(ImageViewer viewer)
    {
        if (!ReferenceEquals(_loupe, viewer)) return;
        var reselect = _loupeReselect; _loupeReselect = true;
        _loupe = null; _loupeDetails.Stop();
        if (_loupeHost != null) { _loupeHost.Child = null; _loupeHost.Visibility = Visibility.Collapsed; }
        MediaPane.Visibility = Visibility.Visible;
        if (_closing || !reselect) return;
        _ = BackToTileAsync(viewer.CurrentItem);
    }

    /// <summary>The tile of the image the loupe showed last: selected, in view and with the keyboard.</summary>
    private async Task BackToTileAsync(MediaItem? item)
    {
        try
        {
            if (item != null && _window is { } window)
            {
                var revision = _browse.Revision;
                if (MediaList.Items.OfType<MediaCard>().FirstOrDefault(card => card.Item.Id == item.Id) is { } loaded)
                {
                    MediaList.SelectedItem = loaded; MediaList.ScrollIntoView(loaded); MediaList.UpdateLayout();
                    (MediaList.ItemContainerGenerator.ContainerFromItem(loaded) as ListBoxItem)?.Focus();
                }
                else if (await Task.Run(() => PositionInView(window, item.Id).Position) is { } position && revision == _browse.Revision && _loupe == null && !_closing)
                    await SelectIndexAsync(position);
            }
            if (_loupe == null && !_closing && !MediaList.IsKeyboardFocusWithin) MediaList.Focus();
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>The image on show becomes the selection at once, so a key that follows works on it; the details follow.</summary>
    private void LoupeMoved(MediaItem item)
    {
        var refreshing = _refreshing; _refreshing = true;
        try
        {
            var card = MediaList.Items.OfType<MediaCard>().FirstOrDefault(card => card.Item.Id == item.Id);
            if (card != null) { MediaList.SelectedItem = card; _offWindowSelection = []; }
            else { MediaList.SelectedItems.Clear(); _offWindowSelection = [item]; }
        }
        finally { _refreshing = refreshing; }
        _loupeDetails.Stop(); _loupeDetails.Start();
    }

    private void FlushLoupeDetails()
    {
        if (!_loupeDetails.IsEnabled) return;
        _loupeDetails.Stop(); if (_loupe != null) ShowDetails();
    }

    /// <summary>After each refresh of the list: the loupe goes on with the list as it is now. An image that left it, as a
    /// rated one under "unrated only" or a removed one, gives way to the one that took its place.</summary>
    private async void LoupeListChanged()
    {
        if (_loupe is not { } viewer) return;
        try
        {
            var query = _imageQuery; var playlist = _imagePlaylist; var order = _imageCollectionOrder;
            var current = viewer.CurrentItem?.Id; var moves = viewer.Moves;
            var (count, position) = await Task.Run(() =>
            {
                if (playlist == null) return (checked((int)_repository.CountMedia(query)), current is { } id ? _repository.GetPosition(query, id) : null);
                var page = _repository.QueryPlaylistPage(playlist, query, order, current);
                return (checked((int)page.Page.TotalCount), current is { } wanted && page.Page.Items.FirstOrDefault()?.Id == wanted ? page.Offset : (int?)null);
            });
            if (!ReferenceEquals(_loupe, viewer) || _closing) return;
            var (itemAt, peekAt) = ImageList(query, playlist, order);
            await viewer.RebaseAsync(itemAt, peekAt, count, position, moves);
        }
        catch (Exception error) when (error is OperationCanceledException or InvalidOperationException or System.IO.IOException or Microsoft.Data.Sqlite.SqliteException) { }
    }

    /// <summary>The loupe is open and the key is not meant for a text field, a list or a slider beside it.</summary>
    private bool LoupeTakesKeys(object? source) => _loupe != null && !TextEntry(source) && !FilterFlyout.IsOpen && !ViewPopup.IsOpen
        && Keyboard.FocusedElement is not (System.Windows.Controls.ListBoxItem or System.Windows.Controls.ListBox or System.Windows.Controls.TreeViewItem or System.Windows.Controls.TreeView
            or System.Windows.Controls.Slider or System.Windows.Controls.MenuItem or System.Windows.Controls.ContextMenu);

    /// <summary>Keys in the loupe: those of the tiles that work on the selection, then those of the viewer.</summary>
    private bool LoupeKey(KeyEventArgs e)
    {
        if (_loupe is not { } viewer) return false;
        var modifiers = Keyboard.Modifiers;
        if (e.Key is not (Key.Left or Key.Right or Key.Home or Key.End)) FlushLoupeDetails();
        var digit = e.Key is >= Key.D0 and <= Key.D5 ? e.Key - Key.D0 : e.Key is >= Key.NumPad0 and <= Key.NumPad5 ? e.Key - Key.NumPad0 : -1;
        if (digit >= 0 && modifiers is ModifierKeys.None or ModifierKeys.Shift && SelectedMedia.Count > 0)
        {
            // With Shift the next image follows; under "unrated only" the rated one leaves and the next takes its place anyway.
            RateSelection(digit);
            if (modifiers == ModifierKeys.Shift && !(digit > 0 && _filter.UnratedOnly)) viewer.Navigate(1);
            return true;
        }
        if (modifiers == ModifierKeys.None)
            switch (e.Key)
            {
                case Key.F when SelectedMedia is { Count: > 0 } chosen: PinFavorite(chosen); return true;
                case Key.B when SelectedMedia.Count > 0: ToggleTargetCollection(); return true;
                case Key.Delete: RemoveSelected(); return true;
                case Key.F2 when SelectedMedia.Count == 1: OpenDetailsAt("details"); EditTitle.Focus(); EditTitle.SelectAll(); return true;
            }
        return viewer.HandleKey(e);
    }
}
