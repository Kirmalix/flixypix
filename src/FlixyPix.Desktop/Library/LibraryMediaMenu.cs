using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>Where the media menu opens. Outside the library (start page, scene wall) entries that work on the library
/// selection first show the medium there.</summary>
internal enum MediaMenuPlace { Library, Home, Wall }

/// <summary>The media menu (UX plan U5.2): one definition for the right click on tiles, the "⋯" button in the details, the
/// context menu key and Shift+F10, the start page and the scene wall. With several media selected it says how many and
/// keeps only what applies to all of them.</summary>
public partial class MainWindow
{
 /// <param name="lead">Entries of the place that come first, such as "Play from here" on the scene wall.</param>
 internal IReadOnlyList<MenuSpec> MediaMenu(IReadOnlyList<MediaItem> items, MediaMenuPlace place = MediaMenuPlace.Library, IEnumerable<MenuSpec>? lead = null)
 {
  if (items.Count == 0) return [];
  var first = items[0]; var single = items.Count == 1;
  var outside = place != MediaMenuPlace.Library;
  // Entries working on the library selection: outside the library they show the medium there first.
  Action Here(Action action) => outside ? () => ShowInLibrary(first, action) : action;
  var entries = new List<MenuSpec>();
  if (!single) entries.Add(MenuSpec.Heading(Tn(items.Count, "1 item", "{0:N0} items")));
  entries.AddRange(lead ?? []);
  entries.Add(MenuSpec.Command(T("Open"), () => OpenFromMenu(first), "", single ? KeyLabel("Enter") : null));
  entries.Add(MenuSpec.Submenu(T("Open with"), OpenWithEntries(items), ""));
  if (outside) entries.Add(MenuSpec.Command(T("Show in library"), () => ShowInLibrary(first), ""));
  entries.Add(MenuSpec.Separator);
  entries.Add(MenuSpec.Submenu(T("Rate"), RatingEntries(items, keys: !outside), ""));
  entries.Add(new MenuCommand(T("Favorite"), () => PinFavorite(items)) { Icon = "", Shortcut = outside ? null : "F", Checked = items.All(i => i.IsFavorite) });
  entries.Add(new MenuCommand(T("Watch later"), () => MarkWatchLater(items)) { Icon = "", Checked = items.All(i => i.WatchLater) });
  entries.Add(MenuSpec.Command(T("Edit tags …"), Here(FocusTagInput), "", KeyLabel("Ctrl+T")));
  entries.Add(MenuSpec.Separator);
  entries.Add(new MenuCommand(T("In the target collection"), () => ToggleTargetCollection()) { Icon = "\uE710", Shortcut = outside ? null : "B", Checked = items.All(i => MediaCard.TargetMembers.Contains(i.Id)), Enabled = !outside });
  entries.Add(MenuSpec.Submenu(T("Add to collection"), CollectionChoices(items), ""));
  entries.Add(MenuSpec.Separator);
  entries.Add(MenuSpec.Submenu(T("Preview"), PreviewEntries(items, Here), ""));
  entries.Add(MenuSpec.Command(T("Media information"), Here(ProbeSelected), "", enabled: single));
  entries.Add(MenuSpec.Submenu(T("Metadata"), MetadataEntries(single || outside ? 1 : items.Count, Here), ""));
  entries.Add(MenuSpec.Submenu(single && first.Files.Count > 0 ? Tn(first.Files.Count + 1, "1 file", "{0} files") : T("Versions"), VersionEntries(items, outside), ""));
  entries.Add(MenuSpec.Command(T("Find similar"), () => ShowSimilarMedia(first), "", enabled: single && first.Kind != MediaKind.Audio));
  entries.Add(MenuSpec.Command(T("More from the same folder"), Here(ShowSameFolder), "", enabled: single));
  entries.Add(MenuSpec.Separator);
  entries.Add(MenuSpec.Submenu(T("File"), FileEntries(items, Here), ""));
  entries.Add(MenuSpec.Submenu(T("Process media"), ProcessEntries(items, Here), ""));
  entries.Add(MenuSpec.Submenu(T("Export"), ExportEntries(items, Here), ""));
  entries.Add(MenuSpec.Separator);
  entries.Add(MenuSpec.Command(_playlist != null && !outside ? T("Remove from collection") : T("Remove from catalog …"),
   outside ? () => RemoveMedia(items, fromCollection: false) : () => RemoveMedia(items), "", outside ? null : KeyLabel("Delete")));
  entries.Add(MenuSpec.Command(T("Delete file …"), () => DeleteMedia(items), ""));
  return entries;
 }

