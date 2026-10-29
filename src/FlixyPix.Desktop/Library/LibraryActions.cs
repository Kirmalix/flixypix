using System.IO;
using System.Windows;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 private void OrganizeMenu(object sender, RoutedEventArgs e) => MenuBelow(sender, [.. Specs([(T("Directory: tags, performers, studios …"),
  () => ShowDirectory()), (T("Scene wall: all scenes and bookmarks"), () => ShowSceneWall()), (T("Statistics"), () => ShowStatistics()), (T("Read technical data"), () => ReadTechnicalData())]),
  MenuSpec.Submenu(T("Search / vocabulary / folder favorites"), Specs(OrganizationEntries())), MenuSpec.Separator, .. Specs([(T("Create tag group rule"), NewGroupRule),
  (T("Import original tag rules"), ImportOriginalRules), (T("Import binary original tag rule …"), ImportBinaryRule),
  (T("Export original tag rules"), ExportOriginalRules), (T("Save as smart collection …"), SaveSearch), (T("Open smart collection …"),
  OpenSavedSearch), (T("Delete smart collection …"), DeleteSavedSearch), (T("Set favorite thresholds …"), FavoriteThresholds),
  (T("Create file name rule …"), NewTagRule), (T("Edit file name rule …"), EditTagRule), (T("Delete file name rule …"), DeleteTagRule),
  (T("Apply tags from rules"), ApplyTagRules)])]);
 private void OpenSavedSearch()
 {
  var searches = _repository.GetSavedSearches();
  if (searches.Count == 0) { Message.Show(this, T("No searches have been saved yet.")); return; }
  Menu(searches.Select(search => (search.Name, (Action)(() => ApplySavedSearch(search)))).ToArray());
 }
 private void DeleteSavedSearch()
 {
  var searches = _repository.GetSavedSearches();
  if (searches.Count == 0) { Message.Show(this, T("No searches have been saved yet.")); return; }
  Menu(searches.Select(search => (search.Name, (Action)(() => { if (Message.Show(this, T("Delete saved search “{0}”?", search.Name),
   T("Delete search"), MessageBoxButton.YesNo, MessageBoxImage.Question,
   MessageBoxResult.No) == MessageBoxResult.Yes) _repository.DeleteSavedSearch(search.Id); }))).ToArray());
 }
 private void NewTagRule() => EditRule(new FilenameTagRule());
 private void EditTagRule()
 {
  var rules = _repository.GetFilenameTagRules();
  if (rules.Count == 0) { Message.Show(this, T("Create a file name rule first.")); return; }
  Menu(rules.Select(rule => (rule.Name + (rule.Enabled ? "" : T(" · paused")), (Action)(() => EditRule(rule)))).ToArray());
 }
 private void EditRule(FilenameTagRule rule)
 {
  if (rule.Groups.Count > 0) { EditGroupRule(rule); return; }
  var input = Dialogs.Fields(this, T("File name rule"), ("Name", rule.Name), (T("File name contains (case-insensitive)"), rule.FilenameContains), (T("Tags · separate with commas"), string.Join(", ", rule.Tags)), (T("Active · yes or no"), rule.Enabled ? T("yes") : T("no")));
  if (input == null) return;
  if (string.IsNullOrWhiteSpace(input[0]) || string.IsNullOrWhiteSpace(input[1]) || string.IsNullOrWhiteSpace(input[2])) { Message.Show(this, T("Name, file name and at least one tag are required.")); return; }
  _repository.UpsertFilenameTagRule(rule with { Name = input[0], FilenameContains = input[1], Tags = input[2].Split(',',
   StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
   Enabled = input[3].Trim().Equals(T("yes"), StringComparison.OrdinalIgnoreCase) });
  Status.Text = T("Rule saved. Run it under Organize → Apply tags from rules.");
 }
 private void DeleteTagRule()
 {
  var rules = _repository.GetFilenameTagRules();
  if (rules.Count == 0) { Message.Show(this, T("No rules have been saved yet.")); return; }
  Menu(rules.Select(rule => (rule.Name, (Action)(() => { if (Message.Show(this, T("Delete rule “{0}”? Tags already assigned will be kept.",
   rule.Name), T("Delete rule"), MessageBoxButton.YesNo, MessageBoxImage.Question,
   MessageBoxResult.No) == MessageBoxResult.Yes) _repository.DeleteFilenameTagRule(rule.Id); }))).ToArray());
 }
 private async void ApplyTagRules()
 {
  await RunOperation(T("Tags from file names"), async token => { var count = await Task.Run(() => _repository.ApplyFilenameTagRules(cancellationToken: token), token); Message.Show(this, Tn(count, "Tags added to 1 item.", "Tags added to {0} items."), T("File name rules")); });
 }
 private async void FindDuplicates()
 {
  await RunOperation(T("Search for identical files"), async token =>
  {
   var items = await Task.Run(() => _repository.Search(), token);
   var progress = new Progress<DuplicateScanProgress>(p => Status.Text = T("SHA-256 · {0}/{1} · {2}", p.Processed, p.Total, Path.GetFileName(p.Path)));
   var result = await DuplicateFinder.FindAsync(items, progress, token);
   var report = Tn(result.Groups.Count, "1 group with identical file content.\nNo files were changed or removed.\n\n",
    "{0} groups with identical file content.\nNo files were changed or removed.\n\n") + string.Join("\n\n", result.Groups.Select((g,
    index) => T("Group {0} · {1:N0} bytes\nSHA-256: {2}\n", index + 1, g.FileSize, g.Sha256) + string.Join("\n", g.Items.Select(i => i.Path))));
   if (result.Errors.Count > 0) report += T("\n\nNot checked:\n") + string.Join("\n", result.Errors);
   Dialogs.Text(this, T("Identical files"), report);
  });
 }
}
