using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>Loading the stored values into the controls, resetting a rubric, and saving.</summary>
internal sealed partial class SettingsWindow
{
 private void LoadPlayback(PlaybackPreferences playback)
 {
  AlwaysFullscreen.IsChecked = playback.AlwaysFullscreen; PlayBestVersion.IsChecked = playback.PlayBestVersion; hardwareDecoding.IsChecked = playback.HardwareDecoding;
  PreventSleep.IsChecked = playback.PreventSleep; MediaKeys.IsChecked = playback.MediaKeys;
  smallForward.Value = playback.SmallJumpForwardSeconds; smallBackward.Value = playback.SmallJumpBackwardSeconds;
  largeForward.Value = playback.LargeJumpForwardSeconds; largeBackward.Value = playback.LargeJumpBackwardSeconds;
  foreach (var (action, box) in hotkeys) box.Value = playback.Hotkeys.GetValueOrDefault(action, "");
  MouseLeft.SelectedIndex = (int)playback.LeftClick; mouseRight.SelectedIndex = (int)playback.RightClick; mouseMiddle.SelectedIndex = (int)playback.MiddleClick;
  mouseBack.SelectedIndex = (int)playback.BackClick; mouseForward.SelectedIndex = (int)playback.ForwardClick; PlayerWheel.SelectedIndex = (int)playback.Wheel; Seek.Load(playback); PlayerDisplay.Load(playback);
 }
 private void LoadHome(HomePreferences home)
 {
  StartOnHome.IsChecked = home.StartOnHome;
  foreach (var (kind, check) in HomeRows) check.IsChecked = home.Rows.Contains(kind);
  Library.RecentDays.Value = home.RecentDays;
 }
 private void LoadHistory(HistoryPreferences history)
 {
  HistoryEnabled.IsChecked = history.Enabled; ShowOCounter.IsChecked = history.ShowOCounter; WatchedShare.Value = Math.Round(history.WatchedShare * 100);
 }
 private void LoadGeneral(GeneralPreferences general)
 {
  IncludeHidden.IsChecked = general.IncludeHiddenFolders; ConfirmRemove.IsChecked = general.ConfirmRemove; ShowExtension.IsChecked = general.ShowExtension;
  FolderCounts.IsChecked = general.ShowFolderCounts; CategoryCounts.IsChecked = general.ShowCategoryCounts; TagCategoryCounts.IsChecked = general.ShowTagCategoryCounts;
  MarkMissing.IsChecked = general.MarkMissingFiles; AlwaysReport.IsChecked = general.AlwaysShowImportReport; RestoreChoice.Value = !general.RestoreLastView ? 0 : general.RestoreScope == RestoreScope.View ? 1 : 2;
  ImageWheel.Value = (int)general.ImageWheel;
  Library.Load(general, repository.GetPreferences().Home);
 }
 private void LoadNfo(NfoPreferences nfo)
 {
  NfoTitle.IsChecked = nfo.ImportTitle; NfoTags.IsChecked = nfo.ImportTags; NfoDescription.IsChecked = nfo.ImportDescription; NfoSubfolders.IsChecked = nfo.SearchSubfolders;
  NfoThumbnail.IsChecked = nfo.ImportThumbnail; NfoFanart.IsChecked = nfo.ImportFanart;
  nfoTagElements.Text = nfo.TagElements; nfoDescriptionElements.Text = nfo.DescriptionElements;
 }
 private void LoadSlideshow(SlideshowPreferences slideshow)
 {
  SlideInterval.Value = slideshow.IntervalSeconds; SlideOrder.Value = (int)slideshow.Order; slideSpeed.Value = (int)slideshow.Speed;
  SlideFade.IsChecked = slideshow.Fade; SlidePan.IsChecked = slideshow.PanAndZoom; SlideInfo.IsChecked = slideshow.ShowInfo;
 }
 private void LoadPreviews(PreviewPreferences previews)
 {
  columns.Value = previews.SheetColumns; rows.Value = previews.SheetRows; SheetWidth.Value = previews.SheetWidth;
  processes.Value = previews.GeneratorProcesses; skipCustom.IsChecked = previews.SkipWhenCustomThumbnail; fingerprintsOnImport.IsChecked = previews.FingerprintsOnImport;
  hoverEnabled.IsChecked = previews.HoverEnabled; hoverFrames.Value = previews.HoverFrames; hoverSpeed.Value = (int)previews.HoverSpeed;
  hoverMode.Value = (int)previews.HoverMode; clipSegments.Value = previews.ClipSegments; clipSeconds.Value = previews.ClipSegmentMilliseconds / 1000.0;
  clipWidth.Value = previews.ClipWidth; clipsOnImport.IsChecked = previews.ClipsOnImport; wallClips.Value = previews.WallClips;
  hoverDelay.IsChecked = previews.HoverDelay; hoverTimestamp.IsChecked = previews.HoverTimestamp;
  SheetFast.IsChecked = previews.SheetFastSeek; SheetBorder.Value = previews.SheetBorder; SheetHeader.IsChecked = previews.SheetHeader;
  SheetTimestampChoice.SelectedIndex = (int)previews.SheetTimestamp; SheetSkip.Value = previews.SheetSkipPercent;
  SheetBackground.Value = previews.SheetBackground; SheetForeground.Value = previews.SheetForeground;
  // A font adopted from the original that FlixyPix does not suggest stays selectable.
  if (!SheetFontChoice.Items.Contains(previews.SheetFont)) SheetFontChoice.Items.Add(previews.SheetFont);
  SheetFontChoice.SelectedItem = previews.SheetFont;
 }
 private void ResetPage()
 {
  if (navigation.SelectedItem is not ListBoxItem { Tag: string name }) return;
  var defaults = new CatalogPreferences();
  switch (name)
  {
   case "Appearance": themePicker.Value = ColorScheme.System; LanguageChoice.SelectedIndex = 0; break;
   case "General":
    // Image wheel, cross-fade and the days of "Zuletzt gesehen" live on other pages and keep their values.
    var (fade, days) = (Library.ImageFade.IsChecked, Library.RecentDays.Value);
    LoadGeneral(defaults.General with { ImageWheel = (ImageWheelAction)ImageWheel.Value }); DetailsTabs.IsChecked = false;
    Library.ImageFade.IsChecked = fade; Library.RecentDays.Value = days; break;
   case "Home page": LoadHome(defaults.Home); break;
   case "Privacy": ResetPrivacy(); break;
   case "Videos": LoadNfo(defaults.Nfo); videoTypes.Values = defaults.Previews.VideoExtensions; videoFavorite.Value = defaults.VideoFavoriteMinimumRating; audioFavorite.Value = defaults.AudioFavoriteMinimumRating; break;
   case "Video player": LoadPlayback(defaults.Playback); break;
   case "History": LoadHistory(defaults.History); break;
   case "Thumbnails": LoadPreviews(defaults.Previews); autoPreviews.IsChecked = true; break;
   case "Images": imageTypes.Values = defaults.Previews.ImageExtensions; imageFavorite.Value = defaults.ImageFavoriteMinimumRating; ImageWheel.Value = (int)defaults.General.ImageWheel; Library.ImageFade.IsChecked = defaults.General.ImageFade; LoadSlideshow(defaults.Slideshow); break;
  }
  Touch(); note.Text = T("Defaults for “{0}” applied · not saved yet", T(name));
 }

