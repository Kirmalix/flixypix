using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>Advanced search as a condition builder: groups that match when all, at least one or none of their conditions
/// apply, rows with a field, a comparison fitting the field and an input in everyday units (stars, minutes, MB, dates).
/// The number of hits follows every change.</summary>
internal sealed class AdvancedSearchWindow : Window
{
 private readonly WindowFrame frame;
 private readonly Func<SearchExpression?, CancellationToken, Task<long>>? count;
 private readonly DispatcherTimer countTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
 private CancellationTokenSource? counting;
 internal ConditionGroup RootGroup { get; }
 internal SearchExpression? Result { get; private set; }
 internal long? LiveCount { get; private set; }
 internal Task CountTask { get; private set; } = Task.CompletedTask;
 internal static IReadOnlyList<string> Tags { get; set; } = [];
 internal static IReadOnlyList<string> Categories { get; set; } = [];

 internal AdvancedSearchWindow(Window owner, SearchExpression? expression, Func<SearchExpression?, CancellationToken, Task<long>>? count = null)
 {
  Owner = owner; Title = T("Advanced search · FlixyPix"); Width = 1000; Height = 720; MinWidth = 760; MinHeight = 460; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
  this.count = count;
  RootGroup = new ConditionGroup(Normalize(expression), 0, Changed, null);
  frame = new WindowFrame(T("Advanced search"), T("Combine conditions: a group applies when all, at least one or none of its conditions hold. Groups can be nested."), RootGroup, "");
  frame.Add(T("Apply search"), Apply, primary: true); frame.Add(T("Cancel"), Close, cancel: true);
  var clear = frame.Add(T("Remove all"), () => { RootGroup.Clear(); Changed(); }); frame.Buttons.Children.Remove(clear); frame.Buttons.Children.Insert(0, clear);
  Content = frame;
  countTimer.Tick += (_, _) => { countTimer.Stop(); CountTask = CountAsync(); };
  Loaded += (_, _) => CountTask = CountAsync();
  Closed += (_, _) => { countTimer.Stop(); counting?.Cancel(); };
 }

 /// <summary>The root is always a group; a single condition or a negated group become one.</summary>
 private static SearchExpression Normalize(SearchExpression? expression) => expression switch
 {
  null => SearchExpression.All(),
  { Operator: SearchOperator.Condition } => SearchExpression.All(expression),
  _ => expression,
 };

 private void Changed() { countTimer.Stop(); countTimer.Start(); }

 private async Task CountAsync()
 {
  counting?.Cancel(); var work = counting = new CancellationTokenSource();
  SearchExpression? expression;
  try { expression = RootGroup.Read(); if (expression != null) CatalogRepository.ValidateSearchExpression(expression); }
  catch (ArgumentException error) { LiveCount = null; frame.Status.Text = "⚠ " + error.Message; frame.Status.Foreground = SettingsUi.Error; return; }
  frame.Status.Foreground = SettingsUi.Muted;
  if (count == null) { frame.Status.Text = expression == null ? T("No condition: all items") : ""; return; }
  frame.Status.Text = T("Counting matches …");
  try
  {
   var hits = await count(expression, work.Token);
   if (work.Token.IsCancellationRequested) return;
   LiveCount = hits; frame.Status.Text = Tn(hits, "1 match", "{0:N0} matches") + (expression == null ? T(" · no condition") : "");
  }
  catch (OperationCanceledException) { }
  catch (Exception error) when (error is InvalidOperationException or ArgumentException) { frame.Status.Text = "⚠ " + error.Message; }
 }

 private void Apply()
 {
  try { var value = RootGroup.Read(); if (value != null) CatalogRepository.ValidateSearchExpression(value); Result = value; DialogResult = true; }
  catch (ArgumentException error) { frame.Status.Text = "⚠ " + error.Message; frame.Status.Foreground = SettingsUi.Error; }
 }

 private sealed record Choice<T>(T Value, string Label) { public override string ToString() => Label; }
 private enum GroupMode { All, Any, None }
 private enum ValueKind { Text, Number, Date, Flag, MediaKind, None }

