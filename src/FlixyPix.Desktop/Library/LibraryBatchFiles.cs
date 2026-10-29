using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using FlixyPix.Core;
using Microsoft.Win32;

namespace FlixyPix.Desktop;

public partial class MainWindow
{

 /// <summary>"Kopieren nach" / "Verschieben nach": the quick access folders, another folder, and managing the list.</summary>
 internal (string Label, Action Action)[] QuickAccessEntries(BatchFileAction action) =>
 [
  .. _settings.QuickAccessFolders.Select(folder => (Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)) is { Length: > 0 } name ? name + "    " + folder : folder,
   (Action)(() => PrepareBatchFiles(action, folder, BatchCollisionPolicy.UniqueName)))),
  (T("Other folder …"), () => PrepareBatchFiles(action)),
  (T("Manage quick access …"), () => OpenSettings("General")),
 ];

 /// <param name="target">A quick access folder; then neither folder nor collision rule are asked (the preview shows renames).</param>
 private async void PrepareBatchFiles(BatchFileAction action, string? target = null, BatchCollisionPolicy? preset = null)
 {
  var ids = MediaList.SelectedItems.OfType<MediaCard>().Select(card => card.Item.Id).Distinct().ToArray();
  if (ids.Length == 0) { Message.Show(this,T("Please select items with Ctrl/Shift.")); return; }
  if (target != null && !Directory.Exists(target)) { Message.Show(this, T("The folder cannot be reached.") + "\n" + target, T("Quick access"), MessageBoxButton.OK, MessageBoxImage.Warning); return; }
  string? directory = target; string? template = null;
  if (action != BatchFileAction.Rename && directory == null)
  {
   var picker = new OpenFolderDialog { AddToRecent = Privacy.AddToRecent, Title = T("Target folder for the selected files") };
   if (picker.ShowDialog(this) != true) return; directory = picker.FolderName;
  }
  else
  {
   var fields = Dialogs.Fields(this,T("File name template"),(T("Template: {name} = previous name, {index} = four-digit number, {ext} = extension"),"{name}-{index}{ext}"));
   if (fields == null) return; template = fields[0];
  }
  var collision = preset;
  if (collision == null)
  {
   var policy = Dialogs.Fields(this,T("Name collisions"),(T("If the target is taken: Cancel, Skip or Unique"),T("Cancel")));
   if (policy == null) return;
   // The answer is typed in the language the dialog asked in.
   bool Is(string answer) => policy[0].Trim().Equals(answer, StringComparison.CurrentCultureIgnoreCase);
   collision = Is(T("Cancel")) ? BatchCollisionPolicy.Cancel : Is(T("Skip")) ? BatchCollisionPolicy.Skip : Is(T("Unique")) ? BatchCollisionPolicy.UniqueName : null;
   if (collision == null) { Message.Show(this,T("Please enter Cancel, Skip or Unique.")); return; }
  }
  await RunOperation(T("Prepare file actions"),async token =>
  {
   var progress = new Progress<BatchFileProgress>(value => Status.Text = T("Checking previews: {0} / {1} · {2}", value.Completed, value.Total, Path.GetFileName(value.Path)));
   var plan = await Task.Run(() => _repository.PlanFileBatchAsync(ids,action,directory,collision.Value,template,token,progress),token);
   if (!ReviewBatchFilePlan(plan)) return;
   var runProgress = new Progress<BatchFileProgress>(value => Status.Text = T("File actions: {0} / {1} · {2}", value.Completed, value.Total, Path.GetFileName(value.Path)));
   var result = await Task.Run(() => _repository.ExecuteFileBatchAsync(plan,token,runProgress),token);
   var failures = result.Items.Where(item => item.Outcome == BatchFileOutcome.Failed).ToArray();
   Dialogs.Text(this,T("Result of the file actions"),T("Succeeded: {0}\nSkipped: {1}\nErrors: {2}", result.Items.Count(item => item.Outcome == BatchFileOutcome.Completed), result.Items.Count(item => item.Outcome == BatchFileOutcome.Skipped), failures.Length) +
    (result.Cancelled ? T("\nCancelled. Actions already completed are kept.") : "") +
    (failures.Length == 0 ? "" : "\n\n" + string.Join('\n',failures.Take(50).Select(item => $"{item.SourcePath}\n{item.Message}"))));
  });
 }

 private bool ReviewBatchFilePlan(BatchFilePlan plan)
 {
  var ready = plan.Items.Count(item => item.State == BatchFileItemState.Ready); var skipped = plan.Items.Count(item => item.State == BatchFileItemState.Skipped); var blocked = plan.Items.Count(item => item.State == BatchFileItemState.Blocked);
  var verb = plan.Action switch { BatchFileAction.Copy => T("copy"), BatchFileAction.Move => T("pan"), _ => T("rename") };
  var panel = new DockPanel { Margin = new Thickness(20) };
  var dialog = new Window { Owner = this, ShowInTaskbar = false, Title = T("Preview of the file actions"), Width = 1140, Height = 680, MinWidth = 800, MinHeight = 450,
   Content = panel, WindowStartupLocation = WindowStartupLocation.CenterOwner };
  var header = new TextBlock {
   Text = T("{0} files {1} · {2} skipped · {3} blocked\nAll entries are listed below. No existing target files are overwritten.",
   ready, verb, skipped, blocked), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,14), FontSize = 15 };
  DockPanel.SetDock(header,Dock.Top); panel.Children.Add(header);
  var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,14,0,0) };
  var cancel = new Button { Content = T("Cancel"), IsCancel = true };
  var accept = new Button { Content = T("{0} files now {1}", ready, verb), IsEnabled = plan.CanExecute };
  accept.Click += (_,_) => dialog.DialogResult = true; buttons.Children.Add(cancel); buttons.Children.Add(accept); DockPanel.SetDock(buttons,Dock.Bottom); panel.Children.Add(buttons);
  var list = new ListView { ItemsSource = plan.Items, FontSize = 12 };
  list.SetValue(VirtualizingPanel.IsVirtualizingProperty,true); list.SetValue(ScrollViewer.CanContentScrollProperty,true);
  var view = new GridView();
  foreach (var column in new[] { ("Status","State",95d),(T("Source"),"SourcePath",350d),(T("Target"),"TargetPath",350d),(T("Note"),"Note",260d) })
   view.Columns.Add(new GridViewColumn { Header = column.Item1, DisplayMemberBinding = new Binding(column.Item2), Width = column.Item3 });
  list.View = view; panel.Children.Add(list); return dialog.ShowDialog() == true;
 }
}
