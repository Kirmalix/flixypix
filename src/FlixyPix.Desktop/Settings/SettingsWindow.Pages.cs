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

/// <summary>The rubrics of the settings window, in the order of the navigation.</summary>
internal sealed partial class SettingsWindow
{
 private void AddPages(Action manageEncryption, Action takeOverOriginal, PrivacySettings privacy)
 {
  AddAppearancePage(); AddGeneralPage(); AddHomePage(); AddVideosPage(); AddPlayerPage(); AddHistoryPage(); AddPreviewsPage(); AddImagesPage(); AddCachePage(); AddPrivacyPage(privacy); AddSecurityPage(manageEncryption); AddDatabasePage(takeOverOriginal); AddToolsPage();
 }

 private void AddAppearancePage()
 {
  AddPage("Appearance",
   SettingsUi.Card(T("Color scheme"), T("The choice is previewed immediately and kept after saving. “Follow Windows” follows the Windows app theme and changes automatically with it."),
    new SettingRow(T("Light or dark"), null, themePicker, wide: true)),
   SettingsUi.Card(T("Language"), T("Follow Windows uses the Windows display language, or English if that language is not supported. Numbers and dates always follow the Windows regional format."),
    new SettingRow(T("Interface language"), T("Applies on this computer after FlixyPix restarts."), LanguageChoice)));
 }

 private void AddGeneralPage()
 {
  var libraryCards = Library.GeneralCards();
  AddPage("General",
   SettingsUi.Card(T("Import"), null,
    new SettingRow(T("Include hidden folders"), T("When off, hidden subfolders are skipped in folder imports. Folders chosen explicitly are always imported."), IncludeHidden),
    new SettingRow(T("Always show the import report"), T("Otherwise the report only opens on errors or notes."), AlwaysReport)),
   SettingsUi.Card(T("Display"), null,
    new SettingRow(T("Show file extension"), T("Appends the extension like “.mp4” to the titles in tiles and list."), ShowExtension),
    new SettingRow(T("Mark missing files"), T("Tiles of unavailable files are dimmed and marked “Missing”."), MarkMissing),
    new SettingRow(T("Item counts by folder"), T("Shows the number of items in each folder, including its subfolders, in the sidebar."), FolderCounts),
    new SettingRow(T("Item counts by category"), T("Shows the number of items in each category in the category selector."), CategoryCounts),
    new SettingRow(T("Item counts by tag category"), T("Shows the number of items with a tag from each category in the sidebar."), TagCategoryCounts),
    new SettingRow(T("Restore on start"),
     T("View only: media type, collection or saved search, sorting and display. Everything: also search text, filters, position and selection plus a continue watching notice. Window size and position follow “Remember window position” under Navigation."),
     RestoreChoice)),
   SettingsUi.Card(T("Sidebars"), T("Both sidebars fold to a narrow bar of symbols: Ctrl+B on the left, Ctrl+D on the right, or the arrow at their edge. Drag their inner edge to change the width."),
    new SettingRow(T("Details as tabs"), T("Shows details, tags and scenes on the right as tabs instead of one below the other. The button at the top of the details switches as well. Applies to this computer."), DetailsTabs)),
   SettingsUi.Card(T("Confirmations"), null,
    new SettingRow(T("Confirm removing from the catalog"), T("Permanently deleting a file always requires confirmation."), ConfirmRemove)),
   libraryCards[0], libraryCards[1], libraryCards[2], libraryCards[3], QuickAccessCard());
 }

 private void AddHomePage()
 {
  AddPage("Home page",
   SettingsUi.Card(T("On start"), null,
    new SettingRow(T("Recently watched"), T("How many days the row “Recently watched” goes back."), Library.RecentDays),
    new SettingRow(T("Start with the home page"), T("FlixyPix opens on the home page. A restored view is then one step back (Alt+←). Ctrl+Home opens the home page at any time."), StartOnHome)),
   SettingsUi.Card(Tc("home page", "Rows"), T("Empty rows are not shown. FlixyPix computes suggestions locally from the tags of what you watched recently."),
    HomePreferences.DefaultRows.Select(kind => (UIElement)new SettingRow(HomePreferences.Label(kind), HomeRowHint(kind), HomeRows[kind])).ToArray()));
 }

