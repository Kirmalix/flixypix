using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>Remembering the view: it is saved on close and every half minute while it changes, and restored on start as
/// far as the library wants ("Beim Start wiederherstellen": off, view only, everything). Back and forward walk through the
/// views of this session (Alt+←/→, mouse buttons 4/5). The window's place is kept on this computer.</summary>
public partial class MainWindow
{
    private readonly ViewHistory _history = new();
    private readonly DispatcherTimer _viewSaveTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool _walkingHistory;
    private Guid? _lastPlayedId;
    private string? _savedViewJson;
    internal ViewHistory History => _history;

    private void InitializeViewHistory(CatalogPreferences? preferences = null)
    {
        _history.Capacity = (preferences ?? _repository.GetPreferences()).General.HistoryLength;
        _viewSaveTimer.Tick += (_, _) => SaveViewNow();
        _viewSaveTimer.Start();
        MouseUp += (_, e) =>
        {
            if (e.ChangedButton is MouseButton.XButton1 or MouseButton.XButton2) { e.Handled = true; _ = WalkHistoryAsync(e.ChangedButton == MouseButton.XButton1 ? -1 : 1); }
        };
    }

    /// <summary>Called after each refresh: a new view becomes a history entry, scrolling only updates the current one.</summary>
    private void RecordView()
    {
        if (_walkingHistory || _closing) return;
        _history.Record(CaptureViewState() with { LastPlayedId = _lastPlayedId });
        BackButton.IsEnabled = _history.CanGoBack; ForwardButton.IsEnabled = _history.CanGoForward;
    }

    internal Task WalkHistoryAsync(int direction)
     => _libraryTasks.Run(async token =>
    {
        // Back from the loupe leads to the tiles it stands in for.
        if (_loupe != null && direction < 0) { CloseLoupe(); return; }
        if (!LeaveRoleView()) return;
        if (CaptureViewState() is var now) _history.Record(now); // keep the current scroll place in the entry we leave
        var target = direction < 0 ? _history.Back() : _history.Forward();
        if (target is null) return;
        _walkingHistory = true;
        try
        {
            ApplyViewState(_repository.Sanitize(target, out _));
            await _lastRefresh;
            await RestorePlaceAsync(target);
        }
        finally { _walkingHistory = false; BackButton.IsEnabled = _history.CanGoBack; ForwardButton.IsEnabled = _history.CanGoForward; }
    });

    private void BackClicked(object sender, RoutedEventArgs e) => _ = WalkHistoryAsync(-1);
    private void ForwardClicked(object sender, RoutedEventArgs e) => _ = WalkHistoryAsync(1);

    /// <summary>Stores the view in the catalog when it changed since the last save; cheap, it keeps the browsing caches.</summary>
    internal void SaveViewNow()
    {
        if (_repository == null || !_startupReady || _restoringView) return;
        try
        {
            var state = CaptureViewState() with { LastPlayedId = _lastPlayedId };
            var json = System.Text.Json.JsonSerializer.Serialize(state with { SavedAt = default });
            if (json == _savedViewJson) return;
            _repository.SaveViewState(state); _savedViewJson = json;
        }
        catch (Exception error) when (error is ObjectDisposedException or ArgumentException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException) { ErrorLog.Write("Ansicht merken", error); }
    }

    /// <summary>Restores the last view as the library's setting asks; without a usable stored state the media overview opens.</summary>
    internal async Task RestoreLastViewAsync()
    {
        _restoringView = true;
        try { await RestoreLastViewCoreAsync(); }
        finally { _restoringView = false; }
    }

