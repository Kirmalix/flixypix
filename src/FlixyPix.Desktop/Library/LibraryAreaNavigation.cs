using FlixyPix.Core;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
    private void RememberCurrentArea()
    {
        // A pending navigation still shows the old tiles. Never save that mixed state.
        if (_startupReady && _lastRefresh.IsCompletedSuccessfully && !_restoringView)
            _browse.RememberArea(CaptureViewState());
    }

    private Task _lastAreaNavigation = Task.CompletedTask;

    internal Task NavigateAreaAsync(string view) => _lastAreaNavigation = _libraryTasks.Run(async token =>
    {
        if (!LeaveRoleView()) { UpdateNavigation(); return; }
        RememberCurrentArea();
        var state = _browse.RecallArea(view) ?? new ViewState
        {
            View = view, SortOrder = Math.Max(0, SortChoice.SelectedIndex),
            ViewMode = ViewChoice.SelectedIndex, TileWidth = (int)Math.Round(TileSize.Value)
        };
        _browse.Invalidate();
        var origin = _browse.CaptureRequest(SelectedMedia.Select(i => i.Id));
        state = await Task.Run(() => _repository.Sanitize(state, out _), token);
        if (_closing || !_browse.IsCurrentSelection(origin, SelectedMedia.Select(i => i.Id))) return;
        if (!ApplyViewState(state)) return;
        var request = _browse.CaptureRequest(SelectedMedia.Select(i => i.Id));
        await _lastRefresh;
        if (!_browse.IsCurrent(request) || _closing) return;
        await RestorePlaceAsync(state);
        if (!_browse.IsCurrent(request) || _closing) return;
        RecordView();
        SaveViewPreferences();
    });
}