 /// <summary>Opens the media menu of the selection at the mouse pointer, or below <paramref name="target"/>.</summary>
 private ContextMenu? OpenMediaMenu(UIElement? target = null, PlacementMode placement = PlacementMode.Bottom)
 {
  var items = SelectedMedia;
  return items.Count == 0 ? null : Menus.Open(target, MediaMenu(items), MenuFailed, placement);
 }
 /// <summary>The media menu for one medium outside the library (start page, scene wall).</summary>
 internal ContextMenu OpenMediaMenu(MediaItem item, MediaMenuPlace place, IEnumerable<MenuSpec>? lead = null) =>
  Menus.Open(null, MediaMenu([_repository.GetMedia(item.Id) ?? item], place, lead), MenuFailed, PlacementMode.MousePoint);
 private void MenuFailed(string label, Exception error) => Message.Show(this, error.Message, label, MessageBoxButton.OK, MessageBoxImage.Warning);

 /// <summary>A key name in the current language, for the right column of the menu.</summary>
 private static string KeyLabel(string hotkey) => string.Join("+", hotkey.Split('+').Select(SettingsUi.KeyName));

 private async void OpenFromMenu(MediaItem item) { StopSlideshow(); await Play(item); }

 /// <summary>Shows the medium in the whole library and selects it; then runs <paramref name="then"/> on that selection.</summary>
 internal async void ShowInLibrary(MediaItem item, Action? then = null)
 {
  ApplyViewState(new ViewState { View = "all" }); Refresh(item.Id); Activate();
  await _lastRefresh;
  if (Selected?.Id == item.Id) then?.Invoke();
 }

 private void FocusTagInput() { RevealTagInput(); Dispatcher.BeginInvoke(() => { if (TagInput.IsEnabled) TagInput.Focus(); }, DispatcherPriority.Loaded); }

 private IEnumerable<MenuSpec> OpenWithEntries(IReadOnlyList<MediaItem> items)
 {
  var item = items[0];
  foreach (var program in _settings.ExternalPrograms.Where(p => items.Any(i => ExternalPrograms.Applies(p, i.Kind))))
   yield return MenuSpec.Command(program.Name, () => OpenWith(program, items), shortcut: program.Hotkey.Length > 0 ? KeyLabel(program.Hotkey) : null);
  yield return MenuSpec.Separator;
  yield return MenuSpec.Command(T("Windows default program"), () => OpenWithDefault(item), enabled: items.Count == 1);
  yield return MenuSpec.Command(T("Choose another app …"), () => OpenWithChooser(item), enabled: items.Count == 1);
  yield return MenuSpec.Separator;
  yield return MenuSpec.Command(T("Manage programs …"), () => OpenSettings("Tools"));
 }

 /// <param name="keys">Shows the digit keys, which work in the library list only.</param>
 private IEnumerable<MenuSpec> RatingEntries(IReadOnlyList<MediaItem> items, bool keys)
 {
  for (var stars = 1; stars <= 5; stars++)
  {
   var value = stars;
   yield return new MenuCommand(new string('★', stars) + new string('☆', 5 - stars), () => RateMedia(items, value)) { Shortcut = keys ? value.ToString() : null, Checked = items.All(i => i.Rating == value * 2) };
  }
  yield return MenuSpec.Separator;
  yield return MenuSpec.Command(T("Remove rating"), () => RateMedia(items, 0), shortcut: keys ? "0" : null, enabled: items.Any(i => i.Rating > 0));
 }

