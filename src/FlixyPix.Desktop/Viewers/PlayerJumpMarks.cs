using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>Jump marks in the player: bookmarks, scene thumbnails and scene starts as a side list and as ticks on the
/// time line. A click on either jumps there; digits 1–9 jump to the n-th mark, the bookmark hotkeys step through them.</summary>
internal sealed partial class VideoPlayerWindow
{
 private sealed record MarkRow(JumpMark Mark, string Number, Brush Dot);
 /// <summary>Width of the time line thumb in the app's slider template; marks sit on the thumb's centre line.</summary>
 private const double TimelineThumbWidth = 14;
 private const string MarkTemplate =
  "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:local=\"clr-namespace:FlixyPix.Desktop;assembly=FlixyPix\"><Grid Margin=\"6,5\"><Grid.ColumnDefinitions><ColumnDefinition Width=\"16\"/><ColumnDefinition Width=\"66\"/><ColumnDefinition Width=\"Auto\"/><ColumnDefinition Width=\"*\"/><ColumnDefinition Width=\"Auto\"/></Grid.ColumnDefinitions>"
  + "<TextBlock Text=\"{Binding Number}\" Foreground=\"{DynamicResource Subtle}\" FontSize=\"11\" VerticalAlignment=\"Center\"/>"
  + "<StackPanel Grid.Column=\"1\" Orientation=\"Horizontal\" VerticalAlignment=\"Center\"><Ellipse Width=\"7\" Height=\"7\" Fill=\"{Binding Dot}\" Margin=\"0,0,7,0\"/><TextBlock Text=\"{Binding Mark.TimeLabel}\" Typography.NumeralAlignment=\"Tabular\" Foreground=\"{DynamicResource Muted}\" FontSize=\"12\"/></StackPanel>"
  + "<Border Grid.Column=\"2\" Width=\"64\" Height=\"36\" Margin=\"0,0,8,0\" Background=\"#22000000\" CornerRadius=\"3\"><Image Source=\"{Binding Mark.PreviewAssetId, Converter={StaticResource MarkThumb}}\" Stretch=\"UniformToFill\"/></Border>"
  + "<StackPanel Grid.Column=\"3\" VerticalAlignment=\"Center\"><TextBlock Text=\"{Binding Mark.Title}\" Tag=\"title\" TextTrimming=\"CharacterEllipsis\" ToolTip=\"{Binding Mark.Title}\"/><TextBlock Text=\"{Binding Mark.KindLabel}\" FontSize=\"10\" Foreground=\"{DynamicResource Subtle}\"/></StackPanel><Button Grid.Column=\"4\" Content=\"&#xE70F;\" FontFamily=\"Segoe MDL2 Assets\" Style=\"{StaticResource IconButton}\" Tag=\"rename\" ToolTip=\"{local:T Rename}\" AutomationProperties.Name=\"{local:T Rename}\"/></Grid></DataTemplate>";

 private readonly Canvas markLayer = new();
 private readonly ListBox markList = new() { Margin = new Thickness(6, 0, 4, 8) };
 private readonly Border markPanel = new() { Width = 290, BorderThickness = new Thickness(1, 0, 0, 0), Visibility = Visibility.Collapsed };
 private readonly TextBlock markCount = new() { FontSize = 11, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
 private readonly TextBlock noMarks = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(14, 4, 14, 0) };
 private readonly Button marksButton = new() { Content = T("Jump marks") };
 private readonly Button previousMarkButton = Symbol("\uE76B", T("Previous bookmark")), nextMarkButton = Symbol("\uE76C", T("Next bookmark"));
 private MarkRow[] markRows = [];
 internal IReadOnlyList<JumpMark> Marks { get; private set; } = [];
 internal bool MarkPanelVisible => markPanel.Visibility == Visibility.Visible;
 internal bool MarkStepsVisible => nextMarkButton.Visibility == Visibility.Visible;
 internal int RenderedMarkCount => markLayer.Children.Count;

