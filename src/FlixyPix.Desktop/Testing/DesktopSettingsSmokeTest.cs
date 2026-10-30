using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 // Contact-sheet thumbnails from the preview settings, using the freshly provisioned FFmpeg.
 private async Task RunContactSheetSmokeTestAsync(Guid videoId, string directory)
 {
  static void Require(bool check, string message) { if (!check) throw new InvalidOperationException(message); }
  var original = _repository.GetPreferences().Previews;
  _repository.SavePreviewPreferences(original with { SheetColumns = 2, SheetRows = 2, SheetWidth = 320 });
  try
  {
   _repository.Upsert(_repository.GetMedia(videoId)! with { ThumbnailPath = null });
   await PrepareMediaAsync(CancellationToken.None);
   var sheet = _repository.GetMedia(videoId)!.ThumbnailPath;
   Require(sheet != null && File.Exists(sheet), "Contact-sheet thumbnail was not generated.");
   var info = await DesktopImaging.Service.GetInfoAsync(sheet!, CancellationToken.None);
   Require(info.Width == 320, $"Contact sheet has width {info.Width} instead of the configured 320 pixels.");

   // The look of the original: border, header and timestamps, made in the background and remembered per thumbnail.
   var styled = original with { SheetColumns = 2, SheetRows = 2, SheetWidth = 640, SheetBorder = 4, SheetHeader = true, SheetTimestamp = SheetTimestamp.BottomRight, SheetFastSeek = true };
   _repository.SavePreviewPreferences(styled);
   var (made, _) = await RegenerateThumbnailsAsync([_repository.GetMedia(videoId)!], CancellationToken.None);
   var video = _repository.GetMedia(videoId)!;
   var styledInfo = await DesktopImaging.Service.GetInfoAsync(video.ThumbnailPath!, CancellationToken.None);
   Require(made == 1 && styledInfo.Width == 640 && styledInfo.Height > 2 * (640 / 2) * 9 / 16 && video.Extra[SheetStyleKey] == SheetSignature(styled),
    $"The styled sheet was not made as configured ({styledInfo.Width} × {styledInfo.Height}).");
   Require((await RegenerateThumbnailsAsync(null, CancellationToken.None)).Made == 0, "Thumbnails already in the current style were made again.");
   _repository.SavePreviewPreferences(styled with { SheetBorder = 8 });
   var before = video.ThumbnailPath!;
   Require((await RegenerateThumbnailsAsync(null, CancellationToken.None)).Made == 1 && !File.Exists(before), "A changed style did not remake the thumbnail and remove the old one.");
   // A folder remakes its videos even in the current style; another folder leaves them alone.
   var videoFolder = Path.GetDirectoryName(_repository.GetMedia(videoId)!.Path)!;
   Require((await RegenerateThumbnailsAsync(null, CancellationToken.None, videoFolder)).Made == 1, "Remaking a folder did not remake its video.");
   Require((await RegenerateThumbnailsAsync(null, CancellationToken.None, Path.Combine(videoFolder, "leer"))).Checked == 0, "Remaking an unrelated folder touched other videos.");

   var settings = new SettingsWindow(this, _repository, _settings, _settings.DataDirectory, () => { }, () => { }, RenderSampleSheetAsync); settings.Show();
   try
   {
    settings.SelectPage("Thumbnails"); MediaList.SelectedItem = MediaList.Items.Cast<MediaCard>().FirstOrDefault(c => c.Item.Id == videoId);
    settings.ShowSample(); await settings.SampleTask;
    Require(settings.HasSample, "The settings did not show a sample sheet.");
    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    settings.ScrollToSample(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    Capture((FrameworkElement)settings.Content, Path.Combine(directory, "settings-previews-sample.png"));
   }
   finally { settings.Close(); }
  }
  finally { _repository.SavePreviewPreferences(original); }
 }

 // Settings window: pages, validation, saving and the shared hotkey notation.
 /// <summary>General options chosen in the settings window change what the library does.</summary>
 private async Task RunSettingsEffectSmokeTestAsync(string fixtureDirectory)
 {
  static void Require(bool check, string message) { if (!check) throw new InvalidOperationException(message); }
  var before = _repository.GetPreferences(); var settingsBefore = _settings;
  var window = new SettingsWindow(this, _repository, _settings, _settings.DataDirectory, () => { }, () => { }); window.Show();
  try
  {
   window.ShowExtension.IsChecked = true; window.MarkMissing.IsChecked = true; window.ConfirmRemove.IsChecked = false; window.AlwaysReport.IsChecked = true;
   window.RestoreChoice.Value = 1; window.ImageWheel.Value = (int)ImageWheelAction.Zoom;
   Require(window.TrySave(), "General options could not be saved.");
  }
  finally { window.Close(); }
  var missing = new MediaItem { Path = Path.Combine(fixtureDirectory, "verschoben.png"), Title = "Verschoben", Kind = MediaKind.Image };
  _repository.Upsert(missing);
  await RefreshAsync(missing.Id);
  var cards = MediaList.Items.Cast<MediaCard>().ToList();
  var missingCard = cards.Single(c => c.Item.Id == missing.Id);
  await Task.WhenAll(cards.Select(c => c.EnsureLoaded()));
  Require(missingCard.IsMissing && cards.Where(c => c.Item.Id != missing.Id && File.Exists(c.Item.Path)).All(c => !c.IsMissing), "Missing files were not marked.");
  Require(missingCard.Title == "Verschoben.png", "File extensions were not shown.");
  Require(ImportReportWanted(new ImportResult { Imported = 1 }), "The import report setting had no effect.");
  // "View only" brings back the view and its sort, not the search text; a library without stored state falls back to the last view.
  _repository.SaveViewState(new ViewState { View = "Image", SortOrder = 1, SearchText = "nicht wiederhergestellt" });
  await RestoreLastViewAsync();
  Require(_view == "Image" && SortChoice.SelectedIndex == 1 && SearchBox.Text.Length == 0, "The last view was not restored.");
  ApplyViewState(new ViewState()); await _lastRefresh;
  ShowImage(cards.First(c => c.Item.Kind == MediaKind.Image && !c.IsMissing).Item);
  var viewer = (ImageViewerWindow)_viewer!; await viewer.ReadyTask;
  var zoom = viewer.ZoomFactor;
  Require(viewer.WheelAction == ImageWheelAction.Zoom && viewer.HandleWheel(120, false) && viewer.ZoomFactor > zoom, "The image viewer ignored the mouse wheel setting.");
  StopSlideshow();
  MediaList.SelectedItem = missingCard; RemoveSelected();
  Require(_repository.GetMedia(missing.Id) == null, "Removing from the catalog still asked although confirmation was off.");
  _repository.SavePreferences(before); _settings = settingsBefore;
  await RefreshAsync();
  Require(!ImportReportWanted(new ImportResult { Imported = 1 }) && MediaList.Items.Cast<MediaCard>().All(c => !c.Title.EndsWith(".png", StringComparison.Ordinal)), "Default general options were not restored.");
 }
 /// <summary>A hand-edited or truncated appsettings.json: readable values stay, the rest is reset, an unreadable file gives
 /// way to the last good copy; the damaged file is kept and the repaired one written back.</summary>
 private static void RunSettingsFileRepairSmokeTest(string directory)
 {
  static void Require(bool check, string message) { if (!check) throw new InvalidOperationException(message); }
  var folder = Path.Combine(directory, "settings-repair"); Directory.CreateDirectory(folder);
  var path = Path.Combine(folder, "appsettings.json");
  var good = new DesktopSettings { Theme = "light", TileWidth = 250, DataDirectory = Path.Combine(folder, "Library") };
  good.SaveTo(path);
  var untouched = DesktopSettings.LastRepair;
  var loaded = DesktopSettings.LoadFrom(path);
  Require(loaded.Theme == "light" && loaded.TileWidth == 250, "A good settings file did not load.");
  Require(ReferenceEquals(DesktopSettings.LastRepair, untouched) && File.Exists(DesktopSettings.LastGoodPath(path)), "A good settings file was treated as damaged or has no last good copy.");
  // Truncated: nothing readable, the last good copy stands in.
  File.WriteAllText(path, File.ReadAllText(path)[..20]);
  var restored = DesktopSettings.LoadFrom(path);
  Require(restored.Theme == "light" && restored.TileWidth == 250 && DesktopSettings.LastRepair is { Source: SettingsSource.LastGoodCopy, DamagedCopy: { } copy } && File.Exists(copy),
   "A truncated settings file did not fall back to the last good copy.");
  // Wrong types, a comment and a trailing comma: readable values stay, the others are reset.
  File.WriteAllText(path, "{ // edited by hand\n \"Theme\": 5, \"TileWidth\": 300, \"Libraries\": \"none\", \"Language\": \"xx\", \"DataDirectory\": \"relative\", }");
  var salvaged = DesktopSettings.LoadFrom(path);
  Require(salvaged.TileWidth == 300 && salvaged.Theme == "system" && salvaged.Libraries.Count == 0 && salvaged.Language == Texts.Automatic && Path.IsPathFullyQualified(salvaged.DataDirectory)
   && DesktopSettings.LastRepair is { Source: SettingsSource.File } repair && new[] { "Theme", "Libraries", "Language", "DataDirectory" }.All(repair.ResetValues.Contains),
   $"Single damaged values were not reset ({string.Join(", ", DesktopSettings.LastRepair?.ResetValues ?? [])}).");
  Require(System.Text.Json.JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(path))?.TileWidth == 300, "The repaired settings were not written back.");
  // Empty and without a last good copy: defaults.
  File.Delete(DesktopSettings.LastGoodPath(path)); File.WriteAllText(path, "");
  Require(DesktopSettings.LoadFrom(path).TileWidth == new DesktopSettings().TileWidth && DesktopSettings.LastRepair is { Source: SettingsSource.Defaults },
   "An empty settings file did not start with defaults.");
 }

 private async Task RunSettingsSmokeTestAsync(string directory)
 {
  static void Require(bool check, string message) { if (!check) throw new InvalidOperationException(message); }
  RunSettingsFileRepairSmokeTest(directory);
  Require(PlayerHotkeys.Match(PlayerActions.Defaults(), Key.Right, ModifierKeys.None) == PlayerActions.SmallForward, "Default hotkey did not match.");
  Require(PlayerHotkeys.Match(PlayerActions.Defaults(), Key.Right, ModifierKeys.Shift) == PlayerActions.LargeForward, "Modifier hotkey did not match.");
  Require(PlayerHotkeys.Format(ModifierKeys.Control | ModifierKeys.Shift, Key.Right) == "Ctrl+Shift+Right", "Hotkey formatting changed.");
  var before = _repository.GetPreferences().Playback;
  var window = new SettingsWindow(this, _repository, _settings, _settings.DataDirectory, () => { }, () => { });
  window.Show();
  try
  {
   await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
   foreach (var (page, file) in new[] { ("Appearance", "settings-appearance.png"), ("General", "settings-general.png"), ("Videos", "settings-videos.png"), ("Video player", "settings-player.png"), ("Thumbnails", "settings-previews.png"), ("Images", "settings-images.png"),
    ("Cache", "settings-cache.png"), ("Security", "settings-security.png"), ("Database", "settings-database.png"), ("Tools", "settings-tools.png") })
   {
    window.SelectPage(page); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    Capture((FrameworkElement)window.Content, Path.Combine(directory, file));
   }
   window.SelectPage("Video player"); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
   window.Seek.WheelStep.BringIntoView(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
   Capture((FrameworkElement)window.Content, Path.Combine(directory, "settings-player-seek.png"));
   Require(window.Search(T("Small jump")) >= 1 && window.Search(T("Large jump")) >= 1, "Settings search did not find the jump options.");
   await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
   Capture((FrameworkElement)window.Content, Path.Combine(directory, "settings-search.png"));
   Require(window.Search("gibt es nicht") == 0, "Settings search matched an unknown term.");
   var width = window.SheetWidth.Value;
   window.SheetWidth.Value = 50;
   Require(!window.TrySave() && window.LastError?.Contains(T("Thumbnail width")) == true, "An out-of-range width was not rejected inline.");
   window.SheetWidth.Value = width;
   window.PlayerDisplay.Shortcuts.IsChecked=false;
   Require(!window.PlayerShortcutsActive && !window.BookmarkShortcut.ShortcutActive
    && System.Windows.Automation.AutomationProperties.GetItemStatus(window.BookmarkShortcut).Contains(T("Inactive")),"Inactive shortcut state is not visible to assistive technology.");
   var bookmarkKey=window.BookmarkShortcut.Value;
   window.SelectPage("Video player"); await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
   window.BookmarkShortcut.BringIntoView(); await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
   Capture((FrameworkElement)window.Content,Path.Combine(directory,"settings-shortcuts-off.png"));
   window.PlayerDisplay.Shortcuts.IsChecked=true;
   Require(window.PlayerShortcutsActive && window.BookmarkShortcut.ShortcutActive && window.BookmarkShortcut.Value==bookmarkKey,
    "Reactivating shortcuts lost the stored assignment.");
   window.AlwaysFullscreen.IsChecked = true;
   // Language: automatic first, then every language in its own name; the choice reaches appsettings.json through the result.
   var languages = window.LanguageChoice.Items.OfType<ComboBoxItem>().ToList();
   Require(languages.Count == Texts.Languages.Count + 1 && (string)languages[0].Tag == Texts.Automatic && languages.Any(item => (string)item.Content == "日本語"),
    "The language choice does not offer automatic and all languages.");
   window.LanguageChoice.SelectedItem = languages.Single(item => (string)item.Tag == "en");
   Require(window.TrySave() && window.Result != null, "Settings could not be saved.");
   Require(window.Result!.Language == "en", "The chosen language was not part of the saved settings.");
   Require(_repository.GetPreferences().Playback.AlwaysFullscreen, "Saved player setting was not stored in the library.");
  }
  finally { window.Close(); _repository.SavePlaybackPreferences(before); }
 }
}