 private IEnumerable<MenuSpec> CollectionChoices(IReadOnlyList<MediaItem> items)
 {
  var imagesOnly = items.All(m => m.Kind == MediaKind.Image);
  foreach (var playlist in _repository.GetPlaylists().OrderBy(p => p.Kind != PlaylistKind.Quick).ThenBy(CollectionName, StringComparer.CurrentCultureIgnoreCase))
   // Slideshows hold images only; for other media they stay visible but cannot be chosen.
   yield return MenuSpec.Command(CollectionName(playlist), () => AddToCollection(playlist, items, null), playlist.Kind == PlaylistKind.Slideshow ? "" : null,
    enabled: playlist.Kind != PlaylistKind.Slideshow || imagesOnly);
  yield return MenuSpec.Separator;
  yield return MenuSpec.Command(T("New collection from the selection …"), () => CreatePlaylist(PlaylistKind.Playlist, null, items));
  if (imagesOnly) yield return MenuSpec.Command(T("New slideshow …"), () => { if (NewCollectionFor(PlaylistKind.Slideshow) is { } p) AddToCollection(p, items, null); });
 }

 private IEnumerable<MenuSpec> PreviewEntries(IReadOnlyList<MediaItem> items, Func<Action, Action> here)
 {
  var single = items.Count == 1; var anyVideo = items.Any(i => i.Kind == MediaKind.Video);
  yield return MenuSpec.Command(T("Recreate thumbnail"), here(RegenerateSelectedThumbnails), enabled: anyVideo);
  yield return MenuSpec.Command(T("Create preview clip"), here(CreateSelectedPreviewClips), enabled: anyVideo);
  yield return MenuSpec.Command(T("Create scene images …"), here(GenerateScenePreviews), enabled: single && items[0].Kind == MediaKind.Video);
  yield return MenuSpec.Command(T("Media artwork / scene images"), here(ShowArtwork), enabled: single);
  yield return MenuSpec.Separator;
  yield return MenuSpec.Command(T("Choose custom thumbnail …"), here(SelectCustomThumbnail), enabled: single);
  yield return MenuSpec.Command(T("Copy thumbnail"), here(() => CopyThumbnail()), enabled: single && items[0].ThumbnailPath != null);
  yield return MenuSpec.Command(Tn(items.Count, "Paste thumbnail (1 item)", "Paste thumbnail ({0:N0} items)"), here(async () => await PasteThumbnailAsync()));
  yield return MenuSpec.Submenu(T("Thumbnails beside the videos"), [
   MenuSpec.Command(Tn(items.Count, "Selection (1 item)", "Selection ({0:N0} items)"), here(async () => await ExportThumbnailsAsync(selectionOnly: true)), enabled: anyVideo),
   MenuSpec.Command(Tn(ViewTotal(), "Whole view (1 item)", "Whole view ({0:N0} items)"), here(async () => await ExportThumbnailsAsync(selectionOnly: false)))]);
 }

 private IEnumerable<MenuSpec> MetadataEntries(int count, Func<Action, Action> here)
 {
  yield return MenuSpec.Command(T("Read metadata from files …"), here(ImportFileMetadata));
  yield return MenuSpec.Command(T("Import NFO metadata …"), here(ImportNfo));
  yield return MenuSpec.Command(Tn(count, "Title from file name (1 item) …", "Titles from file names ({0:N0} items) …"), here(TitlesFromFileNames));
 }

 private IEnumerable<MenuSpec> FileEntries(IReadOnlyList<MediaItem> items, Func<Action, Action> here)
 {
  var single = items.Count == 1; var item = items[0];
  yield return MenuSpec.Command(T("Show in Explorer"), () => Reveal(item), "", enabled: single);
  yield return MenuSpec.Command(T("Copy path"), () => { Clipboard.SetText(string.Join(Environment.NewLine, items.Select(i => i.Path))); Status.Text = T("Path copied"); }, "");
  yield return MenuSpec.Separator;
  yield return MenuSpec.Submenu(T("Copy to"), QuickAccessMenuEntries(BatchFileAction.Copy, here));
  yield return MenuSpec.Submenu(T("Move to"), QuickAccessMenuEntries(BatchFileAction.Move, here));
  yield return MenuSpec.Command(single ? T("Rename file …") : T("Rename selected files …"), single ? here(RenameFile) : () => PrepareBatchFiles(BatchFileAction.Rename));
  yield return MenuSpec.Command(T("Reassign missing file …"), here(RelinkFile), enabled: single);
 }
 private IEnumerable<MenuSpec> QuickAccessMenuEntries(BatchFileAction action, Func<Action, Action> here)
 {
  var entries = QuickAccessEntries(action);
  for (var i = 0; i < entries.Length; i++)
  {
   // The last two entries (another folder, managing the list) come after a line.
   if (i == entries.Length - 2) yield return MenuSpec.Separator;
   yield return MenuSpec.Command(entries[i].Label, here(entries[i].Action), i < entries.Length - 2 ? "" : null);
  }
 }