 /// <summary>One field with its label, input kind and the factor from the shown unit to the stored base unit.</summary>
 private sealed record FieldSpec(SearchField Field, string Label, ValueKind Kind, string Unit = "", double Factor = 1, bool Suggestions = false);
 private static readonly FieldSpec[] Fields =
 [
  new(SearchField.Text, T("All text fields"), ValueKind.Text), new(SearchField.Title, T("Title"), ValueKind.Text),
  new(SearchField.Filename, T("File name"), ValueKind.Text), new(SearchField.Extension, T("File extension"), ValueKind.Text),
  new(SearchField.Directory, T("Folder"), ValueKind.Text), new(SearchField.Path, T("File path"), ValueKind.Text), new(SearchField.Description, T("Description"), ValueKind.Text),
  new(SearchField.Tag, T("Tag"), ValueKind.Text, Suggestions: true), new(SearchField.CategorizedTag, T("Tag in category"), ValueKind.Text, Suggestions: true),
  new(SearchField.TagCategory, T("Tag category"), ValueKind.Text), new(SearchField.Category, T("Media category"), ValueKind.Text, Suggestions: true),
  new(SearchField.Kind, T("Media type"), ValueKind.MediaKind), new(SearchField.Favorite, T("Favorite"), ValueKind.Flag), new(SearchField.WatchLater, T("Watch later"), ValueKind.Flag),
  new(SearchField.Rating, T("Rating"), ValueKind.Number, T("Stars"), 2), new(SearchField.Duration, T("Duration"), ValueKind.Number, "min", 60),
  new(SearchField.FileSize, T("File size"), ValueKind.Number, "MB", 1024 * 1024), new(SearchField.Width, T("Width"), ValueKind.Number, "px"), new(SearchField.Height, T("Height"), ValueKind.Number, "px"),
  new(SearchField.Portrait, T("Portrait"), ValueKind.Flag), new(SearchField.Bitrate, T("Bit rate"), ValueKind.Number, "kbit/s"),
  new(SearchField.Lines, T("Lines (shorter side)"), ValueKind.Number, "px"), new(SearchField.VideoCodec, T("Video codec"), ValueKind.Text),
  new(SearchField.FrameRate, T("Frame rate"), ValueKind.Number, "fps"), new(SearchField.Hdr, "HDR", ValueKind.Flag), new(SearchField.Versions, T("Number of files"), ValueKind.Number),
  new(SearchField.PlaybackPosition, T("Playback position"), ValueKind.Number, "min", 60), new(SearchField.PlayCount, T("Plays"), ValueKind.Number), new(SearchField.ViewCount, T("Image views"), ValueKind.Number),
  new(SearchField.WatchCount, T("Times watched"), ValueKind.Number), new(SearchField.OCount, T("O counter"), ValueKind.Number),
  new(SearchField.AddedAt, T("Import date"), ValueKind.Date), new(SearchField.ReleaseDate, T("Release date"), ValueKind.Date),
 ];
 private static Choice<SearchComparison>[] Comparisons(ValueKind kind) => kind switch
 {
  ValueKind.Text => [new(SearchComparison.Contains, T("contains")), new(SearchComparison.Equals, T("is")), new(SearchComparison.NotEquals, T("is not")), new(SearchComparison.StartsWith, T("starts with")),
   new(SearchComparison.EndsWith, T("ends with")), new(SearchComparison.IsKnown, T("is set")), new(SearchComparison.IsUnknown, T("missing"))],
  ValueKind.Number or ValueKind.Date => [new(SearchComparison.GreaterOrEqual, T("at least")), new(SearchComparison.LessOrEqual, T("at most")), new(SearchComparison.Equals, T("exactly")),
   new(SearchComparison.NotEquals, T("not")), new(SearchComparison.GreaterThan, T("more than")), new(SearchComparison.LessThan, T("less than")), new(SearchComparison.IsKnown, T("is set")), new(SearchComparison.IsUnknown, T("missing"))],
  _ => [new(SearchComparison.Equals, T("is")), new(SearchComparison.NotEquals, T("is not"))],
 };
 private static string Format(double value) => value.ToString("0.##", CultureInfo.CurrentCulture);

