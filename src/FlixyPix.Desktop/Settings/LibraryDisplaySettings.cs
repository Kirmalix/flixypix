using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>Further library settings from the original: what tiles show and how a click acts, the page bar, back and
/// forward, the window place, how several tag filters combine, titles and skipped images at import, deleting into the
/// recycle bin; plus the cross-fade of the image viewer and the days of "Zuletzt gesehen", which sit on their own pages.</summary>
internal sealed class LibraryDisplaySettings
{
 internal event Action? Changed;
 internal readonly CheckBox ShowTitles = SettingsUi.Switch(), FullTitles = SettingsUi.Switch(), ShowProgress = SettingsUi.Switch(), ClickPlays = SettingsUi.Switch(),
  HoverLift = SettingsUi.Switch(), ShowPager = SettingsUi.Switch(), RememberWindow = SettingsUi.Switch(), TagsMatchAny = SettingsUi.Switch(), RecycleBin = SettingsUi.Switch(),
  ImageFade = SettingsUi.Switch();
 internal readonly NumberBox HistoryLength = new(1, GeneralPreferences.MaximumHistoryLength, 10, T("Steps"));
 internal readonly NumberBox RecentDays = new(1, HomePreferences.MaximumRecentDays, 1, T("Days"));
 internal readonly TextBox RemoveCharacters = Field(T("Characters dropped from titles")), SpaceCharacters = Field(T("Characters that become spaces")),
  SkipImages = Field(T("Skip images with these name parts"), 320);
 /// <summary>One box per tile badge (package P6).</summary>
 internal readonly Dictionary<TileBadge, CheckBox> Badges = new()
 {
  [TileBadge.Resolution] = Badge(T("Resolution")), [TileBadge.Hdr] = Badge("HDR"), [TileBadge.FrameRate] = Badge(T("60 fps")),
  [TileBadge.Codec] = Badge(T("Codec")), [TileBadge.Versions] = Badge(T("Several files")),
 };
 private static CheckBox Badge(string label) { var box = new CheckBox { Content = label, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center }; box.SetResourceReference(Control.ForegroundProperty, "Text"); return box; }

 internal LibraryDisplaySettings()
 {
  HistoryLength.SetLabel(T("Steps for back and forward")); RecentDays.SetLabel(T("Days for Recently watched"));
  foreach (var check in new[] { ShowTitles, FullTitles, ShowProgress, ClickPlays, HoverLift, ShowPager, RememberWindow, TagsMatchAny, RecycleBin, ImageFade }.Concat(Badges.Values))
  { check.Checked += (_, _) => Changed?.Invoke(); check.Unchecked += (_, _) => Changed?.Invoke(); }
  HistoryLength.Changed += () => Changed?.Invoke(); RecentDays.Changed += () => Changed?.Invoke();
  foreach (var box in new[] { RemoveCharacters, SpaceCharacters, SkipImages }) box.TextChanged += (_, _) => Changed?.Invoke();
 }

 private static TextBox Field(string name, double width = 160)
 {
  var box = new TextBox { Width = width, HorizontalAlignment = HorizontalAlignment.Left };
  AutomationProperties.SetName(box, name);
  return box;
 }

