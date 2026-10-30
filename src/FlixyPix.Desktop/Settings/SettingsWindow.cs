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

/// <summary>Settings grouped like the original's settings area (videos, player, thumbnails, images, cache,
/// security, database, tools). Only options with a working FlixyPix counterpart are offered. Each rubric is
/// split into cards; every option has a control that fits its value, and the search finds options across rubrics.</summary>
internal sealed partial class SettingsWindow : Window
{
 internal sealed record DesktopChoice(string DataDirectory, string? Ffmpeg, string? Ffprobe, string? Mpv, bool AutoGeneratePreviews, string Theme, bool RegenerateThumbnails = false, string BossKey = "Escape", string Language = Texts.Automatic,
  IReadOnlyList<ExternalProgram>? ExternalPrograms = null, IReadOnlyList<string>? QuickAccessFolders = null,
  IReadOnlyList<WebSearchProvider>? WebSearchProviders = null, bool MinimizeToTray = false, bool DetailsTabs = false,
  PrivacySettings? Privacy = null);
 /// <summary>Boss key of this computer (appsettings.json).</summary>
 internal readonly HotkeyBox BossKey = new() { AcceptsEscape = true };
 internal readonly CheckBox MinimizeToTray = SettingsUi.Switch();
 /// <summary>The details on the right as tabs instead of one below the other (appsettings.json).</summary>
 internal readonly CheckBox DetailsTabs = SettingsUi.Switch();
 internal DesktopChoice? Result { get; private set; }
 internal string? LastError => error.Text.Length == 0 ? null : error.Text;

 private static readonly (string Name, string Glyph, string Description)[] Rubrics =
 [
  (TKey("Appearance"), "", T("Color scheme and language of the interface.")),
  (TKey("General"), "", T("How the library behaves on import, display and start.")),
  (TKey("Home page"), "", T("What the home page shows and whether FlixyPix starts with it.")),
  (TKey("Videos"), "", T("Which video files are imported and from which rating they count as favorites.")),
  (TKey("Video player"), "", T("Playback, jump distances and shortcuts in the built-in player.")),
  (TKey("History"), "", T("O counter, playback history and from when a video counts as watched.")),
  (TKey("Thumbnails"), "", T("Appearance and generation of video thumbnails.")),
  (TKey("Images"), "", T("Which image files are imported and from which rating they count as favorites.")),
  (TKey("Cache"), "", T("View and clear the caches of this library.")),
  (TKey("Privacy"), "", T("Boss key, lock, blurred pictures and traces in Windows.")),
  (TKey("Security"), "", T("Protect the library with encryption.")),
  (TKey("Database"), "", T("Location of the library and import from the original.")),
  (TKey("Tools"), "", T("Programs for playback, thumbnails and media information.")),
 ];
 private static readonly (string Title, string[] Actions)[] HotkeyGroups =
 [
  (T("Playback"), [PlayerActions.PlayPause, PlayerActions.Fullscreen, PlayerActions.SpeedDown, PlayerActions.SpeedUp]),
  (T("Seeking"), [PlayerActions.SmallBackward, PlayerActions.SmallForward, PlayerActions.LargeBackward, PlayerActions.LargeForward, PlayerActions.FrameBackward, PlayerActions.FrameForward]),
  (T("Bookmarks"), [PlayerActions.CreateBookmark, PlayerActions.PreviousBookmark, PlayerActions.NextBookmark]),
  (T("O counter"), [PlayerActions.AddO, PlayerActions.UndoO]),
 ];
 private static readonly (string Folder, string Label, string Hint, bool Clearable)[] Caches =
 [
  ("image-cache", T("Image cache"), T("Downscaled images for fast browsing. Recreated when needed."), true),
  ("archive-cache", T("Archive cache"), T("Extracted images from ZIP and RAR archives. Recreated when needed."), true),
  ("thumbnails", T("Video thumbnails"), T("Created thumbnails. They are kept."), false),
  ("media-assets", T("Custom artwork"), T("Covers and thumbnails you chose. They are kept."), false),
  ("hover-cache", T("Hover preview"), T("Images for the preview on video tiles. Recreated when needed."), true),
  ("clip-cache", T("Preview clips"), T("Short silent clips for the hover preview. Recreated when needed."), true),
 ];

