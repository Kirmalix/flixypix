using System.Windows;
using System.Windows.Threading;

namespace FlixyPix.Desktop;

/// <summary>Another view that takes a moment says so, while the old one stays back. A quick change shows nothing of it.</summary>
public partial class MainWindow
{
    private DispatcherTimer? _loadingTimer;
    private BrowseRequest? _loadingFor;

    /// <summary>From now on the view of <paramref name="request"/> is awaited. Replacing it keeps a hint already shown, so it does not flicker.</summary>
    private void ExpectLoading(BrowseRequest request)
    {
        if (!_startupReady || _closing) return;
        _loadingFor = request;
        if (_loadingTimer == null)
        {
            _loadingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _loadingTimer.Tick += (_, _) => WatchLoading();
        }
        _loadingTimer.Stop(); _loadingTimer.Start();
    }

    private void WatchLoading()
    {
        // Given up without another view following: nothing else would end this.
        if (_closing || _loadingFor is not { } request || !_browse.IsCurrent(request)) { EndLoading(); ReturnPageIfLeft(); return; }
        if (LoadingHint.Visibility == Visibility.Visible) return;
        LoadingHint.Visibility = Visibility.Visible;
        Motion.Arrive(LoadingHint, 0);
    }

    private void EndLoading()
    {
        _loadingFor = null; _loadingTimer?.Stop();
        LoadingHint.Visibility = Visibility.Collapsed;
    }

    private void ReturnPageIfLeft()
    {
        if (!_pageChanging) return;
        _pageChanging = false;
        Motion.EnterPage(MediaPane);
    }
}
