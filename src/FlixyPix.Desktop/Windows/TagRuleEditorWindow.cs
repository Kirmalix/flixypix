using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>Tag rule as a condition builder: each group adds its tags when all or at least one of its conditions match.
/// While editing, the footer tells how many media the rule fits and how many tags it would add.</summary>
internal sealed class TagRuleEditorWindow : Window
{
 internal sealed record Impact(int Media, int NewTags, IReadOnlyList<string> Examples);
 private readonly FilenameTagRule original;
 private readonly TextBox name = new() { Width = 360, HorizontalAlignment = HorizontalAlignment.Left };
 private readonly CheckBox enabled = SettingsUi.Switch();
 private readonly StackPanel groups = new();
 private readonly TextBlock examples = new() { TextWrapping = TextWrapping.Wrap, Foreground = SettingsUi.Muted, FontSize = 12, Margin = new Thickness(0, 4, 0, 0) };
 private readonly WindowFrame frame;
 private readonly Func<FilenameTagRule, CancellationToken, Task<Impact>>? measure;
 private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
 private CancellationTokenSource? measuring;
 internal FilenameTagRule? Result { get; private set; }
 internal Impact? LastImpact { get; private set; }
 internal Task ImpactTask { get; private set; } = Task.CompletedTask;
 internal int GroupCount => groups.Children.Count;

 internal TagRuleEditorWindow(Window owner, FilenameTagRule rule, Func<FilenameTagRule, CancellationToken, Task<Impact>>? measure = null)
 {
  Owner = owner; original = rule; this.measure = measure; Title = T("Tag rule · FlixyPix"); Width = 1000; Height = 720; MinWidth = 780; MinHeight = 480; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
  name.Text = rule.Name; enabled.IsChecked = rule.Enabled; AutomationProperties.SetName(name, T("Rule name")); AutomationProperties.SetName(enabled, T("Rule active"));
  var body = new StackPanel();
  var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
  nameRow.Children.Add(new TextBlock { Text = T("Name"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) }); nameRow.Children.Add(name);
  nameRow.Children.Add(new TextBlock { Text = T("Active"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 0, 10, 0) }); nameRow.Children.Add(enabled);
  body.Children.Add(nameRow); body.Children.Add(groups);
  var addGroup = SettingsUi.SmallButton(T("＋ Group"), T("More conditions with their own tags")); addGroup.Margin = new Thickness(0, 2, 0, 0); addGroup.HorizontalAlignment = HorizontalAlignment.Left;
  addGroup.Click += (_, _) => { AddGroup(new TagRuleGroup { Conditions = [new(TagRuleField.Filename, TagRuleComparison.Contains, "")] }); Changed(); };
  body.Children.Add(addGroup); body.Children.Add(examples);
  frame = new WindowFrame(T("Tag rule"), T("Each group adds its tags as soon as its conditions match. Existing tags are kept. Regular expressions have a time limit."), body, "");
  frame.Add(T("Save"), Save, primary: true); frame.Add(T("Cancel"), Close, cancel: true);
  Content = frame;
  var initial = rule.Groups.Count > 0 ? rule.Groups : [new TagRuleGroup { Conditions = [new(TagRuleField.FilenameWithExtension, TagRuleComparison.Contains, rule.FilenameContains)], Tags = rule.Tags.ToList() }];
  foreach (var group in initial) AddGroup(group);
  name.TextChanged += (_, _) => Changed(); enabled.Click += (_, _) => Changed();
  timer.Tick += (_, _) => { timer.Stop(); ImpactTask = MeasureAsync(); };
  Loaded += (_, _) => ImpactTask = MeasureAsync();
  Closed += (_, _) => { timer.Stop(); measuring?.Cancel(); };
 }

