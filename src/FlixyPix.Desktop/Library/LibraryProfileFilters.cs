using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>"Eigenschaften" in the filter bar: conditions on the profiles of performers, such as hair color, height or the
/// age at recording. A medium matches when one of its performers matches each condition; the search resolves them anew,
/// so performers given a property later are found as well.</summary>
public partial class MainWindow
{
 private static readonly ProfileProperty[] FilterProperties =
 [
  ProfileProperty.Gender, ProfileProperty.AgeAtRecording, ProfileProperty.Age, ProfileProperty.Country, ProfileProperty.Ethnicity, ProfileProperty.HairColor,
  ProfileProperty.EyeColor, ProfileProperty.Height, ProfileProperty.Weight, ProfileProperty.Breasts, ProfileProperty.Tattoos, ProfileProperty.Piercings, ProfileProperty.CareerStart,
 ];

 private FrameworkElement ProfilePanel(Button owner)
 {
  var panel = new StackPanel { Width = 340 };
  panel.Children.Add(Heading(T("Properties of performers")));
  var active = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
  foreach (var condition in _filter.Profiles)
  {
   var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
   var remove = SettingsUi.SmallButton("✕"); remove.ToolTip = T("Remove condition");
   remove.Click += (_, _) => { ApplyFilter(_filter with { Profiles = _filter.Profiles.Where(p => p != condition).ToList() }); _filterPopup!.IsOpen = false; };
   DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove);
   row.Children.Add(new TextBlock { Text = MediaFilter.DescribeProfile(condition), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
   active.Children.Add(row);
  }
  if (_filter.Profiles.Count > 0) panel.Children.Add(active);

  var property = new ComboBox { Margin = new Thickness(0, 0, 0, 6) };
  foreach (var choice in FilterProperties) property.Items.Add(ProfileProperties.Label(choice));
  property.SelectedIndex = 0;
  var comparison = new ComboBox { Width = 140, Margin = new Thickness(0, 0, 6, 0) };
  var value = new ComboBox { IsEditable = true, MinWidth = 150 };
  System.Windows.Automation.AutomationProperties.SetName(property, T("Property")); System.Windows.Automation.AutomationProperties.SetName(comparison, T("Comparison"));
  System.Windows.Automation.AutomationProperties.SetName(value, T("Value"));
  var inputs = new DockPanel(); inputs.Children.Add(comparison); inputs.Children.Add(value);
  var add = new Button { Content = T("Add"), Style = (Style)FindResource("Primary"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
  var hint = SettingsUi.Hint("");
  panel.Children.Add(property); panel.Children.Add(inputs); panel.Children.Add(add); panel.Children.Add(hint);
  IReadOnlyList<DirectoryEntry> directory = [];
  (string Label, SearchComparison Value)[] comparisons = [];

  void Prepare()
  {
   var chosen = FilterProperties[Math.Max(property.SelectedIndex, 0)];
   comparisons = ProfileProperties.IsNumber(chosen)
    ? [(T("at least"), SearchComparison.GreaterOrEqual), (T("at most"), SearchComparison.LessOrEqual), (T("exactly"), SearchComparison.Equals)]
    : ProfileProperties.IsChoice(chosen) ? [(T("is"), SearchComparison.Equals)]
    : [(T("is"), SearchComparison.Equals), (T("contains"), SearchComparison.Contains), (T("is known"), SearchComparison.IsKnown), (T("is unknown"), SearchComparison.IsUnknown)];
   comparison.ItemsSource = comparisons.Select(c => c.Label).ToList(); comparison.SelectedIndex = 0;
   value.IsEditable = !ProfileProperties.IsChoice(chosen);
   value.ItemsSource = chosen switch
   {
    ProfileProperty.Gender => Enum.GetValues<PerformerGender>().Select(ProfileProperties.Label).ToList(),
    ProfileProperty.Breasts => Enum.GetValues<BreastType>().Select(ProfileProperties.Label).ToList(),
    _ when ProfileProperties.IsNumber(chosen) => [],
    _ => directory.Select(e => e.Profile?.Text(chosen)).OfType<string>().Distinct(StringComparer.CurrentCultureIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).ToList(),
   };
   value.Text = ""; if (!value.IsEditable) value.SelectedIndex = 0;
   hint.Text = chosen == ProfileProperty.AgeAtRecording ? T("Counts from the release date of the item, otherwise from its file date.")
    : directory.Count(e => e.Profile is not null) == 0 ? T("No performer has properties yet; enter them in the directory.") : "";
  }
  property.SelectionChanged += (_, _) => Prepare();
  comparison.SelectionChanged += (_, _) => value.IsEnabled = comparison.SelectedIndex < 0 || !IsPresence(comparisons[comparison.SelectedIndex].Value);
  add.Click += (_, _) =>
  {
   if (ReadProfileCondition(FilterProperties[property.SelectedIndex], comparisons[Math.Max(comparison.SelectedIndex, 0)].Value, value) is not { } condition)
   { hint.Text = T("Please enter a value; numbers as whole numbers such as 25."); return; }
   ApplyFilter(_filter with { Profiles = [.. _filter.Profiles.Where(p => p != condition), condition] });
   _filterPopup!.IsOpen = false;
  };
  panel.Loaded += async (_, _) => { directory = await Task.Run(() => _repository.GetDirectory()); Prepare(); };
  return panel;
 }

 private static bool IsPresence(SearchComparison comparison) => comparison is SearchComparison.IsKnown or SearchComparison.IsUnknown;

 /// <summary>The condition from the panel's inputs; null when the value is missing or no whole number where one is needed.</summary>
 internal static SearchCondition? ReadProfileCondition(ProfileProperty property, SearchComparison comparison, ComboBox value)
 {
  try
  {
   if (IsPresence(comparison)) return CatalogRepository.HasProfile(property, comparison).Condition;
   if (property == ProfileProperty.Gender && value.SelectedIndex >= 0)
    return CatalogRepository.HasProfile(property, comparison, Enum.GetValues<PerformerGender>()[value.SelectedIndex].ToString()).Condition;
   if (property == ProfileProperty.Breasts && value.SelectedIndex >= 0)
    return CatalogRepository.HasProfile(property, comparison, Enum.GetValues<BreastType>()[value.SelectedIndex].ToString()).Condition;
   if (ProfileProperties.IsNumber(property))
    return int.TryParse(value.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var number) && number >= 0
     ? CatalogRepository.HasProfile(property, comparison, number: number).Condition : null;
   return value.Text.Trim().Length > 0 ? CatalogRepository.HasProfile(property, comparison, value.Text.Trim()).Condition : null;
  }
  catch (ArgumentException) { return null; }
 }
}
