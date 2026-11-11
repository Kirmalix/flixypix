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
            // An image whose picture is gone shows its RAW file.
            var path = file ?? (item.Kind == MediaKind.Image ? RawPairs.DisplayPath(item) : MediaVersions.PlaybackPath(item, playback.PlayBestVersion, File.Exists) ?? item.Path);
            if (!File.Exists(path)) { Message.Show(this, T("The media file is unavailable. Under Tools you can assign its new location."), T("File missing")); return; }
            if (item.Kind != MediaKind.Image) _lastPlayedId = item.Id;
            // Counts for the start page (recently watched, popular, suggestions).
            item = _repository.RecordPlay(item.Id, DateTimeOffset.Now) ?? item;
            if (item.Kind == MediaKind.Image)
            {
                if (_playback.Current is null && _playback.Window is not null) await _playback.PrepareStartAsync(ReleasePlaybackAsync);
                if (!_playback.IsCurrentStart(request)) return;
                var opened = ShowImage(item);
                // The viewer starts reading once it is part of the window.
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
                await opened.ReadyTask;
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
    /// <summary>Shows an image large: among the tiles on show in the loupe, which takes their place and leafs through the
    /// images of the same list; otherwise, as from the home page or a detail page, in a viewer window.</summary>
    private ImageViewer ShowImage(MediaItem item)
    {
        var previous = _viewer; _viewer = null; previous?.Close();
        var images = _currentImages.Any(i => i.Id == item.Id) ? _currentImages.Select(i => i.Id == item.Id ? item : i).ToArray() : new[] { item };
        var imageQuery = _imageQuery;
        var imagePlaylist = _imagePlaylist; var collectionOrder = _imageCollectionOrder;
        var initial = imagePlaylist == null ? _repository.GetPosition(imageQuery, item.Id) : _repository.QueryPlaylistPage(imagePlaylist, imageQuery, collectionOrder, item.Id).Offset;
        var imageCount = !initial.HasValue ? 0 : checked((int)(imagePlaylist == null ? _repository.CountMedia(imageQuery) : _repository.QueryPlaylistPage(imagePlaylist, imageQuery, collectionOrder).Page.TotalCount));
        var loupe = initial.HasValue && LoupeFits;
        var (itemAt, peekAt) = ImageList(imageQuery, imagePlaylist, collectionOrder);
        var viewer = initial.HasValue ? new ImageViewer(images, item.Id, itemAt, imageCount, initial.Value, peekAt, embedded: loupe) : new ImageViewer(images, item.Id);
        ConfigureViewer(viewer);
        if (loupe) { OpenLoupe(viewer); return viewer; }
        CloseLoupe(reselect: false);
        var window = new ImageViewerWindow(this, viewer); _viewer = window;
        window.Closed += (_, _) => { if (ReferenceEquals(_viewer, window)) { _viewer = null; _collectionPlayback?.Cancel(); } };
        window.Show();
        return viewer;
    }

    /// <summary>The images of a list by their place in it: for showing, which takes an image out of its archive, and for
    /// a look ahead, which does not.</summary>
    private (Func<int, Task<MediaItem?>> ItemAt, Func<int, Task<MediaItem?>> PeekAt) ImageList(MediaQuery imageQuery, Playlist? imagePlaylist, bool collectionOrder)
    {
        MediaItem? At(int index) => (imagePlaylist == null ? _repository.QueryPage(imageQuery with { Offset = index, Limit = 1 }) : _repository.QueryPlaylistPage(imagePlaylist, imageQuery with { Offset = index, Limit = 1 }, collectionOrder).Page).Items.FirstOrDefault();
        return (index => _libraryTasks.Run<MediaItem?>(async token => await Task.Run(() => At(index)) is { } image ? await EnsureArchiveImageAsync(image, token) : null),
            index => _libraryTasks.Run<MediaItem?>(async token => await Task.Run(() => At(index), token)));
    }

    /// <summary>What every image viewer takes from the library: its actions and the settings of the viewer.</summary>
    private void ConfigureViewer(ImageViewer viewer)
    {
        var preferences = _repository.GetPreferences();
        viewer.DeleteMedium = image => DeleteWithQuestion(image, Window.GetWindow(viewer)); viewer.ToggleFavorite = ToggleFavorite;
        viewer.UseAsTagPicture = image => ChooseTagPicture(image, viewer); viewer.UseAsThumbnail = image => ChooseThumbnailTarget(image, viewer);
        viewer.WheelAction = preferences.General.ImageWheel; viewer.FadeOnChange = preferences.General.ImageFade; viewer.MediaKeys = preferences.Playback.MediaKeys; viewer.Slideshow = preferences.Slideshow;
        viewer.DevelopRaw = preferences.General.DevelopRaw;
        viewer.DevelopRawChanged = on => { try { _repository.SaveGeneralPreferences(_repository.GetPreferences().General with { DevelopRaw = on }); } catch (Exception error) when (error is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException) { Status.Text = error.Message; } };
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
