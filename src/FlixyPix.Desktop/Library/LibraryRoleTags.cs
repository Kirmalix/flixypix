using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>A name of a performer or studio for completing in the details: the directory's name, its category and aliases.</summary>
internal sealed record RoleName(string Tag, string Category, TagRole Role, IReadOnlyList<string> Aliases, int Count);

// "Darsteller" and "Studio" in the details: the selected media's tags in categories of that role as chips, and an input that
// completes names and aliases from the directory. A new name goes into the role's most used category.
public partial class MainWindow
{
 private IReadOnlyDictionary<string, TagRole> _categoryRoles = new Dictionary<string, TagRole>();
 private RoleName[] _roleNames = [];
 private RoleEditor[] _roleEditors = [];

 private static RoleName[] RoleNames((string Name, string[] Tags)[] vocabulary, IReadOnlyDictionary<string, TagRole> roles, CatalogPreferences preferences, TagListEntry[] tags)
 {
  var counts = tags.ToDictionary(t => t.Tag, t => t.Count, StringComparer.OrdinalIgnoreCase);
  var aliases = preferences.TagVocabulary.Where(v => v.Aliases is { Count: > 0 }).GroupBy(v => v.Category + "\0" + v.Tag, StringComparer.OrdinalIgnoreCase)
   .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.SelectMany(v => v.Aliases!).ToArray(), StringComparer.OrdinalIgnoreCase);
  return vocabulary.Where(g => roles[g.Name] != TagRole.Tag)
   .SelectMany(g => g.Tags.Select(tag => new RoleName(tag, g.Name, roles[g.Name], aliases.GetValueOrDefault(g.Name + "\0" + tag) ?? [], counts.GetValueOrDefault(tag)))).ToArray();
 }

 private TagRole RoleOf(string category) => _categoryRoles.TryGetValue(category, out var role) ? role : TagRoles.Default(category);

 /// <summary>Where a new name of the role goes: the category of the role with the most names, otherwise "Darsteller" or "Studios".</summary>
 private string MainCategory(TagRole role) =>
  _roleNames.Where(n => n.Role == role).GroupBy(n => n.Category, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Sum(n => n.Count) + g.Count()).FirstOrDefault()?.Key
  ?? (role == TagRole.Performer ? T("Performers") : T("Studios"));

 private void UpdateRoleEditors(IReadOnlyList<MediaItem> items)
 {
  if (_roleEditors.Length == 0)
  {
   _roleEditors = [new RoleEditor(this, TagRole.Performer, T("PERFORMERS")), new RoleEditor(this, TagRole.Studio, T("STUDIO"))];
   foreach (var editor in _roleEditors) RoleTagEditors.Children.Add(editor.Panel);
  }
  RoleTagEditors.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
  foreach (var editor in _roleEditors) editor.Show(items);
 }

 private async Task ChangeCategoryTagsAsync(IReadOnlyCollection<Guid> ids, string category, IReadOnlyCollection<string> add, IReadOnlyCollection<string> remove)
 {
  if (ids.Count == 0 || add.Count + remove.Count == 0) return;
  try
  {
   var changed = await Task.Run(() => _repository.UpdateCategoryTags(ids, category, add, remove));
   Status.Text = (add.Count > 0 ? T("“{0}” assigned", string.Join(T("”, “"), add)) : T("“{0}” removed", string.Join(T("”, “"), remove))) + " · " + category
    + Tn(changed, " · 1 item changed", " · {0:N0} items changed");
   if (ViewDependsOnTags) await RefreshAsync(); else await RefreshTagsInPlaceAsync(ids);
  }
  catch (Exception ex) { Message.Show(this, ex.Message, "Tags", MessageBoxButton.OK, MessageBoxImage.Warning); }
 }

 /// <summary>For tests: the chips of the performer or studio section and adding a name through its input.</summary>
 internal IReadOnlyList<string> RoleChips(TagRole role) => _roleEditors.Single(e => e.Role == role).ChipTexts;
 internal Task AddRoleNameForTestAsync(TagRole role, string text) => _roleEditors.Single(e => e.Role == role).AddAsync(text);
 internal IReadOnlyList<string> RoleCompletionsForTest(TagRole role, string text) => _roleEditors.Single(e => e.Role == role).Complete(text).Select(c => c.Label).ToList();

 private sealed record RoleCompletion(RoleName Name, string Label)
 {
  public override string ToString() => Label;
 }

 /// <summary>One section of the details: chips, input and its completion list.</summary>
 private sealed class RoleEditor
 {
  private readonly MainWindow window;
  private readonly WrapPanel chips = new() { Margin = new Thickness(0, 0, 0, 2) };
  private readonly TextBox input = new() { Padding = new Thickness(9, 7, 9, 7) };
  private readonly ListBox completions = new() { MaxHeight = 240, BorderThickness = new Thickness(0) };
  private readonly Popup popup;
  private IReadOnlyList<MediaItem> items = [];
  internal TagRole Role { get; }
  internal StackPanel Panel { get; } = new();
  internal IReadOnlyList<string> ChipTexts => chips.Children.OfType<Button>().Select(b => (string)b.Content).ToList();

  internal RoleEditor(MainWindow window, TagRole role, string header)
  {
   this.window = window; Role = role;
   Panel.Children.Add(new TextBlock { Text = header, Style = (Style)window.FindResource("Section"), Margin = new Thickness(0, 10, 0, 6) });
   Panel.Children.Add(chips);
   var label = role == TagRole.Performer ? T("Add performer") : T("Add studio");
   System.Windows.Automation.AutomationProperties.SetName(input, label);
   input.ToolTip = T("Name or alias; Enter adds it, a new name goes into “{0}”", window.MainCategory(role));
   Panel.Children.Add(input);
   completions.SetResourceReference(Control.BackgroundProperty, "PopupBackground");
   popup = new Popup { PlacementTarget = input, Placement = PlacementMode.Bottom, AllowsTransparency = true, StaysOpen = true,
    Child = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Child = completions, MinWidth = 220 } };
   ((Border)popup.Child).SetResourceReference(Border.BackgroundProperty, "PopupBackground");
   ((Border)popup.Child).SetResourceReference(Border.BorderBrushProperty, "PopupBorder");
   Panel.Children.Add(popup);
   input.TextChanged += (_, _) => ShowCompletions();
   input.GotKeyboardFocus += (_, _) => input.ToolTip = T("Name or alias; Enter adds it, a new name goes into “{0}”", window.MainCategory(role));
   input.LostKeyboardFocus += (_, e) => { if (e.NewFocus is not DependencyObject target || !completions.IsAncestorOf(target)) popup.IsOpen = false; };
   input.PreviewKeyDown += async (_, e) =>
   {
    var open = popup.IsOpen && completions.Items.Count > 0;
    if (e.Key == Key.Down && open) { completions.SelectedIndex = Math.Min(completions.SelectedIndex + 1, completions.Items.Count - 1); e.Handled = true; }
    else if (e.Key == Key.Up && open) { completions.SelectedIndex = Math.Max(completions.SelectedIndex - 1, 0); e.Handled = true; }
    else if (e.Key == Key.Escape) { popup.IsOpen = false; e.Handled = true; }
    else if (e.Key == Key.Enter) { e.Handled = true; await AddAsync((completions.SelectedItem as RoleCompletion)?.Name.Tag ?? input.Text, completions.SelectedItem as RoleCompletion); }
   };
   completions.PreviewMouseLeftButtonUp += async (_, e) =>
   {
    if (ItemsControl.ContainerFromElement(completions, (DependencyObject)e.OriginalSource) is ListBoxItem { DataContext: RoleCompletion completion }) { e.Handled = true; await AddAsync(completion.Name.Tag, completion); }
   };
  }

  internal void Show(IReadOnlyList<MediaItem> selection)
  {
   items = selection; chips.Children.Clear(); popup.IsOpen = false; input.IsEnabled = selection.Count > 0;
   var counts = new Dictionary<(string Category, string Tag), int>(); var order = new List<(string Category, string Tag)>();
   foreach (var item in selection)
    foreach (var (category, tags) in item.TagCategories.Where(p => window.RoleOf(p.Key) == Role))
     foreach (var tag in tags.Distinct(StringComparer.OrdinalIgnoreCase))
     {
      var key = order.FirstOrDefault(k => string.Equals(k.Category, category, StringComparison.OrdinalIgnoreCase) && string.Equals(k.Tag, tag, StringComparison.OrdinalIgnoreCase));
      if (key == default) { key = (category, tag); order.Add(key); }
      counts[key] = counts.GetValueOrDefault(key) + 1;
     }
   var ids = selection.Select(i => i.Id).ToArray();
   foreach (var key in order)
   {
    var full = counts[key] == selection.Count;
    var chip = new Button { Content = full ? key.Tag + "  ×" : $"{key.Tag} · {counts[key]}/{selection.Count}", Background = full ? FullChipBrush : PartialChipBrush, Padding = new Thickness(9, 4, 9, 4), FontSize = 12,
     ToolTip = key.Category + " · " + (full ? T("Remove") : T("On {0} of {1} items · click: assign to all · right-click: remove from all", counts[key], selection.Count)) };
    chip.Click += async (_, _) => await (full ? window.ChangeCategoryTagsAsync(ids, key.Category, [], [key.Tag]) : window.ChangeCategoryTagsAsync(ids, key.Category, [key.Tag], []));
    if (!full) chip.MouseRightButtonUp += async (_, args) => { args.Handled = true; await window.ChangeCategoryTagsAsync(ids, key.Category, [], [key.Tag]); };
    chips.Children.Add(chip);
   }
  }

  /// <summary>Names of the role whose name or alias contains the text; names that start with it come first.</summary>
  internal IReadOnlyList<RoleCompletion> Complete(string text)
  {
   text = text.Trim(); if (text.Length == 0) return [];
   var present = items.Count == 0 ? [] : items.Select(i => i.TagCategories.Where(p => window.RoleOf(p.Key) == Role).SelectMany(p => p.Value).ToHashSet(StringComparer.OrdinalIgnoreCase))
    .Aggregate((a, b) => { a.IntersectWith(b); return a; });
   return window._roleNames.Where(n => n.Role == Role && !present.Contains(n.Tag))
    .Select(n => (Name: n, Alias: n.Tag.Contains(text, StringComparison.CurrentCultureIgnoreCase) ? null : n.Aliases.FirstOrDefault(a => a.Contains(text, StringComparison.CurrentCultureIgnoreCase))))
    .Where(m => m.Alias != null || m.Name.Tag.Contains(text, StringComparison.CurrentCultureIgnoreCase))
    .OrderByDescending(m => (m.Alias ?? m.Name.Tag).StartsWith(text, StringComparison.CurrentCultureIgnoreCase)).ThenByDescending(m => m.Name.Count).ThenBy(m => m.Name.Tag, StringComparer.CurrentCultureIgnoreCase)
    .Take(8).Select(m => new RoleCompletion(m.Name, m.Name.Tag + (m.Alias != null ? " · " + m.Alias : "") + "   " + m.Name.Category)).ToList();
  }

  private void ShowCompletions()
  {
   var matches = Complete(input.Text);
   completions.ItemsSource = matches; completions.SelectedIndex = -1;
   popup.IsOpen = matches.Count > 0 && input.IsKeyboardFocused;
  }

  /// <summary>Adds the chosen name in its category; a typed name or alias that the directory knows becomes that name,
  /// anything else a new name in the role's main category.</summary>
  internal async Task AddAsync(string text, RoleCompletion? chosen = null)
  {
   var typed = text.Trim(); input.Text = ""; popup.IsOpen = false;
   if (typed.Length == 0 || items.Count == 0) return;
   var name = chosen?.Name ?? window._roleNames.Where(n => n.Role == Role).FirstOrDefault(n => string.Equals(n.Tag, typed, StringComparison.CurrentCultureIgnoreCase)
    || n.Aliases.Any(a => string.Equals(a, typed, StringComparison.CurrentCultureIgnoreCase)));
   await window.ChangeCategoryTagsAsync(items.Select(i => i.Id).ToArray(), name?.Category ?? window.MainCategory(Role), [name?.Tag ?? typed], []);
   input.Focus();
  }
 }
}