 internal sealed class ConditionGroup : Border
 {
  private readonly ComboBox mode = new() { MinWidth = 260, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
  private readonly StackPanel items = new() { Margin = new Thickness(0, 8, 0, 0) };
  private readonly Action changed;
  private readonly int depth;
  internal int ConditionCount => items.Children.OfType<ConditionRow>().Count() + items.Children.OfType<ConditionGroup>().Sum(g => g.ConditionCount);

  internal ConditionGroup(SearchExpression expression, int depth, Action changed, Action<ConditionGroup>? remove)
  {
   this.depth = depth; this.changed = changed;
   Background = depth == 0 ? SettingsUi.CardBackground : Theme.Brush("WindowBackground"); BorderBrush = SettingsUi.CardBorder; BorderThickness = new Thickness(1); CornerRadius = new CornerRadius(8);
   Padding = new Thickness(14, 12, 14, 12); Margin = new Thickness(0, depth == 0 ? 0 : 6, 0, 6);
   mode.ItemsSource = new[] { new Choice<GroupMode>(GroupMode.All, T("All conditions are met")), new(GroupMode.Any, T("At least one condition is met")), new(GroupMode.None, T("No conditions are met")) };
   AutomationProperties.SetName(mode, T("Group operator"));
   var (groupMode, children) = expression.Operator switch
   {
    SearchOperator.Any => (GroupMode.Any, expression.Children),
    SearchOperator.Not when expression.Children is [{ Operator: SearchOperator.Any } any] => (GroupMode.None, any.Children),
    SearchOperator.Not => (GroupMode.None, expression.Children),
    _ => (GroupMode.All, expression.Children),
   };
   mode.SelectedIndex = (int)groupMode; mode.SelectionChanged += (_, _) => changed();
   var header = new DockPanel();
   if (remove != null)
   {
    var drop = new Button { Content = "", Style = (Style)Application.Current.Resources["IconButton"], ToolTip = T("Remove group"), FontSize = 13, Padding = new Thickness(7) };
    AutomationProperties.SetName(drop, T("Remove group")); drop.Click += (_, _) => remove(this); DockPanel.SetDock(drop, Dock.Right); header.Children.Add(drop);
   }
   header.Children.Add(mode);
   var add = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
   var addCondition = SettingsUi.SmallButton(T("＋ Condition")); addCondition.Margin = new Thickness(0, 0, 6, 0);
   addCondition.Click += (_, _) => { AddCondition(new SearchCondition { Field = SearchField.Title, Comparison = SearchComparison.Contains }); changed(); };
   var addGroup = SettingsUi.SmallButton(T("＋ Group"), T("A nested group, for example for “A and (B or C)”")); addGroup.Margin = new Thickness(0);
   addGroup.Click += (_, _) => { if (depth >= 6) return; var group = AddGroup(SearchExpression.Any(SearchExpression.Where(new SearchCondition { Field = SearchField.Tag, Comparison = SearchComparison.Equals }))); changed(); };
   addGroup.IsEnabled = depth < 6;
   add.Children.Add(addCondition); add.Children.Add(addGroup);
   Child = new StackPanel { Children = { header, items, add } };
   foreach (var child in children)
    if (child.Operator == SearchOperator.Condition && child.Condition != null) AddCondition(child.Condition); else AddGroup(child);
  }
  private static TextBlock Text(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center };

  internal ConditionRow AddCondition(SearchCondition condition)
  {
   var row = new ConditionRow(condition, changed, r => { items.Children.Remove(r); changed(); });
   items.Children.Add(row); return row;
  }
  internal ConditionGroup AddGroup(SearchExpression expression)
  {
   var group = new ConditionGroup(expression, depth + 1, changed, g => { items.Children.Remove(g); changed(); });
   items.Children.Add(group); return group;
  }
  internal void Clear() => items.Children.Clear();
  internal void SetMode(int index) => mode.SelectedIndex = index;

  /// <summary>The group as an expression, or null when it holds no conditions (no restriction).</summary>
  internal SearchExpression? Read()
  {
   var children = items.Children.OfType<UIElement>().Select(e => e switch { ConditionRow row => row.Read(), ConditionGroup group => group.Read(), _ => null }).OfType<SearchExpression>().ToArray();
   if (children.Length == 0) return null;
   return (GroupMode)Math.Max(0, mode.SelectedIndex) switch
   {
    GroupMode.Any => children.Length == 1 ? children[0] : SearchExpression.Any(children),
    GroupMode.None => SearchExpression.Not(children.Length == 1 ? children[0] : SearchExpression.Any(children)),
    _ => children.Length == 1 && depth > 0 ? children[0] : SearchExpression.All(children),
   };
  }
 }

