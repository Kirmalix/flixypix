using System.Windows;
using System.Windows.Controls;
using FlixyPix.Core;

namespace FlixyPix.Desktop;

/// <summary>Providers of "Im Browser suchen" in the tools rubric: name and address with {query}; they belong to this computer.</summary>
internal sealed partial class SettingsWindow
{
 private List<WebSearchProvider> searchProviders = [];
 private readonly StackPanel providerRows = new() { Margin = new Thickness(16, 4, 16, 8) };
 internal IReadOnlyList<WebSearchProvider> SearchProviders => searchProviders;

 private Border WebSearchCard()
 {
  var add = SettingsUi.SmallButton(T("＋ Add provider …")); add.Click += (_, _) => EditProvider(null);
  var defaults = SettingsUi.SmallButton(T("Restore defaults")); defaults.Click += (_, _) => { searchProviders = [.. WebSearch.Defaults]; ShowProviders(); Touch(); };
  ShowProviders();
  return SettingsUi.Card(T("Search in browser"), T("For tags, performers and studios: in the context menu of a tag and in the directory. FlixyPix only opens the browser and fetches nothing itself."),
   providerRows, new SettingRow(T("Provider"), T("Web address with {0} for the name and optionally {1} for the interface language.", "{query}", "{lang}"), SettingsUi.Buttons(defaults, add)));
 }

 private void ShowProviders()
 {
  providerRows.Children.Clear();
  if (searchProviders.Count == 0) providerRows.Children.Add(SettingsUi.Hint(T("No providers; the menu stays empty.")));
  foreach (var provider in searchProviders.ToList())
  {
   var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
   text.Children.Add(new TextBlock { Text = provider.Name, FontWeight = FontWeights.SemiBold });
   text.Children.Add(new TextBlock { Text = provider.Url, FontSize = 12, Foreground = SettingsUi.Muted, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = provider.Url });
   var edit = SettingsUi.SmallButton(T("Edit …")); edit.Click += (_, _) => EditProvider(provider);
   var remove = SettingsUi.SmallButton(T("Remove")); remove.Click += (_, _) => { searchProviders.Remove(provider); ShowProviders(); Touch(); };
   var row = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
   var buttons = SettingsUi.Buttons(edit, remove); DockPanel.SetDock(buttons, Dock.Right);
   row.Children.Add(buttons); row.Children.Add(text);
   providerRows.Children.Add(row);
  }
 }

 private void EditProvider(WebSearchProvider? provider)
 {
  var values = Dialogs.Fields(this, provider == null ? T("Add provider") : T("Edit provider"), (T("Name"), provider?.Name ?? ""),
   (T("Web address with {0}", "{query}"), provider?.Url ?? "https://"));
  if (values == null) return;
  var result = new WebSearchProvider(values[0], values[1]);
  if (WebSearch.Problem(result) is { } problem) { Message.Show(this, problem, T("Search in browser"), MessageBoxButton.OK, MessageBoxImage.Warning); return; }
  var index = provider == null ? -1 : searchProviders.IndexOf(provider);
  if (index >= 0) searchProviders[index] = result; else searchProviders.Add(result);
  ShowProviders(); Touch();
 }

 /// <summary>Null when the list equals the defaults, so later default changes still reach this computer.</summary>
 private List<WebSearchProvider>? ChosenProviders() => searchProviders.SequenceEqual(WebSearch.Defaults) ? null : searchProviders.ToList();
}
