using FlixyPix.Core;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
    private async Task RevealPlaybackAsync(PlaybackSession session)
    {
        if (!ReferenceEquals(_playback.Current, session) || _closing) return;
        var revision = _browse.Revision;
        int? position = null;
        if (!IsRoleView && !IsPageView && !IsImageSetView && !IsPublicationGroupView && _view != "home" && _window is { } window)
            position = await Task.Run(() => PositionInView(window, session.MediaId).Position);
        if (_closing || revision != _browse.Revision || !ReferenceEquals(_playback.Current, session)) return;
        if (position is null)
        {
            ApplyViewState(new ViewState { View = "Video", SortOrder = Math.Max(0, SortChoice.SelectedIndex), ViewMode = ViewChoice.SelectedIndex, TileWidth = TileWidthSetting });
            var refresh = RefreshAsync(session.MediaId, publicationGuard: () => ReferenceEquals(_playback.Current, session));
            _lastRefresh = refresh;
            revision = _browse.Revision;
            await refresh;
        }
        else
        {
            var refresh = RefreshAsync(session.MediaId, publicationGuard: () => ReferenceEquals(_playback.Current, session));
            _lastRefresh = refresh;
            revision = _browse.Revision;
            await refresh;
        }
        if (_closing || revision != _browse.Revision || !ReferenceEquals(_playback.Current, session)) return;
        UpdatePager();
    }
}