 private IEnumerable<MenuSpec> ProcessEntries(IReadOnlyList<MediaItem> items, Func<Action, Action> here)
 {
  var playable = items.Count == 1 && items[0].Kind != MediaKind.Image;
  yield return MenuSpec.Command(T("Convert to MP4 …"), here(() => ProcessMedia("convert")), enabled: playable);
  yield return MenuSpec.Command(T("Export clip …"), here(() => ProcessMedia("clip")), enabled: playable);
  yield return MenuSpec.Command(T("Create animated GIF …"), here(() => ProcessMedia("gif")), enabled: playable && items[0].Kind == MediaKind.Video);
  yield return MenuSpec.Separator;
  yield return MenuSpec.Command(T("Jobs / trim / join …"), OpenQueue);
 }

 private IEnumerable<MenuSpec> ExportEntries(IReadOnlyList<MediaItem> items, Func<Action, Action> here)
 {
  yield return MenuSpec.Submenu(T("As playlist"), [
   MenuSpec.Command(Tn(items.Count, "Selection (1 item) …", "Selection ({0:N0} items) …"), here(async () => await SaveM3uAsync(selectionOnly: true))),
   MenuSpec.Command(Tn(ViewTotal(), "Whole view (1 item) …", "Whole view ({0:N0} items) …"), here(async () => await SaveM3uAsync(selectionOnly: false)))]);
  yield return MenuSpec.Command(T("Write to XMP sidecars …"), here(ExportSidecarFiles));
  yield return MenuSpec.Command(T("Export NFO metadata …"), here(ExportNfo));
 }

 private int ViewTotal() => _window is { } window ? PositionInView(window, Guid.Empty).Total : 0;

 /// <summary>Stars for every medium (0 removes the rating), as the keys 0–5 do.</summary>
 internal void RateMedia(IReadOnlyList<MediaItem> items, int stars)
 {
  if (items.Count == 0) return;
  foreach (var item in items) _repository.Upsert((_repository.GetMedia(item.Id) ?? item) with { Rating = stars * 2 });
  Status.Text = (stars == 0 ? T("Rating removed") : Tn(stars, "1 star", "{0} stars")) + (items.Count > 1 ? Tn(items.Count, " · 1 item", " · {0:N0} items") : ""); Refresh();
 
 }

 /// <summary>Pins all media as favorites, or unpins them when all of them are pinned already (the heart on the tiles).</summary>
 internal void PinFavorite(IReadOnlyList<MediaItem> items)
 {
  if (items.Count == 0) return;
  var pin = !items.All(i => (_repository.GetMedia(i.Id) ?? i).IsFavorite);
  foreach (var item in items) _repository.Upsert((_repository.GetMedia(item.Id) ?? item) with { IsFavorite = pin });
  Status.Text = items.Count == 1 ? (pin ? T("“{0}” pinned as favorite", items[0].Title) : T("“{0}” no longer pinned as favorite", items[0].Title))
   : pin ? Tn(items.Count, "1 item pinned as favorite", "{0:N0} items pinned as favorite") : Tn(items.Count, "1 item no longer pinned as favorite", "{0:N0} items no longer pinned as favorite");
  Refresh();
 }

 /// <summary>Marks all media for later, or takes the mark back when all of them carry it.</summary>
 internal void MarkWatchLater(IReadOnlyList<MediaItem> items)
 {
  if (items.Count == 0) return;
  var mark = !items.All(i => (_repository.GetMedia(i.Id) ?? i).WatchLater);
  foreach (var item in items) _repository.Upsert((_repository.GetMedia(item.Id) ?? item) with { WatchLater = mark });
  Status.Text = items.Count == 1 ? (mark ? T("“{0}” marked for later", items[0].Title) : T("“{0}” removed from Watch later", items[0].Title))
   : mark ? Tn(items.Count, "1 item marked for later", "{0:N0} items marked for later") : Tn(items.Count, "1 item removed from Watch later", "{0:N0} items removed from Watch later");
  Refresh();
 }