 internal sealed class ConditionRow : DockPanel
 {
  private readonly ComboBox fieldChoice = new() { Width = 170, Margin = new Thickness(0, 0, 6, 0) };
  private readonly ComboBox comparison = new() { Width = 135, Margin = new Thickness(0, 0, 6, 0) };
  private readonly TextBox text = new() { Width = 220, Margin = new Thickness(0, 0, 2, 0), VerticalContentAlignment = VerticalAlignment.Center };
  private readonly TextBox category = new() { Width = 150, Margin = new Thickness(0, 0, 6, 0), ToolTip = T("Tag category"), VerticalContentAlignment = VerticalAlignment.Center };
  private readonly Button suggest = new() { Content = "", FontSize = 10, Padding = new Thickness(7), Margin = new Thickness(0, 0, 6, 0), ToolTip = T("Suggestions from the library") };
  private IReadOnlyList<string> suggestions = [];
  private readonly TextBox number = new() { Width = 90, Margin = new Thickness(0, 0, 4, 0), TextAlignment = TextAlignment.Right };
  private readonly TextBlock unit = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Foreground = SettingsUi.Muted };
  private readonly DatePicker date = new() { Width = 150, Margin = new Thickness(0, 0, 6, 0) };
  private readonly ComboBox kind = new() { Width = 120, Margin = new Thickness(0, 0, 6, 0) };
  private readonly Action changed;
  private bool loading = true;

  internal ConditionRow(SearchCondition condition, Action changed, Action<ConditionRow> remove)
  {
   this.changed = changed; Margin = new Thickness(0, 0, 0, 6); LastChildFill = false;
   AutomationProperties.SetName(fieldChoice, T("Field")); AutomationProperties.SetName(comparison, T("Comparison")); AutomationProperties.SetName(text, T("Value")); AutomationProperties.SetName(number, T("Number")); AutomationProperties.SetName(date, T("Date"));
   fieldChoice.ItemsSource = Fields; fieldChoice.DisplayMemberPath = nameof(FieldSpec.Label);
   kind.ItemsSource = new[] { new Choice<string>("Image", T("Image")), new("Video", T("VIDEO")), new("Audio", T("Audio")) };
   suggest.Style = (Style)Application.Current.Resources["IconButton"]; AutomationProperties.SetName(suggest, T("Suggestions"));
   suggest.Click += (_, _) => Suggest();
   var drop = new Button { Content = "", Style = (Style)Application.Current.Resources["IconButton"], ToolTip = T("Remove condition"), FontSize = 11, Padding = new Thickness(7) };
   AutomationProperties.SetName(drop, T("Remove condition")); drop.Click += (_, _) => remove(this);
   foreach (var element in new UIElement[] { fieldChoice, comparison, category, text, suggest, number, unit, date, kind, drop }) { SetDock(element, Dock.Left); Children.Add(element); }
   fieldChoice.SelectedItem = Fields.FirstOrDefault(f => f.Field == condition.Field) ?? Fields[1];
   ApplyField();
   var spec = Spec;
   // Booleans are shown as "is/is not favourite": a stored "= false" becomes "is not".
   var stored = spec.Kind == ValueKind.Flag && condition.Flag == false ? (condition.Comparison == SearchComparison.Equals ? SearchComparison.NotEquals : SearchComparison.Equals) : condition.Comparison;
   comparison.SelectedItem = ((Choice<SearchComparison>[])comparison.ItemsSource).FirstOrDefault(c => c.Value == stored) ?? ((Choice<SearchComparison>[])comparison.ItemsSource)[0];
   text.Text = condition.Text ?? ""; category.Text = condition.TagCategory ?? "";
   if (condition.Number is { } value) number.Text = Format(value / spec.Factor);
   if (condition.Date is { } day) date.SelectedDate = day.LocalDateTime.Date;
   kind.SelectedItem = ((Choice<string>[])kind.ItemsSource).FirstOrDefault(k => string.Equals(k.Value, condition.Text, StringComparison.OrdinalIgnoreCase)) ?? ((Choice<string>[])kind.ItemsSource)[1];
   UpdateInputs(); loading = false;
   fieldChoice.SelectionChanged += (_, _) => { ApplyField(); UpdateInputs(); Changed(); };
   comparison.SelectionChanged += (_, _) => { UpdateInputs(); Changed(); };
   foreach (var box in new[] { text, category }) box.TextChanged += (_, _) => Changed();
   number.TextChanged += (_, _) => Changed(); date.SelectedDateChanged += (_, _) => Changed(); kind.SelectionChanged += (_, _) => Changed();
  }
  private FieldSpec Spec => (FieldSpec)fieldChoice.SelectedItem;
  private SearchComparison Comparison => (comparison.SelectedItem as Choice<SearchComparison>)?.Value ?? SearchComparison.Contains;
  private void Changed() { if (!loading) changed(); }
  internal void Set(SearchField chosen, SearchComparison compare, string value)
  {
   fieldChoice.SelectedItem = Fields.First(f => f.Field == chosen);
   comparison.SelectedItem = ((Choice<SearchComparison>[])comparison.ItemsSource).First(c => c.Value == compare);
   if (Spec.Kind == ValueKind.Number) number.Text = value; else text.Text = value;
  }

