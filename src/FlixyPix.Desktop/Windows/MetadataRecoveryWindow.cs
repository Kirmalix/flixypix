using System.Windows;
using System.Windows.Controls;

namespace FlixyPix.Desktop;

internal sealed record MetadataRecoveryRow(Guid MediaId, string Title, bool Exists);

internal sealed class MetadataRecoveryWindow : Window
{
 private readonly Dictionary<Guid, CheckBox> choices = [];
 private bool discardAll;
 internal Button RestoreButton { get; }
 internal IReadOnlyCollection<Guid> SelectedIds => discardAll ? [] : choices.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).ToArray();

 internal MetadataRecoveryWindow(Window owner, IReadOnlyList<MetadataRecoveryRow> rows)
 {
  Owner = owner; Title = T("Unsaved changes"); Width = 680; Height = 520; MinWidth = 480; MinHeight = 320;
  WindowStartupLocation = WindowStartupLocation.CenterOwner;
  var body = new StackPanel();
  foreach (var row in rows)
  {
   var content = new StackPanel();
   content.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(row.Title) ? row.MediaId.ToString() : row.Title, TextWrapping = TextWrapping.Wrap });
   if (!row.Exists) content.Children.Add(new TextBlock { Text = T("Missing"), Foreground = SettingsUi.Muted });
   var choice = new CheckBox { Content = content, IsChecked = true, IsEnabled = row.Exists, Margin = new Thickness(0, 8, 0, 8) };
   choices.Add(row.MediaId, choice); body.Children.Add(choice);
  }
  var frame = new WindowFrame(T("Unsaved changes"),
   T("Choose drafts to restore. Unselected drafts are discarded; missing media drafts are kept. Saved media stay unchanged."), body);
  frame.Status.Text = Tn(rows.Count, "1 item", "{0:N0} items");
  frame.Add(T("Cancel"), () => DialogResult = false, cancel: true);
  frame.Add(T("Discard changes"), () => { discardAll = true; DialogResult = true; });
  RestoreButton = frame.Add(T("Restore"), () => DialogResult = true, primary: true);
  Content = frame;
 }

 internal void Select(Guid id, bool selected) { if (choices[id].IsEnabled) choices[id].IsChecked = selected; }
}