 /// <summary>Removes the media from the open collection, or from the catalog after one question; the files stay.</summary>
 internal void RemoveMedia(IReadOnlyList<MediaItem> items, bool fromCollection = true)
 {
  if (items.Count == 0) return;
  if (fromCollection && _playlist != null)
  {
   var ids = items.Select(i => i.Id).ToHashSet();
   var entries = CollectionEntries(_playlist).Where(entry => !ids.Contains(entry.MediaId)).ToList();
   _playlist = _repository.UpsertPlaylist(_playlist with { MediaIds = entries.Select(entry => entry.MediaId).ToList(), Entries = entries });
   Refresh(); Status.Text = Tn(items.Count, "Item removed from collection", "{0:N0} items removed from collection"); return;
  }
  // Asking can be switched off like the original's "Never prompt when deleting media"; deleting the file itself always asks.
  var question = items.Count == 1 ? T("Remove “{0}” from the catalog?\n\nThe media file stays on the disk.", items[0].Title)
   : Tn(items.Count, "Remove 1 item from the catalog?\n\nThe media files stay on the disk.", "Remove {0:N0} items from the catalog?\n\nThe media files stay on the disk.");
  if (_repository.GetPreferences().General.ConfirmRemove && Message.Show(this, question, T("Remove from catalog"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
  foreach (var item in items) { _repository.Delete(item.Id); ForgetInLibraryFolders(item.Path); }
  Refresh();
  Status.Text = items.Count == 1 ? T("“{0}” removed from the catalog", items[0].Title) : Tn(items.Count, "1 item removed from the catalog", "{0:N0} items removed from the catalog");
 }

 /// <summary>Deletes the files after one question (recycle bin or for good, as the library says) and their catalog entries.</summary>
 internal void DeleteMedia(IReadOnlyList<MediaItem> items)
 {
  if (items.Count == 1) { if (DeleteWithQuestion(items[0])) return; }
  if (items.Count == 0) return;
  foreach (var item in items) FileAccessPolicy.Current.EnsureWritable(item.Path);
  var recycle = _repository.GetPreferences().General.DeleteToRecycleBin;
  var question = recycle ? Tn(items.Count, "Move 1 file to the Recycle Bin?", "Move {0:N0} files to the Recycle Bin?")
   : Tn(items.Count, "Permanently delete 1 file from the disk?\n\nThis action cannot be undone.", "Permanently delete {0:N0} files from the disk?\n\nThis action cannot be undone.");
  if (Message.Show(this, question, recycle ? T("Delete file") : T("Permanently delete file"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
  var failed = new List<string>();
  foreach (var item in items)
  {
   try { if (File.Exists(item.Path)) DeleteFromDisk(item.Path, recycle); _repository.Delete(item.Id); }
   catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException) { failed.Add(item.Path + ": " + error.Message); }
  }
  Refresh();
  if (failed.Count > 0) Message.Show(this, string.Join("\n", failed.Take(10)), recycle ? T("Delete file") : T("Permanently delete file"), MessageBoxButton.OK, MessageBoxImage.Warning);
 }

 /// <summary>The context menu key and Shift+F10 open the media menu at the focused tile.</summary>
 private bool MediaMenuKey(KeyEventArgs e)
 {
  var shiftF10 = (e.Key == Key.F10 || e.Key == Key.System && e.SystemKey == Key.F10) && Keyboard.Modifiers == ModifierKeys.Shift;
  if (e.Key != Key.Apps && !shiftF10 || !MediaList.IsKeyboardFocusWithin || SelectedMedia.Count == 0) return false;
  e.Handled = true;
  var target = Keyboard.FocusedElement as ListBoxItem ?? MediaList.ItemContainerGenerator.ContainerFromItem(MediaList.SelectedItem) as ListBoxItem;
  OpenMediaMenu(target, PlacementMode.Center);
  return true;
 }
}
