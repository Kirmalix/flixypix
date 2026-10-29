using System.Windows;
using System.Windows.Controls;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

public partial class MainWindow
{
 internal void ShowAdultContentPack()
 {
  var window = new AdultContentPackWindow(this, _repository);
  window.ShowDialog();
  if (window.Changed) Refresh();
 }
}

internal sealed class AdultContentPackWindow : Window
{
 private readonly CatalogRepository repository;
 private readonly ComboBox language = new() { ItemsSource = new[] { "de", "en", "fr", "es", "it", "pt", "nl", "pl", "ja", "ru", "English (original)" }, Width = 150 };
 private readonly CheckBox genres = new() { Content = T("Genres"), IsChecked = true };
 private readonly CheckBox performers = new() { Content = T("Performers"), IsChecked = true };
 private readonly CheckBox studios = new() { Content = T("Studios"), IsChecked = true };
 private readonly CheckBox roles = new() { Content = T("Change category roles"), IsChecked = false };
 private readonly TextBlock source = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,12,0), VerticalAlignment = VerticalAlignment.Center };
 private readonly Button chooseProgram = new() { Content = T("Choose program folder …") };
 private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,12,0,12) };
 private readonly ListView list = new() { MaxHeight = 360 };
 private readonly TextBox search = new() { Margin = new Thickness(0,8,0,8) };
 private readonly ComboBox roleFilter = new() { ItemsSource = new[] { T("All"), T("Tags"), T("Performers"), T("Studios") }, SelectedIndex = 0, Width = 180, HorizontalAlignment = HorizontalAlignment.Left };
 private readonly Button apply = new() { Content = T("Apply") }, previewButton = new() { Content = T("Preview") }, undoButton = new() { Content = T("Undo"), IsEnabled = false };
 private string? program = OriginalPresets.FindProgram();
 private ContentPackPreview? preview;
 private ContentPackUndo? undo;
 private CancellationTokenSource? operation;
 internal bool Changed { get; private set; }

 internal AdultContentPackWindow(Window owner, CatalogRepository repository)
 {
  Owner = owner; this.repository = repository; Title = T("Adult Content Pack"); Width = 1000; Height = 740;
  WindowStartupLocation = WindowStartupLocation.CenterOwner;
  language.SelectedItem = Texts.Current.Language == "qps" ? "en" : Texts.Current.Language;
  var panel = new StackPanel { Margin = new Thickness(22) };
  panel.Children.Add(new TextBlock { Text = T("Adult Content Pack"), FontSize = 24 });
  panel.Children.Add(new TextBlock { Text = T("Media are not tagged automatically."), TextWrapping = TextWrapping.Wrap });
  var origin = new DockPanel { Margin = new Thickness(0,12,0,0) };
  DockPanel.SetDock(chooseProgram, Dock.Right); origin.Children.Add(chooseProgram); origin.Children.Add(source);
  panel.Children.Add(origin);
  var options = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,16,0,8) };
  foreach (var control in new FrameworkElement[] { language, genres, performers, studios }) { control.Margin = new Thickness(0,0,18,0); options.Children.Add(control); }
  panel.Children.Add(options); panel.Children.Add(roles);
  panel.Children.Add(search); panel.Children.Add(roleFilter); panel.Children.Add(list); panel.Children.Add(status);
  var columns = new GridView();
  foreach (var (label, property, width) in new[] { (T("Name"), "Name", 250d), (T("Category"), "Category", 160d), (T("Result"), "Status", 100d), (T("Note"), "Note", 360d) })
   columns.Columns.Add(new GridViewColumn { Header = label, DisplayMemberBinding = new System.Windows.Data.Binding(property), Width = width });
  list.View = columns; VirtualizingPanel.SetIsVirtualizing(list, true);
  search.TextChanged += (_, _) => Filter();
  roleFilter.SelectionChanged += (_, _) => Filter();
  var buttons = new StackPanel { Orientation = Orientation.Horizontal };
  var cancel = new Button { Content = T("Cancel") }; cancel.Click += (_, _) => operation?.Cancel();
  var close = new Button { Content = T("Close") }; close.Click += (_, _) => Close();
  foreach (var b in new[] { previewButton, apply, undoButton, cancel, close }) { b.Margin = new Thickness(0,0,12,0); buttons.Children.Add(b); }
  panel.Children.Add(buttons); Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
  previewButton.Click += async (_, _) => await Preview();
  apply.Click += async (_, _) => await Apply();
  undoButton.Click += (_, _) => { if (undo != null && repository.UndoContentPack(undo)) { Changed = false; status.Text = T("Undone"); undo = null; undoButton.IsEnabled = false; } else status.Text = T("The catalog changed. Undo is no longer available."); };
  foreach (var box in new[] { genres, performers, studios, roles }) { box.Checked += (_, _) => Invalidate(); box.Unchecked += (_, _) => Invalidate(); }
  language.SelectionChanged += (_, _) => Invalidate();
  chooseProgram.Click += (_, _) => ChooseProgram();
  ShowSource();
  apply.IsEnabled = false;
  Closing += (_, e) => { if (operation != null) { operation.Cancel(); e.Cancel = true; } else undo = null; };
 }
 /// <summary>FlixyPix does not ship the name lists; they come from the program file of an installed original.</summary>
 private void ShowSource()
 {
  source.Text = program != null
   ? T("Performers, studios and the original genre names are read from {0}.", program)
   : T("Performers, studios and the original genre names are read from an installation of EazyFlixPix 4. None was found; translated genres are available.");
  performers.IsEnabled = studios.IsEnabled = program != null;
  if (program == null) performers.IsChecked = studios.IsChecked = false;
 }
 private void ChooseProgram()
 {
  var picker = new Microsoft.Win32.OpenFolderDialog { AddToRecent = Privacy.AddToRecent, Title = T("Choose program folder …") };
  if (picker.ShowDialog(this) != true) return;
  if (OriginalPresets.FindProgram(picker.FolderName, new() { ProgramFolders = [] }) is not { } found) { status.Text = T("This folder contains no installation of EazyFlixPix 4."); return; }
  program = found; ShowSource(); performers.IsChecked = studios.IsChecked = true; Invalidate();
 }
 private void Invalidate() { if (undo != null) return; preview = null; apply.IsEnabled = false; }
 internal Task PreviewForTest() => Preview();
 internal int PreviewCount => preview?.Tags.Items.Count ?? 0;
 internal Task ApplyAllForTest() => ApplyRows(preview!.Tags.Items.Where(i => i.CanApply).Select(i => i.Row.Line).ToArray());
 internal void UndoForTest() => undoButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
 private void Filter() => list.ItemsSource = preview?.Tags.Items.Where(i => (roleFilter.SelectedIndex == 0 || (int)i.Role == roleFilter.SelectedIndex - 1) && (search.Text.Length == 0 || i.Row.Name.Contains(search.Text, StringComparison.CurrentCultureIgnoreCase) || i.Category.Contains(search.Text, StringComparison.CurrentCultureIgnoreCase))).Select(i => new PackageRowView(i)).ToList();
 private async Task Preview()
 {
  operation = new(); previewButton.IsEnabled = apply.IsEnabled = false;
  try
  {
   var code = (string?)language.SelectedItem ?? "en"; if (code == "English (original)") code = "original"; var program = this.program; var g = genres.IsChecked == true; var p = performers.IsChecked == true; var s = studios.IsChecked == true; var r = roles.IsChecked == true;
   preview = await Task.Run(() => repository.PreviewAdultContentPack(AppContext.BaseDirectory, program, code, g,p,s,r));
   Filter(); status.Text = T("{0} entries; {1} conflicts. Close keeps the import; undo is available only here.", preview.Tags.Items.Count, preview.Tags.Items.Count(i => !i.CanApply));
   apply.IsEnabled = true;
  }
  catch (Exception ex) { status.Text = ex.Message; }
  finally { operation.Dispose(); operation = null; previewButton.IsEnabled = true; }
 }
 private async Task Apply()
 {
  if (preview == null) return;
  // The existing selection preview allows choosing individual package rows.
  var selection = new TagPackageWindow(this, T("Adult Content Pack"), preview.Tags, []);
  if (selection.ShowDialog() != true) return;
  await ApplyRows(selection.ChosenLines);
 }
 private async Task ApplyRows(IReadOnlyCollection<int> chosenLines)
 {
  if (preview == null) return;
  operation = new(); apply.IsEnabled = previewButton.IsEnabled = false;
  try
  {
   status.Text = T("Importing …");
   var result = await Task.Run(() => repository.ApplyAdultContentPack(preview, chosenLines, operation.Token));
   undo = result.Undo; Changed = undo != null; undoButton.IsEnabled = Changed;
   status.Text = Changed ? T("Import complete. Undo is available until this dialog closes.") : T("No changes.");
  }
  catch (Exception ex) { status.Text = ex.Message; apply.IsEnabled = previewButton.IsEnabled = true; }
  finally { operation.Dispose(); operation = null; }
 }
}