 private readonly CatalogRepository repository;
 private readonly string libraryDirectory;
 private readonly ListBox navigation = new() { Margin = new Thickness(0, 10, 0, 0) };
 private readonly TextBox search = new() { Margin = new Thickness(0), Padding = new Thickness(30, 7, 9, 7) };
 private readonly ContentControl page = new();
 private readonly ScrollViewer scroller;
 private readonly Dictionary<string, FrameworkElement> pages = [];
 private readonly List<(string Page, SettingRow Row)> searchable = [];
 private readonly List<(NumberBox Box, string Label)> numbers = [];
 private readonly Dictionary<string, HotkeyBox> hotkeys = [];
 internal readonly PlayerSeekSettings Seek = new();
 internal readonly PlayerDisplaySettings PlayerDisplay = new();
 internal readonly LibraryDisplaySettings Library = new();
 private readonly ExtensionEditor videoTypes = new(new PreviewPreferences().VideoExtensions, ".webm"), imageTypes = new(new PreviewPreferences().ImageExtensions, ".heic");
 private readonly ThemePicker themePicker = new();
 /// <summary>Language of this computer (appsettings.json); applies on the next start.</summary>
 internal readonly ComboBox LanguageChoice = new() { MinWidth = 260 };
 private readonly ColorScheme themeBefore = Theme.Mode;
 private readonly RatingPicker videoFavorite = new(), audioFavorite = new(), imageFavorite = new();
 internal readonly CheckBox AlwaysFullscreen = SettingsUi.Switch(), PlayBestVersion = SettingsUi.Switch();
 private readonly CheckBox hardwareDecoding = SettingsUi.Switch(), skipCustom = SettingsUi.Switch(), autoPreviews = SettingsUi.Switch(), fingerprintsOnImport = SettingsUi.Switch();
 private readonly CheckBox hoverEnabled = SettingsUi.Switch(), hoverDelay = SettingsUi.Switch(), hoverTimestamp = SettingsUi.Switch();
 internal readonly CheckBox IncludeHidden = SettingsUi.Switch(), ConfirmRemove = SettingsUi.Switch(), ShowExtension = SettingsUi.Switch(), MarkMissing = SettingsUi.Switch(),
  AlwaysReport = SettingsUi.Switch(), PreventSleep = SettingsUi.Switch(), MediaKeys = SettingsUi.Switch(),
  FolderCounts = SettingsUi.Switch(), CategoryCounts = SettingsUi.Switch(), TagCategoryCounts = SettingsUi.Switch();
 /// <summary>Restore on start: 0 off, 1 the view only, 2 everything.</summary>
 internal readonly SegmentedChoice RestoreChoice = new([(0, T("Off")), (1, T("View only")), (2, T("Everything"))]);
 internal readonly CheckBox StartOnHome = SettingsUi.Switch();
 /// <summary>One switch per start page row; the order of the rows stays as the library keeps it.</summary>
 internal readonly Dictionary<HomeRowKind, CheckBox> HomeRows = HomePreferences.DefaultRows.ToDictionary(kind => kind, _ => SettingsUi.Switch());
 internal readonly SegmentedChoice ImageWheel = new([((int)ImageWheelAction.Navigate, T("Next image")), ((int)ImageWheelAction.Zoom, T("Zoom")), ((int)ImageWheelAction.Scroll, T("Browse"))]);
 private readonly SegmentedChoice hoverSpeed = new([((int)HoverSpeed.Slow, T("Slow")), ((int)HoverSpeed.Medium, T("Medium")), ((int)HoverSpeed.Fast, T("Fast"))]);
 private readonly NumberBox hoverFrames;
 private readonly NumberBox clipSegments, clipSeconds, clipWidth, wallClips;
 private readonly CheckBox clipsOnImport = SettingsUi.Switch();
 internal readonly CheckBox HistoryEnabled = SettingsUi.Switch(), ShowOCounter = SettingsUi.Switch();
 internal readonly NumberBox WatchedShare;
 private readonly TextBlock historySummary = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
 private readonly SegmentedChoice hoverMode = new([((int)HoverMode.Frames, T("Images")), ((int)HoverMode.Clip, T("Preview clip"))]);
 internal readonly ComboBox MouseLeft = MouseChoice(), mouseRight = MouseChoice(), mouseMiddle = MouseChoice(), mouseBack = MouseChoice(), mouseForward = MouseChoice();
 internal readonly ComboBox PlayerWheel = new() { Width = 230, ItemsSource = Enum.GetValues<PlayerWheelAction>().Select(w => Capital(CatalogRepository.WheelLabel(w))).ToArray() };
 private static ComboBox MouseChoice() => new() { Width = 230, ItemsSource = Enum.GetValues<PlayerMouseAction>().Select(a => Capital(CatalogRepository.MouseLabel(a))).ToArray() };
 private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
 internal readonly CheckBox NfoTitle = SettingsUi.Switch(), NfoTags = SettingsUi.Switch(), NfoDescription = SettingsUi.Switch(), NfoSubfolders = SettingsUi.Switch(),
  NfoThumbnail = SettingsUi.Switch(), NfoFanart = SettingsUi.Switch();
 private readonly TextBox nfoTagElements = new() { MinWidth = 420 }, nfoDescriptionElements = new() { MinWidth = 420 };
 internal readonly NumberBox SlideInterval;
 internal readonly SegmentedChoice SlideOrder = new([((int)SlideshowOrder.InOrder, T("In order")), ((int)SlideshowOrder.Random, T("Random")), ((int)SlideshowOrder.LessRandom, T("Less random"))]);
 private readonly SegmentedChoice slideSpeed = new([((int)HoverSpeed.Slow, T("Slow")), ((int)HoverSpeed.Medium, T("Medium")), ((int)HoverSpeed.Fast, T("Fast"))]);
 internal readonly CheckBox SlideFade = SettingsUi.Switch(), SlidePan = SettingsUi.Switch(), SlideInfo = SettingsUi.Switch();
 private readonly NumberBox smallForward, smallBackward, largeForward, largeBackward;
 internal readonly NumberBox SheetWidth;
 internal readonly CheckBox SheetFast = SettingsUi.Switch(), SheetHeader = SettingsUi.Switch(), RegenerateAfterSave = SettingsUi.Switch();
 internal readonly NumberBox SheetBorder, SheetSkip;
 internal readonly ComboBox SheetTimestampChoice = new() { Width = 170 }, SheetFontChoice = new() { Width = 170 };
 internal readonly ColorField SheetBackground = new(T("Background"), "#14171C", "#000000", "#FFFFFF"), SheetForeground = new(T("Text color"), "#FFFFFF", "#000000", "#F3C76B");
 private readonly Image samplePreview = new() { Stretch = Stretch.Uniform, MaxHeight = 360, MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left };
 private readonly TextBlock sampleNote = SettingsUi.Hint(T("Shows the thumbnail of a video from the library with the values of this page, even before saving."));
 private readonly Func<PreviewPreferences, CancellationToken, Task<string?>>? renderSample;
 private readonly DispatcherTimer sampleTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
 private CancellationTokenSource? sampleRun;
 private bool sampleShown;
 internal Task SampleTask { get; private set; } = Task.CompletedTask;
 private readonly SegmentedChoice columns = Choice(1, 6), rows = Choice(1, 6), processes = Choice(1, 8);
 private readonly PathPicker ffmpeg, ffprobe, mpv;
 private readonly string displayedDirectory;
 private readonly Action? manageLibraries;
 private readonly UniformGrid sheetCells = new();
 private readonly Border sheetFrame = new() { BorderBrush = SettingsUi.FieldBorder, BorderThickness = new Thickness(1), Background = SettingsUi.Field, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
 private readonly TextBlock sheetResult = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center },
  conflictNote = new() { Foreground = SettingsUi.Error, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 0, 16, 12), Visibility = Visibility.Collapsed },
  securityStatus = new() { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center },
  securityIcon = SettingsUi.Icon("", 22),
  cacheTotal = new() { VerticalAlignment = VerticalAlignment.Center },
  error = new() { Foreground = SettingsUi.Error, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center },
  note = new() { Foreground = SettingsUi.Muted, VerticalAlignment = VerticalAlignment.Center };
 private readonly Dictionary<string, (TextBlock Size, Border Bar)> cacheViews = [];
 private readonly Button resetPage = new() { Content = T("Defaults"), ToolTip = T("Reset the options of this section to their defaults. Takes effect only after saving.") };
 private string lastPage = Rubrics[0].Name;
 private bool loading = true, dirty;

 internal SettingsWindow(Window owner, CatalogRepository repository, DesktopSettings settings, string displayedDataDirectory, Action manageEncryption, Action takeOverOriginal,
  Func<PreviewPreferences, CancellationToken, Task<string?>>? renderSample = null, Action? manageLibraries = null)
 {
  Owner = owner; this.repository = repository; libraryDirectory = settings.DataDirectory; this.renderSample = renderSample;
  displayedDirectory = displayedDataDirectory; this.manageLibraries = manageLibraries; programs = [.. settings.ExternalPrograms]; quickFolders = [.. settings.QuickAccessFolders]; searchProviders = [.. settings.WebSearchProviders ?? WebSearch.Defaults];
  Title = T("Settings · FlixyPix"); Width = 1020; Height = 740; MinWidth = 820; MinHeight = 520; WindowStartupLocation = WindowStartupLocation.CenterOwner;
  smallBackward = Number(1, 3600, 1, "s", T("Small jump back")); smallForward = Number(1, 3600, 1, "s", T("Small jump forward"));
  largeBackward = Number(1, 3600, 5, "s", T("Large jump back")); largeForward = Number(1, 3600, 5, "s", T("Large jump forward"));
  SheetWidth = Number(160, 3840, 80, "px", T("Thumbnail width"));
  SheetBorder = Number(0, 20, 1, "px", T("Border around the scenes")); SheetSkip = Number(0, 20, 1, "%", T("Skip start and end"));
  foreach (var label in new[] { T("None"), T("Top left"), T("Top right"), T("Bottom left"), T("Bottom right") }) SheetTimestampChoice.Items.Add(label);
  foreach (var family in FlixyPix.MediaTools.FontFiles.Suggested.Where(f => FlixyPix.MediaTools.FontFiles.Find(f) != null)) SheetFontChoice.Items.Add(family);
  AutomationProperties.SetName(SheetTimestampChoice, T("Time stamp")); AutomationProperties.SetName(SheetFontChoice, T("Font"));
  hoverFrames = Number(1, HoverPreview.MaximumFrames, 1, T("Images"), T("Images used for the hover preview"));
  clipSegments = Number(1, 32, 1, T("Parts"), T("Parts of a preview clip")); clipSeconds = Number(0.25, 5, 0.25, "s", T("Length of each part"));
  clipWidth = Number(120, 1280, 40, "px", T("Width of the preview clips")); wallClips = Number(1, 24, 1, T("Clips"), T("Clips at once on the video wall"));
  WatchedShare = Number(HistoryPreferences.MinimumShare * 100, HistoryPreferences.MaximumShare * 100, 5, "%", T("Counts as watched from"));
  SlideInterval = Number(SlideshowPreferences.MinimumInterval, SlideshowPreferences.MaximumInterval, .5, "s", T("Display time per image"));
  ffmpeg = new PathPicker(false, T("Programs (*.exe)|*.exe"), value => DescribeTool("ffmpeg", value));
  ffprobe = new PathPicker(false, T("Programs (*.exe)|*.exe"), value => DescribeTool("ffprobe", value));
  mpv = new PathPicker(false, T("Programs (*.exe)|*.exe"), value => DescribeTool("mpv", value));
  foreach (var (action, _, _) in PlayerActions.All) { var box = new HotkeyBox(); box.Changed += () => { UpdateConflicts(); Touch(); }; hotkeys[action] = box; }
  Seek.Changed += () => { UpdateConflicts(); Touch(); };
  PlayerDisplay.Changed += () => { UpdateShortcutState(); Touch(); }; Library.Changed += Touch;
  foreach (var editor in new[] { videoTypes, imageTypes }) editor.Changed += Touch;
  foreach (var picker in new[] { videoFavorite, audioFavorite, imageFavorite }) picker.Changed += Touch;
  themePicker.Value = Theme.ParseMode(settings.Theme);
  foreach (var (value, label) in LanguageSetup.Choices()) LanguageChoice.Items.Add(new ComboBoxItem { Content = label, Tag = value });
  LanguageChoice.SelectedItem = LanguageChoice.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == settings.Language) ?? LanguageChoice.Items[0];
  LanguageChoice.SelectionChanged += (_, _) => Touch(); AutomationProperties.SetName(LanguageChoice, T("Language"));
  BossKey.Value = settings.BossKey; BossKey.Changed += Touch; AutomationProperties.SetName(BossKey, T("Boss key"));
  MinimizeToTray.IsChecked = settings.MinimizeToTray; MinimizeToTray.Click += (_, _) => Touch(); AutomationProperties.SetName(MinimizeToTray, T("Minimize to the notification area"));
  DetailsTabs.IsChecked = settings.DetailsTabs; DetailsTabs.Click += (_, _) => Touch(); AutomationProperties.SetName(DetailsTabs, T("Details as tabs"));
  // The scheme applies at once as a preview; closing without saving restores the previous one.
  themePicker.Changed += () => { if (loading) return; Theme.Apply(themePicker.Value); Touch(); };
  foreach (var check in new[] { AlwaysFullscreen, PlayBestVersion, hardwareDecoding, skipCustom, autoPreviews, fingerprintsOnImport, clipsOnImport, hoverEnabled, hoverDelay, hoverTimestamp, IncludeHidden,
   ConfirmRemove, ShowExtension, MarkMissing, FolderCounts, CategoryCounts, TagCategoryCounts, AlwaysReport, PreventSleep, MediaKeys, SlideFade, SlidePan, SlideInfo, NfoTitle, NfoTags,
   NfoDescription, NfoSubfolders, NfoThumbnail, NfoFanart, HistoryEnabled, ShowOCounter }.Concat(HomeRows.Values).Append(StartOnHome)) { check.Checked += (_, _) => Touch();
   check.Unchecked += (_, _) => Touch(); }
  foreach (var choice in new[] { columns, rows, processes, hoverSpeed, hoverMode, ImageWheel, SlideOrder, slideSpeed, RestoreChoice }) choice.Changed += Touch;
  foreach (var combo in new[] { MouseLeft, mouseRight, mouseMiddle, mouseBack, mouseForward, PlayerWheel }) { combo.SelectionChanged += (_, _) => Touch(); AutomationProperties.SetName(combo, T("Mouse action")); }
  foreach (var box in new[] { nfoTagElements, nfoDescriptionElements }) box.TextChanged += (_, _) => Touch();
  AutomationProperties.SetName(nfoTagElements, T("NFO elements as tags")); AutomationProperties.SetName(nfoDescriptionElements, T("NFO elements of the description"));
  foreach (var path in new[] { ffmpeg, ffprobe, mpv }) path.Changed += Touch;
  columns.Changed += UpdateSheetPreview; rows.Changed += UpdateSheetPreview; SheetWidth.Changed += UpdateSheetPreview;
  foreach (var check in new[] { SheetFast, SheetHeader, RegenerateAfterSave }) { check.Checked += (_, _) => Touch(); check.Unchecked += (_, _) => Touch(); }
  foreach (var combo in new[] { SheetTimestampChoice, SheetFontChoice }) combo.SelectionChanged += (_, _) => { Touch(); ScheduleSample(); };
  foreach (var color in new[] { SheetBackground, SheetForeground }) color.Changed += () => { Touch(); ScheduleSample(); };
  foreach (var check in new[] { SheetFast, SheetHeader }) { check.Checked += (_, _) => ScheduleSample(); check.Unchecked += (_, _) => ScheduleSample(); }
  foreach (var box in new[] { SheetWidth, SheetBorder, SheetSkip }) box.Changed += ScheduleSample;
  columns.Changed += ScheduleSample; rows.Changed += ScheduleSample;
  sampleTimer.Tick += (_, _) => { sampleTimer.Stop(); SampleTask = RenderSampleAsync(); };

  var preferences = repository.GetPreferences();
  videoTypes.Values = preferences.Previews.VideoExtensions; imageTypes.Values = preferences.Previews.ImageExtensions;
  videoFavorite.Value = preferences.VideoFavoriteMinimumRating; audioFavorite.Value = preferences.AudioFavoriteMinimumRating; imageFavorite.Value = preferences.ImageFavoriteMinimumRating;
  LoadPlayback(preferences.Playback); LoadPreviews(preferences.Previews); LoadGeneral(preferences.General); LoadHome(preferences.Home); LoadSlideshow(preferences.Slideshow); LoadNfo(preferences.Nfo); LoadHistory(preferences.History); autoPreviews.IsChecked = settings.AutoGeneratePreviews;
  ffmpeg.Text = settings.Ffmpeg ?? ""; ffprobe.Text = settings.Ffprobe ?? ""; mpv.Text = settings.Mpv ?? "";

  AddPages(manageEncryption, takeOverOriginal, settings.Privacy);

  scroller = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 12, 0) };
  navigation.SelectionChanged += (_, _) => ShowSelectedPage();
  navigation.PreviewMouseLeftButtonDown += (_, _) => search.Clear();
  navigation.SelectedIndex = 0;
  search.TextChanged += (_, _) => { if (search.Text.Trim().Length == 0) ShowSelectedPage(); else { ShowSearch(search.Text.Trim()); navigation.SelectedIndex = -1; } };
  search.PreviewKeyDown += (_, e) =>
  {
   if (e.Key != Key.Enter) return;
   e.Handled = true;
   if (search.Text.Trim().Length > 0 && searchable.FirstOrDefault(entry => Matches(entry, search.Text.Trim())) is { Row: not null } first) Jump(first.Page, first.Row);
  };
  var searchIcon = SettingsUi.Icon("", 13); searchIcon.Foreground = SettingsUi.Muted; searchIcon.Margin = new Thickness(10, 0, 0, 0); searchIcon.IsHitTestVisible = false; searchIcon.HorizontalAlignment = HorizontalAlignment.Left;
  var placeholder = new TextBlock { Text = T("Search settings"), Foreground = SettingsUi.Muted, Margin = new Thickness(31, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
  search.TextChanged += (_, _) => placeholder.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
  System.Windows.Automation.AutomationProperties.SetName(search, T("Search settings"));
  var side = new DockPanel { Width = 220, Margin = new Thickness(0, 0, 20, 0) };
  var searchArea = new Grid { Children = { search, searchIcon, placeholder } }; DockPanel.SetDock(searchArea, Dock.Top);
  side.Children.Add(searchArea); side.Children.Add(navigation);

  var save = new Button { Content = T("Save"), IsDefault = true, Style = (Style)Application.Current.Resources["Primary"], Margin = new Thickness(6, 0, 0, 0) }; save.Click += (_, _) => { if (TrySave()) Close(); };
  var cancel = new Button { Content = T("Cancel"), IsCancel = true, Margin = new Thickness(6, 0, 0, 0) };
  resetPage.Margin = new Thickness(0); resetPage.Click += (_, _) => ResetPage();
  var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0) }; var footerButtons = SettingsUi.Buttons(resetPage, save, cancel);
  DockPanel.SetDock(footerButtons, Dock.Right); footer.Children.Add(footerButtons);
  footer.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0), Children = { error, note } });
  var body = new DockPanel(); DockPanel.SetDock(footer, Dock.Bottom); body.Children.Add(footer);
  DockPanel.SetDock(side, Dock.Left); body.Children.Add(side); body.Children.Add(scroller);
  Content = new Border { Padding = new Thickness(22), Child = body };
  PreviewKeyDown += (_, e) => { if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { search.Focus(); search.SelectAll(); e.Handled = true; } };
  Loaded += async (_, _) => await UpdateCacheSummaryAsync();
  UpdateConflicts(); UpdateSheetPreview();
  loading = false;
 }

 internal void SelectPage(string name) { search.Clear(); navigation.SelectedItem = navigation.Items.OfType<ListBoxItem>().First(item => (string)item.Tag == name); }
 /// <summary>Shows the search results for <paramref name="text"/> and returns how many options matched.</summary>
 internal int Search(string text) { search.Text = text; return searchable.Count(entry => Matches(entry, text)); }

 private static SegmentedChoice Choice(int minimum, int maximum) => new(Enumerable.Range(minimum, maximum - minimum + 1).Select(v => (v, v.ToString(CultureInfo.CurrentCulture))));
 private NumberBox Number(double minimum, double maximum, double step, string unit, string label)
 {
  var box = new NumberBox(minimum, maximum, step, unit); box.SetLabel(label); box.Changed += Touch; numbers.Add((box, label)); return box;
 }
 private static StackPanel JumpPair(NumberBox backward, NumberBox forward)
 {
  TextBlock Caption(string text) => new() { Text = text, Foreground = SettingsUi.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
  var back = Caption(T("◀ back")); var ahead = Caption(T("forward ▶")); ahead.Margin = new Thickness(22, 0, 8, 0);
  return SettingsUi.Buttons(back, backward, ahead, forward);
 }

 /// <summary>Adds a rubric; <paramref name="name"/> is its German name, which also identifies the page in code and tests.</summary>
 private void AddPage(string name, params UIElement[] cards)
 {
  var (_, glyph, description) = Rubrics.First(rubric => rubric.Name == name);
  var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
  panel.Children.Add(new TextBlock { Text = T(name), FontSize = 24, FontWeight = FontWeights.SemiBold });
  panel.Children.Add(new TextBlock { Text = description, Foreground = SettingsUi.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 16) });
  foreach (var card in cards) panel.Children.Add(card);
  pages[name] = panel;
  foreach (var (cardTitle, row) in Rows(panel, T(name))) { row.Section = cardTitle; searchable.Add((name, row)); }
  var icon = SettingsUi.Icon(glyph, 15); icon.Margin = new Thickness(0, 0, 12, 0); icon.Foreground = SettingsUi.Muted;
  navigation.Items.Add(new ListBoxItem { Content = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 4, 6, 4), Children = { icon, new TextBlock { Text = T(name) } } }, Tag = name });
 }
 private static IEnumerable<(string Card, SettingRow Row)> Rows(DependencyObject root, string card)
 {
  foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
  {
   if (child is SettingRow row) { yield return (card, row); continue; }
   var title = child is Border { Child: StackPanel { Children: [StackPanel { Children: [TextBlock heading, ..] }, ..] } } ? heading.Text : card;
   foreach (var nested in Rows(child, title)) yield return nested;
  }
 }
 private string? PageOf(DependencyObject element)
 {
  for (var current = element; current != null; current = LogicalTreeHelper.GetParent(current))
   if (current is FrameworkElement framework && pages.FirstOrDefault(pair => pair.Value == framework).Key is { } name) return name;
  return null;
 }

 private void ShowSelectedPage()
 {
  if (search.Text.Trim().Length > 0) return;
  // Leaving the search restores the rubric that was open before it.
  if (navigation.SelectedItem is not ListBoxItem { Tag: string name }) { navigation.SelectedItem = navigation.Items.OfType<ListBoxItem>().First(item => (string)item.Tag == lastPage); return; }
  lastPage = name; page.Content = pages[name]; scroller.ScrollToTop();
  resetPage.IsEnabled = name is "Appearance" or "General" or "Videos" or "Video player" or "Thumbnails" or "Images" or "Privacy";
 }
 private static bool Matches((string Page, SettingRow Row) entry, string text) =>
  new[] { entry.Row.Label, entry.Row.Explanation, entry.Row.Section, T(entry.Page) }.Any(part => part?.Contains(text, StringComparison.CurrentCultureIgnoreCase) == true);
 private void ShowSearch(string text)
 {
  var results = new ListBox { Margin = new Thickness(0) };
  foreach (var entry in searchable.Where(entry => Matches(entry, text)))
  {
   var item = new StackPanel { Margin = new Thickness(10, 6, 10, 6) };
   item.Children.Add(new TextBlock { Text = entry.Row.Label, FontWeight = FontWeights.SemiBold });
   item.Children.Add(SettingsUi.Hint(T(entry.Page) + " › " + entry.Row.Section + (entry.Row.Explanation is { } hint ? " · " + hint : "")));
   var target = entry; var listItem = new ListBoxItem { Content = item, Cursor = Cursors.Hand };
   listItem.MouseLeftButtonUp += (_, _) => Jump(target.Page, target.Row);
   listItem.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Jump(target.Page, target.Row); e.Handled = true; } };
   results.Items.Add(listItem);
  }
  var panel = new StackPanel();
  panel.Children.Add(new TextBlock { Text = T("Search results"), FontSize = 24, FontWeight = FontWeights.SemiBold });
  panel.Children.Add(new TextBlock { Text = results.Items.Count == 0 ? T("No setting matches “{0}”.", text) : Tn(results.Items.Count,
   "1 setting matches “{1}”. Enter opens the first one.", "{0} settings match “{1}”. Enter opens the first one.", text),
   Foreground = SettingsUi.Muted, Margin = new Thickness(0, 2, 0, 16) });
  panel.Children.Add(results);
  page.Content = panel; resetPage.IsEnabled = false;
 }
 private void Jump(string name, SettingRow row)
 {
  SelectPage(name);
  Dispatcher.BeginInvoke(() => { row.BringIntoView(); row.Flash(); row.Control.Focus(); }, DispatcherPriority.Loaded);
 }
}