 private void AddVideosPage()
 {
  AddPage("Videos",
   SettingsUi.Card(T("File types"), T("Folder imports include only files with these extensions. Individually selected files can always be imported."),
    new SettingRow(T("Video files"), T("Type an extension and confirm with Enter; separate several with semicolons."), videoTypes, wide: true)),
   SettingsUi.Card(T("Favorites"), T("Items count as favorites from this rating on."),
    new SettingRow(T("Videos"), null, videoFavorite), new SettingRow(T("Audio"), null, audioFavorite)),
   SettingsUi.Card(T("NFO files"), T("Kodi-style .nfo files next to videos (Organize ▾ → Read NFO files). Data is imported only after you confirm the preview."),
    new SettingRow(T("Import title"), null, NfoTitle), new SettingRow(T("Import tags"), T("Separate elements with semicolons; each becomes a tag category, for example genre → Genre."), NfoTags),
    new SettingRow(T("Elements as tags"), null, nfoTagElements, wide: true),
    new SettingRow(T("Import description"), T("“plot” becomes the text below the details."), NfoDescription), new SettingRow(T("Elements of the description"), null, nfoDescriptionElements, wide: true),
    new SettingRow(T("Search subfolders"), T("Also finds the NFO file in subfolders of the video folder."), NfoSubfolders),
    new SettingRow(T("Import thumbnail"), T("A local image referenced by <thumb> becomes the video’s thumbnail. Images from web addresses are not loaded."), NfoThumbnail),
    new SettingRow(T("Import fanart"), T("Local images referenced by <fanart> are added to the video’s artwork."), NfoFanart)));
 }

 private void AddPlayerPage()
 {
  var seekCards = Seek.Cards(); var displayCards = PlayerDisplay.Cards();
  AddPage("Video player",
   SettingsUi.Card(T("Playback"), null,
    new SettingRow(T("Always start in full screen"), T("Videos open directly in full screen. Esc or F11 switch back."), AlwaysFullscreen),
    new SettingRow(T("Play the best version"), T("An item with several files plays the one with the most lines, then the highest bit rate. Off, it plays the main file. Another version can always be chosen in the menu of the item."), PlayBestVersion),
    new SettingRow(T("Hardware decoding"), T("Reduces CPU load for high-resolution videos (FFmpeg option “{0}”).", "-hwaccel auto"), hardwareDecoding)),
   SettingsUi.Card(T("Jump distances"), T("How far the jump shortcuts jump. Default: 10 and 30 seconds."),
    new SettingRow(T("Small jump"), null, JumpPair(smallBackward, smallForward)), new SettingRow(T("Large jump"), null, JumpPair(largeBackward, largeForward))),
   SettingsUi.Card(T("Behavior"), null,
    new SettingRow(T("Suppress the screen saver"), T("While videos and slideshows play, the screen and computer don't go to sleep."), PreventSleep),
    new SettingRow(T("Use media keys"), T("Play/pause, previous and next jump mark, and stop via keyboard or headset."), MediaKeys)),
   SettingsUi.Card(T("Mouse in the video"), T("What mouse buttons and wheel do over the image. A double-click always switches to full screen."),
    new SettingRow(T("Left click"), null, MouseLeft), new SettingRow(T("Right click"), null, mouseRight), new SettingRow(T("Middle button"), null, mouseMiddle),
    new SettingRow(T("Side button “Back”"), null, mouseBack), new SettingRow(T("Side button “Forward”"), null, mouseForward),
    new SettingRow(T("Mouse wheel"), T("“Seek” jumps by the step under “Seek with the mouse wheel”."), PlayerWheel)),
   seekCards[0], seekCards[1], seekCards[2],
   displayCards[0], displayCards[1],
   HotkeyCards());
 }

