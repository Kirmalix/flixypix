using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FlixyPix.Desktop;

/// <summary>A small search list: typing asks <c>find</c> for matches, Enter or a double click takes the selected one.</summary>
internal sealed class ChoiceDialog<TValue> : Window where TValue : class
{
 private readonly TextBox query = new() { Padding = new Thickness(6, 5, 6, 5), Margin = new Thickness(0, 0, 0, 8) };
 private readonly ListBox results = new() { MinHeight = 260 };
 private readonly Func<string, IReadOnlyList<(TValue Value, string Label)>> find;
 internal TValue? Chosen { get; private set; }

 private sealed record Row(TValue Value, string Label) { public override string ToString() => Label; }

 internal ChoiceDialog(Window owner, string title, string hint, Func<string, IReadOnlyList<(TValue Value, string Label)>> find, string initial = "")
 {
  Owner = owner; Title = title; Width = 560; Height = 480; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false; this.find = find;
  var layout = new DockPanel { Margin = new Thickness(0) };
  var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top);
  top.Children.Add(new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, Foreground = SettingsUi.Muted, Margin = new Thickness(0, 0, 0, 8) });
  top.Children.Add(query); layout.Children.Add(top); layout.Children.Add(results);
  var frame = new WindowFrame(title, null, layout, "", scroll: false);
  frame.Add(T("Apply"), Accept, primary: true); frame.Add(T("Cancel"), Close, cancel: true);
  Content = frame;
  query.TextChanged += (_, _) => Fill();
  query.PreviewKeyDown += (_, e) =>
  {
   if (e.Key == Key.Down && results.Items.Count > 0) { results.SelectedIndex = Math.Min(results.SelectedIndex + 1, results.Items.Count - 1); e.Handled = true; }
   else if (e.Key == Key.Up && results.SelectedIndex > 0) { results.SelectedIndex--; e.Handled = true; }
   else if (e.Key == Key.Enter) { Accept(); e.Handled = true; }
  };
  results.MouseDoubleClick += (_, _) => Accept();
  query.Text = initial; Fill();
  Loaded += (_, _) => { query.Focus(); query.SelectAll(); };
 }

 internal int ResultCount => results.Items.Count;
 internal void Search(string text) => query.Text = text;
 internal void Accept() { if (results.SelectedItem is Row row) { Chosen = row.Value; DialogResult = true; } }

 private void Fill()
 {
  results.ItemsSource = find(query.Text.Trim()).Select(match => new Row(match.Value, match.Label)).ToList();
  if (results.Items.Count > 0) results.SelectedIndex = 0;
 }
}