 private void Changed() { timer.Stop(); timer.Start(); }
 private void AddGroup(TagRuleGroup group)
 {
  RuleGroup? card = null;
  card = new RuleGroup(group, Changed, () => { groups.Children.Remove(card!); Changed(); });
  groups.Children.Add(card);
 }
 private FilenameTagRule Read()
 {
  var value = original with { Name = name.Text.Trim(), Enabled = enabled.IsChecked == true, FilenameContains = "", Tags = [], Groups = groups.Children.OfType<RuleGroup>().Select(g => g.Read()).ToList() };
  _ = CatalogRepository.EvaluateTagRule(new() { Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Regelprüfung.mp4") }, value);
  return value;
 }

 private async Task MeasureAsync()
 {
  measuring?.Cancel(); var work = measuring = new CancellationTokenSource();
  FilenameTagRule rule;
  try { rule = Read(); }
  catch (ArgumentException error) { LastImpact = null; frame.Status.Text = "⚠ " + error.Message; frame.Status.Foreground = SettingsUi.Error; examples.Text = ""; return; }
  frame.Status.Foreground = SettingsUi.Muted;
  if (measure == null) { frame.Status.Text = ""; return; }
  frame.Status.Text = T("Checking the effect …");
  try
  {
   var impact = await measure(rule with { Enabled = true }, work.Token);
   if (work.Token.IsCancellationRequested) return;
   LastImpact = impact;
   frame.Status.Text = Tn(impact.Media, "Matches 1 item", "Matches {0:N0} items") + Tn(impact.NewTags, " · 1 new tag", " · {0:N0} new tags") + (enabled.IsChecked == true ? "" : T(" · currently inactive"));
   examples.Text = impact.Examples.Count == 0 ? "" : T("For example: ") + string.Join(" · ", impact.Examples);
  }
  catch (OperationCanceledException) { }
  catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.Text.RegularExpressions.RegexMatchTimeoutException) { frame.Status.Text = "⚠ " + error.Message; }
 }

 private void Save()
 {
  try { Result = Read(); DialogResult = true; }
  catch (ArgumentException error) { frame.Status.Text = "⚠ " + error.Message; frame.Status.Foreground = SettingsUi.Error; }
 }

 private sealed class RuleGroup : Border
 {
  private readonly ComboBox match = new() { MinWidth = 260, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
  private readonly TextBox tags = new() { MinWidth = 260, VerticalContentAlignment = VerticalAlignment.Center };
  private readonly StackPanel rows = new() { Margin = new Thickness(0, 8, 0, 0) };
  private readonly Action changed;
  internal RuleGroup(TagRuleGroup group, Action changed, Action remove)
  {
   this.changed = changed;
   Background = SettingsUi.CardBackground; BorderBrush = SettingsUi.CardBorder; BorderThickness = new Thickness(1); CornerRadius = new CornerRadius(8); Padding = new Thickness(14, 12, 14, 12); Margin = new Thickness(0, 0, 0, 10);
   match.ItemsSource = new[] { T("All conditions are met"), T("At least one condition is met") }; match.SelectedIndex = group.Match == TagRuleMatch.Any ? 1 : 0; AutomationProperties.SetName(match, T("Combination"));
   tags.Text = string.Join("; ", group.Tags); AutomationProperties.SetName(tags, T("Tags that are added"));
   var drop = new Button { Content = "", Style = (Style)Application.Current.Resources["IconButton"], ToolTip = T("Remove group"), FontSize = 13, Padding = new Thickness(7) };
   AutomationProperties.SetName(drop, T("Remove group")); drop.Click += (_, _) => remove();
   var header = new DockPanel(); DockPanel.SetDock(drop, Dock.Right); header.Children.Add(drop);
   header.Children.Add(match);
   var add = SettingsUi.SmallButton(T("＋ Condition")); add.Margin = new Thickness(0, 0, 0, 10); add.HorizontalAlignment = HorizontalAlignment.Left;
   add.Click += (_, _) => { AddRow(new(TagRuleField.Filename, TagRuleComparison.Contains, "")); changed(); };
   var result = new DockPanel(); var label = Text(T("Add these tags")); label.Margin = new Thickness(0, 0, 10, 0); label.FontWeight = FontWeights.SemiBold;
   DockPanel.SetDock(label, Dock.Left); result.Children.Add(label); result.Children.Add(tags);
   Child = new StackPanel { Children = { header, rows, add, result, new TextBlock { Text = T("Separate several tags with semicolons."), FontSize = 11, Foreground = SettingsUi.Muted, Margin = new Thickness(0, 3, 0, 0) } } };
   foreach (var condition in group.Conditions) AddRow(condition);
   match.SelectionChanged += (_, _) => changed(); tags.TextChanged += (_, _) => changed();
  }
  private static TextBlock Text(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center };
  private void AddRow(TagRuleCondition condition)
  {
   RuleRow? row = null;
   row = new RuleRow(condition, changed, () => { rows.Children.Remove(row!); changed(); });
   rows.Children.Add(row);
  }
  internal TagRuleGroup Read() => new()
  {
   Match = match.SelectedIndex == 1 ? TagRuleMatch.Any : TagRuleMatch.All, Conditions = rows.Children.OfType<RuleRow>().Select(r => r.Read()).ToList(),
   Tags = tags.Text.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList(),
  };
 }

 private sealed class RuleRow : DockPanel
 {
  private static readonly string[] FieldLabels = [T("File name without extension"), T("File name with extension"), T("Path without drive"), T("Title"), T("Existing tag")];
  private readonly ComboBox fieldChoice = new() { ItemsSource = FieldLabels, Width = 190, Margin = new Thickness(0, 0, 6, 0) };
  private readonly ComboBox comparison = new() { Width = 170, Margin = new Thickness(0, 0, 6, 0) };
  private readonly TextBox value = new() { Width = 300, Margin = new Thickness(0, 0, 6, 0), VerticalContentAlignment = VerticalAlignment.Center };
  internal RuleRow(TagRuleCondition condition, Action changed, Action remove)
  {
   Margin = new Thickness(0, 0, 0, 6); LastChildFill = false;
   AutomationProperties.SetName(fieldChoice, T("Field")); AutomationProperties.SetName(comparison, T("Comparison")); AutomationProperties.SetName(value, T("Value"));
   fieldChoice.SelectedIndex = (int)condition.Field; Fit(condition.Comparison); value.Text = condition.Value;
   var drop = new Button { Content = "", Style = (Style)Application.Current.Resources["IconButton"], ToolTip = T("Remove condition"), FontSize = 11, Padding = new Thickness(7) };
   AutomationProperties.SetName(drop, T("Remove condition")); drop.Click += (_, _) => remove();
   foreach (var element in new UIElement[] { fieldChoice, comparison, value, drop }) { SetDock(element, Dock.Left); Children.Add(element); }
   fieldChoice.SelectionChanged += (_, _) => { Fit(Comparison); changed(); };
   comparison.SelectionChanged += (_, _) => { UpdateHint(); changed(); }; value.TextChanged += (_, _) => changed();
  }
  private sealed record Option(TagRuleComparison Value, string Label) { public override string ToString() => Label; }
  private TagRuleComparison Comparison => comparison.SelectedItem is Option choice ? choice.Value : TagRuleComparison.Contains;
  /// <summary>An existing tag is compared with "has one of the tags"; texts with "contains" or a regular expression.</summary>
  private void Fit(TagRuleComparison wanted)
  {
   Option[] choices = fieldChoice.SelectedIndex == (int)TagRuleField.Tag
    ? [new(TagRuleComparison.HasTag, T("has one of the tags"))]
    : [new(TagRuleComparison.Contains, T("contains")), new(TagRuleComparison.Matches, T("matches the pattern (regex)"))];
   comparison.ItemsSource = choices;
   comparison.SelectedItem = choices.FirstOrDefault(c => c.Value == wanted) ?? choices[0];
   UpdateHint();
  }
  private void UpdateHint() => value.ToolTip = Comparison switch
  {
   TagRuleComparison.HasTag => T("Separate tags with semicolons"),
   TagRuleComparison.Matches => T("Regular expression, case-insensitive"),
   _ => T("Text, case-insensitive"),
  };
  internal TagRuleCondition Read() => new((TagRuleField)Math.Max(0, fieldChoice.SelectedIndex), Comparison, value.Text);
 }
}