 private void AddHistoryPage()
 {
  var clear = SettingsUi.SmallButton(T("Delete history …"), T("Deletes all entries; play counts, O counters and the last playback stay."));
  clear.Click += (_, _) =>
  {
   if (Message.Show(this, T("Delete the entire playback history of this library? Play counts, O counters and the last playback stay."), T("Delete history"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
   var removed = repository.ClearHistory(); UpdateHistorySummary(); note.Text = Tn(removed, "1 history entry deleted", "{0:N0} history entries deleted");
  };
  UpdateHistorySummary();
  AddPage("History",
   SettingsUi.Card(T("Playback history"), T("For each playback FlixyPix notes when it began, how long you watched and which share of the video you saw. Nothing leaves the computer."),
    new SettingRow(T("Keep a playback history"), T("Off: new playbacks and O presses are no longer noted. Existing entries stay until you delete them."), HistoryEnabled),
    new SettingRow(T("Counts as watched from"), T("Share of a video that must have been seen for the playback to count as watched (filter “watched”, statistics). Plays before the history count as watched."), WatchedShare),
    new SettingRow(T("Stored entries"), null, SettingsUi.Buttons(historySummary, clear))),
   SettingsUi.Card(T("O counter"), T("Counts climaxes per item: button in the player and in the details, shortcuts below “Video player”. It can be sorted and searched."),
    new SettingRow(T("Show the O counter"), T("Hidden, the counter keeps its values and its row on the home page disappears."), ShowOCounter)));
 }

 private void UpdateHistorySummary() => historySummary.Text = Tn((int)Math.Min(int.MaxValue, repository.CountHistory()), "1 entry", "{0:N0} entries");

 private void AddPreviewsPage()
 {
  var presets = new WrapPanel();
  foreach (var (label, c, r, w) in new[] { (T("Single frame"), 1, 1, 480), (T("2 × 2"), 2, 2, 1280), ("3 × 3", 3, 3, 1280), ("4 × 4", 4, 4, 1920) })
  {
   var preset = SettingsUi.SmallButton(label); preset.Margin = new Thickness(0, 0, 6, 0);
   preset.Click += (_, _) => { columns.Value = c; rows.Value = r; SheetWidth.Value = w; UpdateSheetPreview(); };
   presets.Children.Add(preset);
  }
  sheetFrame.Child = sheetCells;
  var sheetPreview = new Grid { Margin = new Thickness(16, 14, 16, 14) };
  sheetPreview.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) }); sheetPreview.ColumnDefinitions.Add(new ColumnDefinition());
  Grid.SetColumn(sheetResult, 1); sheetPreview.Children.Add(sheetFrame); sheetPreview.Children.Add(sheetResult);
  AddPage("Thumbnails",
   SettingsUi.Card(T("Video thumbnail"), T("Several scenes as a grid, or a single image. Applies to newly created thumbnails."),
    new SettingRow(T("Template"), null, presets), sheetPreview,
    new SettingRow(T("Columns"), null, columns), new SettingRow(T("Rows"), null, rows),
    new SettingRow(T("Width"), T("Width of the whole thumbnail; the height follows from the aspect ratio of the video."), SheetWidth)),
   SettingsUi.Card(T("Design"), T("With a border, header and time stamps. Everything is created in a single FFmpeg call per video."),
    new SettingRow(T("Create quickly"), T("Takes the nearest keyframe per scene instead of the exact second. Several times faster; the time stamp shows the actual position."), SheetFast),
    new SettingRow(T("Border"), T("Spacing around and between the scenes."), SheetBorder),
    new SettingRow(T("Header"), T("File name, resolution, duration and size above the scenes."), SheetHeader),
    new SettingRow(T("Time stamp"), T("Position of each scene in the video."), SheetTimestampChoice),
    new SettingRow(T("Font"), T("For the header and time stamps."), SheetFontChoice),
    new SettingRow(T("Background"), T("Color of the border and header."), SheetBackground),
    new SettingRow(T("Text color"), T("Color of the header."), SheetForeground),
    new SettingRow(T("Skip start and end"), T("That way opening and closing credits don't fill the grid."), SheetSkip),
    SamplePanel()),
   SettingsUi.Card(T("Generation"), null,
    new SettingRow(T("Simultaneous preview processes"), Tn(Environment.ProcessorCount,
     "More processes are faster but increase CPU and disk load. This computer has 1 logical core.",
     "More processes are faster but increase CPU and disk load. This computer has {0} logical cores."), processes),
    new SettingRow(T("Keep custom thumbnail"), T("Don't create a new thumbnail if one was chosen manually."), skipCustom),
    new SettingRow(T("Create on import"), T("Create thumbnails and media information right after the import."), autoPreviews),
    new SettingRow(T("Compute fingerprints on import"), T("Compute the fingerprints for “Find similar items” right after the import, so the search starts at once. Takes about a second per video."), fingerprintsOnImport),
    new SettingRow(T("Adjust existing"), T("After saving, all video thumbnails that still look different are recreated in the background. A cancelled run continues next time."), RegenerateAfterSave)),
   SettingsUi.Card(T("Hover preview"), T("When the mouse moves over a video, frames from the video change on the tile. They are created the first time and cached."),
    new SettingRow(T("Show preview"), null, hoverEnabled),
    new SettingRow(T("Show"), T("Changing images or the preview clip. A missing clip is made the first time."), hoverMode),
    new SettingRow(T("Number of images"), T("1 to 20, spread evenly across the video."), hoverFrames),
    new SettingRow(T("Speed"), T("How fast the images change."), hoverSpeed),
    new SettingRow(T("Start with a delay"), T("Briefly hovering over a tile does not start a preview yet."), hoverDelay),
    new SettingRow(T("Show time stamp"), T("Position of the frame in the video, bottom right on the tile."), hoverTimestamp)),
   SettingsUi.Card(T("Preview clips"), T("Short silent clips from parts of a video for the hover preview. They are made once with FFmpeg and kept in the library."),
    new SettingRow(T("Parts"), T("Spread evenly across the video."), clipSegments),
    new SettingRow(T("Length of each part"), null, clipSeconds),
    new SettingRow(T("Width"), T("The height follows from the aspect ratio of the video."), clipWidth),
    new SettingRow(T("Create on import"), T("Create the preview clips right after the import."), clipsOnImport),
    new SettingRow(T("Clips at once on the video wall"), T("In the video wall view the clips of the first videos in view play by themselves. Fewer clips put less load on the computer."), wallClips)));
 }