    private async Task RestoreLastViewCoreAsync()
    {
        var general = _repository.GetPreferences().General;
        var stored = general.RestoreLastView ? _repository.GetViewState() : null;
        StartupMark("last view read");
        if (!general.RestoreLastView) { Refresh(); await _lastRefresh; RecordView(); return; }
        if (stored is null)
        {
            ApplyViewState(new ViewState { View = "all", SortOrder = _settings.SortOrder, ViewMode = _settings.ViewMode, TileWidth = _settings.TileWidth });
            await _lastRefresh; RecordView(); return;
        }
        var restored = await Task.Run(() => { var sanitized = _repository.Sanitize(stored, out var removed); StartupMark("last view checked"); return (State: sanitized.Restricted(general.RestoreScope), Dropped: removed); });
        if (_closing) return;
        var state = restored.State; var dropped = restored.Dropped;
        ApplyViewState(state, state.SavedSearchId is { } search ? _repository.GetSavedSearches().FirstOrDefault(s => s.Id == search)?.Name : null);
        await _lastRefresh;
        StartupMark("last view shown");
        if (general.RestoreScope == RestoreScope.All)
        {
            await RestorePlaceAsync(state);
            StartupMark("place restored");
            if (dropped.Count > 0) ShowToast(T("No longer present: ") + string.Join(", ", dropped), MessageBoxImage.Warning);
        }
        _lastPlayedId = stored.LastPlayedId;
        RecordView();
    }

    private bool _restoringView;

    /// <summary>Scrolls to where the state was (the anchor medium if it is still in the result, else the saved index) and
    /// restores the whole selection, including media outside the loaded window.</summary>
    private async Task RestorePlaceAsync(ViewState state)
    {
        var restoring = _restoringView;
        _restoringView = true;
        try { await RestorePlaceCoreAsync(state); }
        finally { _restoringView = restoring; }
    }

    private async Task RestorePlaceCoreAsync(ViewState state)
    {
        if (IsImageSetView || IsPublicationGroupView || IsPageView) return;
        if (IsRoleView) { _roleBrowser?.RestoreDirectoryState(state.Directory); return; }
        if (_window is not { } window) return;
        var request = _browse.CaptureRequest(SelectedMedia.Select(i => i.Id));
        var index = state.Position;
        // Usually the medium still stands where it stood; numbering the whole result is only needed if not.
        if (state.AnchorId is { } anchor && !(window.Playlist == null && await Task.Run(() => _repository.IsAtPosition(window.Query, index, anchor, _libraryTasks.Token)))
            && await Task.Run(() => PositionInView(window, anchor).Position) is { } found) index = found;
        if (!_browse.IsCurrentSelection(request, SelectedMedia.Select(i => i.Id)) || _closing) return;
        index = Math.Clamp(index, 0, Math.Max(0, _total - 1));
        if (index > 0) await ScrollToAsync(index);
        var selected = await Task.Run(() => _repository.GetViewSelection(state.Selection), _libraryTasks.Token);
        if (!_browse.IsCurrentSelection(request, SelectedMedia.Select(i => i.Id)) || _closing) return;
        var wanted = selected.Select(i => i.Id).ToHashSet();
        _refreshing = true;
        try
        {
            MediaList.SelectedItems.Clear();
            var loaded = MediaList.Items.Cast<MediaCard>().ToArray();
            foreach (var card in loaded.Where(c => wanted.Contains(c.Item.Id))) MediaList.SelectedItems.Add(card);
            var loadedIds = loaded.Select(c => c.Item.Id).ToHashSet();
            _offWindowSelection = selected.Where(i => !loadedIds.Contains(i.Id)).ToArray(); UpdateSelectionBar();
        }
        finally { _refreshing = false; }
        ShowDetails(); UpdatePager();
    }

    /// <summary>The stored place of the main window when the library wants it back ("Fensterplatz merken") and it is usable.</summary>
    internal WindowPlace? PlaceToRestore() =>
     _repository.GetPreferences().General.RememberWindow && _settings.MainWindowPlace is { } place && place.Width >= MinWidth && place.Height >= MinHeight ? place : null;

    private void SaveWindowPlace()
    {
        // The window takes its place before the library is open, so the wish of the library is kept in this choice.
        if (!_repository.GetPreferences().General.RememberWindow) _settings = _settings with { MainWindowPlace = null, MainWindowPixels = null };
        else
        {
            var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
            if (bounds.IsEmpty || bounds.Width <= 0) return;
            _settings = _settings with { MainWindowPlace = new WindowPlace(bounds.Left, bounds.Top, bounds.Width, bounds.Height, WindowState == WindowState.Maximized), MainWindowPixels = WindowPlacement.Read(this) };
        }
        try { (_settings with { DataDirectory = _pendingDataDirectory ?? _settings.DataDirectory }).Save(); }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException) { ErrorLog.Write("Fensterposition merken", error); }
    }
}
