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
 private async Task Play(MediaItem item, double? start = null, string? file = null)
 {
  try
  {
   item = await EnsureArchiveImageAsync(item);
   var playback = _repository.GetPreferences().Playback;
   var path = file ?? (item.Kind == MediaKind.Image ? item.Path : MediaVersions.PlaybackPath(item, playback.PlayBestVersion, File.Exists) ?? item.Path);
   if (!File.Exists(path)) { Message.Show(this, T("The media file is unavailable. Under Tools you can assign its new location."), T("File missing")); return; }
   if (item.Kind != MediaKind.Image) _lastPlayedId = item.Id;
   // Counts for the start page (recently watched, popular, suggestions).
   item = _repository.RecordPlay(item.Id, DateTimeOffset.Now) ?? item;
   if (item.Kind == MediaKind.Image)
   {
    ShowImage(item);
    if (_viewer is ImageViewerWindow imageViewer) await imageViewer.ReadyTask;
    return;
   }
   // The next video opens where the previous one was, in the same window size or full screen.
   (Rect Bounds, bool Fullscreen)? previous = _videoWindow is { IsLoaded: true } shown ? (shown.IsFullscreen ? shown.RestoreBounds : new Rect(shown.Left, shown.Top, shown.Width, shown.Height), shown.IsFullscreen) : null;
   await StopPlayer();
   var window = new VideoPlayerWindow(this,item,_repository) { Topmost = playback.AlwaysOnTop }; _videoWindow = window;
   // Volume of the previous video, or the start volume (original "remembervolume", "defaultvolume").
   window.InitialVolume = playback.RememberVolume ? _settings.PlayerVolume : playback.StartVolume;
   window.Closing += (_, _) => { if (playback.RememberVolume) _settings = _settings with { PlayerVolume = (int)Math.Round(window.Volume) }; };
   ConnectPlayer(window, item);
   if (previous is { } place && !place.Bounds.IsEmpty)
   {
    window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = place.Bounds.Left; window.Top = place.Bounds.Top; window.Width = place.Bounds.Width; window.Height = place.Bounds.Height;
    if (place.Fullscreen) window.Loaded += (_, _) => { if (!window.IsFullscreen) window.ToggleFullscreen(); };
   }
   window.Closing += async (_,args) =>
   {
    if(_stoppingPlayer || !ReferenceEquals(_videoWindow,window))return;
    args.Cancel=true; await StopPlayer();
   };
   window.Show(); var handle = await window.ReadyTask;
   if(!window.IsLoaded)return;
   var player = await _tools.PlayAsync(path,start??(playback.ResumePlayback?item.PlaybackPositionSeconds:0),handle,hardwareDecoding:playback.HardwareDecoding,reportMouse:false);
   if(!window.IsLoaded || !ReferenceEquals(_videoWindow,window)){await player.DisposeAsync();return;}
   _player=player; _playingId=item.Id; _historyEntry=_repository.BeginPlayback(item.Id,DateTimeOffset.Now);
   await window.AttachPlayerAsync(player);
   _ = ContinueAtEndAsync(player, window, item);
   Status.Text = T("Playing · {0} · controls in the player window", item.Title);
  }
  catch (OperationCanceledException) { if(item.Kind!=MediaKind.Image)await StopPlayer(); }
  catch (Exception ex) { if(item.Kind!=MediaKind.Image)await StopPlayer(); Message.Show(this, ex.Message, T("Playback"), MessageBoxButton.OK, MessageBoxImage.Warning); }
 }
 private void ShowImage(MediaItem item)
 {
  var previous = _viewer; _viewer = null; previous?.Close();
  var images = _currentImages.Any(i => i.Id == item.Id) ? _currentImages.Select(i => i.Id == item.Id ? item : i).ToArray() : new[] { item };
  var imageQuery = _imageQuery;
  var imagePlaylist=_imagePlaylist; var collectionOrder=_imageCollectionOrder;
  var initial = imagePlaylist==null ? _repository.GetPosition(imageQuery,item.Id) : _repository.QueryPlaylistPage(imagePlaylist,imageQuery,collectionOrder,item.Id).Offset;
  _viewer = initial.HasValue ? new ImageViewerWindow(this,images,item.Id,async index =>
  {
   var page = await Task.Run(() => imagePlaylist==null ? _repository.QueryPage(imageQuery with { Offset=index,Limit=1 }) : _repository.QueryPlaylistPage(imagePlaylist,imageQuery with { Offset=index,Limit=1 },collectionOrder).Page);
   return page.Items.FirstOrDefault() is { } image ? await EnsureArchiveImageAsync(image) : null;
  },_imageCount,initial.Value) : new ImageViewerWindow(this, images, item.Id);
  var viewer = (ImageViewerWindow)_viewer; var preferences = _repository.GetPreferences();
  viewer.DeleteMedium = image => DeleteWithQuestion(image, viewer); viewer.ToggleFavorite = ToggleFavorite;
  viewer.UseAsTagPicture = image => ChooseTagPicture(image, viewer); viewer.UseAsThumbnail = image => ChooseThumbnailTarget(image, viewer);
  viewer.WheelAction = preferences.General.ImageWheel; viewer.FadeOnChange = preferences.General.ImageFade; viewer.MediaKeys = preferences.Playback.MediaKeys; viewer.Slideshow = preferences.Slideshow;
  viewer.Closed += (_, _) => { if (ReferenceEquals(_viewer, viewer)) { _viewer = null; _collectionPlayback?.Cancel(); } };
  _viewer.Show();
 }
 private async Task SavePlaybackPosition()
 {
  if (_player == null || !_playingId.HasValue) return;
  try { var position = await _player.GetPositionAsync(); var item = _repository.GetMedia(_playingId.Value); if (position.HasValue && item != null) _repository.Upsert(item with { PlaybackPositionSeconds = Math.Max(position.Value, 0) }); }
  catch (Exception ex) { Status.Text = T("Playback position not saved: ") + ex.Message; }
 }
 private async Task StopPlayer()
 {
  if(_stoppingPlayer)return; _stoppingPlayer=true;
  try
  {
   var player=_player; var window=_videoWindow;
   if(player!=null){await SavePlaybackPosition();FinishPlayback(window);try{await player.DisposeAsync();}catch(IOException error){Status.Text=T("Player stopped: ")+error.Message;}}
   _player=null;_playingId=null;_historyEntry=null;_videoWindow=null;
   window?.Close();
  }
  finally{_stoppingPlayer=false;}
 }
}