 private void AddImagesPage()
 {
  AddPage("Images",
   SettingsUi.Card(T("File types"), T("Folder imports include only files with these extensions."),
    new SettingRow(T("Image files"), T("Type an extension and confirm with Enter; separate several with semicolons."), imageTypes, wide: true)),
   SettingsUi.Card(T("Favorites"), T("Images count as favorites from this rating on."), new SettingRow(T("Images"), null, imageFavorite)),
   SettingsUi.Card(T("Image viewer"), null,
    new SettingRow(T("Mouse wheel"), T("What the mouse wheel does without Ctrl; with Ctrl it always zooms."), ImageWheel),
    new SettingRow(T("Cross-fade between images"), T("The next image fades in smoothly over the previous one."), Library.ImageFade)),
   SettingsUi.Card(T("Slideshow"), T("In the image viewer with the slideshow button or the S key."),
    new SettingRow(T("Display time per image"), T("0.25 to 30 seconds."), SlideInterval),
    new SettingRow(T("Order"), T("“Less random” jumps to a random position and shows a few images in order from there."), SlideOrder),
    new SettingRow(T("Cross-fade"), T("Smooth transition to the next image."), SlideFade),
    new SettingRow(T("Pan and zoom"), T("Slow movement across each image."), SlidePan),
    new SettingRow(T("Speed of the transitions"), T("Duration of cross-fades and pans."), slideSpeed),
    new SettingRow(T("Show image details"), T("File name, size and date above the image; toggle with I in the viewer."), SlideInfo)));
 }