  /// <summary>Up to 30 tags or categories of the library that contain the typed text, most used first.</summary>
  private void Suggest()
  {
   var typed = text.Text.Trim();
   var menu = new ContextMenu { PlacementTarget = text, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
   foreach (var value in suggestions.Where(s => typed.Length == 0 || s.Contains(typed, StringComparison.OrdinalIgnoreCase)).Take(30))
   { var item = new MenuItem { Header = value }; item.Click += (_, _) => { text.Text = value; text.Focus(); text.CaretIndex = value.Length; }; menu.Items.Add(item); }
   if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = T("No matching suggestions"), IsEnabled = false });
   menu.IsOpen = true;
  }

  private void ApplyField()
  {
   var previous = Comparison;
   var choices = Comparisons(Spec.Kind);
   comparison.ItemsSource = choices; comparison.SelectedItem = choices.FirstOrDefault(c => c.Value == previous) ?? choices[0];
   suggestions = Spec.Field switch { SearchField.Tag or SearchField.CategorizedTag => Tags, SearchField.Category => Categories, _ => [] };
   unit.Text = Spec.Unit;
  }
  private void UpdateInputs()
  {
   var presence = Comparison is SearchComparison.IsKnown or SearchComparison.IsUnknown;
   var spec = Spec;
   text.Visibility = !presence && spec.Kind == ValueKind.Text ? Visibility.Visible : Visibility.Collapsed;
   suggest.Visibility = text.Visibility == Visibility.Visible && suggestions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
   category.Visibility = spec.Field == SearchField.CategorizedTag ? Visibility.Visible : Visibility.Collapsed;
   number.Visibility = unit.Visibility = !presence && spec.Kind == ValueKind.Number ? Visibility.Visible : Visibility.Collapsed;
   date.Visibility = !presence && spec.Kind == ValueKind.Date ? Visibility.Visible : Visibility.Collapsed;
   kind.Visibility = spec.Kind == ValueKind.MediaKind ? Visibility.Visible : Visibility.Collapsed;
  }

  /// <summary>The condition in base units; throws with a readable message when the input is incomplete.</summary>
  internal SearchExpression Read()
  {
   var spec = Spec; var compare = Comparison;
   var condition = new SearchCondition { Field = spec.Field, Comparison = compare };
   if (spec.Field == SearchField.CategorizedTag)
   {
    if (category.Text.Trim().Length == 0) throw new ArgumentException(T("“Tag in category”: please enter the category."));
    condition = condition with { TagCategory = category.Text.Trim() };
   }
   if (compare is SearchComparison.IsKnown or SearchComparison.IsUnknown) return SearchExpression.Where(condition);
   switch (spec.Kind)
   {
    case ValueKind.Number:
     if (!double.TryParse(number.Text.Trim().Replace('.', ','), NumberStyles.Float, CultureInfo.GetCultureInfo("de-DE"), out var value) || !double.IsFinite(value) || value < 0)
      throw new ArgumentException(T("“{0}”: please enter a number", spec.Label) + (spec.Unit.Length > 0 ? $" ({spec.Unit})." : "."));
     return SearchExpression.Where(condition with { Number = value * spec.Factor });
    case ValueKind.Date:
     if (date.SelectedDate is not { } day) throw new ArgumentException(T("“Import date”: please choose a date."));
     return SearchExpression.Where(condition with { Date = new DateTimeOffset(day.Date, TimeZoneInfo.Local.GetUtcOffset(day.Date)) });
    case ValueKind.Flag: return SearchExpression.Where(condition with { Flag = true });
    case ValueKind.MediaKind: return SearchExpression.Where(condition with { Text = (kind.SelectedItem as Choice<string>)?.Value ?? "Video" });
    default:
     if (text.Text.Trim().Length == 0) throw new ArgumentException(T("“{0}”: please enter a value.", spec.Label));
     return SearchExpression.Where(condition with { Text = text.Text.Trim() });
   }
  }
 }
}
