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

/// <summary>Opening a medium in the player or the image viewer, and stopping playback.</summary>
public partial class MainWindow
{
    /// <param name="file">A version of the medium to play; otherwise the main file or, as set, the best version (P6).</param>
    private Task Play(MediaItem item, double? start = null, string? file = null)
     => _libraryTasks.Run(async libraryToken =>
    {
        if (item.Kind == MediaKind.Video && IsRoleView && !LeaveRoleView()) return;
        var request = _playback.BeginStart();
        try
        {
            item = await EnsureArchiveImageAsync(item, libraryToken);
            libraryToken.ThrowIfCancellationRequested();
            if (!_playback.IsCurrentStart(request)) return;
            var playback = _repository.GetPreferences().Playback;
            var path = file ?? (item.Kind == MediaKind.Image ? item.Path : MediaVersions.PlaybackPath(item, playback.PlayBestVersion, File.Exists) ?? item.Path);
            if (!File.Exists(path)) { Message.Show(this, T("The media file is unavailable. Under Tools you can assign its new location."), T("File missing")); return; }
            if (item.Kind != MediaKind.Image) _lastPlayedId = item.Id;
            // Counts for the start page (recently watched, popular, suggestions).
            item = _repository.RecordPlay(item.Id, DateTimeOffset.Now) ?? item;
            if (item.Kind == MediaKind.Image)
            {
                if (_playback.Current is null && _playback.Window is not null) await _playback.PrepareStartAsync(ReleasePlaybackAsync);
                if (!_playback.IsCurrentStart(request)) return;
                ShowImage(item);
                if (_viewer is ImageViewerWindow imageViewer) await imageViewer.ReadyTask;
                return;
            }
            // The next video opens where the previous one was, in the same window size or full screen.
            (Rect Bounds, bool Fullscreen)? previous = _videoWindow is { IsLoaded: true } shown ? (shown.IsFullscreen ? shown.RestoreBounds : new Rect(shown.Left, shown.Top, shown.Width, shown.Height), shown.IsFullscreen) : null;
            await _playback.PrepareStartAsync(ReleasePlaybackAsync);
            if (!_playback.IsCurrentStart(request) || _closing) return;
            var source = PlaybackSource.Resolve(item, path);
            var window = new VideoPlayerWindow(this, item, _repository, source) { Topmost = playback.AlwaysOnTop }; _playback.Stage(request, window);
            // Volume of the previous video, or the start volume (original "remembervolume", "defaultvolume").
            window.InitialVolume = playback.RememberVolume ? _settings.PlayerVolume : playback.StartVolume;
            window.Closing += (_, _) => { if (playback.RememberVolume) _settings = _settings with { PlayerVolume = (int)Math.Round(window.Volume) }; };
            ConnectPlayer(window, item);
            if (previous is { } place && !place.Bounds.IsEmpty)
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = place.Bounds.Left; window.Top = place.Bounds.Top; window.Width = place.Bounds.Width; window.Height = place.Bounds.Height;
                if (place.Fullscreen) window.Loaded += (_, _) => { if (!window.IsFullscreen) window.ToggleFullscreen(); };
            }
            window.Closing += async (_, args) =>
         {
          if (_stoppingPlayer || !ReferenceEquals(_videoWindow, window)) return;
          args.Cancel = true; await StopPlayer();
      };
            window.Show(); var handle = await window.ReadyTask;
            if (!window.IsLoaded) return;
            var player = await _tools.PlayAsync(path, start ?? (playback.ResumePlayback ? item.PlaybackPositionSeconds : 0), handle, cancellationToken: libraryToken, hardwareDecoding: playback.HardwareDecoding, reportMouse: false);
            if (!window.IsLoaded || !_playback.IsCurrentStart(request) || !ReferenceEquals(_videoWindow, window))
            {
                await player.DisposeAsync();
                if (_playback.Current is null && ReferenceEquals(_playback.Window, window)) await _playback.PrepareStartAsync(ReleasePlaybackAsync);
                return;
            }
            long? history;
            try { history = _repository.BeginPlayback(item.Id, DateTimeOffset.Now); }
            catch { await player.DisposeAsync(); throw; }
            var session = new PlaybackSession(item.Id, player, window, history);
            if (!_playback.Publish(request, session)) { await player.DisposeAsync(); return; }
            await window.AttachPlayerAsync(player);
            if (!ReferenceEquals(_playback.Current, session)) return;
            if (item.Kind == MediaKind.Video) await RevealPlaybackAsync(session);
            if (!ReferenceEquals(_playback.Current, session)) return;
            _ = ContinueAtEndAsync(player, window, item);
            Status.Text = T("Playing · {0} · controls in the player window", item.Title);
        }
        catch (OperationCanceledException) { if (item.Kind != MediaKind.Image && _playback.IsCurrentStart(request)) await StopPlayer(); }
        catch (Exception ex) { if (item.Kind != MediaKind.Image && _playback.IsCurrentStart(request)) await StopPlayer(); if (!_closing) Message.Show(this, ex.Message, T("Playback"), MessageBoxButton.OK, MessageBoxImage.Warning); }
    });
    private void ShowImage(MediaItem item)
    {
        var previous = _viewer; _viewer = null; previous?.Close();
        var images = _currentImages.Any(i => i.Id == item.Id) ? _currentImages.Select(i => i.Id == item.Id ? item : i).ToArray() : new[] { item };
        var imageQuery = _imageQuery;
        var imagePlaylist = _imagePlaylist; var collectionOrder = _imageCollectionOrder;
        var initial = imagePlaylist == null ? _repository.GetPosition(imageQuery, item.Id) : _repository.QueryPlaylistPage(imagePlaylist, imageQuery, collectionOrder, item.Id).Offset;
        _viewer = initial.HasValue ? new ImageViewerWindow(this, images, item.Id, index => _libraryTasks.Run<MediaItem?>(async token =>
        {
            var page = await Task.Run(() => imagePlaylist == null ? _repository.QueryPage(imageQuery with { Offset = index, Limit = 1 }) : _repository.QueryPlaylistPage(imagePlaylist, imageQuery with { Offset = index, Limit = 1 }, collectionOrder).Page);
            return page.Items.FirstOrDefault() is { } image ? await EnsureArchiveImageAsync(image, token) : null;
        }), _imageCount, initial.Value) : new ImageViewerWindow(this, images, item.Id);
        var viewer = (ImageViewerWindow)_viewer; var preferences = _repository.GetPreferences();
        viewer.DeleteMedium = image => DeleteWithQuestion(image, viewer); viewer.ToggleFavorite = ToggleFavorite;
        viewer.UseAsTagPicture = image => ChooseTagPicture(image, viewer); viewer.UseAsThumbnail = image => ChooseThumbnailTarget(image, viewer);
        viewer.WheelAction = preferences.General.ImageWheel; viewer.FadeOnChange = preferences.General.ImageFade; viewer.MediaKeys = preferences.Playback.MediaKeys; viewer.Slideshow = preferences.Slideshow;
        viewer.Closed += (_, _) => { if (ReferenceEquals(_viewer, viewer)) { _viewer = null; _collectionPlayback?.Cancel(); } };
        _viewer.Show();
    }
    private Task SavePlaybackPosition() => _playback.Current is { } session ? SavePlaybackPositionAsync(session, final: false) : Task.CompletedTask;

    private async Task SavePlaybackPositionAsync(PlaybackSession session, bool final)
    {
        try
        {
            await PlaybackPositionCheckpoint.SaveAsync(session.MediaId, session.Player.GetPositionAsync,
             () => final || ReferenceEquals(_playback.Current, session),
             (id, seconds) => _repository.UpdatePlaybackPosition(id, seconds));
        }
        catch (Exception error) { Status.Text = T("Playback position not saved: ") + error.Message; }
    }

    private Task StopPlayer() => _playback.StopAsync(ReleasePlaybackAsync);

    private async Task ReleasePlaybackAsync(PlaybackSession? session, VideoPlayerWindow? window)
    {
        try
        {
            if (session is not null)
            {
                await SavePlaybackPositionAsync(session, final: true);
                FinishPlayback(session);
            }
        }
        finally
        {
            try { if (session is not null) await session.Player.DisposeAsync(); }
            catch (IOException error) { Status.Text = T("Player stopped: ") + error.Message; }
            finally { window?.Close(); }
        }
    }
}