 private void AddCachePage()
 {
  var cacheRows = new List<UIElement>();
  foreach (var (folder, label, hint, clearable) in Caches)
  {
   var size = new TextBlock { Width = 90, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Text = "…" };
   var bar = new Border { Background = SettingsUi.Accent, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
   var track = new Border { Width = 140, Height = 6, CornerRadius = new CornerRadius(3), Background = SettingsUi.FieldBorder, Margin = new Thickness(14, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, Child = bar };
   var clear = SettingsUi.SmallButton(T("Clear")); clear.Margin = new Thickness(14, 0, 0, 0); clear.Click += (_, _) => ClearCache(folder, label);
   if (!clearable) clear.Visibility = Visibility.Hidden;
   var controls = SettingsUi.Buttons(track, size, clear);
   cacheViews[folder] = (size, bar); cacheRows.Add(new SettingRow(label, hint, controls));
  }
  var openLibrary = SettingsUi.SmallButton(T("Open folder")); openLibrary.Click += (_, _) => OpenFolder(libraryDirectory);
  var recount = SettingsUi.SmallButton(T("Recalculate")); recount.Click += async (_, _) => await UpdateCacheSummaryAsync();
  var totals = new DockPanel { Margin = new Thickness(16, 10, 16, 10) }; var totalButtons = SettingsUi.Buttons(recount, openLibrary);
  DockPanel.SetDock(totalButtons, Dock.Right); totals.Children.Add(totalButtons); totals.Children.Add(cacheTotal); cacheRows.Add(totals);
  AddPage("Cache", SettingsUi.Card(T("Caches of this library"), T("Clearing frees space without losing catalog data."), [.. cacheRows]));
 }

 private void AddSecurityPage(Action manageEncryption)
 {
  var manage = SettingsUi.SmallButton(T("Manage encryption …")); manage.Click += (_, _) => manageEncryption();
  var status = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 14, 16, 14), Children = { securityIcon, securityStatus } };
  securityStatus.Margin = new Thickness(12, 0, 0, 0);
  AddPage("Security",
   SettingsUi.Card(T("Protect library"), T("In the original, a fixed program password only locked the interface. FlixyPix instead encrypts the catalog, backups and exports with your own password."),
    status, new SettingRow(T("Encryption"), T("Turn on, change the password or create a new recovery key. The password is asked for on every start and never stored."), manage)));
  Activated += (_, _) => UpdateSecurity(); UpdateSecurity();
 }

 private void AddDatabasePage(Action takeOverOriginal)
 {
  var openCurrent = SettingsUi.SmallButton(T("Open in Explorer")); openCurrent.Click += (_, _) => OpenFolder(libraryDirectory);
  var takeOver = SettingsUi.SmallButton(T("Import library …")); takeOver.Click += (_, _) => { if (!ConfirmDiscard()) return; dirty = false; Close(); takeOverOriginal(); };
  var manage = SettingsUi.SmallButton(T("Database management …")); manage.IsEnabled = manageLibraries != null;
  manage.Click += (_, _) => { if (!ConfirmDiscard()) return; dirty = false; Close(); manageLibraries?.Invoke(); };
  AddPage("Database",
   SettingsUi.Card(T("Libraries"), T("A library keeps its catalog, thumbnails and caches in its folder. You can create several libraries and switch between them; one is always open."),
    new SettingRow(T("Open library"), libraryDirectory, openCurrent),
    new SettingRow(T("Database management"), T("Libraries, checking and repair, backup, import and export, security and legacy data in one place; also from the Catalog menu."), manage)),
   SettingsUi.Card(Tc("settings card", "Import from EazyFlixPix 4"), null,
    new SettingRow(T("Import original database"), T("Creates a new, separate library from an original database (.sdf). The original file stays unchanged."), takeOver)));
 }

 private void AddToolsPage()
 {
  AddPage("Tools",
   SettingsUi.Card(T("Media tools"), T("Leave empty to use the bundled versions. Custom versions take effect immediately after saving."),
    new SettingRow("FFmpeg", T("Creates thumbnails and converts media."), ffmpeg, wide: true),
    new SettingRow("ffprobe", T("Reads duration, resolution and codecs."), ffprobe, wide: true),
    new SettingRow("mpv", T("Plays videos and audio in the built-in player."), mpv, wide: true)),
   ProgramsCard(), WebSearchCard());
 }

 private static string HomeRowHint(HomeRowKind kind) => kind switch
 {
  HomeRowKind.ContinueWatching => T("Videos you started, most recently watched first."),
  HomeRowKind.ForYou => T("Unwatched items with tags from videos you watched recently; if there is no viewing history, highly rated items."),
  HomeRowKind.RecentlyAdded => T("The latest imports."),
  HomeRowKind.WatchLater => T("Your watch list."),
  HomeRowKind.FavoritePerformers => T("Performers with the most items, favorites first."),
  HomeRowKind.Popular => T("Well rated and watched often."),
  HomeRowKind.MoreWithTag => T("Up to three rows for tags you have watched frequently recently."),
  HomeRowKind.Studios => T("Studios with the most items."),
  HomeRowKind.Rediscover => T("Rated very well, not watched for three months."),
  HomeRowKind.RecentlyWatched => T("Everything you watched in the set number of days, newest first."),
  HomeRowKind.MostO => T("The items with the highest O counter. Hidden when the O counter is hidden."),
  _ => T("A new random selection every day."),
 };
}
