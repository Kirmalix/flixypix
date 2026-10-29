using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>The properties of a performer (gender, dates, looks, active years) or a studio (network) in the profile, with
/// links for both. Choice fields suggest the values already given in the library.</summary>
internal sealed partial class DirectoryWindow
{
 private readonly ComboBox gender = new(), breasts = new();
 private readonly ComboBox country = Suggesting(), ethnicity = Suggesting(), hair = Suggesting(), eyes = Suggesting(), parentStudio = Suggesting();
 private readonly TextBox birth = new(), death = new(), height = new(), weight = new(), measurements = new(), tattoos = new(), piercings = new(),
  careerStart = new(), careerEnd = new(), links = new() { AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, MinHeight = 48, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
 private readonly TextBlock age = new() { Foreground = SettingsUi.Muted, FontSize = 12, Margin = new Thickness(0, 3, 0, 0) };
 private readonly TextBlock subStudios = new() { Foreground = SettingsUi.Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
 private readonly StackPanel performerFields = new(), studioFields = new(), propertySection = new();

 internal TagProfile? ShownProfile { get { TryReadProperties(null, out var profile, out _); return profile; } }
 internal bool PerformerFieldsVisible => propertySection.Visibility == Visibility.Visible && performerFields.Visibility == Visibility.Visible;
 internal string AgeText => age.Text;
 /// <summary>For tests: fills the performer fields as a user would.</summary>
 internal void EnterPerformer(string? birthDate, string? hairColor, string? heightCm, PerformerGender? sex)
 {
  birth.Text = birthDate ?? ""; hair.Text = hairColor ?? ""; height.Text = heightCm ?? ""; gender.SelectedIndex = sex is { } value ? (int)value + 1 : 0;
 }
 internal void EnterParentStudio(string studio) => parentStudio.Text = studio;
 internal void SaveForTest() => Save();

 private static ComboBox Suggesting() => new() { IsEditable = true, IsTextSearchEnabled = true };

 private FrameworkElement ProfileSection()
 {
  gender.Items.Add("–"); foreach (var value in Enum.GetValues<PerformerGender>()) gender.Items.Add(ProfileProperties.Label(value));
  breasts.Items.Add("–"); foreach (var value in Enum.GetValues<BreastType>()) breasts.Items.Add(ProfileProperties.Label(value));
  var date = CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern;
  foreach (var (control, label) in new (Control, string)[] { (gender, T("Gender")), (birth, T("Date of birth")), (death, T("Date of death")), (country, T("Country")),
   (ethnicity, T("Ethnicity")), (hair, T("Hair color")), (eyes, T("Eye color")), (height, T("Height (cm)")), (weight, T("Weight (kg)")), (measurements, T("Measurements")),
   (breasts, T("Breasts")), (tattoos, T("Tattoos")), (piercings, T("Piercings")), (careerStart, T("Active from")), (careerEnd, T("Active until")), (links, T("Links")),
   (parentStudio, T("Belongs to")) })
   Label(label, control);
  birth.TextChanged += (_, _) => ShowAge();
  death.TextChanged += (_, _) => ShowAge();
  var dates = Pair(Field(T("Date of birth"), birth, date), Field(T("Date of death"), death));
  ((StackPanel)dates.Children[0]).Children.Add(age);
  performerFields.Children.Add(Pair(Field(T("Gender"), gender), Field(T("Country"), country)));
  performerFields.Children.Add(dates);
  performerFields.Children.Add(Pair(Field(T("Hair color"), hair), Field(T("Eye color"), eyes)));
  performerFields.Children.Add(Pair(Field(T("Ethnicity"), ethnicity), Field(T("Breasts"), breasts)));
  performerFields.Children.Add(Pair(Field(T("Height (cm)"), height), Field(T("Weight (kg)"), weight)));
  performerFields.Children.Add(Pair(Field(T("Measurements"), measurements, T("for example 34C-24-34")), Field(T("Tattoos"), tattoos)));
  performerFields.Children.Add(Pair(Field(T("Piercings"), piercings), new StackPanel()));
  performerFields.Children.Add(Pair(Field(T("Active from"), careerStart, T("year")), Field(T("Active until"), careerEnd, T("year"))));
  studioFields.Children.Add(Field(T("Belongs to"), parentStudio, T("The studio or network this studio is part of; filters can include sub-studios.")));
  studioFields.Children.Add(subStudios);
  propertySection.Children.Add(new TextBlock { Text = T("Properties"), FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 6, 0, 8) });
  propertySection.Children.Add(performerFields); propertySection.Children.Add(studioFields);
  propertySection.Children.Add(Field(T("Links"), links, T("One web address per line, such as a website or a profile page.")));
  foreach (var radio in roles) radio.Checked += (_, _) => ShowRoleFields();
  return propertySection;
 }

 private static Grid Pair(FrameworkElement left, FrameworkElement right)
 {
  var grid = new Grid();
  grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new() { Width = new GridLength(10) });
  grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
  Grid.SetColumn(right, 2); grid.Children.Add(left); grid.Children.Add(right);
  return grid;
 }

 /// <summary>The role chosen in the profile; it can differ from the saved one until saving.</summary>
 private TagRole ShownRole => Array.FindIndex(roles, r => r.IsChecked == true) is >= 0 and var index ? (TagRole)index : TagRole.Tag;

 private void ShowRoleFields()
 {
  var role = ShownRole;
  propertySection.Visibility = role == TagRole.Tag ? Visibility.Collapsed : Visibility.Visible;
  performerFields.Visibility = role == TagRole.Performer ? Visibility.Visible : Visibility.Collapsed;
  studioFields.Visibility = role == TagRole.Studio ? Visibility.Visible : Visibility.Collapsed;
 }