 private void Touch()
 {
  if (loading) return;
  dirty = true; error.Text = ""; note.Text = T("Unsaved changes");
 }
 private bool ConfirmDiscard() => !dirty || Result != null
  || Message.Show(this, T("The changes have not been saved yet. Discard them?"), "Einstellungen", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
 /// <summary>Closes without asking about unsaved changes (smoke tests: a failed check must not wait on a prompt).</summary>
 internal void CloseDiscarding() { dirty = false; Close(); }
 protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
 {
  if (!ConfirmDiscard()) e.Cancel = true;
  base.OnClosing(e);
  if (!e.Cancel && Result == null && Theme.Mode != themeBefore) Theme.Apply(themeBefore);
 }

 private bool Fail(FrameworkElement element, string message, Action? focus = null)
 {
  if (PageOf(element) is { } name) SelectPage(name);
  error.Text = "⚠ " + message; note.Text = "";
  Dispatcher.BeginInvoke(() => { element.BringIntoView(); if (focus != null) focus(); else element.Focus(); }, DispatcherPriority.Loaded);
  return false;
 }

 internal bool TrySave()
 {
  foreach (var (box, label) in numbers) if (!box.IsValid) return Fail(box, T("{0}: please enter a value of {1}.", label, box.Range), box.FocusInput);
  foreach (var (picker, label) in new[] { (ffmpeg, "FFmpeg"), (ffprobe, "ffprobe"), (mpv, "mpv") })
   if (!picker.IsValid) return Fail(picker, label + T(": please check the path."), picker.FocusInput);
  if (hotkeys.Values.FirstOrDefault(box => box.Conflict != null) is { } clash) return Fail(clash, T("A shortcut is assigned twice."));
  if (videoTypes.Values.Count == 0) return Fail(videoTypes, T("Please enter at least one video file type."));
  if (imageTypes.Values.Count == 0) return Fail(imageTypes, T("Please enter at least one image file type."));
  foreach (var (color, label) in new[] { (SheetBackground, T("Background")), (SheetForeground, T("Text color")) })
   if (!color.IsValid) return Fail(color, label + T(": please enter a color like #14171C."));
  if (Seek.FirstInvalid() is { } invalidSeek) return Fail(invalidSeek.Field, invalidSeek.Message);
  if (PlayerDisplay.FirstInvalid() is { } invalidDisplay) return Fail(invalidDisplay.Field, invalidDisplay.Message);
  if (Library.FirstInvalid() is { } invalidLibrary) return Fail(invalidLibrary.Field, invalidLibrary.Message);
  try
  {
   var preferences = repository.GetPreferences();
   var playback = PlayerDisplay.Apply(Seek.Apply(preferences.Playback)) with
   {
    AlwaysFullscreen = AlwaysFullscreen.IsChecked == true, PlayBestVersion = PlayBestVersion.IsChecked == true, HardwareDecoding = hardwareDecoding.IsChecked == true,
    SmallJumpForwardSeconds = smallForward.Value!.Value, SmallJumpBackwardSeconds = smallBackward.Value!.Value,
    LargeJumpForwardSeconds = largeForward.Value!.Value, LargeJumpBackwardSeconds = largeBackward.Value!.Value,
    Hotkeys = hotkeys.ToDictionary(pair => pair.Key, pair => pair.Value.Value, StringComparer.Ordinal),
    PreventSleep = PreventSleep.IsChecked == true, MediaKeys = MediaKeys.IsChecked == true,
    LeftClick = (PlayerMouseAction)Math.Max(0, MouseLeft.SelectedIndex), RightClick = (PlayerMouseAction)Math.Max(0, mouseRight.SelectedIndex), MiddleClick = (PlayerMouseAction)Math.Max(0, mouseMiddle.SelectedIndex),
    BackClick = (PlayerMouseAction)Math.Max(0, mouseBack.SelectedIndex), ForwardClick = (PlayerMouseAction)Math.Max(0, mouseForward.SelectedIndex), Wheel = (PlayerWheelAction)Math.Max(0, PlayerWheel.SelectedIndex),
   };
   var previews = PreviewsFrom(preferences.Previews) with
   {
    GeneratorProcesses = processes.Value,
    SkipWhenCustomThumbnail = skipCustom.IsChecked == true, FingerprintsOnImport = fingerprintsOnImport.IsChecked == true, VideoExtensions = videoTypes.Values, ImageExtensions = imageTypes.Values,
    HoverEnabled = hoverEnabled.IsChecked == true, HoverFrames = (int)Math.Round(hoverFrames.Value!.Value), HoverSpeed = (HoverSpeed)hoverSpeed.Value, HoverMode = (HoverMode)hoverMode.Value,
    ClipSegments = (int)Math.Round(clipSegments.Value!.Value), ClipSegmentMilliseconds = (int)Math.Round(clipSeconds.Value!.Value * 1000),
    ClipWidth = (int)Math.Round(clipWidth.Value!.Value / 2) * 2, ClipsOnImport = clipsOnImport.IsChecked == true, WallClips = (int)Math.Round(wallClips.Value!.Value),
    HoverDelay = hoverDelay.IsChecked == true, HoverTimestamp = hoverTimestamp.IsChecked == true,
   };
   var slideshow = preferences.Slideshow with
   {
    IntervalSeconds = SlideInterval.Value!.Value, Order = (SlideshowOrder)SlideOrder.Value, Speed = (HoverSpeed)slideSpeed.Value,
    Fade = SlideFade.IsChecked == true, PanAndZoom = SlidePan.IsChecked == true, ShowInfo = SlideInfo.IsChecked == true,
   };
   var history = new HistoryPreferences { Enabled = HistoryEnabled.IsChecked == true, ShowOCounter = ShowOCounter.IsChecked == true, WatchedShare = WatchedShare.Value!.Value / 100 };
   CatalogRepository.ValidatePlayback(playback); CatalogRepository.ValidatePreviews(previews); CatalogRepository.ValidateSlideshow(slideshow); CatalogRepository.ValidateHistory(history);
   var choice = new DesktopChoice(displayedDirectory, NullIfEmpty(ffmpeg.Text), NullIfEmpty(ffprobe.Text), NullIfEmpty(mpv.Text),
    autoPreviews.IsChecked == true, Theme.ModeName(themePicker.Value), RegenerateAfterSave.IsChecked == true, BossKey.Value,
    (string)((ComboBoxItem)LanguageChoice.SelectedItem).Tag, programs.ToList(), quickFolders.ToList(), ChosenProviders(), MinimizeToTray.IsChecked == true, DetailsTabs.IsChecked == true, ChosenPrivacy());
   repository.SavePreferences(preferences with
   {
    Playback = playback, Previews = previews, Slideshow = slideshow, History = history,
    // Rows keep their stored order; newly switched-on rows join in the default order.
    Home = new HomePreferences
    {
     StartOnHome = StartOnHome.IsChecked == true, RecentDays = (int)Math.Round(Library.RecentDays.Value!.Value),
     Rows = [.. preferences.Home.Rows.Concat(HomePreferences.DefaultRows).Distinct().Where(kind => HomeRows[kind].IsChecked == true)],
    },
    Nfo = new NfoPreferences
    {
     ImportTitle = NfoTitle.IsChecked == true, ImportTags = NfoTags.IsChecked == true, ImportDescription = NfoDescription.IsChecked == true, SearchSubfolders = NfoSubfolders.IsChecked == true,
     ImportThumbnail = NfoThumbnail.IsChecked == true, ImportFanart = NfoFanart.IsChecked == true,
     TagElements = string.Join(";", NfoPreferences.Split(nfoTagElements.Text)), DescriptionElements = string.Join(";", NfoPreferences.Split(nfoDescriptionElements.Text)),
    },
    General = Library.Apply(preferences.General) with
    {
     IncludeHiddenFolders = IncludeHidden.IsChecked == true, ConfirmRemove = ConfirmRemove.IsChecked == true, ShowExtension = ShowExtension.IsChecked == true,
     ShowFolderCounts = FolderCounts.IsChecked == true, ShowCategoryCounts = CategoryCounts.IsChecked == true, ShowTagCategoryCounts = TagCategoryCounts.IsChecked == true,
     MarkMissingFiles = MarkMissing.IsChecked == true, AlwaysShowImportReport = AlwaysReport.IsChecked == true, RestoreLastView = RestoreChoice.Value > 0, RestoreScope = RestoreChoice.Value == 1 ? RestoreScope.View : RestoreScope.All,
     ImageWheel = (ImageWheelAction)ImageWheel.Value,
    },
    VideoFavoriteMinimumRating = videoFavorite.Value, ImageFavoriteMinimumRating = imageFavorite.Value, AudioFavoriteMinimumRating = audioFavorite.Value,
   });
   Result = choice; dirty = false; return true;
  }
  catch (ArgumentException failure) { error.Text = "⚠ " + failure.Message; note.Text = ""; return false; }
 }
}
