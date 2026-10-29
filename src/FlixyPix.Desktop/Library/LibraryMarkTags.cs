using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>Bookmarks and scenes in the details: rename in place (F2 or a click on the selected title), tags with
/// suggestions in the right-click menu, and tag chips above each list that filter it (UX plan U6).</summary>
public partial class MainWindow
{
 private readonly Dictionary<ListBox, string> _markTagFilters = [];
 private DispatcherTimer? _renameDelay;

 private static IReadOnlyList<string> TagsOfMark(object mark) => mark switch { Bookmark b => b.Tags, Scene s => s.Tags, _ => [] };
 private static string TitleOfMark(object mark) => mark switch { Bookmark b => b.Title, Scene s => s.Title, _ => "" };
 private static Guid IdOfMark(object mark) => mark switch { Bookmark b => b.Id, Scene s => s.Id, _ => Guid.Empty };

 /// <summary>Saves the mark and shows the lists again with it selected.</summary>
 private void SaveMark(object mark)
 {
  if (mark is Bookmark bookmark) _repository.UpsertBookmark(bookmark); else if (mark is Scene scene) _repository.UpsertScene(scene);
  var list = mark is Scene ? Scenes : Bookmarks;
  ShowDetails();
  list.SelectedItem = list.Items.Cast<object>().FirstOrDefault(m => IdOfMark(m) == IdOfMark(mark));
 }
 private void RenameMark(object mark, string name) => SaveMark(mark switch { Bookmark b => b with { Title = name }, Scene s => s with { Title = name }, _ => mark });
 private void SetMarkTags(object mark, IEnumerable<string> tags)
 {
  var cleaned = CatalogRepository.NormalizeSceneTags(tags);
  SaveMark(mark switch { Bookmark b => b with { Tags = cleaned }, Scene s => s with { Tags = cleaned }, _ => mark });
 }

 internal bool BeginRenameMark(ListBox list) =>
  list.SelectedItem is { } mark && InlineEdit.Begin(list, mark, TitleOfMark(mark), name => RenameMark(mark, name));