 private Grid BuildTimeline()
 {
  position.IsMoveToPointEnabled = true; position.Margin = new Thickness(0);
  var grid = new Grid { Margin = new Thickness(8, 6, 8, 4) };
  grid.Children.Add(position); grid.Children.Add(markLayer);
  grid.SizeChanged += (_, _) => RenderMarkers();
  return grid;
 }

 private Border BuildMarkPanel()
 {
  markPanel.SetResourceReference(Border.BackgroundProperty, "ChromeBackground"); markPanel.SetResourceReference(Border.BorderBrushProperty, "Divider");
  var heading = new TextBlock { Text = T("JUMP MARKS"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
  heading.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); markCount.SetResourceReference(TextBlock.ForegroundProperty, "Subtle");
  var add = new Button { Content = T("＋ Set here"), Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(0), FontSize = 12, ToolTip = T("Set a bookmark at the current position") + KeyHint(PlayerActions.CreateBookmark) };
  add.Click += async (_, _) => await Command(() => RunActionAsync(PlayerActions.CreateBookmark));
  var close = new Button { Content = "", Style = (Style)Application.Current.Resources["IconButton"], FontSize = 13, Margin = new Thickness(4, 0, -4, 0), ToolTip = T("Hide the list") + KeyHint(PlayerActions.ToggleMarks) };
  AutomationProperties.SetName(close, T("Hide the list")); close.Click += (_, _) => ToggleMarkPanel();
  var header = new DockPanel { Margin = new Thickness(14, 14, 12, 6) }; DockPanel.SetDock(close, Dock.Right); DockPanel.SetDock(add, Dock.Right);
  header.Children.Add(close); header.Children.Add(add); header.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { heading, markCount } });
  var steps = string.Join(" / ", new[] { PlayerActions.PreviousBookmark, PlayerActions.NextBookmark }.Select(a => playback.Hotkeys.GetValueOrDefault(a, "")).Where(k => k.Length > 0).Select(DisplayKey));
  var hint = new TextBlock { Text = T("Click jumps to the mark · digits 1–9") + (steps.Length > 0 ? T(" · {0}: previous/next", steps) : ""), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14, 0, 12, 8) };
  hint.SetResourceReference(TextBlock.ForegroundProperty, "Subtle"); noMarks.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
  noMarks.Text = T("No bookmarks or scenes yet. “Set here”{0} adds a mark at the current position.", KeyHint(PlayerActions.CreateBookmark));
  markList.ItemTemplate = (DataTemplate)XamlReader.Parse(MarkTemplate);
  var rowStyle = new Style(typeof(ListBoxItem), (Style)Application.Current.Resources[typeof(ListBoxItem)]);
  rowStyle.Setters.Add(new Setter(MarginProperty, new Thickness(0, 0, 4, 2))); markList.ItemContainerStyle = rowStyle;
  markList.PreviewMouseLeftButtonUp += async (_, e) => { if(editingMark || IsRenameButton(e.OriginalSource as DependencyObject))return; if ((e.OriginalSource as FrameworkElement)?.DataContext is MarkRow row) await Command(() => SeekToMarkAsync(row.Mark)); };
  markList.AddHandler(ButtonBase.ClickEvent,new RoutedEventHandler((_,e)=>
  {
   if(e.OriginalSource is Button { Tag:"rename",DataContext:MarkRow row }) { e.Handled=true; BeginRenameMark(row); }
  }));
  markList.PreviewMouseRightButtonUp+=(_,e)=>
  {
   if((e.OriginalSource as FrameworkElement)?.DataContext is not MarkRow row)return;
   e.Handled=true; markList.SelectedItem=row;
   var menu=Menus.Create([MenuSpec.Command(T("Rename"),()=>BeginRenameMark(row),shortcut:"F2")],(_,error)=>status.Text=error.Message);
   OpenMarkMenu=menu; markMenuOpen=true; menu.Closed+=(_,_)=>{markMenuOpen=false;}; menu.IsOpen=true;
  };
  var body = new DockPanel(); DockPanel.SetDock(header, Dock.Top); DockPanel.SetDock(hint, Dock.Top);
  body.Children.Add(header); body.Children.Add(hint); body.Children.Add(new Grid { Children = { markList, noMarks } });
  markPanel.Child = body;
  marksButton.Click += (_, _) => ToggleMarkPanel();
  marksButton.ToolTip = T("Show or hide the jump mark list") + KeyHint(PlayerActions.ToggleMarks);
  foreach (var (button, action) in new[] { (previousMarkButton, PlayerActions.PreviousBookmark), (nextMarkButton, PlayerActions.NextBookmark) })
  { button.ToolTip += KeyHint(action); button.Click += async (_, _) => await Command(() => RunActionAsync(action)); }
  UpdateMarkSteps();
  return markPanel;
 }
 /// <summary>The list beside the video; in full screen the bar lives in the overlay and the list stays hidden.</summary>
 private bool marksWanted;
 internal void ToggleMarkPanel()
 {
  if(IsFullscreen)return;
  var wanted=!marksWanted;
  var current=repository.GetPreferences().Playback;
  repository.SavePlaybackPreferences(current with { ShowMarks=wanted });
  marksWanted=wanted; ShowMarkPanel(wanted); ApplyTransform();
 }
 private void ShowMarkPanel(bool visible) { markPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed; UpdateMarkSteps(); }
 /// <summary>While the list is hidden, previous/next buttons beside "Jump marks" step through the marks instead.</summary>
 private void UpdateMarkSteps() { previousMarkButton.Visibility = nextMarkButton.Visibility = !MarkPanelVisible && Marks.Count > 0 ? Visibility.Visible : Visibility.Collapsed; }

 /// <summary>Raised after the marks were (re)loaded; the library makes missing pictures.</summary>
 internal Action? MarksChanged { get; set; }
 internal void RefreshMarks()
 {
  if(editingMark)return;
  Marks = JumpMarks.Build(repository.GetBookmarks(media.Id), repository.GetOrderedScenes(media.Id), media.DurationSeconds);
  markRows = Marks.Select((mark, index) => new MarkRow(mark, index < 9 ? (index + 1).ToString(CultureInfo.CurrentCulture) : "", Theme.Brush(mark.Kind == JumpMarkKind.Scene ? "SceneMark" : "TimelineMark"))).ToArray();
  markList.ItemsSource = markRows; markCount.Text = Marks.Count.ToString("N0", CultureInfo.CurrentCulture);
  noMarks.Visibility = Marks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
  marksButton.Content = Marks.Count == 0 ? T("Jump marks") : T("Jump marks · {0:N0}", Marks.Count);
  // Refreshing the contents never changes the remembered visibility.
  ShowMarkPanel(marksWanted && !IsFullscreen);
  UpdateMarkSteps();
  RenderMarkers(); UpdateCurrentMark();
  MarksChanged?.Invoke();
 }

 private void RenderMarkers()
 {
  markLayer.Children.Clear();
  var width = markLayer.ActualWidth;
  if (!position.IsEnabled || position.Maximum <= 0 || width <= TimelineThumbWidth) return;
  foreach (var mark in Marks)
  {
   var x = TimelineThumbWidth / 2 + Math.Clamp(mark.PositionSeconds / position.Maximum, 0, 1) * (width - TimelineThumbWidth);
   var bar = new Border { Width = 3, Height = 12, CornerRadius = new CornerRadius(1.5), Background = Theme.Brush(mark.Kind == JumpMarkKind.Scene ? "SceneMark" : "TimelineMark") };
   var hit = new Border { Width = 11, Height = 22, Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = bar, ToolTip = $"{mark.TimeLabel} · {mark.Title} ({mark.KindLabel})" };
   Canvas.SetLeft(hit, x - hit.Width / 2); Canvas.SetTop(hit, 0);
   hit.MouseLeftButtonDown += async (_, e) => { e.Handled = true; await Command(() => SeekToMarkAsync(mark)); };
   markLayer.Children.Add(hit);
  }
 }

 private void UpdateCurrentMark()
 {
  if(editingMark || markMenuOpen)return;
  var current = JumpMarks.Current(Marks, LastPositionSeconds);
  var row = current == null ? null : markRows.FirstOrDefault(r => r.Mark == current);
  if (ReferenceEquals(markList.SelectedItem, row)) return;
  markList.SelectedItem = row; if (row != null) markList.ScrollIntoView(row);
 }

 /// <summary>A name field over the new bookmark in the list; in full screen, where the list stays hidden, a small dialog.</summary>
 internal ContextMenu? OpenMarkMenu { get; private set; }
 private bool editingMark,markMenuOpen;
 internal ListBox MarkList=>markList;
 internal Action? MarkRenamed { get; set; }
 private static bool IsRenameButton(DependencyObject? source)
 {
  for(var current=source;current!=null;current=current is Visual?VisualTreeHelper.GetParent(current):LogicalTreeHelper.GetParent(current))
   if(current is Button { Tag:"rename" })return true;
  return false;
 }
 internal bool BeginRenameSelectedMark()=>markList.SelectedItem is MarkRow row && BeginRenameMark(row);
 private bool BeginRenameMark(MarkRow row)
 {
  if(editingMark || closing)return false;
  markList.SelectedItem=row; editingMark=true;
  if(InlineEdit.Begin(markList,row,row.Mark.Title,name=>SaveMarkName(row.Mark,name),()=>
  {
   editingMark=false; if(closing)return;
   RefreshMarks(); markList.SelectedItem=markRows.FirstOrDefault(r=>r.Mark.Kind==row.Mark.Kind && r.Mark.SourceId==row.Mark.SourceId);
  }))return true;
  editingMark=false; return false;
 }
 private void SaveMarkName(JumpMark mark,string name)
 {
  try
  {
   if(mark.Kind==JumpMarkKind.Scene)
   {
    var source=repository.GetScenes(media.Id).FirstOrDefault(s=>s.Id==mark.SourceId);
    if(source==null)throw new InvalidOperationException(T("The jump mark no longer exists."));
    repository.UpsertScene(source with { Title=name });
   }
   else
   {
    var source=repository.GetBookmarks(media.Id).FirstOrDefault(b=>b.Id==mark.SourceId);
    if(source==null)throw new InvalidOperationException(T("The jump mark no longer exists."));
    repository.UpsertBookmark(source with { Title=name });
   }
   RefreshMarks(); MarkRenamed?.Invoke();
  }
  catch(Exception error) when(error is InvalidOperationException or ArgumentException or System.IO.IOException) { status.Text=error.Message; }
 }
 private void NameBookmark(Bookmark bookmark)
 {
  var row=markRows.FirstOrDefault(r=>r.Mark.SourceId==bookmark.Id && r.Mark.Kind!=JumpMarkKind.Scene);
  if(row==null)return;
  if(MarkPanelVisible && BeginRenameMark(row))return;
  if(Dialogs.Fields(this,T("Name bookmark"),("Name",bookmark.Title)) is [var name] && !string.IsNullOrWhiteSpace(name))SaveMarkName(row.Mark,name.Trim());
 }

 internal async Task SeekToMarkAsync(JumpMark mark) { await SeekToAsync(mark.PositionSeconds); UpdateCurrentMark(); }

 private string CurrentMarkLabel() => JumpMarks.Current(Marks, LastPositionSeconds) is { } mark ? " · ▸ " + mark.Title : "";

 private static int? DigitOf(Key key) => key switch
 {
  >= Key.D1 and <= Key.D9 => key - Key.D0,
  >= Key.NumPad1 and <= Key.NumPad9 => key - Key.NumPad0,
  _ => null,
 };

 private string KeyHint(string action) => playback.Hotkeys.GetValueOrDefault(action, "") is { Length: > 0 } key ? " (" + DisplayKey(key) + ")" : "";
 private static string DisplayKey(string hotkey) => string.Join("+", hotkey.Split('+').Select(SettingsUi.KeyName));
}
