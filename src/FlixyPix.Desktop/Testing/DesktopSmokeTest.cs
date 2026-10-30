using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FlixyPix.Core;
using FlixyPix.MediaTools;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 internal async Task RunSmokeTestAsync(string directory)
 {
  static void Require(bool check, string message) { if (!check) throw new InvalidOperationException(message); }
  Require(Title==ProductIdentity.Name,"The application window did not use the new product name.");
  var fixtureDirectory = Path.Combine(directory, "fixtures");
  Directory.CreateDirectory(fixtureDirectory);
  var dummyVideo = Path.Combine(fixtureDirectory, "scene-fixture.mp4");
  if (File.Exists(dummyVideo)) File.Delete(dummyVideo);
  string[] titles = ["Abendlicht am See", "Berge im Morgennebel", "Unterwegs nach Norden", "Kleine Augenblicke", "Sommer im Garten", "Horizont und Weite"];
  for (var index = 0; index < titles.Length; index++)
  {
   var visual = new DrawingVisual();
   using (var drawing = visual.RenderOpen())
   {
    var hue = index * 17;
    drawing.DrawRectangle(new LinearGradientBrush(Color.FromRgb((byte)(45 + hue), (byte)(82 + hue), 125), Color.FromRgb(25, 34, (byte)(74 + hue)), 90), null, new Rect(0, 0, 800, 480));
    drawing.DrawEllipse(new SolidColorBrush(Color.FromRgb(247, (byte)(206 - hue), 137)), null, new Point(565 - index * 28, 142), 57, 57);
    var mountains = Geometry.Parse("M0,390 L190,160 L360,360 L490,235 L800,430 L800,480 L0,480 Z");
    drawing.DrawGeometry(new SolidColorBrush(Color.FromArgb(230, 36, (byte)(66 + hue), 95)), null, mountains);
    drawing.DrawRectangle(new SolidColorBrush(Color.FromArgb(100, 17, 25, 40)), null, new Rect(0, 415, 800, 65));
   }
   var bitmap = new RenderTargetBitmap(800, 480, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
   var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
   using var stream = File.Create(Path.Combine(fixtureDirectory, titles[index] + ".png")); encoder.Save(stream);
  }
  await ImportPaths([fixtureDirectory]);
  Require(_repository.Search().Count == 6, "Expected six imported images.");
  Require(_repository.Search().All(i => i.Width == 800 && i.Height == 480), "Image dimensions were not prepared during import.");
  // Counts in the sidebar: the folder tree shows six media and loses the number when counts are switched off.
  static string AllFoldersCount(object? tree) => (tree as IEnumerable<BrowseNode>)?.FirstOrDefault()?.Count.Trim() ?? "";
  Require(AllFoldersCount(Folders.ItemsSource) == "6", $"The folder tree did not count the media ({AllFoldersCount(Folders.ItemsSource)}).");
  var countsBefore = _repository.GetPreferences().General;
  _repository.SaveGeneralPreferences(countsBefore with { ShowFolderCounts = false }); await RefreshAsync();
  Require(AllFoldersCount(Folders.ItemsSource) == "", "Switching folder counts off did not remove them.");
  _repository.SaveGeneralPreferences(countsBefore); await RefreshAsync();
  // Exercise the actual desktop import path with a synthetic encoded video.
  // Only the freshly provisioned FFmpeg is used, never the historical tools.
  if (ToolExists(_settings.Ffmpeg, "ffmpeg"))
  {
   var media = Path.Combine(fixtureDirectory, "synthetic-video.mp4");
   var start = new System.Diagnostics.ProcessStartInfo(_settings.Ffmpeg ?? Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe"))
   { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
   foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-f", "lavfi", "-i", "testsrc=size=160x90:rate=10", "-t", "1", "-c:v", "libx264", "-pix_fmt", "yuv420p", media }) start.ArgumentList.Add(argument);
   using var encoder = System.Diagnostics.Process.Start(start)!;
   var error = encoder.StandardError.ReadToEndAsync(); var stdout = encoder.StandardOutput.ReadToEndAsync();
   await encoder.WaitForExitAsync(); Require(encoder.ExitCode == 0, await error); await stdout;
   await ImportPaths([media]);
   var imported = _repository.Search().Single(i => i.Path == media);
   Require(imported.Width == 160 && imported.Height == 90 && imported.DurationSeconds is >= .9 and <= 1.1 && File.Exists(imported.ThumbnailPath), "Video import did not prepare metadata and thumbnail.");
   await RunContactSheetSmokeTestAsync(imported.Id, directory);
   await RunHoverPreviewSmokeTestAsync(directory, imported.Id);
   await RunSceneWallSmokeTestAsync(directory, imported.Id);
   await RunVideoWallSmokeTestAsync(directory, imported.Id);
   await RunQuickActionsSmokeTestAsync(imported.Id);
   await RunHistorySmokeTestAsync(directory, imported.Id);
   await RunVersionsSmokeTestAsync(directory, imported.Id);
   if(ToolExists(_settings.Mpv,"mpv"))
   {
    // Jump marks: one scene and two bookmarks inside the one-second clip, one bookmark beyond its end.
    _repository.UpsertScene(new Scene { MediaId=imported.Id,Title="Einstieg",StartSeconds=.2,EndSeconds=.5 });
    _repository.UpsertBookmark(new Bookmark { MediaId=imported.Id,Title="Höhepunkt",PositionSeconds=.7,DisplayOrder=0 });
    _repository.UpsertBookmark(new Bookmark { MediaId=imported.Id,Title="",PositionSeconds=.45,DisplayOrder=1 });
    _repository.UpsertBookmark(new Bookmark { MediaId=imported.Id,Title="Hinter dem Ende",PositionSeconds=90 });
    await Play(imported,0);Require(_videoWindow?.HasPlayer==true&&_videoWindow.VideoHandle!=0,"Embedded mpv did not attach.");
    await _player!.PauseAsync(true);await _videoWindow!.SeekToAsync(.35);
    await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
    Require(_videoWindow.Marks.Select(m=>m.PositionSeconds).SequenceEqual([.2,.45,.7])&&_videoWindow.MarkPanelVisible&&!_videoWindow.MarkStepsVisible&&_videoWindow.RenderedMarkCount==3,"Player jump marks were not listed and drawn in time order.");
    await _videoWindow.SeekToMarkAsync(JumpMarks.Numbered(_videoWindow.Marks,3)!);
    Require(Math.Abs(_videoWindow.LastPositionSeconds-.7)<.01,"Jumping to a chosen mark failed.");
    await _videoWindow.SeekToAsync(.35);
    _videoWindow.SetManualTransform(new(0,-.1,-.1,1.2,1.2));
    Require(_videoWindow.VideoBounds.Width>0&&_videoWindow.VideoBounds.X<0,"Player transformation did not change child geometry.");
    await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
    Capture((FrameworkElement)_videoWindow.Content,Path.Combine(directory,"embedded-player.png"));
    // Play/pause shows its symbol over the video; the time line shows a frame where the pointer rests.
    await _videoWindow.TogglePauseAsync(); Require(_videoWindow.FlashShown, "Play/pause did not show its symbol over the video."); await _videoWindow.TogglePauseAsync(); await _player!.PauseAsync(true);
    await _videoWindow.LoadSeekFrameAsync(.5, CancellationToken.None);
    Require(_videoWindow.SeekPreviewShown, "Hovering the time line did not show a frame.");
    Require(_videoWindow.StepRequested != null && (_videoWindow.TrackMenuSummary.StartsWith(T("Audio {0} · {1}", "\u0001", "").Split('\u0001')[0],
     StringComparison.Ordinal) || _videoWindow.TrackMenuSummary.StartsWith(T("Audio off"), StringComparison.Ordinal)),
     "Previous/next or the track menu are missing: " + _videoWindow.TrackMenuSummary + " · step " + (_videoWindow.StepRequested != null));
    // The further shortcuts of the original: scene from start and end marks, toggle a bookmark, screenshot, slow motion.
    var scenesBefore = _repository.GetScenes(imported.Id).Count;
    await _videoWindow.SeekToAsync(.1); _videoWindow.Act(PlayerActions.ClipStart); await _videoWindow.SeekToAsync(.3); _videoWindow.Act(PlayerActions.ClipEnd); _videoWindow.Act(PlayerActions.ClipCreate);
    Require(_repository.GetScenes(imported.Id).Count == scenesBefore + 1 && _videoWindow.ClipStart == null, "Start and end marks did not create a scene.");
    var bookmarksBefore = _repository.GetBookmarks(imported.Id).Count;
    await _videoWindow.SeekToAsync(.6); _videoWindow.ToggleBookmarkHere(); Require(_repository.GetBookmarks(imported.Id).Count == bookmarksBefore + 1, "Toggling did not set a bookmark.");
    _videoWindow.ToggleBookmarkHere(); Require(_repository.GetBookmarks(imported.Id).Count == bookmarksBefore, "Toggling again did not remove the bookmark.");
    _videoWindow.ScreenshotFolder = Path.Combine(directory, "screenshots");
    var shot = await _videoWindow.SaveScreenshotAsync(); Require(File.Exists(shot) && new FileInfo(shot).Length > 0, "The screenshot was not saved.");
    _videoWindow.ToggleSlowMotion(); Require(_videoWindow.SlowMotion, "Slow motion did not switch on."); _videoWindow.ToggleSlowMotion(); Require(!_videoWindow.SlowMotion, "Slow motion did not switch off.");
    // A subtitle file joins the tracks and is selected; the offset moves in steps; both are remembered for the medium.
    var subtitleFile = Path.Combine(directory, "untertitel-test.srt"); File.WriteAllText(subtitleFile, "1\n00:00:00,000 --> 00:00:01,000\nHallo\n");
    await _videoWindow.LoadSubtitleFileAsync(subtitleFile); await _videoWindow.ShiftSubtitlesAsync(1); await _videoWindow.ShiftSubtitlesAsync(1);
    Require(_videoWindow.TrackMenuSummary.Contains("untertitel-test.srt", StringComparison.Ordinal) && Math.Abs((await _player!.GetSubtitleDelayAsync() ?? 0) - .2) < .001,
     "The subtitle file or its offset did not reach the player: " + _videoWindow.TrackMenuSummary);
    Require(_repository.GetMedia(imported.Id) is { SubtitlePath: var rememberedSubtitle, SubtitleDelaySeconds: .2 } && rememberedSubtitle == subtitleFile, "The subtitles were not remembered for the medium.");
    await _videoWindow.ShiftSubtitlesAsync(0); Require(_videoWindow.SubtitleDelay == 0 && _repository.GetMedia(imported.Id)!.SubtitleDelaySeconds == 0, "Resetting the subtitle offset failed.");
    // Bookmarks and scenes get a picture made from the video; the player's mark list shows it.
    var pictured = _repository.UpsertBookmark(new() { MediaId = imported.Id, Title = "Mit Bild", PositionSeconds = .5 });
    await EnsureMarkPreviewsAsync(imported);
    var withPicture = _repository.GetBookmarks(imported.Id).Single(b => b.Id == pictured.Id);
    Require(withPicture.PreviewAssetId is { } markAsset && MarkThumbnail(markAsset) != null && _repository.GetMarkPreviewNeeds(imported.Id).All(n => n.PositionSeconds >= (imported.DurationSeconds ?? 0)),
     $"The bookmark did not get a picture from the video ({_repository.GetMarkPreviewNeeds(imported.Id).Count} marks open).");
    var converter = new MarkThumbnailConverter();
    Require(converter.Convert(withPicture.PreviewAssetId!.Value, typeof(object), "has", System.Globalization.CultureInfo.InvariantCulture) is true
     && converter.Convert(withPicture.PreviewAssetId.Value, typeof(object), "large", System.Globalization.CultureInfo.InvariantCulture) is BitmapSource { PixelWidth: > 192 }
     && converter.Convert(Guid.NewGuid(), typeof(object), "has", System.Globalization.CultureInfo.InvariantCulture) is false, "Hovering a mark picture would not show it enlarged.");
    _videoWindow.RefreshMarks(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    Capture((FrameworkElement)_videoWindow.Content, Path.Combine(directory, "player-marks.png"));
    _repository.DeleteBookmark(pictured.Id); _videoWindow.RefreshMarks();
    // Mouse over the video: mpv reports the wheel; the plain wheel seeks by default, Ctrl+wheel follows its own binding (volume, 5 points).
    var volumeBefore = _videoWindow.Volume;
    await NativePlayerWheelAsync(-120, System.Windows.Input.ModifierKeys.Control); await NativePlayerWheelAsync(-120, System.Windows.Input.ModifierKeys.Control);
    for (var wait = 0; _videoWindow.Volume == volumeBefore && wait < 40; wait++) await Task.Delay(50);
    await Task.Delay(300);
    for (var wait = 0; Math.Abs(_videoWindow.Volume - (volumeBefore - 10)) > .01 && wait < 40; wait++) await Task.Delay(50);
    Require(Math.Abs(_videoWindow.Volume - (volumeBefore - 10)) < .01 && _videoWindow.WheelMode == PlayerWheelAction.Seek, $"The mouse wheel over the video did not follow its setting ({volumeBefore} → {_videoWindow.Volume}).");
    Require(VideoPlayerWindow.WheelInput(120, System.Windows.Input.ModifierKeys.None) == "WHEEL_UP" && VideoPlayerWindow.WheelInput(-120, System.Windows.Input.ModifierKeys.Control) == "Ctrl+WHEEL_DOWN",
     "The wheel over the control bar does not reach the wheel settings.");
    // Middle click: sound off and back to the volume before.
    var audible = _videoWindow.Volume;
    NativePlayerMessage(0x0208); await Task.Delay(100); var muted = _videoWindow.Volume; NativePlayerMessage(0x0208); await Task.Delay(100);
    Require(muted == 0 && Math.Abs(_videoWindow.Volume - audible) < .01, $"The middle click did not mute and restore the sound ({audible} → {muted} → {_videoWindow.Volume}).");
    // Right click: the small player menu with the shortcuts of the player.
    var playerMenu = _videoWindow.ShowPlayerMenu(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    var playerLabels = Menus.Labels(playerMenu).ToList();
    Require(playerMenu.IsOpen && playerLabels.Contains(T("Set bookmark")) && playerLabels.Contains(T("Jump marks")) && playerLabels.Any(l => l.StartsWith(T("Tracks") + " ▸ ", StringComparison.Ordinal))
     && playerMenu.Items.OfType<System.Windows.Controls.MenuItem>().First(i => (string)i.Header == T("Set bookmark")).InputGestureText == "B",
     "The player menu is incomplete: " + string.Join(" | ", playerLabels));
    playerMenu.IsOpen = false;
    // A double click switches full screen only: the play state its clicks toggled comes back.
    var pausedBefore = _videoWindow.IsPaused;
    await CheckNativePlayerInputAsync();
    NativePlayerMessage(0x0201); NativePlayerMessage(0x0202); NativePlayerMessage(0x0203); NativePlayerMessage(0x0202);
    await Task.Delay(1100);
    Require(_videoWindow.IsFullscreen && _videoWindow.IsPaused == pausedBefore, "A double click in the player changed the play state or did not switch to full screen.");
    _videoWindow.ToggleFullscreen(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    Require(_videoWindow.MarkPanelVisible,"Full screen lost the remembered panel state.");
    _videoWindow.ToggleMarkPanel();
    Require(!_repository.GetPreferences().Playback.ShowMarks,"Hiding the panel was not saved.");
    _videoWindow.RefreshMarks(); Require(!_videoWindow.MarkPanelVisible,"Refreshing marks reopened the hidden panel.");
    _videoWindow.ToggleFullscreen(); await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
    _videoWindow.ToggleMarkPanel(); Require(!_repository.GetPreferences().Playback.ShowMarks,"Full screen changed the saved panel state.");
    _videoWindow.ToggleFullscreen(); await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
    Require(!_videoWindow.MarkPanelVisible,"Leaving full screen reopened the hidden panel.");
    _videoWindow.ToggleMarkPanel();
    await CheckPlayerEditingAndVolumeAsync();
    await _videoWindow!.CheckKeyboardControlsAsync();
    // A click on the time line holds the clicked point although the position is polled meanwhile, then seeks there.
    var timeline = _videoWindow.Timeline; var clicked = timeline.Maximum * .6; var pressed = true; _videoWindow.PointerPressed = () => pressed;
    timeline.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
    timeline.Value = clicked; await Task.Delay(600);
    Require(Math.Abs(timeline.Value - clicked) < .01, "Polling moved the time line back while it was pressed.");
    pressed = false;
    timeline.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
    bool Near() => _videoWindow.LastPositionSeconds >= clicked - .3 && _videoWindow.LastPositionSeconds <= clicked + 1.5;
    for (var wait = 0; !Near() && wait < 40; wait++) await Task.Delay(50);
    Require(Near(), $"A click on the time line did not seek there ({_videoWindow.LastPositionSeconds:0.##} instead of {clicked:0.##} s).");
    await Task.Delay(1100);
    _videoWindow.PointerPressed = () => System.Windows.Input.Mouse.LeftButton == System.Windows.Input.MouseButtonState.Pressed;
    Theme.Apply(ColorScheme.Light); await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
    Capture((FrameworkElement)_videoWindow.Content,Path.Combine(directory,"embedded-player-light.png"));
    Theme.Apply(ColorScheme.Dark);
    // Player settings must take effect, not only be stored.
    Require(_videoWindow.KeepsAwake && SleepGuard.Active, "Playback did not keep the display awake.");
    var hwdec = await _player!.GetTextPropertyAsync("hwdec");
    Require(hwdec == "auto-safe", $"Hardware decoding did not reach mpv (hwdec={hwdec ?? "null"}, setting={_repository.GetPreferences().Playback.HardwareDecoding}).");
    Require(VideoPlayerWindow.MediaKeyAction(System.Windows.Input.Key.MediaPlayPause) == PlayerActions.PlayPause && VideoPlayerWindow.MediaKeyAction(System.Windows.Input.Key.MediaNextTrack) == PlayerActions.NextBookmark, "Media keys are not mapped.");
    _videoWindow.ToggleMarkPanel(); await StopPlayer(); await Play(imported,0);
    Require(!_videoWindow!.MarkPanelVisible,"Reopening the player lost the hidden panel state.");
    _videoWindow.ToggleMarkPanel();
    await StopPlayer();Require(_videoWindow==null&&_player==null,"Embedded player did not release resources.");
    Require(!SleepGuard.Active, "The display stayed blocked after playback.");
    var playbackBefore = _repository.GetPreferences().Playback;
    _repository.SavePlaybackPreferences(playbackBefore with { AlwaysFullscreen = true, HardwareDecoding = false, SmallJumpBackwardSeconds = 15, PreventSleep = false,
     WheelModifiers = [new(WheelModifier.Shift, PlayerWheelAction.Seek, 25, JumpUnit.Percent)], CustomJumps = [new("D9", 90, JumpUnit.Percent)],
     RememberVolume = false, StartVolume = 40, AlwaysOnTop = true,
     Hotkeys = new Dictionary<string, string>(playbackBefore.Hotkeys) { [PlayerActions.SwitchWheel] = "W" } });
    try
    {
     await Play(imported,0); await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
     Require(_videoWindow!.WindowStyle==WindowStyle.None && _videoWindow.WindowState==WindowState.Maximized, "Always-fullscreen had no effect.");
     Require(_videoWindow.ControlLabels.Contains("−15 s"), "The jump width setting did not reach the player.");
     await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
     Require(_videoWindow.OverlayShown, "In full screen the controls did not float over the video.");
     _videoWindow.SimulateRest(mouseOverControls: true); Require(_videoWindow.OverlayShown, "The control bar hid while the mouse was over its controls.");
     _videoWindow.SimulateRest(); Require(!_videoWindow.OverlayShown, "The control bar did not hide while the mouse rested.");
     Require(await _player!.GetTextPropertyAsync("hwdec") == "no", "Switching hardware decoding off did not reach mpv.");
     Require(!_videoWindow.KeepsAwake && !SleepGuard.Active, "Sleep prevention ignored its setting.");
     // Start volume, staying on top, Shift+wheel by a share of the length and a jump on a key of one's own.
     Require(Math.Abs(_videoWindow.Volume - 40) < .01 && _videoWindow.Topmost, $"Start volume or always-on-top had no effect (volume {_videoWindow.Volume}).");
     var length = imported.DurationSeconds ?? 0;
     await _player!.PauseAsync(true); await _videoWindow.SeekToAsync(0);
     await NativePlayerWheelAsync(120, System.Windows.Input.ModifierKeys.Shift);
     for (var wait = 0; _videoWindow.LastPositionSeconds < .2 * length && wait < 40; wait++) await Task.Delay(50);
     Require(length > 0 && Math.Abs(_videoWindow.LastPositionSeconds - .25 * length) < .05 * length + .05, $"Shift+wheel did not seek a quarter of the video ({_videoWindow.LastPositionSeconds:0.##} of {length:0.##} s).");
     await NativePlayerWheelAsync(240,System.Windows.Input.ModifierKeys.Shift);
     Require(Math.Abs(_videoWindow.LastPositionSeconds-.75*length)<.05*length+.05,"Rapid native wheel steps were lost.");
     await NativePlayerWheelAsync(-120,System.Windows.Input.ModifierKeys.None);
     Require(_videoWindow.LastPositionSeconds<.05,"Native wheel down did not seek backwards.");
     var nine = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(_videoWindow)!, 0, System.Windows.Input.Key.D9) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
     _videoWindow.RaiseEvent(nine);
     for (var wait = 0; _videoWindow.LastPositionSeconds < .9 * length && wait < 40; wait++) await Task.Delay(50);
     Require(nine.Handled && _videoWindow.LastPositionSeconds >= .9 * length - .05, $"The custom jump on 9 did not jump 90 % ({_videoWindow.LastPositionSeconds:0.##} s).");
     var switchKey = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(_videoWindow)!, 0, System.Windows.Input.Key.W) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
     _videoWindow.RaiseEvent(switchKey); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
     Require(switchKey.Handled && _videoWindow.WheelMode == PlayerWheelAction.Volume, "The shortcut did not switch the wheel from seeking to volume.");
     await StopPlayer();
     // Notices, hiding the bar, the mark list, shortcuts and screenshots follow their settings.
     var shots = Path.Combine(directory, "screenshots-numbered");
     _repository.SavePlaybackPreferences(playbackBefore with { ShowMessages = false, AutoHideControls = false, ShowMarks = false, KeyboardShortcuts = false,
      CustomJumps = [new("D9", 90, JumpUnit.Percent)], Screenshots = new() { Folder = shots, Prefix = "Probe", Format = ScreenshotFormat.Jpg, Numbering = true } });
     await Play(imported, 0); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
     Require(!_videoWindow!.MarkPanelVisible && _videoWindow.Marks.Count > 0, "The mark list opened although it should not open by itself.");
     Require(_videoWindow.MarkStepsVisible, "Previous/next mark buttons are missing while the mark list is hidden.");
     await _player!.PauseAsync(true); await _videoWindow.SeekToAsync(0);
     var blocked = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(_videoWindow)!, 0, System.Windows.Input.Key.D9) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
     _videoWindow.RaiseEvent(blocked); await Task.Delay(300);
     Require(!blocked.Handled && _videoWindow.LastPositionSeconds < .1, "A shortcut worked although shortcuts are switched off.");
     await _videoWindow.TogglePauseAsync(); Require(!_videoWindow.FlashShown, "The play symbol showed although notices are off."); await _videoWindow.TogglePauseAsync();
     var firstShot = await _videoWindow.SaveScreenshotAsync(); var secondShot = await _videoWindow.SaveScreenshotAsync();
     Require(Path.GetFileName(firstShot) == "Probe 0001.jpg" && Path.GetFileName(secondShot) == "Probe 0002.jpg" && Path.GetDirectoryName(firstShot) == shots && File.Exists(secondShot),
      $"Screenshots did not follow folder, name, format and numbering ({firstShot}, {secondShot}).");
     _videoWindow.ToggleFullscreen(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
     _videoWindow.SimulateRest(); Require(_videoWindow.OverlayShown, "The control bar hid although it should stay.");
     await StopPlayer();
    }
    finally { _repository.SavePlaybackPreferences(playbackBefore); }
   }
   _repository.Delete(imported.Id); File.Delete(media);
  }
  var archivePath = Path.Combine(directory, "synthetic-images.zip");
  using (var archive = System.IO.Compression.ZipFile.Open(archivePath, System.IO.Compression.ZipArchiveMode.Create))
  {
   var entry = archive.CreateEntry("Bilder/Erinnerung.png");
   using var target = entry.Open(); using var input = File.OpenRead(_repository.Search().First().Path); input.CopyTo(target);
  }
  var archiveBefore = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(archivePath));
  var archiveService = new FlixyPix.Archives.ImageArchiveService(Path.Combine(_settings.DataDirectory, "archive-cache"));
  var archiveEntry = archiveService.Inspect(archivePath).Single();
  var cache = await archiveService.ExtractAsync(archiveEntry);
  var archiveItem = _repository.Upsert(new MediaItem { Path = cache, Kind = MediaKind.Image, Extra = new() { ["archivePath"] = archivePath, ["entryName"] = archiveEntry.EntryName, ["sha256"] = archiveEntry.ArchiveSha256 } });
  File.Delete(cache); var recovered = await EnsureArchiveImageAsync(archiveItem);
  Require(File.Exists(recovered.Path) && recovered.Id == archiveItem.Id && archiveBefore.SequenceEqual(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(archivePath))), "Archive cache recovery changed identity or source.");
  _repository.Delete(archiveItem.Id);
  var first = _repository.Search().First();
  await RefreshAsync(first.Id);
  await _previewReady;
  Require(Selected?.Id == first.Id && Preview.Source != null, "Selection/preview did not load.");
  await ChangeTagsAsync([first.Id], ["Natur", "Erinnerungen", "Natur"], []);
  EditTitle.Text = "Abendlicht am See"; EditRating.Value = 9;
  // The rating is stored at once; the unsaved title stays in its box through the refresh.
  Require(_repository.GetMedia(first.Id)?.Rating == 9, "Star rating was not saved immediately.");
  await _lastRefresh; Require(EditTitle.Text == "Abendlicht am See" && EditRating.Value == 9, "Quick save lost the unsaved title.");
  EditDescription.Text = "Ein ruhiger Moment am Wasser. Dieses Bild wurde für den Oberflächentest aus Vektoren erzeugt.";
  SaveDetails(this, new RoutedEventArgs());
  var saved = _repository.Search().Single(i => i.Id == first.Id);
  Require(saved.Rating == 9 && saved.Tags.Count == 2 && saved.Description!.Contains("Vektoren"), "Editing failed or half-star rating lost.");
  _repository.Upsert(saved with { WatchLater = true, Category = "Natur", TagCategories = new() { ["Orte"] = ["See"], ["Motive"] = ["Abendlicht"] } });
  var second = _repository.Search().First(item => item.Id != first.Id);
  _repository.Upsert(second with { IsFavorite = true, Rating = 1 });
  _repository.SavePreferences(_repository.GetPreferences() with { ImageFavoriteMinimumRating = 10 });
  Require(!_repository.IsFavorite(_repository.Search().First(item => item.Id == first.Id)) && _repository.IsFavorite(_repository.Search().First(item => item.Id == second.Id)), "Configured favorite threshold/pin failed.");
  _repository.SavePreferences(_repository.GetPreferences() with { ImageFavoriteMinimumRating = 8 });
  _view = "favorites"; await RefreshAsync(); Require(MediaList.Items.Count == 2, "Favorites view failed to include threshold and pin.");
  _view = "watchlater"; await RefreshAsync(); Require(MediaList.Items.Count == 1, "Watch-later view failed.");
  _view = "all"; _category = "Natur"; await RefreshAsync(); Require(MediaList.Items.Count == 1, "Media category filter failed.");
  _category = null; _tagGroup = "Orte"; _tag = "See"; await RefreshAsync(); Require(MediaList.Items.Count == 1 && CategorizedTags.Items.Count > 0, "Categorized tag filtering failed.");
  _tag = null; _tagGroup = null; _folder = fixtureDirectory; await RefreshAsync(); Require(MediaList.Items.Count == 6, "Folder filtering failed."); _folder = null;
  var thumbnailSource = _repository.Search().First(item => item.Id != first.Id).Path;
  await SetCustomThumbnail(_repository.Search().Single(item => item.Id == first.Id), thumbnailSource);
  var custom = _repository.Search().Single(item => item.Id == first.Id).ThumbnailPath;
  Require(custom != thumbnailSource && File.Exists(custom) && File.ReadAllBytes(custom!).SequenceEqual(File.ReadAllBytes(thumbnailSource)), "Custom thumbnail copy failed.");
  SearchBox.Text = "Abendlicht"; _searchTimer.Stop(); await RefreshAsync(); Require(MediaList.Items.Count == 1, "Search did not narrow results.");
  SearchBox.Text = ""; _searchTimer.Stop();
  _tag = "Natur"; await RefreshAsync(); Require(MediaList.Items.Count == 1, "Tag filter failed."); _tag = null;
  await RunLibraryNavigationSmokeTestAsync(directory);
  await RunInputAccessSmokeTestAsync(directory);
  await RunTaggingSmokeTestAsync(directory, first.Id);
  _repository.UpsertPlaylist(new Playlist { Name = "Meine Erinnerungen", Kind = PlaylistKind.Slideshow, MediaIds = _repository.Search().Select(i => i.Id).ToList() });
  _repository.UpsertPlaylist(new Playlist { Name = "Für später", MediaIds = [first.Id] });
  File.WriteAllBytes(dummyVideo, []);
  var video = new MediaItem { Path = dummyVideo, Title = "Szenen-Test (keine Mediendaten)", Kind = MediaKind.Video, DurationSeconds = 120 };
  _repository.Upsert(video);
  _repository.UpsertScene(new Scene { MediaId = video.Id, Title = "Lieblingsmoment", StartSeconds = 10, EndSeconds = 20 });
  var scene = _repository.GetScenes(video.Id).Single();
  _repository.UpsertBookmark(new Bookmark { MediaId = video.Id, Title = "Merken", PositionSeconds = 42 });
  _repository.UpsertPlaylist(new Playlist { Name = "Szenenliste", MediaIds = [video.Id], Entries = [new PlaylistEntry(video.Id, scene.Id)] });
  Require(_repository.GetBookmarks(video.Id).Count == 1, "Bookmark persistence failed.");
  _repository.UpsertSavedSearch(new SavedSearch { Name = "Naturbilder", Tag = "Natur", MinimumRating = 8 });
  _repository.UpsertFilenameTagRule(new FilenameTagRule { Name = "Abendlicht", FilenameContains = "Abendlicht", Tags = ["Abend"] });
  Require(_repository.ApplyFilenameTagRules() >= 1 && _repository.GetSavedSearches().Any(), "Tag rules/saved searches failed.");
  var groupRule = _repository.UpsertFilenameTagRule(new FilenameTagRule { Name = "Naturerinnerungen", Groups = [new() { Conditions = [new(TagRuleField.Title,TagRuleComparison.Contains,"Abendlicht"),new(TagRuleField.Tag,TagRuleComparison.HasTag,"Natur")], Tags = ["Lieblingsbilder"] }] });
  Require(_repository.ApplyFilenameTagRules() == 1 && _repository.Search().Single(i => i.Id == first.Id).Tags.Contains("Lieblingsbilder"), "Grouped tag rule failed.");
  var ruleEditor = CreateTagRuleEditor(groupRule with { Groups = [groupRule.Groups[0] with { Tags = ["Lieblingsbilder", "Sonnenuntergang"] }] }); ruleEditor.Show();
  await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); await ruleEditor.ImpactTask;
  // The rule already gave "Lieblingsbilder"; only the added "Sonnenuntergang" counts as new.
  Require(ruleEditor.GroupCount == 1 && ruleEditor.LastImpact is { Media: 1, NewTags: 1 }, $"The rule editor did not measure the rule's effect ({ruleEditor.LastImpact}).");
  await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); Capture((FrameworkElement)ruleEditor.Content,Path.Combine(directory,"tag-rule-editor.png")); ruleEditor.Close();
  var asset = _repository.UpsertMediaAsset(new() { MediaId = first.Id, Data = File.ReadAllBytes(first.Path), Label = "Abendlicht · gespeicherte Mediengrafik", Kind = MediaAssetKind.Artwork });
  var beforeArtworkRestore = _repository.Search().Single(i => i.Id == first.Id);
  _repository.Upsert(beforeArtworkRestore with { ThumbnailPath = null, Extra = new(beforeArtworkRestore.Extra) { ["thumbnail.assetId"] = asset.Id.ToString() } });
  await RestoreAssetPreviewsAsync(CancellationToken.None);
  var restoredThumbnail = _repository.Search().Single(i => i.Id == first.Id).ThumbnailPath;
  Require(File.Exists(restoredThumbnail) && File.ReadAllBytes(restoredThumbnail!).SequenceEqual(asset.Data), "Stored artwork did not restore the selected thumbnail.");
  var gallery = new MediaArtworkWindow(this,_repository,first,Path.Combine(directory,"artwork-cache"),_ => Task.CompletedTask); gallery.Show();
  await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
  await gallery.ReadyTask;
  Require(gallery.AssetCount == 2 && gallery.HasPreview,"Artwork gallery did not render stored image bytes.");
  Capture((FrameworkElement)gallery.Content,Path.Combine(directory,"media-artwork.png")); gallery.Close();
  _repository.UpsertTagAsset(new(){Tag=new("Motive","Abendlicht"),Label="Tagbild · Abendlicht",Data=File.ReadAllBytes(first.Path)});
  var tagArtwork=new TagAssetsWindow(this,_repository,Path.Combine(directory,"tag-artwork-cache"));tagArtwork.Show();await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);await tagArtwork.ReadyTask;
  Require(tagArtwork.AssetCount==1&&tagArtwork.HasPreview,"Tag artwork did not render stored bytes.");Capture((FrameworkElement)tagArtwork.Content,Path.Combine(directory,"tag-artwork.png"));tagArtwork.Close();
  // Directory: tags, performers and studios with a package imported without dialogs and its review window.
  var packagePath = Path.Combine(directory, "Darsteller" + TagPackages.Extension);
  File.WriteAllText(packagePath, TagPackages.Signature + "\r\nRolle;Kategorie;Name;Aliasse;Favorit\r\nDarsteller;Darsteller;Jane Doe;J. Doe;ja\r\nStudio;Studios;Acme Pictures;Acme;\r\n;Genre;;;\r\n", new System.Text.UTF8Encoding(true));
  var directoryWindow = new DirectoryWindow(this, _repository); directoryWindow.Show(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
  var rowsBefore = directoryWindow.RowCount;
  Require(directoryWindow.ImportPackage(packagePath, null, review: false) == 2 && directoryWindow.RowCount == rowsBefore + 2, "The tag package was not imported into the directory.");
  Require(_repository.GetTagRole("Studios") == TagRole.Studio && _repository.GetDirectory().Single(e => e.Tag == "Jane Doe") is { IsFavorite: true, Category: "Darsteller" },
   "The package did not set role and favourite.");
  await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
  Capture((FrameworkElement)directoryWindow.Content, Path.Combine(directory, "directory.png"));
  var reviewPackage = TagPackages.Parse("Rolle;Kategorie;Name\r\nDarsteller;Darsteller;Jane Doe\r\nDarsteller;Darsteller;Neue Person\r\nStudio;Darsteller;Falsch\r\n;;Ohne Kategorie\r\n");
  var review = new TagPackageWindow(directoryWindow, "Beispiel" + TagPackages.Extension, _repository.PlanTagPackage(reviewPackage), reviewPackage.Warnings);
  review.Show(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
  Require(review.ChosenLines.Count == 2, "The package review did not preselect exactly the applicable rows.");
  Capture((FrameworkElement)review.Content, Path.Combine(directory, "tag-package.png")); review.Close(); directoryWindow.Close();
  await RunProfileSmokeTestAsync(directory);
  await RunSimilarSmokeTestAsync(directory);
  // Filter flyout: thirteen sections; a date window that excludes everything empties the grid and its chip brings everything back.
  async Task Settled() { await _lastRefresh; await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); }
  Refresh(); await Settled(); var unfiltered = _total;
  Require(FilterPills.Count == 13 && FilterBar.Children.Count == 13, "The filter flyout does not list its thirteen sections.");
  ApplyFilter(_filter with { FileDate = new DateFilter(DateWindow.Custom, null, new DateTimeOffset(1990, 1, 1, 0, 0, 0, TimeSpan.Zero)) }); await Settled();
  Require(_total == 0 && FilterPills[T("File date")].Text.Text.StartsWith(T("File date"), StringComparison.Ordinal) && CountLabel.Text.Contains(T("File {0}", _filter.FileDate.Describe()), StringComparison.Ordinal),
   $"The file date filter did not apply ({_total} hits, pill {FilterPills[T("File date")].Text.Text}).");
  Require(EmptyTitle.Text == T("No matches") && EmptyReset.Visibility == Visibility.Visible && FirstRunActions.Visibility != Visibility.Visible,
   "An empty result in a filled library still invites the first import.");
  ApplyFilter(_filter with { FileDate = new(), Imported = new DateFilter(DateWindow.LastYear) }); await Settled(); var recent = _total;
  ApplyFilter(_filter with { Imported = new DateFilter(DateWindow.OlderThanYear) }); await Settled();
  Require(recent > 0 && recent + _total == unfiltered, $"The import date windows do not split the library ({recent} + {_total} ≠ {unfiltered}).");
  ApplyFilter(_filter with { Imported = new() }); await Settled();
  // Tags cycle include → exclude → off from the bar's list.
  var natur = _repository.GetDirectory().First(e => e.Tag == "Natur");
  CycleFacet(natur, TagRole.Tag); await Settled(); var withNatur = _total;
  CycleFacet(natur, TagRole.Tag); await Settled(); var withoutNatur = _total;
  Require(withNatur > 0 && withNatur + withoutNatur == unfiltered && _excludedTags.Contains("Natur"), $"Including and excluding a tag did not split the result ({withNatur} + {withoutNatur} ≠ {unfiltered}).");
  CycleFacet(natur, TagRole.Tag); await Settled();
  Require(_requiredTags.Count == 0 && _excludedTags.Count == 0, "The third click did not switch the tag filter off.");
  // A saved search keeps the whole view and brings it back; the sidebar counts its hits.
  ApplyFilter(_filter with { Watched = false, Include = [new("Darsteller", "Jane Doe", TagRole.Performer)] }); await Settled();
  Require(_total == 0, "A performer without media still matched.");
  OpenFilterPopup(FilterPills[T("Performers")].Button, FacetPanel(FilterPills[T("Performers")].Button, TagRole.Performer)); await Task.Delay(500); await Settled();
  Capture((FrameworkElement)_filterPopup!.Child, Path.Combine(directory, "filter-performers.png"));
  _filterPopup.IsOpen = false;
  Capture(RootContent, Path.Combine(directory, "desktop-filter-bar.png"));
  var savedView = _repository.UpsertSavedSearch(new SavedSearch { Name = "Ungesehen mit Jane", State = CaptureViewState() with { Position = 0, AnchorId = null, Selection = [] } });
  ApplyViewState(new ViewState()); await Settled();
  Require(_filter.IsEmpty && _total == unfiltered, "Resetting the view kept filters.");
  ApplySavedSearch(savedView); await Settled();
  Require(_filter.Watched == false && _filter.Include.Count == 1 && ViewTitle.Text == "Ungesehen mit Jane" && _total == 0, "The saved search did not restore its view.");
  await Task.Delay(300); await Settled();
  var savedRow = CollectionNodes().Single(r => r.Search?.Id == savedView.Id);
  Require(savedRow.CountLabel == "0" && _repository.GetSavedSearches().Single(s => s.Id == savedView.Id).LastOpenedAt is not null, "The sidebar did not count the saved search.");
  _repository.DeleteSavedSearch(savedView.Id); ApplyViewState(new ViewState()); await Settled();
  // One application on screen: a window without owner joins the main window's group; the boss key (Esc) hides all
  // windows and leaves a tray icon, which brings them back.
  var loose = new Window { Width = 320, Height = 200, Title = "Gruppe" }; loose.Show(); await Settled();
  Require(ReferenceEquals(loose.Owner, this) && !loose.ShowInTaskbar && ShowInTaskbar, "A window without owner did not join the main window's group under its taskbar button.");
  var boss = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(loose)!, 0, System.Windows.Input.Key.Escape) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
  loose.RaiseEvent(boss); await Settled();
  // On a desktop without taskbar (the hidden test desktop) there is no tray icon, so nothing may stay hidden.
  if (AppShell.HasTaskbar)
  {
   Require(boss.Handled && AppShell.IsHidden && AppShell.TrayShown && !AppShell.IsOnScreen(this) && !AppShell.IsOnScreen(loose), "The boss key did not hide every window behind a tray icon.");
   AppShell.ShowAll(); await Settled();
  }
  else Require(boss.Handled && !AppShell.IsHidden && AppShell.IsOnScreen(this) && AppShell.IsOnScreen(loose), "Without a tray icon the boss key hid the windows for good.");
  Require(!AppShell.IsHidden && !AppShell.TrayShown && AppShell.IsOnScreen(this) && AppShell.IsOnScreen(loose), "The tray icon did not bring the windows back.");
  // Minimizing into the tray keeps playback running and comes back to the former state; without a taskbar nothing hides.
  var minimizeBefore = AppShell.MinimizeToTray; var paused = false; void Paused() => paused = true; AppShell.Hiding += Paused;
  try
  {
   AppShell.MinimizeToTray = true; WindowState = WindowState.Minimized; await Settled();
   if (AppShell.HasTaskbar)
   {
    Require(AppShell.IsHidden && AppShell.TrayShown && !AppShell.IsOnScreen(this) && !paused, "Minimizing did not go into the tray or paused playback.");
    AppShell.ShowAll(); await Settled();
   }
   Require(!AppShell.IsHidden && WindowState == WindowState.Normal && AppShell.IsOnScreen(this) && !paused, "Coming back from the tray did not restore the window.");
  }
  finally { AppShell.MinimizeToTray = minimizeBefore; AppShell.Hiding -= Paused; if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; }
  loose.Close();
  // Back and forward walk the views of this session; scrolling only updates the current entry.
  ApplyViewState(new ViewState { View = "Video" }); await Settled(); ApplyViewState(new ViewState { View = "Image" }); await Settled();
  await WalkHistoryAsync(-1); Require(_view == "Video" && BackButton.IsEnabled && ForwardButton.IsEnabled, "Back did not return to the previous view.");
  await WalkHistoryAsync(1); Require(_view == "Image" && !ForwardButton.IsEnabled, "Forward did not return to the next view.");
  ApplyViewState(new ViewState()); await Settled();
  // Restoring everything brings back filters, the place in the list, the selection and offers to continue the last video.
  var generalBefore = _repository.GetPreferences().General;
  _repository.SavePreferences(_repository.GetPreferences() with { General = generalBefore with { RestoreLastView = true, RestoreScope = RestoreScope.All } });
  _repository.Upsert(video with { PlaybackPositionSeconds = 50 }); _lastPlayedId = video.Id;
  ApplyFilter(_filter with { Imported = new DateFilter(DateWindow.LastYear) }); await Settled();
  await SelectIndexAsync(30); var placeId = Selected!.Id; var placeIndex = _visibleFirst;
  SaveViewNow();
  ApplyViewState(new ViewState()); _lastPlayedId = null; await Settled(); await ScrollToAsync(0);
  await RestoreLastViewAsync(); await Settled();
  Require(_filter.Imported.Window == DateWindow.LastYear && Selected?.Id == placeId && Math.Abs(_visibleFirst - placeIndex) <= Tiles!.Columns,
   $"Restoring everything lost filter, place or selection (first {_visibleFirst}, was {placeIndex}).");
  Require(VisibleToasts.Any(t => t.StartsWith(T("Continue watching: {0} (at {1})", "\u0001", "").Split('\u0001')[0], StringComparison.Ordinal)), "Continuing the last video was not offered after restoring.");
  Capture(RootContent, Path.Combine(directory, "desktop-restored.png"));
  _repository.SavePreferences(_repository.GetPreferences() with { General = generalBefore });
  ApplyViewState(new ViewState()); await Settled();
  Require(_repository.GetScenes(video.Id).Count == 1 && _repository.GetPlaylists().Count >= 2, "Scenes/collections failed.");
  _advancedExpression=SearchExpression.All(SearchExpression.Where(new(){Field=SearchField.Rating,Comparison=SearchComparison.GreaterOrEqual,Number=8}),SearchExpression.Not(SearchExpression.Where(new(){Field=SearchField.Tag,Comparison=SearchComparison.Equals,Text="missing"})));
  await RefreshAsync(first.Id);await _previewReady;Require(MediaList.Items.Count==1,"Structured search UI integration failed.");
  var searchEditor=CreateAdvancedSearch();searchEditor.Show();await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);await searchEditor.CountTask;
  Require(searchEditor.RootGroup.Read() is {} loaded&&System.Text.Json.JsonSerializer.Serialize(loaded)==System.Text.Json.JsonSerializer.Serialize(_advancedExpression),"The search builder changed the loaded expression.");
  Require(searchEditor.RootGroup.ConditionCount==2&&searchEditor.LiveCount==_repository.QueryPage(new(){Expression=_advancedExpression,Limit=1}).TotalCount,"The search builder did not load the expression or count its hits.");
  // A new condition in everyday units (minutes) narrows the live count; the read expression stores seconds.
  var minutes=searchEditor.RootGroup.AddCondition(new SearchCondition{Field=SearchField.Duration,Comparison=SearchComparison.GreaterOrEqual,Number=0});minutes.Set(SearchField.Duration,SearchComparison.GreaterOrEqual,"2");
  Require(searchEditor.RootGroup.Read() is {Operator:SearchOperator.All} built&&built.Children[^1].Condition is {Number:120},"Minutes were not stored as seconds.");
  await Task.Delay(450);await searchEditor.CountTask;await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
  Capture((FrameworkElement)searchEditor.Content,Path.Combine(directory,"advanced-search.png"));searchEditor.Close();
  _advancedExpression=null;
  _repository.UpsertVocabularyTag(new("Nicht zugeordnet","Personen",true));_repository.ToggleFolderFavorite(new(fixtureDirectory,MediaKind.Image));
  var backup = Path.Combine(directory, "backup.json"); _repository.Export(backup);
  using (var copy = new CatalogRepository(Path.Combine(directory, "restored.db"))) { var imported = copy.ImportJson(backup);
   Require(imported.Errors.Count == 0 && copy.Search().Any(i => i.Rating == 9 && i.WatchLater && i.Category == "Natur"
   && i.TagCategories.ContainsKey("Orte")) && copy.GetPreferences().ImageFavoriteMinimumRating == 8 && copy.GetBookmarks().Any()
   && copy.GetPlaylists().Any(p => p.Entries.Any(entry => entry.SceneId != null)) && copy.GetMediaAssets(first.Id).Single(a =>
   a.Id == asset.Id).Data.SequenceEqual(asset.Data) && copy.GetMediaAssets(first.Id).Any(a => a.IsCustom && a.Kind == MediaAssetKind.Thumbnail
   && a.Data.SequenceEqual(File.ReadAllBytes(thumbnailSource))) && copy.GetFilenameTagRules().Any(r => r.Groups.Count == 1),
   "Backup restore failed."); }
  _repository.Delete(video.Id);
  File.Delete(dummyVideo);
  // Endless scrolling over more media than one loaded window.
  var pagingItems = Enumerable.Range(0, 1500).Select(index => new MediaItem { Path = Path.Combine(fixtureDirectory, $"pagination-{index:D4}.png"), Title = $"Pagination {index:D4}", Kind = MediaKind.Image }).ToList();
  foreach (var item in pagingItems) _repository.Upsert(item);
  _anchor = 0; await RefreshAsync(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
  var total = _repository.QueryPage(new() { Limit = 1 }).TotalCount;
  Require(MediaList.Items.Count == WindowSize && WindowStart == 0, "The first window was not loaded.");
  var tilePanel = Descendants<VirtualizingTilePanel>(MediaList).FirstOrDefault() ?? throw new InvalidOperationException("The tile grid does not use the virtualizing panel.");
  Require(tilePanel.Realized > 0 && tilePanel.Realized < 60, $"The tile grid built {tilePanel.Realized} tiles instead of only the visible ones.");
  var started = MediaList.Items.Cast<MediaCard>().Count(c => c.LoadStarted);
  // Tiles that were briefly in view while the layout settled may have loaded too; the rest of the window must not.
  Require(started > 0 && started < 60, $"{started} of {WindowSize} tiles read their file and thumbnail although only {tilePanel.Realized} are shown.");
  Require(tilePanel.ExtentHeight > tilePanel.ViewportHeight * 10, "The scroll range does not cover the whole result.");
  await ScrollToAsync((int)total - 1);
  Require(WindowStart > 0 && WindowStart + MediaList.Items.Count == total, $"Scrolling to the end did not load the last window ({WindowStart} + {MediaList.Items.Count} of {total}).");
  var lastCard = (MediaCard)MediaList.Items[^1];
  Require(MediaList.ItemContainerGenerator.ContainerFromItem(lastCard) is System.Windows.Controls.ListBoxItem { IsVisible: true }, "The last tile was not shown at the end.");
  await ScrollToAsync(0);
  Require(WindowStart == 0, "Scrolling back did not load the first window.");
  // A selection survives scrolling out of the loaded window and returns with it.
  var kept = (MediaCard)MediaList.Items[0]; MediaList.SelectedItem = kept;
  await ScrollToAsync((int)total - 1);
  Require(OffWindowSelectionCount == 1 && Selected?.Id == kept.Item.Id && SelectedMedia.Count == 1, "The selection was lost when its tile left the loaded window.");
  await ScrollToAsync(0);
  Require(OffWindowSelectionCount == 0 && MediaList.SelectedItems.Cast<MediaCard>().Any(c => c.Item.Id == kept.Item.Id), "The selection did not come back with its window.");
  // End and Home work on the whole result, not only the loaded window.
  MediaList.Focus(); await SelectIndexAsync((int)total - 1);
  Require(Selected?.Id == ((MediaCard)MediaList.Items[^1]).Item.Id && WindowStart + MediaList.Items.Count == total, "End did not select the last medium of the whole result.");
  await SelectIndexAsync(0);
  Require(WindowStart == 0 && Selected?.Id == ((MediaCard)MediaList.Items[0]).Item.Id, "Home did not select the first medium.");
  // Tiles stretch over the full row: the columns follow the target width and fill the view without a gap.
  var panel = Tiles!; var targetWidth = VirtualizingTilePanel.GetTargetTileWidth(MediaList);
  Require(panel.TileWidth >= targetWidth && Math.Abs(panel.TileWidth * panel.Columns - panel.ViewportWidth) < 1 && panel.ViewportWidth / (panel.Columns + 1) < targetWidth,
   $"Tiles do not fill the row: {panel.Columns} × {panel.TileWidth:F1} for {panel.ViewportWidth:F1} (target {targetWidth}).");
  // A wider target gives fewer, wider tiles and keeps the tile at the top in view.
  await SelectIndexAsync(40); var topBefore = _visibleFirst; var columnsBefore = panel.Columns;
  TileSize.Value = TileSize.Maximum; await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
  Require(panel.Columns < columnsBefore && _visibleFirst <= topBefore && _visibleLast >= topBefore, $"Changing the tile size lost the place ({topBefore} → {_visibleFirst}–{_visibleLast}).");
  TileSize.Value = 192; await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
  // Pages: jumping to a page puts its first medium at the top, the bar shows the page.
  await GoToPageAsync(3);
  Require(_visibleFirst == 2 * PageSize && CurrentPage == 3 && PagerBar.Visibility == Visibility.Visible && PageLabel.Text == T("{0:N0}–{1:N0} of {2:N0}", 2 * PageSize + 1, Math.Min(3 * PageSize, _total), _total),
   $"Page 3 did not start at {2 * PageSize} (first {_visibleFirst}, label {PageLabel.Text}).");
  await GoToPageAsync(PageCount);
  Require(CurrentPage == PageCount && _visibleLast == total - 1, "The last page did not show the end of the result.");
  Require(PageNumbers(1, 1).SequenceEqual([1]) && PageNumbers(5, 9).SequenceEqual([1, 0, 4, 5, 6, 0, 9]) && PageNumbers(3, 9).SequenceEqual([1, 2, 3, 4, 0, 9]),
   "Page numbers are not abbreviated as expected.");
  await SelectIndexAsync(0);
  // A tile lifts with a shadow under the pointer and settles again; rows of the list view would stay flat.
  // Raising MouseLeave does not change IsMouseOver. Avoid the real pointer: Drop intentionally
  // retains its shadow while the physical pointer is still over the element.
  var hovered = Enumerable.Range(0, MediaList.Items.Count).Select(MediaList.ItemContainerGenerator.ContainerFromIndex)
   .OfType<System.Windows.Controls.ListBoxItem>().First(item => !item.IsMouseOver);
  hovered.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0) { RoutedEvent = System.Windows.Input.Mouse.MouseEnterEvent });
  await Task.Delay(400);
  Require(Motion.IsLifted(hovered) && hovered.RenderTransform is TranslateTransform { Y: -Motion.LiftHeight }, "The tile did not lift under the pointer.");
  Capture(RootContent, Path.Combine(directory, "desktop-hover-lift.png"));
  hovered.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0) { RoutedEvent = System.Windows.Input.Mouse.MouseLeaveEvent });
  await Task.Delay(400);
  Require(hovered.RenderTransform is TranslateTransform { Y: 0 } && hovered.Effect == null, "The tile did not settle after the pointer left.");
  // Started videos show how far they were watched.
  var watched = new MediaCard(new MediaItem { Path = Path.Combine(fixtureDirectory, "angefangen.mp4"), Kind = MediaKind.Video, DurationSeconds = 600, PlaybackPositionSeconds = 240 }, false);
  Require(watched.ProgressVisibility == Visibility.Visible && Math.Abs(watched.Progress - .4) < 1e-9 && watched.ProgressLabel.Contains("40", StringComparison.Ordinal), "The watch progress is wrong.");
  Require(new MediaCard(watched.Item with { PlaybackPositionSeconds = 590 }, false).ProgressVisibility == Visibility.Collapsed, "A finished video still shows progress.");
  // A narrow window folds both sidebars to their rails without changing the saved choice; widening brings both back.
  var (oldWidth, oldState) = (Width, WindowState); WindowState = WindowState.Normal; Width = 940; await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
  Require(!SidebarExpanded && SidebarRail.IsVisible && !DetailsExpanded && DetailsColumn.Width.Value == RailWidth && !_settings.SidebarCollapsed && !_settings.DetailsCollapsed,
   "A narrow window did not adapt its columns.");
  Capture(RootContent, Path.Combine(directory, "desktop-narrow.png"));
  Width = Math.Max(1300, oldWidth); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
  Require(SidebarExpanded && DetailsExpanded && DetailsColumn.Width.Value == _settings.DetailsWidth, "The wide window did not restore sidebar and details.");
  Width = oldWidth; WindowState = oldState; await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
  // Large view of a video thumbnail on the space bar.
  var lookSource = Directory.EnumerateFiles(fixtureDirectory, "*.png").First();
  var look = new QuickLookWindow(this, new MediaItem { Path = Path.Combine(fixtureDirectory, "grossansicht.mp4"), Title = "Großansicht", Kind = MediaKind.Video, DurationSeconds = 754, Width = 1920, Height = 1080, ThumbnailPath = lookSource }, () => Task.CompletedTask);
  look.Show(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
  Require(look.HasPicture, "The large view did not show the thumbnail.");
  Capture((FrameworkElement)look.Content, Path.Combine(directory, "quick-look.png")); look.Close();
  // Both sidebars fold to their rails and come back; a symbol of a rail opens its bar.
  ToggleSidebar(); ToggleDetails(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
  Require(!SidebarExpanded && SidebarRail.IsVisible && SidebarColumn.ActualWidth == RailWidth && !DetailsExpanded && DetailsRail.IsVisible && _settings.SidebarCollapsed && _settings.DetailsCollapsed,
   "The sidebars did not fold to their rails.");
  Capture(RootContent, Path.Combine(directory, "desktop-sidebar-folded.png"));
  OpenSidebarAt("tags"); OpenDetailsAt("scenes"); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
  Require(SidebarExpanded && DetailsExpanded && !_settings.SidebarCollapsed && !_settings.DetailsCollapsed, "A symbol of a rail did not open its bar.");
  // The details as tabs show one part at a time; switching back shows everything again.
  SetDetailsTabs(true); ShowDetailsTab("scenes"); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
  Require(DetailsTabBar.IsVisible && DetailsScenesPart.Visibility == Visibility.Visible && DetailsTagsPart.Visibility == Visibility.Collapsed && DetailsFieldsPart.Visibility == Visibility.Collapsed,
   "The scenes tab does not show the scenes alone.");
  RevealTagInput(); Require(DetailsTab == "tags" && DetailsTagsPart.Visibility == Visibility.Visible, "The tag field did not bring its tab.");
  Capture(RootContent, Path.Combine(directory, "details-tabs.png"));
  SetDetailsTabs(false);
  Require(!DetailsTabBar.IsVisible && DetailsTagsPart.Visibility == Visibility.Visible && DetailsFieldsPart.Visibility == Visibility.Visible && DetailsScenesPart.Visibility == Visibility.Visible,
   "Switching back did not show everything one below the other.");
  // The filter flyout opens at a section; short forms in the search become filters and chips; the view menu opens.
  FilterButtonClicked(FilterButton, new RoutedEventArgs()); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
  Require(FilterFlyout.IsOpen && FilterSectionHost.Child != null, "The filter flyout did not open at a section.");
  Capture(FilterFlyoutFrame, Path.Combine(directory, "filter-flyout.png"));
  FilterFlyout.IsOpen = false;
  SearchBox.Text = "#kurzform -#ohne ★3";
  SearchKeyDown(SearchBox, new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(SearchBox)!, 0, System.Windows.Input.Key.Enter) { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
  await Settled();
  Require(SearchBox.Text.Length == 0 && _requiredTags.SequenceEqual(["kurzform"]) && _excludedTags.SequenceEqual(["ohne"]) && RatingFilter.SelectedIndex == 3
   && ActiveTagFilters.Children.Count >= 3 && FilterButtonText.Text == T("Filter · {0:N0}", 3), $"Short forms did not become filters ({FilterButtonText.Text}).");
  ResetFiltersClicked(this, new RoutedEventArgs()); await Settled();
  Require(RatingFilter.SelectedIndex == 0 && _requiredTags.Count == 0 && FilterButtonText.Text == T("Filter"), "Removing all filters left some behind.");
  ViewMenuClicked(ViewButton, new RoutedEventArgs()); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
  Require(ViewPopup.IsOpen, "The view menu did not open."); ViewPopup.IsOpen = false;
  // Previous/next in the player walk the view's order over images and wrap only with repeat.
  var videos = new[] { "000 Video A", "000 Video B", "zzz Video C" }.Select(title => _repository.Upsert(new MediaItem { Path = Path.Combine(fixtureDirectory, title + ".mp4"), Title = title, Kind = MediaKind.Video })).ToArray();
  await RefreshAsync();
  Require(Neighbor(videos[0], 1, false)?.Id == videos[1].Id && Neighbor(videos[2], 1, false) == null && Neighbor(videos[2], 1, true)?.Id == videos[0].Id && Neighbor(videos[0], -1, true)?.Id == videos[2].Id,
   "Previous/next did not follow the view or wrapped without repeat.");
  foreach (var fake in videos) _repository.Delete(fake.Id);
  var far = pagingItems[1400];
  await RefreshAsync(far.Id); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
  Require(Selected?.Id == far.Id && MediaList.ItemContainerGenerator.ContainerFromItem(MediaList.SelectedItem) is System.Windows.Controls.ListBoxItem { IsVisible: true }, "A medium far down was not selected and shown.");
  foreach (var item in pagingItems) _repository.Delete(item.Id);
  _anchor = 0;
  await RefreshAsync(first.Id);
  SortChoice.SelectedIndex = 1; await RefreshAsync(); Require((MediaList.Items[0] as MediaCard)?.Title == "Unterwegs nach Norden", "Descending title sort failed.");
  SortChoice.SelectedIndex = 0; await RefreshAsync(first.Id);
  ViewChoice.SelectedIndex = 1; await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); Capture((FrameworkElement)RootContent, Path.Combine(directory, "desktop-list.png"));
  Require(_settings.ViewMode == 1, "View mode preference did not update."); ViewChoice.SelectedIndex = 0;
  var hashBefore = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(first.Path));
  ShowImage(_repository.Search().Single(item => item.Id == first.Id)); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
  Require(_viewer is ImageViewerWindow, "Image viewer not opened.");
  var viewer = (ImageViewerWindow)_viewer!; await viewer.ReadyTask; Require(viewer.HasImage,"Image viewer failed to decode image.");
   var zoom = viewer.ZoomFactor; viewer.ChangeZoom(1.25); Require(viewer.ZoomFactor > zoom, "Viewer zoom failed."); viewer.Rotate();
   Require(viewer.Rotation == 90, "View-only rotation failed.");
  // Flip (view only), copy picture and path, wallpaper and favorite; clipboard and wallpaper go through hooks, so the user's stay untouched.
  var (putImage, putText, setWallpaper) = (ImageViewerWindow.PutImage, ImageViewerWindow.PutText, ImageViewerWindow.SetWallpaper);
  try
  {
   object? copied = null; string? copiedText = null, wallpaperPath = null;
   ImageViewerWindow.PutImage = image => copied = image; ImageViewerWindow.PutText = text => copiedText = text; ImageViewerWindow.SetWallpaper = path => { wallpaperPath = path; return true; };
   var shown = viewer.CurrentItem!;
   viewer.Flip(true); Require(viewer.FlippedHorizontally && !viewer.FlippedVertically, "Flipping did not mirror the view.");
   Require(viewer.CopyImage() && copied != null && viewer.CopyPath() && copiedText == shown.Path, "Copying the picture or its path failed.");
   Require(viewer.UseAsWallpaper() && wallpaperPath == Path.GetFullPath(shown.Path), "The wallpaper got the wrong file.");
   var favorite = viewer.SwitchFavorite(); Require(favorite == _repository.IsFavorite(_repository.GetMedia(shown.Id)!), "The favorite switch did not reach the catalog.");
   viewer.SwitchFavorite(); viewer.Flip(true); Require(!viewer.FlippedHorizontally, "Flipping back failed.");
  }
  finally { (ImageViewerWindow.PutImage, ImageViewerWindow.PutText, ImageViewerWindow.SetWallpaper) = (putImage, putText, setWallpaper); }
  Capture((FrameworkElement)viewer.Content, Path.Combine(directory, "image-viewer.png"));
  // Details, film strip and the slideshow with its settings.
  viewer.SetInfo(true); Require(viewer.InfoShown && viewer.InfoText.Contains(first.Title) && viewer.InfoText.Contains("800 × 480"), "Image details were not shown over the picture.");
  viewer.SetStrip(true); await viewer.StripTask; await Task.Delay(400); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
  Require(viewer.StripCount > 1, "The film strip did not list the neighbouring images.");
  Capture((FrameworkElement)viewer.Content, Path.Combine(directory, "image-viewer-strip.png"));
  viewer.Slideshow = new SlideshowPreferences { IntervalSeconds = .25, Order = SlideshowOrder.InOrder, Fade = true, PanAndZoom = true };
  var slideStart = viewer.CurrentIndex; viewer.StartSlideshow(); await Task.Delay(900);
  Require(viewer.SlideshowRunning && viewer.CurrentIndex != slideStart, "The slideshow did not advance with its interval.");
  viewer.StopSlideshow(); await viewer.ReadyTask;
  viewer.Slideshow = viewer.Slideshow with { Order = SlideshowOrder.Random };
  Require(Enumerable.Range(0, 20).All(_ => viewer.NextSlideIndex() != viewer.CurrentIndex), "A random slide repeated the current image.");
  viewer.SetInfo(false); viewer.SetStrip(false);
  var indexBefore = viewer.CurrentIndex; viewer.Navigate(1); await viewer.ReadyTask; Require(viewer.HasImage,"Image viewer next image decoding failed."); Require(viewer.CurrentIndex != indexBefore, "Viewer next image failed."); viewer.Fit(); StopSlideshow();
  Require(hashBefore.SequenceEqual(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(first.Path))), "Viewer modified source image.");
  var queueDirectory = Path.Combine(directory, "queue");
  var queue = new MediaQueueWindow(this, queueDirectory, _tools, first.Path, []); queue.AddPreparedJob(new MediaJob { Kind = MediaJobKind.Convert, Input = first.Path, Output = Path.Combine(queueDirectory, "output.mp4") }); queue.Show();
  await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); Capture((FrameworkElement)queue.Content, Path.Combine(directory, "media-queue.png"));
  var syntheticVideo = Path.Combine(fixtureDirectory, "synthetic-video.mp4");
  if (File.Exists(syntheticVideo))
  {
   // Clip tool: lossless join of matching whole files, normalized join with crossfade, random clips per video.
   var lossless = await queue.PlanCombineAsync([syntheticVideo, syntheticVideo], new CombinePlan(MuteAudio: true));
   Require(lossless.Kind == MediaJobKind.Combine && lossless.MuteAudio, "Matching whole videos were not joined losslessly.");
   var faded = await queue.PlanCombineAsync([syntheticVideo, syntheticVideo], new CombinePlan(CombineRange.Random, LengthSeconds: .6, CrossfadeSeconds: .2, Seed: 7));
   Require(faded.Kind == MediaJobKind.CombineSegments && faded.CrossfadeSeconds == .2 && faded.Segments.All(s => s.EndSeconds - s.StartSeconds == .6 || s.EndSeconds == null), "Crossfade did not plan normalized segments.");
   var clips = await queue.PlanRandomClipsAsync([syntheticVideo, syntheticVideo], .5, true, queueDirectory, 3);
   Require(clips.Count == 2 && clips.All(c => c.Kind == MediaJobKind.Clip && c.MuteAudio && c.DurationSeconds is > 0 and <= 1) && clips[0].Output != clips[1].Output, "Random clips were not planned per video.");
  }
  queue.Close(); await queue.ClosedTask;
  var reopened = new MediaQueueWindow(this, queueDirectory, _tools, first.Path, []); Require(reopened.JobCount == 1, "Queue window did not persist prepared job."); reopened.Close(); await reopened.ClosedTask;
  await RunSettingsSmokeTestAsync(directory);
  await RunSettingsEffectSmokeTestAsync(fixtureDirectory);
  await RunOpeningSmokeTestAsync(directory);
  await RunPrivacySmokeTestAsync(directory);
  await RunLibraryFolderSmokeTestAsync(directory, fixtureDirectory);
  await RunMigrationSmokeTestAsync(directory);
  await RunTakeoverSmokeTestAsync(directory);
  await RunNotificationSmokeTestAsync(directory);
  await RunMetadataSmokeTestAsync(directory);
  await RunHomeSmokeTestAsync(directory);
  await RunCollectionSmokeTestAsync(directory);
  await RunLibrarySettingsSmokeTestAsync(directory, fixtureDirectory);
  await RefreshAsync(first.Id);
  Status.Text = "Bereit · 6 Bilder importiert · Deine Bibliothek ist lokal gespeichert";
  await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
  await Task.WhenAll(MediaList.Items.Cast<MediaCard>().Select(card => card.ReadyTask));
  await _previewReady;
  UpdateLayout();
  Capture(RootContent, Path.Combine(directory, "desktop.png"));
  await RunThemeSmokeTestAsync(directory);
 }
 /// <summary>The hover preview renders, caches and cycles frames of a real video and restores the thumbnail on leave.</summary>
 private async Task RunHoverPreviewSmokeTestAsync(string directory, Guid videoId)
 {
  static void Require(bool check, string message) { if (!check) throw new InvalidOperationException(message); }
  var before = _repository.GetPreferences().Previews;
  _repository.SavePreviewPreferences(before with { HoverEnabled = true, HoverDelay = false, HoverFrames = 3, HoverSpeed = HoverSpeed.Fast, HoverTimestamp = true });
  HoverFollowsMouse = false;
  try
  {
   await RefreshAsync(videoId);
   var card = MediaList.Items.Cast<MediaCard>().Single(c => c.Item.Id == videoId);
   HoverFramesShown = 0; HoverOver(card);
   var deadline = DateTime.UtcNow.AddSeconds(30);
   while (HoverFramesShown < 4 && DateTime.UtcNow < deadline) await Task.Delay(100);
   Require(HoverFramesShown >= 4 && card.PreviewFrame != null && card.PreviewTime.Length > 0, "Hover preview did not render and cycle frames.");
   var cached = Directory.GetFiles(Path.Combine(_settings.DataDirectory, "hover-cache"), "*.jpg").Where(f => !f.EndsWith(".part.jpg", StringComparison.OrdinalIgnoreCase)).ToArray();
   Require(cached.Length == 3, "Hover frames were not cached.");
   await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); Capture(RootContent, Path.Combine(directory, "desktop-hover-preview.png"));
   HoverOver(null); await HoverPreviewTask;
   Require(card.PreviewFrame == null && !card.IsPreviewing, "Leaving the tile did not restore the thumbnail.");
   // Clip mode makes the missing preview clip in the clip cache, plays it on the tile and ends it on leave.
   _repository.SavePreviewPreferences(before with { HoverEnabled = true, HoverDelay = false, HoverMode = HoverMode.Clip, ClipSegments = 4, ClipSegmentMilliseconds = 500 });
   HoverClipsShown = 0; HoverOver(card); await HoverPreviewTask;
   var clip = PreviewClipPath(card.Item, ClipSpec(_repository.GetPreferences().Previews));
   Require(HoverClipsShown == 1 && File.Exists(clip) && card.PreviewClip?.LocalPath == clip && card.IsPreviewing, "Clip mode did not make and show the preview clip.");
   await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
   var player = Descendants<LoopingClip>(MediaList).SingleOrDefault(p => p.Source != null);
   Require(player?.Content is System.Windows.Controls.MediaElement, "The tile did not get a clip player.");
   HoverOver(null); await HoverPreviewTask; await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
   Require(card.PreviewClip == null && !card.IsPreviewing && player!.Content == null, "Leaving the tile did not end the clip.");
  }
  finally { _repository.SavePreviewPreferences(before); HoverFollowsMouse = true; }
 }
 /// <summary>Both colour schemes keep every listed text pair readable, switch in place and render without leftovers.</summary>
 private async Task RunThemeSmokeTestAsync(string directory)
 {
  static void Require(bool check, string message) { if (!check) throw new InvalidOperationException(message); }
  foreach (var dark in new[] { true, false })
   foreach (var (foreground, background, minimum) in Theme.ContrastPairs)
   {
    var ratio = Theme.Contrast(Theme.Color(foreground, dark), Theme.Color(background, dark));
    Require(ratio >= minimum, $"{(dark ? "Dark" : "Light")} scheme: {foreground} on {background} has contrast {ratio:0.00}, needs {minimum}.");
   }
  var text = (SolidColorBrush)Theme.Brush("Text");
  Theme.Apply(ColorScheme.Light); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
  Require(Theme.ReplacedKeys.Count == 0, "Theme brushes were frozen by a style or template and had to be replaced: " + string.Join(", ", Theme.ReplacedKeys));
  Require(!Theme.IsDark && ReferenceEquals(text, Theme.Brush("Text")) && text.Color == Theme.Color("Text", false), "Light scheme did not recolour the shared brushes in place.");
  Capture(RootContent, Path.Combine(directory, "desktop-light.png"));
  var settings = new SettingsWindow(this, _repository, _settings, _settings.DataDirectory, () => { }, () => { }); settings.Show();
  try
  {
   foreach (var (page, file) in new[] { ("Appearance", "settings-appearance-light.png"), ("Video player", "settings-player-light.png") })
   { settings.SelectPage(page); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); Capture((FrameworkElement)settings.Content, Path.Combine(directory, file)); }
   settings.SelectPage("Appearance"); await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
   Descendants<ThemePicker>((FrameworkElement)settings.Content).Single().Value=ColorScheme.Light;
   settings.SelectPage("Video player"); await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
   settings.PlayerDisplay.Shortcuts.IsChecked=false; settings.BookmarkShortcut.BringIntoView();
   await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
   Capture((FrameworkElement)settings.Content,Path.Combine(directory,"settings-shortcuts-off-light.png"));
   settings.PlayerDisplay.Shortcuts.IsChecked=_repository.GetPreferences().Playback.KeyboardShortcuts;
  }
  finally { settings.CloseDiscarding(); }
  Require(Theme.Mode == ColorScheme.Light, "Closing the settings without changes altered the scheme.");
  Theme.Apply(ColorScheme.Dark); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
  Require(Theme.IsDark && text.Color == Theme.Color("Text", true), "Dark scheme was not restored.");
 }
 private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
 {
  for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
  {
   var child = VisualTreeHelper.GetChild(root, index);
   if (child is T match) yield return match;
   foreach (var nested in Descendants<T>(child)) yield return nested;
  }
 }
 private static void Capture(FrameworkElement element, string path)
 {
  // The brush draws the element at its layout offset, so its margin belongs to the picture; without it the right and
  // bottom edges were cut off by exactly the margin and buttons looked as if they lay outside the window.
  element.UpdateLayout(); var width = element.ActualWidth + element.Margin.Left + element.Margin.Right; var height = element.ActualHeight + element.Margin.Top + element.Margin.Bottom;
  var screenshot = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
  var visual = new DrawingVisual();
  var bounds=new Rect(0,0,width,height);
  // Explicit source bounds also retain the space occupied by a native HwndHost.
  // Paint the window background first so contrast problems show as they do on screen.
  using (var drawing = visual.RenderOpen()) { drawing.DrawRectangle(Window.GetWindow(element)?.Background,null,bounds); drawing.DrawRectangle(new VisualBrush(element) { ViewboxUnits=BrushMappingMode.Absolute,Viewbox=bounds },null,bounds); }
  screenshot.Render(visual);
  var output = new PngBitmapEncoder(); output.Frames.Add(BitmapFrame.Create(screenshot)); using var file = File.Create(path); output.Save(file);
 }
}