 internal UIElement[] GeneralCards() =>
 [
  SettingsUi.Card(T("Tiles"), null,
   new SettingRow(T("Title instead of file name"), T("When off, tiles and list show the file name."), ShowTitles),
   new SettingRow(T("Wrap long titles"), T("Titles take up to two lines instead of being cut off after one line."), FullTitles),
   new SettingRow(T("Progress bar"), T("A thin bar shows how much of a video you have watched."), ShowProgress),
   new SettingRow(T("Click the image to play"), T("A single click on a thumbnail opens the item; clicking its title or details still selects it."), ClickPlays),
   new SettingRow(T("Lift on hover"), T("Tiles lift slightly with a shadow."), HoverLift),
   new SettingRow(T("Page bar below the grid"), T("Page X of Y with direct page navigation (Ctrl+G)."), ShowPager),
   new SettingRow(T("Badges"), T("Shown top left on the picture. Resolution from 720p, HDR, 50 frames per second and more, video codec and the number of files of an item. HDR, frame rate and codec need the technical data (Organize ▾ → Read technical data)."),
    new WrapPanel { Children = { Badges[TileBadge.Resolution], Badges[TileBadge.Hdr], Badges[TileBadge.FrameRate], Badges[TileBadge.Codec], Badges[TileBadge.Versions] } }, wide: true)),
  SettingsUi.Card(T("Navigation"), null,
   new SettingRow(T("Back and forward"), T("Alt+← and Alt+→ remember this many views in this session."), HistoryLength),
   new SettingRow(T("Remember window position"), T("The main window opens again in the same place and size."), RememberWindow),
   new SettingRow(T("Several tags: at least one is enough"), T("Default for new tag filters; you can switch this at any time in the filter bar."), TagsMatchAny)),
  SettingsUi.Card(T("Titles and exclusions on import"), T("Applies to newly imported items, also from media folders. Images chosen individually are never skipped."),
   new SettingRow(T("Remove characters"), T("For example “[]#”: “Holiday[2019]” becomes “Holiday2019”."), RemoveCharacters),
   new SettingRow(T("Replace characters with spaces"), T("For example “_.”: “By_the.Lake” becomes “By the Lake”."), SpaceCharacters),
   new SettingRow(T("Skip images whose name contains"), T("Separate name parts with semicolons, e.g. “thumb; folder; cover”."), SkipImages)),
  SettingsUi.Card(T("Delete files"), null,
   new SettingRow(T("To the Recycle Bin"), T("“Delete file” moves the file to the Recycle Bin; when this option is off, the file is permanently deleted."), RecycleBin)),
 ];

 internal void Load(GeneralPreferences general, HomePreferences home)
 {
  ShowTitles.IsChecked = general.ShowTitles; FullTitles.IsChecked = general.FullTitles; ShowProgress.IsChecked = general.ShowProgress; ClickPlays.IsChecked = general.ClickPlays;
  HoverLift.IsChecked = general.HoverLift; ShowPager.IsChecked = general.ShowPager; HistoryLength.Value = general.HistoryLength; RememberWindow.IsChecked = general.RememberWindow;
  TagsMatchAny.IsChecked = general.TagsMatchAny; RemoveCharacters.Text = general.TitleRemoveCharacters; SpaceCharacters.Text = general.TitleSpaceCharacters;
  SkipImages.Text = string.Join("; ", general.SkipImageNames); RecycleBin.IsChecked = general.DeleteToRecycleBin; ImageFade.IsChecked = general.ImageFade;
  RecentDays.Value = home.RecentDays;
  foreach (var (badge, box) in Badges) box.IsChecked = general.TileBadges.Contains(badge);
 }

 internal (FrameworkElement Field, string Message)? FirstInvalid() =>
  !HistoryLength.IsValid ? (HistoryLength, T("Back and forward: ") + HistoryLength.Range)
  : !RecentDays.IsValid ? (RecentDays, T("Last watched: ") + RecentDays.Range)
  : null;

 internal GeneralPreferences Apply(GeneralPreferences general) => general with
 {
  ShowTitles = ShowTitles.IsChecked == true, FullTitles = FullTitles.IsChecked == true, ShowProgress = ShowProgress.IsChecked == true, ClickPlays = ClickPlays.IsChecked == true,
  HoverLift = HoverLift.IsChecked == true, ShowPager = ShowPager.IsChecked == true, HistoryLength = (int)Math.Round(HistoryLength.Value!.Value), RememberWindow = RememberWindow.IsChecked == true,
  TagsMatchAny = TagsMatchAny.IsChecked == true, TitleRemoveCharacters = new string(RemoveCharacters.Text.Distinct().ToArray()), TitleSpaceCharacters = new string(SpaceCharacters.Text.Distinct().ToArray()),
  SkipImageNames = SkipImages.Text.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
  DeleteToRecycleBin = RecycleBin.IsChecked == true, ImageFade = ImageFade.IsChecked == true,
  TileBadges = [.. Badges.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key)],
 };
}