 private void ShowProperties(DirectoryEntry? entry)
 {
  var profile = entry?.Profile ?? new TagProfile();
  IEnumerable<string> Known(Func<TagProfile, string?> value) =>
   entries.Select(e => e.Profile is { } p ? value(p) : null).OfType<string>().Distinct(StringComparer.CurrentCultureIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase);
  country.ItemsSource = Known(p => p.Country).ToList(); ethnicity.ItemsSource = Known(p => p.Ethnicity).ToList();
  hair.ItemsSource = Known(p => p.HairColor).ToList(); eyes.ItemsSource = Known(p => p.EyeColor).ToList();
  parentStudio.ItemsSource = entries.Where(e => e.Role == TagRole.Studio && !string.Equals(e.Tag, entry?.Tag, StringComparison.OrdinalIgnoreCase))
   .Select(e => e.Tag).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).ToList();
  gender.SelectedIndex = profile.Gender is { } sex ? (int)sex + 1 : 0;
  breasts.SelectedIndex = profile.Breasts is { } type ? (int)type + 1 : 0;
  birth.Text = Date(profile.BirthDate); death.Text = Date(profile.DeathDate);
  country.Text = profile.Country ?? ""; ethnicity.Text = profile.Ethnicity ?? ""; hair.Text = profile.HairColor ?? ""; eyes.Text = profile.EyeColor ?? "";
  height.Text = Number(profile.HeightCm); weight.Text = Number(profile.WeightKg); measurements.Text = profile.Measurements ?? "";
  tattoos.Text = profile.Tattoos ?? ""; piercings.Text = profile.Piercings ?? ""; careerStart.Text = Number(profile.CareerStart); careerEnd.Text = Number(profile.CareerEnd);
  parentStudio.Text = profile.ParentStudio ?? ""; links.Text = string.Join(Environment.NewLine, profile.Links ?? []);
  var children = entry == null ? [] : entries.Where(e => e.Profile?.ParentStudio is { } parent && string.Equals(parent, entry.Tag, StringComparison.OrdinalIgnoreCase))
   .Select(e => e.Tag).Order(StringComparer.CurrentCultureIgnoreCase).ToList();
  subStudios.Text = children.Count == 0 ? "" : T("Sub-studios: {0}", string.Join(", ", children));
  subStudios.Visibility = children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
  ShowRoleFields(); ShowAge();
  static string Date(DateOnly? value) => value?.ToString("d", CultureInfo.CurrentCulture) ?? "";
  static string Number(int? value) => value?.ToString(CultureInfo.CurrentCulture) ?? "";
 }

 private void ShowAge()
 {
  age.Text = "";
  if (!TryDate(birth.Text, out var born) || born is not { } date) return;
  var profile = new TagProfile { BirthDate = date, DeathDate = TryDate(death.Text, out var died) ? died : null };
  if (profile.AgeOn(DateOnly.FromDateTime(DateTime.Now)) is { } years)
   age.Text = profile.DeathDate is null ? Tn(years, "1 year old", "{0} years old") : Tn(years, "died aged 1", "died aged {0}");
 }

 private static bool TryDate(string text, out DateOnly? value) => MainWindow.TryDate(text, out value);

 private static bool TryNumber(string text, out int? value)
 {
  value = null;
  if (string.IsNullOrWhiteSpace(text)) return true;
  if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var number)) return false;
  value = number; return true;
 }

 /// <summary>Reads the fields of the shown role; fields of the other role are kept from <paramref name="saved"/>, as are fields
 /// of later versions. False with a message when a date or number cannot be read.</summary>
 private bool TryReadProperties(TagProfile? saved, out TagProfile? profile, out string problem)
 {
  profile = null; problem = "";
  var role = ShownRole;
  if (role == TagRole.Tag) { profile = saved; return true; }
  var result = saved ?? new TagProfile();
  if (role == TagRole.Performer)
  {
   if (!TryDate(birth.Text, out var born)) { problem = T("The date of birth cannot be read; please enter it like {0}.", DateTime.Today.ToString("d", CultureInfo.CurrentCulture)); return false; }
   if (!TryDate(death.Text, out var died)) { problem = T("The date of death cannot be read; please enter it like {0}.", DateTime.Today.ToString("d", CultureInfo.CurrentCulture)); return false; }
   if (!TryNumber(height.Text, out var cm) || !TryNumber(weight.Text, out var kg)) { problem = T("Height and weight are whole numbers."); return false; }
   if (!TryNumber(careerStart.Text, out var from) || !TryNumber(careerEnd.Text, out var until)) { problem = T("Active years are whole years such as 2015."); return false; }
   result = result with
   {
    Gender = gender.SelectedIndex > 0 ? (PerformerGender)(gender.SelectedIndex - 1) : null, Breasts = breasts.SelectedIndex > 0 ? (BreastType)(breasts.SelectedIndex - 1) : null,
    BirthDate = born, DeathDate = died, Country = country.Text, Ethnicity = ethnicity.Text, HairColor = hair.Text, EyeColor = eyes.Text, HeightCm = cm, WeightKg = kg,
    Measurements = measurements.Text, Tattoos = tattoos.Text, Piercings = piercings.Text, CareerStart = from, CareerEnd = until,
   };
  }
  else result = result with { ParentStudio = parentStudio.Text };
  result = result with { Links = links.Text.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList() };
  profile = result.Normalized();
  return true;
 }
}