 /// <summary>Shows the tag chips above both lists and keeps a chosen tag while it is still there.</summary>
 private void ShowMarkTagFilters()
 {
  RenderMarkTagFilter(Scenes, SceneTagFilter);
  RenderMarkTagFilter(Bookmarks, BookmarkTagFilter);
 }
 private void RenderMarkTagFilter(ListBox list, WrapPanel host)
 {
  host.Children.Clear();
  var tags = (list.ItemsSource as IEnumerable<object> ?? []).SelectMany(TagsOfMark).GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
   .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase).Select(g => g.First()).ToList();
  var active = _markTagFilters.GetValueOrDefault(list);
  if (active != null && !tags.Contains(active, StringComparer.OrdinalIgnoreCase)) { _markTagFilters.Remove(list); active = null; }
  list.Items.Filter = active == null ? null : mark => TagsOfMark(mark).Contains(active, StringComparer.OrdinalIgnoreCase);
  host.Visibility = tags.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
  foreach (var tag in tags.Prepend(null))
  {
   var chosen = string.Equals(tag, active, StringComparison.OrdinalIgnoreCase);
   var chip = new Button { Content = tag ?? T("All"), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 4), FontSize = 11,
    ToolTip = tag == null ? T("Show all") : T("Only with the tag “{0}”", tag) };
   chip.SetResourceReference(BackgroundProperty, chosen ? "ChipFull" : "ButtonBackground");
   chip.Click += (_, _) => { if (tag == null) _markTagFilters.Remove(list); else _markTagFilters[list] = tag; RenderMarkTagFilter(list, host); };
   host.Children.Add(chip);
  }
 }

 private void MarkListKeyDown(object sender, KeyEventArgs e)
 {
  var list = (ListBox)sender;
  if (e.Key == Key.F2) { e.Handled = BeginRenameMark(list); }
  else if (e.Key == Key.Enter && list.SelectedItem != null) { e.Handled = true; PlayMark(list); }
  else if (e.Key == Key.Delete && list.SelectedItem != null) { e.Handled = true; if (list == Scenes) DeleteScene(list, e); else DeleteBookmark(list, e); }
 }

 /// <summary>A click on the title of the row that is already selected renames it, once it is clear that no double click follows.</summary>
 private void MarkListMouseDown(object sender, MouseButtonEventArgs e)
 {
  _renameDelay?.Stop();
  var list = (ListBox)sender;
  if (e.ClickCount != 1 || e.OriginalSource is not TextBlock { Tag: "title", DataContext: { } mark } || !ReferenceEquals(mark, list.SelectedItem)) return;
  _renameDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Clamp(GetDoubleClickTime(), 200, 900) + 50) };
  _renameDelay.Tick += (_, _) => { _renameDelay?.Stop(); if (ReferenceEquals(list.SelectedItem, mark)) BeginRenameMark(list); };
  _renameDelay.Start();
 }

 private void MarkListRightClicked(object sender, MouseButtonEventArgs e)
 {
  var list = (ListBox)sender;
  if ((e.OriginalSource as FrameworkElement)?.DataContext is not ({ } mark and (Bookmark or Scene))) return;
  e.Handled = true; list.SelectedItem = mark;
  Menus.Open(null, MarkMenu(list, mark), MenuFailed, PlacementMode.MousePoint);
 }

 [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();

 private void PlayMark(ListBox list) { if (list == Scenes) PlaySelectedScene(); else PlaySelectedBookmark(); }

 internal IReadOnlyList<MenuSpec> MarkMenu(ListBox list, object mark)
 {
  var scene = list == Scenes;
  return
  [
   MenuSpec.Command(scene ? T("Play scene") : T("Play from here"), () => PlayMark(list), "", T("Enter")),
   MenuSpec.Separator,
   MenuSpec.Command(T("Rename"), () => BeginRenameMark(list), "", "F2"),
   MenuSpec.Submenu(T("Tags"), MarkTagEntries(mark), ""),
   MenuSpec.Command(scene ? T("Edit scene …") : T("Edit bookmark …"), () => { if (scene) EditSelectedScene(list, new()); else EditSelectedBookmark(list, new()); }, ""),
   MenuSpec.Command(T("Choose thumbnail …"), () => { if (scene) SetSelectedScenePreview(list, new()); else SetSelectedBookmarkPreview(list, new()); }, ""),
   MenuSpec.Separator,
   MenuSpec.Command(scene ? T("Remove scene") : T("Remove bookmark"), () => { if (scene) DeleteScene(list, new()); else DeleteBookmark(list, new()); }, "", T("Del")),
  ];
 }

 /// <summary>The mark's own tags, the tags used most on other marks and the favourite tags of the vocabulary, each a switch.</summary>
 private IEnumerable<MenuSpec> MarkTagEntries(object mark)
 {
  var own = TagsOfMark(mark);
  var suggestions = own.Concat(_repository.GetMarkTags().Select(t => t.Tag).Take(12))
   .Concat(_repository.GetPreferences().TagVocabulary.Where(t => t.IsFavorite).Select(t => t.Tag))
   .Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToList();
  foreach (var tag in suggestions)
  {
   var has = own.Contains(tag, StringComparer.OrdinalIgnoreCase);
   yield return new MenuCommand(tag, () => SetMarkTags(mark, has ? own.Where(t => !string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)) : [.. own, tag])) { Checked = has };
  }
  yield return MenuSpec.Separator;
  yield return MenuSpec.Command(T("Edit tags …"), () => EditMarkTags(mark));
  yield return MenuSpec.Command(T("Remove all tags"), () => SetMarkTags(mark, []), enabled: own.Count > 0);
 }

 private void EditMarkTags(object mark)
 {
  if (Dialogs.Fields(this, T("Tags of “{0}”", TitleOfMark(mark)), (T("Tags, separated by semicolons"), string.Join("; ", TagsOfMark(mark)))) is not [var text]) return;
  try { SetMarkTags(mark, text.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)); }
  catch (ArgumentException error) { Message.Show(this, error.Message, T("Tags")); }
 }
}
