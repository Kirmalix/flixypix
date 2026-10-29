using System.Windows;
using FlixyPix.Core;
using Microsoft.Win32;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 private void NewGroupRule() => EditGroupRule(new() { Groups = [new() { Conditions = [new(TagRuleField.Filename,TagRuleComparison.Contains,"")] }] });
 private void EditGroupRule(FilenameTagRule rule)
 {
  var editor = CreateTagRuleEditor(rule);
  if (editor.ShowDialog() == true && editor.Result is { } value) { _repository.UpsertFilenameTagRule(value); Status.Text = T("Condition groups saved"); }
 }
 internal TagRuleEditorWindow CreateTagRuleEditor(FilenameTagRule rule) => new(this, rule, MeasureTagRuleAsync);

 /// <summary>How many media a rule fits and how many tags it would add, over the whole library in the background.</summary>
 private async Task<TagRuleEditorWindow.Impact> MeasureTagRuleAsync(FilenameTagRule rule, CancellationToken token)
 {
  int media = 0, added = 0; var examples = new List<string>();
  await foreach (var item in StreamCatalog(new MediaQuery(), token))
  {
   var tags = CatalogRepository.EvaluateTagRule(item, rule);
   if (tags.Count == 0) continue;
   var fresh = tags.Count(t => !item.Tags.Contains(t, StringComparer.OrdinalIgnoreCase));
   media++; added += fresh;
   if (examples.Count < 5 && fresh > 0) examples.Add(item.Title);
  }
  return new(media, added, examples);
 }
 private async void ImportOriginalRules()
 {
  var picker = new OpenFileDialog { AddToRecent = Privacy.AddToRecent, Title = T("Import tag rules from FlixyPix"), Filter = T("JSON tag rules|*.json") };
  if (picker.ShowDialog(this) != true) return;
  await RunOperation(T("Import tag rules"),async token => { var result = await Task.Run(() => _repository.ImportOriginalTagRules(picker.FileName),token); ShowImportResult(result); });
 }
 private void ExportOriginalRules()
 {
  var picker = new SaveFileDialog { AddToRecent = Privacy.AddToRecent, Title = T("Export tag group rules in the original format"), Filter = T("JSON tag rules|*.json"), FileName = "FlixyPix-Tagregeln.json" };
  if (picker.ShowDialog(this) != true) return;
  var rules = _repository.GetFilenameTagRules().Where(rule => rule.Groups.Count > 0).Select(rule => rule.Id).ToArray();
  if (rules.Length == 0) { Message.Show(this,T("There are no group rules yet. Simple rules are part of the JSON catalog backup.")); return; }
  _repository.ExportOriginalTagRules(picker.FileName,rules); Status.Text = T("Tag group rules exported");
 }
}
